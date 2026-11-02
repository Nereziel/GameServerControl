import fs from 'node:fs';

const core = fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js', 'utf8');
const admin = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-admin.js', 'utf8');
const workspace = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js', 'utf8');
const surface = [core, admin, workspace].join('\n');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}
function rejectToken(source, token, message) {
  if (source.includes(token)) throw new Error(`${message}: unexpected ${token}`);
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

// SEC-07 replaced executable inline handlers with delegated data-gsc-* actions. Role-aware UX
// must inspect the same metadata or ReadOnly users would see enabled Admin controls even though
// the backend still rejects them.
rejectToken(admin, "button[onclick]", 'ReadOnly UX must not depend on removed inline onclick attributes');
rejectToken(admin, "input[onkeydown]", 'ReadOnly UX must not depend on removed inline key handlers');
requireToken(admin, "[data-gsc-action]", 'ReadOnly UX must inspect delegated SEC-07 action metadata');
requireToken(admin, "getAttribute('data-gsc-action')", 'ReadOnly UX must classify delegated action names');
requireToken(workspace, 'serverWorkspacePlayerAction', 'Workspace must expose delegated player kick/ban controls');
requireToken(admin, 'PlayerAction', 'ReadOnly mutation classification must cover workspace player actions');
requireToken(admin, 'Admin', 'ReadOnly mutation classification must cover workspace RCON admin commands');
requireToken(admin, "'add'", 'ReadOnly mutation classification must cover add/discovery mutations');
for (const id of ['updateBtn','createServerBtn','addExistingBtn','tokensBtn']) {
  requireToken(admin, id, `ReadOnly UX must disable static Admin control ${id}`);
}

console.log('ReadOnly Web UI contract OK.');
