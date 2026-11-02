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
const linuxSourceInstaller = fs.readFileSync('deploy/linux/install.sh', 'utf8');
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

function requireBefore(source, earlier, later, message) {
  const earlierIndex = source.indexOf(earlier);
  const laterIndex = source.indexOf(later);
  if (earlierIndex < 0 || laterIndex < 0 || earlierIndex >= laterIndex) {
    throw new Error(`${message}: expected ${earlier} before ${later}`);
  }
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
requireToken(linuxInstaller, 'umask 077', 'Release Linux installer must create private temporary/state files');
requireToken(linuxInstaller, 'chmod 700 "$INSTALL_DIR"', 'Release Linux installer must restrict the Agent state directory');
requireToken(linuxInstaller, 'chmod 600', 'Release Linux installer must restrict persisted secret files');
requireBefore(linuxInstaller, 'chmod 700 "$INSTALL_DIR"', 'preserve_dir="$tmp_dir/preserved"', 'Existing Linux secret state must be restricted before it is copied/moved during upgrade');
rejectToken(linuxInstaller, 'echo "   $api_token"', 'Release Linux installer must not print the persisted API token');
requireToken(linuxSourceInstaller, 'umask 077', 'Source-build Linux installer must create private state files');
requireToken(linuxSourceInstaller, 'chmod 700 "$INSTALL_DIR"', 'Source-build Linux installer must restrict the Agent state directory');
requireToken(linuxSourceInstaller, 'chmod 600', 'Source-build Linux installer must restrict persisted secret files');
rejectToken(linuxSourceInstaller, 'GENERATED NEW AGENT API TOKEN', 'Source-build Linux installer must not scrape the bootstrap token from journald');
requireToken(installer, 'function Set-AgentDirectoryAcl', 'Windows installer must centralize restrictive Agent ACL handling');
requireToken(installer, 'SYSTEM:(OI)(CI)F', 'Windows Agent ACL must grant SYSTEM');
requireToken(installer, 'BUILTIN\\Administrators:(OI)(CI)F', 'Windows Agent ACL must grant local Administrators');
requireBefore(installer, 'Set-AgentDirectoryAcl -Path $agentDir', "$preserve = @('appsettings.json'", 'Existing Windows secret state must be restricted before it is copied/moved during upgrade');
const newAgentDir = 'New-Item -ItemType Directory -Force -Path $agentDir | Out-Null';
const newAgentDirIndex = installer.indexOf(newAgentDir);
const aclAfterNewDirIndex = installer.indexOf('Set-AgentDirectoryAcl -Path $agentDir', newAgentDirIndex + newAgentDir.length);
if (newAgentDirIndex < 0 || aclAfterNewDirIndex < 0) {
  throw new Error('Fresh Windows Agent directory must be restricted before release contents and preserved secrets are copied into it');
}

console.log('Audit SEC-03 through SEC-06 contract OK.');
