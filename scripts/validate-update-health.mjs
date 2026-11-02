import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');

function read(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), 'utf8');
}

function requireCondition(condition, message) {
  if (!condition) throw new Error(message);
}

const updateTokenPath = path.join(root, 'src/GameServerControl.Agent/Auth/UpdateHealthToken.cs');
requireCondition(fs.existsSync(updateTokenPath),
  'BUG-02: scoped update-health token validator is missing.');

const updateToken = read('src/GameServerControl.Agent/Auth/UpdateHealthToken.cs');
const auth = read('src/GameServerControl.Agent/Auth/TokenAuthHandler.cs');
const checker = read('src/GameServerControl.Agent/Notifications/UpdateChecker.cs');
const windows = read('deploy/windows/install.ps1');
const linux = read('deploy/linux/install-from-release.sh');

requireCondition(updateToken.includes('.update-health-token'),
  'BUG-02: temporary update-health credential must use the reserved local state file.');
requireCondition(/CryptographicOperations\.FixedTimeEquals/.test(updateToken),
  'BUG-02: update-health token comparison must be constant-time.');
requireCondition(/string\.Equals\(Request\.Path\.Value, "\/api\/health"/.test(auth) && /UpdateHealthToken\.IsValid/.test(auth),
  'BUG-02: temporary update credential must be accepted only on exact /api/health authentication.');
requireCondition(/X-GameServerControl-Version/i.test(auth) && /X-GameServerControl-Pid/i.test(auth),
  'BUG-02: authenticated update-health response must expose running version and process id metadata.');
requireCondition(/public string CurrentVersion/.test(checker),
  'BUG-02: UpdateChecker must expose the running assembly version independently of the latest release.');

for (const [name, text] of [['Windows installer', windows], ['Linux installer', linux]]) {
  requireCondition(text.includes('.update-health-token'),
    `BUG-02: ${name} must create a temporary update-health credential.`);
  requireCondition(text.includes('/api/health'),
    `BUG-02: ${name} must probe /api/health before accepting an update.`);
  requireCondition(/Authorization/i.test(text) && /Bearer/i.test(text),
    `BUG-02: ${name} health probe must authenticate with a bearer credential.`);
  requireCondition(/30/.test(text),
    `BUG-02: ${name} health gate must have a bounded 30-second verification window.`);
  requireCondition(/consecutive/i.test(text) && /3/.test(text),
    `BUG-02: ${name} must require three consecutive successful health probes.`);
  requireCondition(/X-GameServerControl-Version/i.test(text) && /expected/i.test(text),
    `BUG-02: ${name} must compare the authenticated health response to the expected release version.`);
  requireCondition(/X-GameServerControl-Pid/i.test(text),
    `BUG-02: ${name} must verify process identity is stable across health probes.`);
}

const windowsHealth = windows.indexOf('/api/health');
const windowsRemovePrevious = windows.indexOf("Remove-Item $previousAgentDir -Recurse -Force", windowsHealth);
requireCondition(windowsHealth >= 0 && windowsRemovePrevious > windowsHealth,
  'BUG-02: Windows must not delete Agent.previous before authenticated health/version verification.');

const linuxHealth = linux.indexOf('/api/health');
const linuxRemovePrevious = linux.indexOf('rm -rf "$PREVIOUS_DIR"', linuxHealth);
requireCondition(linuxHealth >= 0 && linuxRemovePrevious > linuxHealth,
  'BUG-02: Linux must not delete the previous payload before authenticated health/version verification.');

console.log('BUG-02 authenticated update health/version rollback contract OK.');
