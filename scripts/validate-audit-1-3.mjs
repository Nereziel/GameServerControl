import fs from 'node:fs';

const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');
const filesEndpoints = fs.readFileSync('src/GameServerControl.Agent/Config/ServerConfigFileEndpoints.cs', 'utf8');
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

requireToken(program, 'ServerViewDto.From', 'ReadOnly server listings must use a secret-free DTO');
requireToken(program, 'ConfigSecretRedaction.ForReadOnly', 'ReadOnly config responses must redact Password fields');

const rawContentRouteStart = filesEndpoints.indexOf('api.MapGet("/servers/{id}/files/content"');
if (rawContentRouteStart < 0) throw new Error('Raw config-file content endpoint is missing.');
const rawContentRouteEnd = filesEndpoints.indexOf('api.MapPost(', rawContentRouteStart);
const rawContentRoute = filesEndpoints.slice(rawContentRouteStart, rawContentRouteEnd < 0 ? undefined : rawContentRouteEnd);
requireToken(rawContentRoute, '.RequireAuthorization("admin")', 'Raw config-file content must be Admin-only');

for (const token of ['AGENT_ADMIN_TOKEN', 'AGENT_READONLY_TOKEN']) {
  requireToken(worker, `'${token}'`, `Worker requiredConfig must require ${token}`);
  requireToken(wrangler, `"${token}"`, `Wrangler required secrets must include ${token}`);
  requireToken(deploy, `${token}: \${{ secrets.GSC_CF_${token.replace('AGENT_', 'AGENT_')} }}`, `Deploy workflow must map ${token}`);
}

requireToken(deploy, 'GSC_CF_AGENT_ADMIN_TOKEN', 'Deploy workflow must use the Admin Agent token secret');
requireToken(deploy, 'GSC_CF_AGENT_READONLY_TOKEN', 'Deploy workflow must use the ReadOnly Agent token secret');
requireToken(docs, 'GSC_CF_AGENT_ADMIN_TOKEN', 'Cloudflare documentation must describe the Admin Agent token secret');
requireToken(docs, 'GSC_CF_AGENT_READONLY_TOKEN', 'Cloudflare documentation must describe the ReadOnly Agent token secret');
rejectToken(deploy, 'GSC_CF_AGENT_API_TOKEN', 'Legacy single-token deploy mapping must be removed');
rejectToken(worker, 'env.AGENT_API_TOKEN', 'Worker must not fall back to the legacy single-token binding');

requireToken(agentProject, '<PackageReference Include="System.Management.Automation" Version="7.4.21" />', 'Embedded PowerShell must be on the audited security patch level');

console.log('Audit fixes 1-3 contract OK.');
