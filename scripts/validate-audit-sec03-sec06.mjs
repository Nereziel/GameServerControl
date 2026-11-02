import fs from 'node:fs';

const release = fs.readFileSync('.github/workflows/release.yml', 'utf8');
const installer = fs.readFileSync('deploy/windows/install.ps1', 'utf8');
const valheim = fs.readFileSync('src/GameServerControl.Agent/Mods/ValheimBepInExModManager.cs', 'utf8');
const firstRun = fs.readFileSync('src/GameServerControl.Agent/Auth/FirstRunTokenGenerator.cs', 'utf8');
const tokenRegistry = fs.readFileSync('src/GameServerControl.Agent/Auth/TokenRegistry.cs', 'utf8');
const serverStore = fs.readFileSync('src/GameServerControl.Agent/Servers/ServerStore.cs', 'utf8');
const relay = fs.readFileSync('src/GameServerControl.Agent/Networking/PublicRelayManager.cs', 'utf8');
const linuxUnit = fs.readFileSync('deploy/linux/gameservercontrol-agent.service', 'utf8');
const linuxInstaller = fs.readFileSync('deploy/linux/install-from-release.sh', 'utf8');
const pathHelperPath = 'src/GameServerControl.Shared/PathContainment.cs';
const secretHelperPath = 'src/GameServerControl.Shared/SecretFilePermissions.cs';
const pathHelper = fs.existsSync(pathHelperPath) ? fs.readFileSync(pathHelperPath, 'utf8') : '';
const secretHelper = fs.existsSync(secretHelperPath) ? fs.readFileSync(secretHelperPath, 'utf8') : '';

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

function rejectToken(source, token, message) {
  if (source.includes(token)) throw new Error(`${message}: unexpected ${token}`);
}

// SEC-03: release/update execution must be pinned to a concrete release and hash verified.
requireToken(release, 'SHA256SUMS', 'Release must publish a SHA-256 manifest');
requireToken(release, 'sha256sum', 'Release must generate SHA-256 values for staged assets');
requireToken(installer, 'SHA256SUMS', 'Windows installer/updater must consume the release checksum manifest');
requireToken(installer, 'Get-FileHash', 'Windows installer/updater must verify SHA-256 before execution/extraction');
requireToken(installer, '$env:GSC_VERSION = $tag', 'Updater must pin the verified installer to the resolved release tag');
rejectToken(installer, 'Invoke-Expression', 'Windows updater must never execute downloaded script text');
rejectToken(installer, 'raw.githubusercontent.com/$repo/main/deploy/windows/install.ps1', 'Windows updater must not execute mutable main');

// SEC-04: destructive Valheim paths must be canonicalized and contained below the mods root.
requireToken(pathHelper, 'ResolveDirectChild', 'A shared direct-child path guard is required');
requireToken(pathHelper, 'IsWithinRoot', 'A shared canonical containment guard is required');
requireToken(pathHelper, 'Path.GetFullPath', 'Containment must compare canonical paths');
requireToken(valheim, 'PathContainment.ResolveDirectChild', 'Valheim install/uninstall paths must use the direct-child guard');
requireToken(valheim, 'PathContainment.IsWithinRoot', 'Metadata-driven deletes must verify containment');

// SEC-05/06: credentials must not reach service stdout and secret state must be restrictive.
rejectToken(firstRun, 'Console.WriteLine($"     {token}")', 'Bootstrap API token must not be printed to stdout');
requireToken(secretHelper, 'File.SetUnixFileMode', 'Secret files must be chmodded explicitly on Unix');
requireToken(secretHelper, 'UnixFileMode.UserRead | UnixFileMode.UserWrite', 'Secret files must be 0600 on Unix');
requireToken(firstRun, 'SecretFilePermissions', 'appsettings token persistence must use the common secret-file helper');
requireToken(tokenRegistry, 'SecretFilePermissions', 'tokens.json must use the common secret-file helper');
requireToken(serverStore, 'SecretFilePermissions', 'servers.json must use the common secret-file helper');
requireToken(relay, 'SecretFilePermissions', 'public-relay.json must use the common secret-file helper');
requireToken(linuxUnit, 'UMask=0077', 'systemd must default Agent-created files to private permissions');
requireToken(linuxInstaller, 'chmod 700 "$INSTALL_DIR"', 'Linux installer must restrict the Agent state directory');
requireToken(linuxInstaller, 'chmod 600', 'Linux installer must restrict persisted secret files');
requireToken(installer, 'icacls', 'Windows installer must restrict the Agent directory ACL');
requireToken(installer, 'SYSTEM:(OI)(CI)F', 'Windows Agent ACL must grant SYSTEM');
requireToken(installer, 'BUILTIN\\Administrators:(OI)(CI)F', 'Windows Agent ACL must grant local Administrators');

console.log('Audit SEC-03 through SEC-06 contract OK.');
