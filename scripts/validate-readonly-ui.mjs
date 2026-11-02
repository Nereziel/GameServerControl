import fs from 'node:fs';

const core = fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js', 'utf8');
const workspace = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js', 'utf8');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

requireToken(core, 'AUTH=', 'Web UI must keep authenticated identity metadata');
requireToken(core, '/api/auth/me', 'Web UI must load the authenticated Agent identity');
requireToken(core, 'function isReadOnly', 'Web UI needs a ReadOnly role predicate');
requireToken(core, 'function applyAuthUi', 'Web UI must apply role-aware controls');
requireToken(core, 'MutationObserver', 'Role-aware controls must also apply to dynamically rendered workspace content');
requireToken(core, 'authRole', 'Current role must be visible in the Web UI');
requireToken(core, 'Read-only access', 'ReadOnly UX must explain why mutation controls are disabled');
requireToken(workspace, 'adminDisabled()', 'Server power controls must render disabled for ReadOnly users');

console.log('ReadOnly Web UI contract OK.');
