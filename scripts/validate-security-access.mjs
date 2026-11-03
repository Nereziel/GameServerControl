import fs from 'node:fs';

const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');
const audit = fs.readFileSync('src/GameServerControl.Agent/Auth/AuditLogger.cs', 'utf8');
const roles = fs.readFileSync('src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs', 'utf8');
const hub = fs.readFileSync('src/GameServerControl.Agent/Hubs/StatusHub.cs', 'utf8');
const example = fs.readFileSync('src/GameServerControl.Agent/appsettings.json.example', 'utf8');
const agentSurface = program + '\n' + audit;

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}
function rejectToken(source, token, message) {
  if (source.includes(token)) throw new Error(`${message}: unexpected ${token}`);
}

requireToken(agentSurface, '/api/auth/me', 'Authenticated identity endpoint is required');
requireToken(agentSurface, 'TokenAuthHandler.TokenIdClaim', 'Identity endpoint must surface the authenticated token id');
requireToken(agentSurface, 'TokenAuthHandler.RoleClaim', 'Identity endpoint must surface the authenticated role');
requireToken(audit, 'IsAuthenticated', 'Identity endpoint must reject unauthenticated requests');

requireToken(hub, '[Authorize(Policy = "admin")]', 'Interactive SignalR console input must remain Admin-only');
requireToken(hub, 'SendConsoleInput', 'Interactive SignalR console input contract is required');
rejectToken(roles, 'IsReadOnlyControlPost', 'ReadOnly API policy must not bypass normal Admin enforcement for tail control');
rejectToken(roles, '/logs/tail', 'ReadOnly API policy must not special-case the shared log-tail control endpoint');
requireToken(hub, '_logTail.Subscribe(serverId, Context.ConnectionId)', 'ReadOnly LogOnly lifecycle must be tied to the authenticated SignalR connection');
requireToken(hub, '_logTail.UnsubscribeConnection(Context.ConnectionId)', 'SignalR disconnect must release LogOnly tail ownership');

requireToken(roles, 'string.Equals(p, "/api/discover", StringComparison.OrdinalIgnoreCase)',
  'ReadOnly host discovery must be blocked before it can reveal filesystem paths');
requireToken(roles, 'IsInstallJobEndpoint(p)',
  'ReadOnly install-job details must be blocked before job lookup');
requireToken(roles, 'private const string InstallJobPrefix = "/api/servers/install/"',
  'Install-job ReadOnly guard must be scoped to the install progress route');
requireToken(roles, 'InvokeWithRedactedModsAsync(ctx)',
  'ReadOnly mod listing must pass through response redaction');
requireToken(roles, 'root["modsFolder"] = null;',
  'ReadOnly mod listing must not expose the absolute mods folder');

requireToken(audit, 'Agent:CloudGateway:TrustedTokenIds', 'Cloud audit identity must be bound to explicitly trusted Agent token ids');
requireToken(audit, '_trustedGatewayTokenIds.Contains(tokenId)', 'Cloud audit headers must be tied to the authenticated trusted token id');
requireToken(audit, 'TokenAuthHandler.TokenIdClaim', 'Audit entries must record authenticated token ids');
requireToken(audit, 'TokenAuthHandler.RoleClaim', 'Audit entries must record authenticated token roles');
requireToken(audit, 'AuditRetentionDays', 'Audit logs must have bounded retention');
requireToken(audit, 'PruneOldLogs', 'Audit retention must be actively enforced');
requireToken(audit, 'cloudflare-worker', 'Cloud gateway audit marker is required');

requireToken(example, '"AuditRetentionDays"', 'Example config must document audit retention');
requireToken(example, '"CloudGateway"', 'Example config must document trusted cloud gateway token ids');
requireToken(example, '"TrustedTokenIds"', 'Example config must document trusted cloud gateway token ids');

console.log('Security/access contract OK.');
