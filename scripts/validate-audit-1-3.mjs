import fs from 'node:fs';

const roles = fs.readFileSync('src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs', 'utf8');
const serverView = fs.readFileSync('src/GameServerControl.Shared/ServerViewDto.cs', 'utf8');
const worker = fs.readFileSync('src/GameServerControl.Cloud/src/index.js', 'utf8');
const wrangler = fs.readFileSync('src/GameServerControl.Cloud/wrangler.jsonc', 'utf8');
const hosts = fs.readFileSync('src/GameServerControl.Cloud/src/hosts.js', 'utf8');
const deploy = fs.readFileSync('.github/workflows/cloudflare-deploy.yml', 'utf8');
const docs = fs.readFileSync('docs/cloudflare-remote-management.md', 'utf8');
const agentProject = fs.readFileSync('src/GameServerControl.Agent/GameServerControl.Agent.csproj', 'utf8');
const nginxGateway = fs.readFileSync('deploy/relay/admin-gateway/nginx-gateway.conf.example', 'utf8');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

function rejectToken(source, token, message) {
  if (source.includes(token)) throw new Error(`${message}: unexpected ${token}`);
}

requireToken(roles, 'ServerViewDto.From', 'ReadOnly server listings must use a secret-free DTO');
requireToken(roles, 'TryRedactPasswordValues', 'ReadOnly managed config responses must redact Password fields');
requireToken(roles, 'secretValues', 'ReadOnly managed config responses must expose only secret presence metadata');
requireToken(roles, 'FileContentSuffix', 'ReadOnly raw config-file content must be explicitly denied');
requireToken(roles, 'StatusCodes.Status403Forbidden', 'ReadOnly raw config-file content must return 403');
rejectToken(serverView, 'string? RconPassword', 'ServerViewDto must not expose the RCON password value');
rejectToken(serverView, 'string? DiscordWebhookUrl', 'ServerViewDto must not expose the Discord webhook value');
requireToken(serverView, 'HasRconPassword', 'ServerViewDto should expose only RCON password presence');
requireToken(serverView, 'HasDiscordWebhookUrl', 'ServerViewDto should expose only webhook presence');

// Cloudflare mode uses an encrypted credential pair per independent Agent
// (stored in D1), not one global pair pointing at a single coordinator.
for (const token of ['adminToken', 'readOnlyToken']) {
  requireToken(hosts, token, 'Cloud per-host credentials must have distinct Admin/ReadOnly roles');
}
requireToken(worker, 'authorizeGatewayRequest', 'Cloud gateway must reject ReadOnly modifications');
requireToken(worker, 'verifyPanelAccess', 'Cloud gateway must validate Cloudflare Access');
requireToken(worker, 'getHost(env, hostId)', 'Cloud gateway must use a selected host record');
requireToken(hosts, "'AES-GCM'", 'D1 Agent tokens must be encrypted using AES-GCM');
requireToken(hosts, 'gatewayUrl(env, host.id', 'Host requests must use the fixed VPS gateway URL');
requireToken(hosts, 'role === \'Admin\'', 'Cloud gateway must select correct per-host token role');
requireToken(hosts, 'HOSTS_DB', 'Remote Agent credentials must live in D1, not browser storage');
requireToken(wrangler, '"HOSTS_ENCRYPTION_KEY"', 'Worker must require a separate encryption-key secret');
requireToken(wrangler, '"GATEWAY_SHARED_KEY"', 'Worker must authenticate to VPS gateway');
requireToken(wrangler, '"HOSTS_DB"', 'Worker must bind a D1 database');
requireToken(deploy, 'GSC_CF_HOSTS_ENCRYPTION_KEY', 'Deployment must supply the encryption key');
requireToken(deploy, 'GSC_CF_GATEWAY_SHARED_KEY', 'Deployment must supply a distinct VPS gateway key');
requireToken(docs, 'Admin', 'Cloudflare docs must describe the per-host Admin role');
requireToken(docs, 'ReadOnly', 'Cloudflare docs must describe the per-host ReadOnly role');
rejectToken(worker, 'env.AGENT_API_TOKEN', 'Worker must not fall back to a legacy Agent token');
rejectToken(worker, 'env.AGENT_ADMIN_TOKEN', 'Worker must not use a coordinator-wide Admin token');
rejectToken(deploy, 'GSC_CF_AGENT_API_TOKEN', 'Legacy single-token deploy mapping must be removed');

requireToken(agentProject, '<PackageReference Include="System.Management.Automation" Version="7.4.21" />', 'Embedded PowerShell must be on the audited security patch level');


for (const token of [
  'include /etc/nginx/gsc-admin-gateway/secret.conf;',
  'if ($http_x_gsc_proxy_key != $gsc_gateway_key)',
  'location = /_gsc_gateway/health',
  'location ^~ /gsc-agent/pc1/',
  'location ^~ /gsc-agent/pc2/',
  'proxy_pass http://PC1_TAILSCALE_IPV4:5099/;',
  'proxy_pass http://PC2_TAILSCALE_IPV4:5099/;',
  'proxy_set_header Authorization $http_authorization;',
  'proxy_set_header X-GSC-Proxy-Key "";',
  'proxy_set_header CF-Access-Jwt-Assertion "";',
  'proxy_set_header Upgrade $http_upgrade;',
  'proxy_set_header Connection $gsc_admin_connection_upgrade;',
  'location / {'
]) {
  requireToken(nginxGateway, token, 'Nginx gateway must be fixed-route, key-protected and WebSocket-capable');
}
rejectToken(nginxGateway, 'proxy_pass http://
, 'Nginx gateway must not accept dynamic upstream destinations');
rejectToken(nginxGateway, '.overcore.eu', 'Nginx template must not reveal deployment hostnames');

console.log('Audit fixes 1-3 contract OK.');
