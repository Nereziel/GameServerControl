import fs from 'node:fs';

const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');
const audit = fs.readFileSync('src/GameServerControl.Agent/Auth/AuditLogger.cs', 'utf8');
const hub = fs.readFileSync('src/GameServerControl.Agent/Hubs/StatusHub.cs', 'utf8');
const example = fs.readFileSync('src/GameServerControl.Agent/appsettings.json.example', 'utf8');
const agentSurface = program + '\n' + audit;

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

requireToken(agentSurface, '/api/auth/me', 'Authenticated identity endpoint is required');
requireToken(agentSurface, 'TokenAuthHandler.TokenIdClaim', 'Identity endpoint must surface the authenticated token id');
requireToken(agentSurface, 'TokenAuthHandler.RoleClaim', 'Identity endpoint must surface the authenticated role');
requireToken(audit, 'IsAuthenticated', 'Identity endpoint must reject unauthenticated requests');

requireToken(hub, '[Authorize(Policy = "admin")]', 'Interactive SignalR console input must remain Admin-only');
requireToken(hub, 'SendConsoleInput', 'Interactive SignalR console input contract is required');

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
