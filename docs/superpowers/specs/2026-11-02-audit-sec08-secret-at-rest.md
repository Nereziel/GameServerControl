# SEC-08 Secret At-Rest Protection Design

## Intent

Remove reusable plaintext credentials from GameServerControl Agent state while preserving the existing API/UI/runtime contracts and automatically migrating existing installations.

## Goals

- Encrypt persisted server RCON passwords and Discord webhook URLs.
- Encrypt the persisted public-relay bearer token.
- Move sensitive `appsettings.json` values into encrypted secret state while keeping them available through `IConfiguration` in memory.
- Cover the bootstrap Agent token, certificate password, provider API keys, and Hyper-V guest passwords.
- Migrate existing plaintext state automatically on startup without printing secret values.
- Preserve the encryption key/state through Windows and Linux upgrades.
- Keep managed `tokens.json` hash-only; do not make reusable bearer tokens recoverable.

## Non-goals

- Protect secrets from root, LocalSystem, local Administrators, or the Agent service identity while the Agent is running.
- Introduce a remote KMS or external secret-management dependency.
- Change browser/API payload shapes or server definition models.
- Encrypt non-secret game configuration files managed by the Files editor.

## Approaches

1. **Windows DPAPI only.** Strong on Windows but does not solve Linux and would split the storage model by platform. Rejected.
2. **ASP.NET Data Protection key ring.** Integrates with the web stack, but explicit cross-platform key lifecycle and installer preservation are less obvious for this standalone state use case. Rejected for this change.
3. **AES-GCM envelope with a platform-protected master key. Chosen.** A random 256-bit master key encrypts individual secrets with AES-GCM and purpose-bound associated data. Windows stores the master key wrapped with DPAPI LocalMachine; Linux stores the random key only in the already-restricted Agent directory with mode 0600. This gives one deterministic envelope format and explicit upgrade/migration behavior.

## Storage model

- `secret.key`: versioned master-key envelope. Windows payload is DPAPI LocalMachine protected; Linux payload is the random master key encoded for storage. The file is restricted by the existing `SecretFilePermissions` controls and Windows installer ACLs.
- `secret-state.json`: encrypted configuration values keyed by configuration path. No reusable plaintext secret is written to this file.
- Protected values use a versioned `gsc-secret-v1:` envelope containing a random nonce, AES-GCM ciphertext, and authentication tag.
- The configuration path or server-field identity is supplied as AES-GCM associated data so ciphertext cannot be silently swapped between purposes.

## Configuration migration

At startup, before authentication/service registration consumes credentials:

1. Load `secret.key` and `secret-state.json`.
2. Parse the physical `appsettings.json` Agent subtree and identify sensitive leaf names: `ApiToken`, `CertPassword`, `ApiKey`, and `Password`.
3. Move real plaintext values from those leaves into encrypted state and replace the on-disk value with a non-secret sentinel.
4. Apply decrypted values back into the in-memory `IConfiguration` unless a higher-precedence runtime value already supplies the credential.
5. If no bootstrap Agent token exists, generate it directly into encrypted state instead of first writing plaintext to `appsettings.json`.

Environment/runtime overrides are not copied to disk merely because they exist in `IConfiguration`.

## Server and relay state

`ServerRegistry` and `ServerStore` share one codec for `RconPassword` and `DiscordWebhookUrl`. Loading accepts legacy plaintext, returns plaintext only in memory, and rewrites legacy state to encrypted envelopes. Saving always writes encrypted envelopes. Existing plaintext backup state is replaced with an encrypted backup during migration.

`PublicRelayManager` accepts legacy plaintext `Global.ApiToken`, decrypts protected values in memory, and rewrites legacy state encrypted on first load/save.

## Failure behavior

- Authentication failure/tampering of a protected secret is a hard decryption failure; do not silently treat ciphertext as plaintext.
- Logs may identify the path/purpose that failed but never include secret values or ciphertext contents.
- Missing key material for existing encrypted state is an operational error, not a trigger to generate a replacement key.

## Upgrade behavior

Windows and Linux release installers must preserve `secret.key` and `secret-state.json` alongside the existing Agent state. Permission-hardening loops must include both files. The source Linux installer must also preserve existing state rather than deleting the encryption key during reinstall.

## Security boundary

On Linux, an attacker able to read both the service-owned `secret.key` and encrypted state can decrypt secrets. Filesystem ownership/mode remains part of the trust boundary. On Windows, DPAPI LocalMachine additionally binds the master key to the machine, while SYSTEM/local Administrators remain trusted. This change primarily removes reusable plaintext from state files, backups, and copied configuration artifacts.