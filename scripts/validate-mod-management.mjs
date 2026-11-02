import fs from 'node:fs';

const ui = fs.readFileSync('src/GameServerControl.Agent/WebUi/jobs-mods.js', 'utf8');
const failures = [];
const need = (text, label) => { if (!ui.includes(text)) failures.push(`Missing ${label}: ${text}`); };

need('configureInstalledMod(', 'installed mod Configure workflow');
need('/files`)', 'allowed config-file discovery API usage');
need('/files/content', 'config file content API usage');
need('/files/validate', 'config syntax validation API usage');
need('expectedSha256', 'SHA-protected config save');
need('Mods / plugins', 'fallback to all mod/plugin config files');
need('safeExternalModUrl(', 'HTTP(S)-only installed source helper');
need('modRestartNotice(', 'running-server restart warning');
need('const failures=[]', 'bulk update failure aggregation');
need('Update all finished', 'bulk update completion summary');

if (failures.length) {
  console.error(failures.join('\n'));
  process.exit(1);
}
console.log('Mod/plugin management contract OK');
