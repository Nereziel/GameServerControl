import fs from 'node:fs';

const src = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js', 'utf8');

const required = [
  ['/backups/settings', 'retention settings API'],
  ['swBackupLabel', 'optional backup label input'],
  ['swBackupRetention', 'retention control'],
  ["method: 'POST', body: JSON.stringify({ label:", 'named backup creation'],
  ['Safety copy', 'restore safety explanation'],
  ['backup.kind', 'backup kind metadata'],
  ['backup.skippedFiles', 'skipped-file warning'],
  ['serverWorkspaceSaveBackupRetention', 'retention save action'],
  ['serverWorkspaceCreateBackup', 'named backup action'],
  ['restoreDisabled', 'running-state restore guard']
];

for (const [token, purpose] of required) {
  if (!src.includes(token)) throw new Error(`Backup UI missing ${purpose}: ${token}`);
}

console.log('Backup workspace UI contract OK.');
