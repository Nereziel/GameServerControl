# GSC phased reliability improvements — design (2026-11-16)

## Goal
Make GameServerControl safer to operate on existing game-world data, improve update/install recovery, then expand supported game workflows without weakening existing server isolation.

## Existing architecture
Privileged Agent exposes authenticated WebUI and manages installation, backup, Runner and relay; one low-privilege Windows Runner identity per BareMetal server. Cloudflare Worker proxies the same WebUI to independently managed Agents. Windows release updates have pinned version checks and rollback.

## Priority A — Safety before features
- Incomplete backup archives must not consume the retention quota for restorable snapshots. Preserve the newest configured number of *restorable* normal archives, retain at most three partial archives and three safety archives independently. Incomplete backups remain visible but return an unsuccessful result and remain non-restorable. No backup size limit is added or modified.
- Linux Agent and Relay release installers must download and SHA-256 verify their versioned archive against that release's SHA256SUMS **before** stopping services or extracting.
- Bound Minecraft ZIP extraction: reject untrusted paths and symlink entries, high decompression ratios, excessive entry count, and unreasonable total uncompressed size. Apply only to Minecraft server/add-on extraction, not backup ZIP archives.
- Windows Agent ACL issue #55 needs dedicated on-host, elevated/unelevated integration verification; do not grant Users/Everyone broad access or weaken Runner isolation.
 
## Priority B — Operations
- Make install jobs durable across Agent restarts and cancellable; never delete successfully downloaded Minecraft data on a later registry error; provide explicit recovery.
- Detect incompatible Agent/Cloudflare feature versions, disabling unsupported controls.
- Validate Java runtime version rather than mere java.exe presence.
- E2E browser/regression verification of the create/install flow and flaky integration host-disposal issue.

## Priority C — Expanded features
- Optional off-machine backup target with integrity validation, no external copies by default.
- Forge/NeoForge/modpack installers with approved vendor origins, verified artifacts, staged/rollback installation, and EULA acceptance.
- Clear CPU/RAM/disk/availability monitoring and troubleshooting UI.

## Constraints
All edits restricted to Nereziel/GameServerControl; no changes to PC1, production VPS, Cloudflare or other repositories. Keep tokens and secrets in owning systems, never emit them. No modification of existing backup size limits. Separate changes into independently testable PRs with CI and documented limitations.
