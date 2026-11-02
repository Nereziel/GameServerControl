import fs from 'node:fs';

const core = fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js', 'utf8');
const admin = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-admin.js', 'utf8');
const workspace = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js', 'utf8');
const surface = [core, admin, workspace].join('\n');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

requireToken(surface, 'AUTH=', 'Web UI must keep authenticated identity metadata');
requireToken(surface, '/api/auth/me', 'Web UI must load the authenticated Agent identity');
requireToken(surface, 'function isReadOnly', 'Web UI needs a ReadOnly role predicate');
requireToken(surface, 'function applyAuthUi', 'Web UI must apply role-aware controls');
requireToken(surface, 'MutationObserver', 'Role-aware controls must also apply to dynamically rendered workspace content');
requireToken(surface, 'authRole', 'Current role must be visible in the Web UI');
requireToken(surface, 'Read-only access', 'ReadOnly UX must explain why mutation controls are disabled');
requireToken(surface, 'ADMIN_MUTATION_CALLS', 'Dynamic mutation controls must be identified centrally');
requireToken(surface, 'serverWorkspaceSendConsole', 'Interactive workspace console must be covered by role-aware UI');

console.log('ReadOnly Web UI contract OK.');
