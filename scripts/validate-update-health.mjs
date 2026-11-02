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
const program = read('src/GameServerControl.Agent/Program.cs');
const checker = read('src/GameServerControl.Agent/Notifications/UpdateChecker.cs');
const windows = read('deploy/windows/install.ps1');
const linux = read('deploy/linux/install-from-release.sh');

requireCondition(updateToken.includes('.update-health-token'),
  'BUG-02: temporary update-health credential must use the reserved local state file.');
requireCondition(/CryptographicOperations\.FixedTimeEquals/.test(updateToken),
  'BUG-02: update-health token comparison must be constant-time.');
requireCondition(/Request\.Path[^\n]*\/api\/health/.test(auth) && /UpdateHealthToken/.test(auth),
  'BUG-02: temporary update credential must be accepted only on exact /api/health authentication.');
requireCondition(/public string CurrentVersion/.test(checker),
  'BUG-02: UpdateChecker must expose the running assembly version independently of the latest release.');
requireCondition(/MapGet\("\/health"[\s\S]{0,500}version[\s\S]{0,200}Environment\.ProcessId/.test(program),
  'BUG-02: authenticated /api/health must report running version and process id.');

for (const [name, text] of [['Windows installer', windows], ['Linux installer', linux]]) {
  requireCondition(text.includes('.update-health-token'),
    `BUG-02: ${name} must create a temporary update-health credential.`);
  requireCondition(text.includes('/api/health'),
    `BUG-02: ${name} must probe /api/health before accepting an update.`);
  requireCondition(/Authorization/.test(text) && /Bearer/.test(text),
    `BUG-02: ${name} health probe must authenticate with a bearer credential.`);
  requireCondition(/30/.test(text),
    `BUG-02: ${name} health gate must have a bounded 30-second verification window.`);
  requireCondition(/consecutive|Consecutive/.test(text) && /3/.test(text),
    `BUG-02: ${name} must require three consecutive successful health probes.`);
  requireCondition(/version/i.test(text) && /expected/i.test(text),
    `BUG-02: ${name} must compare the health response to the expected release version.`);
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
