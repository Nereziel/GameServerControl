import fs from 'node:fs';

function read(path) {
  return fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
}
function requireFile(path) {
  if (!fs.existsSync(path)) throw new Error(`SEC-08 missing required file: ${path}`);
  return read(path);
}
function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}
function rejectToken(source, token, message) {
  if (source.includes(token)) throw new Error(`${message}: found ${token}`);
}

const protector = requireFile('src/GameServerControl.Agent/Security/SecretProtector.cs');
const configStore = requireFile('src/GameServerControl.Agent/Security/ConfigurationSecretStore.cs');
const serverCodec = requireFile('src/GameServerControl.Agent/Servers/ServerSecretProtection.cs');
const registry = read('src/GameServerControl.Agent/Servers/ServerRegistry.cs');
const store = read('src/GameServerControl.Agent/Servers/ServerStore.cs');
const relay = read('src/GameServerControl.Agent/Networking/PublicRelayManager.cs');
const firstRun = read('src/GameServerControl.Agent/Auth/FirstRunTokenGenerator.cs');
const certHelper = read('src/GameServerControl.Agent/Auth/CertHelper.cs');
const winInstaller = read('deploy/windows/install.ps1');
const linuxReleaseInstaller = read('deploy/linux/install-from-release.sh');
const linuxSourceInstaller = read('deploy/linux/install.sh');
const security = read('SECURITY.md');

requireToken(protector, 'AesGcm', 'SEC-08 secret envelopes must use authenticated encryption');
requireToken(protector, 'gsc-secret-v1:', 'SEC-08 protected values need a versioned envelope');
requireToken(protector, 'DataProtectionScope.LocalMachine', 'Windows master key must be DPAPI LocalMachine protected');
requireToken(protector, 'SecretFilePermissions', 'Master-key storage must use restrictive file permissions');
requireToken(protector, 'purpose', 'AES-GCM envelopes must bind ciphertext to a purpose');

for (const key of ['ApiToken', 'CertPassword', 'ApiKey', 'Password'])
  requireToken(configStore, key, `Configuration migration must classify ${key} as sensitive`);
requireToken(configStore, 'secret-state.json', 'Encrypted configuration state needs a stable file');
requireToken(configStore, '<stored-encrypted>', 'appsettings plaintext must be replaced by a non-secret sentinel');
requireToken(firstRun, 'MigrateAndApply', 'General configuration migration must run before auth/service consumers are instantiated');
requireToken(firstRun, 'ConfigurationSecretStore', 'First-run bootstrap token must persist through encrypted secret state');
requireToken(certHelper, 'ResolveEffectiveSecret', 'HTTPS certificate password must resolve encrypted state before Kestrel uses it');
requireToken(certHelper, 'AppContext.BaseDirectory', 'Certificate secret resolution must use the single Agent state root, not a custom PFX directory');
rejectToken(firstRun, '"ApiToken": "{token}"', 'First-run token must not be written into appsettings plaintext');

requireToken(serverCodec, 'RconPassword', 'Server RCON password must be protected at rest');
requireToken(serverCodec, 'DiscordWebhookUrl', 'Server Discord webhook must be protected at rest');
requireToken(registry, 'ServerSecretProtection', 'ServerRegistry must decrypt and migrate legacy server secrets');
requireToken(store, 'ServerSecretProtection', 'ServerStore must encrypt server secrets on every save');
requireToken(relay, 'SecretProtector', 'Public relay state must encrypt its bearer token');
requireToken(relay, 'relay:api-token', 'Relay token encryption must use a dedicated purpose');

for (const [name, source] of [
  ['Windows installer', winInstaller],
  ['Linux release installer', linuxReleaseInstaller],
  ['Linux source installer', linuxSourceInstaller]
]) {
  requireToken(source, 'secret.key', `${name} must preserve/protect the SEC-08 master key`);
  requireToken(source, 'secret-state.json', `${name} must preserve/protect encrypted configuration state`);
}

rejectToken(security, 'Server/RCON/password values are not encrypted at rest yet.', 'SECURITY.md must not retain the obsolete plaintext-at-rest limitation');
requireToken(security, 'AES-GCM', 'SECURITY.md must document SEC-08 encrypted state');
requireToken(security, 'DPAPI', 'SECURITY.md must document Windows key protection');

console.log('Audit SEC-08 secret at-rest contract OK.');
