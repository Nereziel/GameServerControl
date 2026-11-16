import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Exercise the same shared choice renderer used by the modal and workspace tabs.
const source = fs.readFileSync('src/GameServerControl.Agent/WebUi/config-backups.js', 'utf8');
const start = source.indexOf('function configField(f, v) {');
const end = source.indexOf('\nasync function saveConfig(', start);
assert.ok(start >= 0 && end > start, 'configField renderer must exist');
const context = { esc: text => String(text) };
vm.runInNewContext(source.slice(start, end), context);

const cases = [
  ['preset', ['','Normal','Casual','Easy','Hard','Hardcore','Immersive','Hammer'], 'hard', 'Hard'],
  ['modifier:combat', ['','VeryEasy','Easy','Hard','VeryHard'], 'veryhard', 'VeryHard'],
  ['modifier:deathpenalty', ['','Casual','VeryEasy','Easy','Hard','Hardcore'], 'casual', 'Casual'],
  ['modifier:resources', ['','MuchLess','Less','More','MuchMore','Most'], 'muchmore', 'MuchMore'],
  ['modifier:raids', ['','None','MuchLess','Less','More','MuchMore'], 'none', 'None'],
  ['modifier:portals', ['','Casual','Hard','VeryHard'], 'veryhard', 'VeryHard']
];
for (const [key, choices, stored, expected] of cases) {
  const html = context.configField({ key, label: key, type: 'Choice', choices }, stored);
  assert.ok(html.includes('<option selected>' + expected + '</option>'),
    key + ' must restore the canonical option when the server sends ' + stored);
  const empty = context.configField({ key, label: key, type: 'Choice', choices }, '');
  assert.ok(empty.includes('<option selected></option>'),
    key + ' must keep the intentional empty/default option');
}
console.log('Valheim World rules choices survive lowercase persisted values.');
