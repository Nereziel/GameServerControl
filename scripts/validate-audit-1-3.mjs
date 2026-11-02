import fs from 'node:fs';

const roles = fs.readFileSync('src/GameServerControl.Agent/Auth/RoleEnforcementMiddleware.cs', 'utf8');
const serverView = fs.readFileSync('src/GameServerControl.Shared/ServerViewDto.cs', 'utf8');
const worker = fs.readFileSync('src/GameServerControl.Cloud/src/index.js', 'utf8');
const wrangler = fs.readFileSync('src/GameServerControl.Cloud/wrangler.jsonc', 'utf8');
const deploy = fs.readFileSync('.github/workflows/cloudflare-deploy.yml', 'utf8');
const docs = fs.readFileSync('docs/cloudflare-remote-management.md', 'utf8');
const agentProject = fs.readFileSync('src/GameServerControl.Agent/GameServerControl.Agent.csproj', 'utf8');

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
rejectToken(serverView, 'RconPassword,', 'ServerViewDto must not expose the RCON password value');
rejectToken(serverView, 'DiscordWebhookUrl,', 'ServerViewDto must not expose the Discord webhook value');
requireToken(serverView, 'HasRconPassword', 'ServerViewDto should expose only RCON password presence');
requireToken(serverView, 'HasDiscordWebhookUrl', 'ServerViewDto should expose only webhook presence');

for (const token of ['AGENT_ADMIN_TOKEN', 'AGENT_READONLY_TOKEN']) {
  requireToken(worker, `'${token}'`, `Worker requiredConfig must require ${token}`);
  requireToken(wrangler, `"${token}"`, `Wrangler required secrets must include ${token}`);
  requireToken(deploy, token, `Deploy workflow must map ${token}`);
  requireToken(docs, `\`${token}\``, `Cloudflare docs must document ${token}`);
}

requireToken(deploy, 'GSC_CF_AGENT_ADMIN_TOKEN', 'Deploy workflow must use the Admin Agent token secret');
requireToken(deploy, 'GSC_CF_AGENT_READONLY_TOKEN', 'Deploy workflow must use the ReadOnly Agent token secret');
requireToken(docs, 'GSC_CF_AGENT_ADMIN_TOKEN', 'Cloudflare documentation must describe the Admin Agent token secret');
requireToken(docs, 'GSC_CF_AGENT_READONLY_TOKEN', 'Cloudflare documentation must describe the ReadOnly Agent token secret');
rejectToken(deploy, 'GSC_CF_AGENT_API_TOKEN', 'Legacy single-token deploy mapping must be removed');
rejectToken(worker, 'env.AGENT_API_TOKEN', 'Worker must not fall back to the legacy single-token binding');
rejectToken(docs, 'GSC_CF_AGENT_API_TOKEN', 'Cloudflare documentation must not reference the legacy GitHub secret');

requireToken(agentProject, '<PackageReference Include="System.Management.Automation" Version="7.4.21" />', 'Embedded PowerShell must be on the audited security patch level');

console.log('Audit fixes 1-3 contract OK.');
