import fs from 'node:fs';

const workflow = fs.readFileSync('.github/workflows/release.yml', 'utf8');

function requireToken(token, message) {
  if (!workflow.includes(token)) throw new Error(`${message}: missing ${token}`);
}

requireToken('\n  validate:\n', 'Release workflow must define a validation gate');
requireToken('needs: prepare', 'Validation gate must use the exact release source resolved by prepare');
requireToken('node scripts/validate-webui.mjs', 'Release gate must validate Web UI JavaScript');
requireToken('node scripts/validate-readonly-ui.mjs', 'Release gate must validate ReadOnly UI behavior');
requireToken('node scripts/validate-global-settings.mjs', 'Release gate must validate global Settings ownership');
requireToken('node scripts/validate-mod-management.mjs', 'Release gate must validate mod/plugin management');
requireToken('dotnet run --project tools/GameServerControl.BackupCheck -c Release', 'Release gate must validate backup safety');
requireToken('dotnet run --project tools/GameServerControl.SecurityCheck -c Release', 'Release gate must validate token storage security');
requireToken('node scripts/validate-security-access.mjs', 'Release gate must validate Agent security/access contracts');
requireToken('node scripts/validate-backup-lifecycle.mjs', 'Release gate must validate backup lifecycle');
requireToken('node scripts/validate-backup-ui.mjs', 'Release gate must validate backup workspace UI');
requireToken('dotnet run --project tools/GameServerControl.ProfileCheck -c Release', 'Release gate must validate game profiles');
requireToken('npm test', 'Release gate must run Cloudflare Worker tests');
requireToken('npm run build', 'Release gate must build the Cloudflare Worker/Web UI bundle');
requireToken('wrangler deploy --dry-run', 'Release gate must dry-run the Cloudflare Worker deployment');

const buildWindows = workflow.indexOf('\n  build-windows:');
const buildLinux = workflow.indexOf('\n  build-linux:');
if (buildWindows < 0 || buildLinux < 0) throw new Error('Release platform jobs are missing.');

const windowsBlock = workflow.slice(buildWindows, buildLinux);
const releaseBlockStart = workflow.indexOf('\n  release:', buildLinux);
const linuxBlock = workflow.slice(buildLinux, releaseBlockStart < 0 ? workflow.length : releaseBlockStart);

for (const [name, block] of [['build-windows', windowsBlock], ['build-linux', linuxBlock]]) {
  if (!block.includes('needs: [prepare, validate]'))
    throw new Error(`${name} must be gated by prepare + validate.`);
}

for (const asset of [
  'GameServerControl-Agent-windows.zip',
  'GameServerControl-Runner-windows.zip',
  'GameServerControl-Tray-windows.zip',
  'GameServerControl-Agent-linux.tar.gz',
  'GameServerControl-Relay-linux.tar.gz',
  'GameServerControl-Install-Windows.ps1',
  'GameServerControl-Install-Cloudflare-Tunnel-Windows.ps1',
  'GameServerControl-Install-Linux.sh',
  'GameServerControl-Install-Relay.sh'
]) requireToken(asset, 'Release asset contract changed unexpectedly');

console.log('Release validation gate contract OK.');
