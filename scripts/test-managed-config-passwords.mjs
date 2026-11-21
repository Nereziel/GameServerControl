import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const src = fs.readFileSync('src/GameServerControl.Agent/WebUi/config-backups.js', 'utf8');
const end = src.indexOf('async function openBackups(');
assert.ok(end > 0, 'config editor code must be available');

let dialog = '';
let captured = null;
const fields = [
  { dataset: { cfg: 'AdminPassword' }, type: 'password', value: 'fixture-admin-secret' },
  { dataset: { cfg: 'OwnerId' }, type: 'text', value: 'player-fixture' }
];
const ctx = {
  SERVERS: [{ id: 'pal', name: 'Palworld' }],
  esc: x => String(x).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;'),
  api: async (_url, opts) => {
    if (!opts) return {
      schema: { sections: [{ title: 'Settings', fields: [
        { key: 'AdminPassword', label: 'Admin password', type: 'Password' },
        { key: 'OwnerId', label: 'Owner ID', type: 'Text' }
      ] }] },
      values: { adminpassword: 'fixture-admin-secret', ownerid: 'player-fixture' }
    };
    captured = JSON.parse(opts.body);
    return { written: true };
  },
  uiAction: () => '',
  openModal: (_title, html) => { dialog = html; },
  document: {
    querySelectorAll(selector) {
      assert.equal(selector, '#modal [data-cfg]', 'Saving modal must never collect workspace fields');
      return fields;
    }
  },
  toast: () => {},
  closeModal: () => {}
};
vm.runInNewContext(src.slice(0, end), ctx);
await ctx.openConfig('pal');
assert.match(dialog, /data-cfg="AdminPassword"[^>]*value="fixture-admin-secret"/, 'case-variant config key must restore Palworld admin password');
assert.doesNotMatch(dialog, /<form(?:\s|>)/i, 'game config must not masquerade as a login form');
assert.match(dialog, /data-cfg="AdminPassword"[^>]*autocomplete="new-password"/, 'disable login autofill on managed password field');
for (const ignored of ['data-1p-ignore', 'data-lpignore', 'data-bwignore']) {
  assert.ok(dialog.includes(ignored), 'missing password manager opt-out ' + ignored);
}
await ctx.saveConfig('pal');
assert.equal(captured.AdminPassword, 'fixture-admin-secret');
assert.equal(captured.OwnerId, 'player-fixture');
assert.equal(Object.keys(captured).length, 2);

// All game configuration screens share the same field renderer.
for (const [key, casing] of [
  ['password', 'PASSWORD'],
  ['AdminPassword', 'adminpassword'],
  ['ServerPassword', 'SERVERPASSWORD'],
  ['Password', 'password'],
  ['ClientPassword', 'clientpassword']
]) {
  const html = ctx.configField({ key, label: key, type: 'Password' },
    ctx.configValue({ [casing]: 'fixture-value' }, { key }));
  assert.ok(html.includes('value="fixture-value"'), key + ' must rehydrate mixed-case values');
  assert.ok(html.includes('autocomplete="new-password"'), key + ' must avoid saved-login autofill');
}
for (const file of ['src/GameServerControl.Agent/WebUi/server-admin.js',
                    'src/GameServerControl.Agent/WebUi/config-backups.js']) {
  const source = fs.readFileSync(file, 'utf8');
  for (const id of file.includes('server-admin') ? ['eRpass'] : ['aRpass']) {
    assert.match(source, new RegExp('id="' + id + '"[^>]*autocomplete="new-password"'),
      id + ' must be protected from login autofill');
  }
}
const satisfactory = fs.readFileSync('src/GameServerControl.Agent/Config/SatisfactoryConfig.cs', 'utf8');
const schema = fs.readFileSync('src/GameServerControl.Shared/ConfigSchema.cs', 'utf8');
assert.ok(schema.includes('new ConfigField("ClearClientPassword", "Remove join password"'),
  'Satisfactory must offer an explicit way to clear its write-only join password');
assert.ok(satisfactory.includes('ResolveClientPasswordUpdate(values)'),
  'Satisfactory must not blindly submit default-empty write-only passwords');
console.log('Managed config password reload, modal scoping, and password-manager opt-out regressions passed.');
