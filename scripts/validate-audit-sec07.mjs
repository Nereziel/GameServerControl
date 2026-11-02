import fs from 'node:fs';
import path from 'node:path';

const webUiDir = 'src/GameServerControl.Agent/WebUi';
const indexHtml = fs.readFileSync(path.join(webUiDir, 'index.html'), 'utf8');
const webUiSources = fs.readdirSync(webUiDir)
  .filter(name => /\.(?:html|js)$/i.test(name))
  .map(name => [name, fs.readFileSync(path.join(webUiDir, name), 'utf8')]);
const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');
const securityHeadersPath = 'src/GameServerControl.Agent/Security/WebSecurityHeadersMiddleware.cs';
const securityHeaders = fs.existsSync(securityHeadersPath) ? fs.readFileSync(securityHeadersPath, 'utf8') : '';
const cloudWorker = fs.readFileSync('src/GameServerControl.Cloud/src/index.js', 'utf8');

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

function rejectPattern(source, pattern, message, fileName) {
  const match = source.match(pattern);
  if (match) throw new Error(`${message} in ${fileName}: ${match[0]}`);
}

for (const [name, source] of webUiSources) {
  rejectPattern(source, /\bon(?:click|change|input|keydown|keyup|submit|focus|blur)\s*=/i,
    'SEC-07 forbids inline DOM event-handler attributes', name);
  rejectPattern(source, /\.on(?:click|change|input|keydown|keyup|submit|focus|blur)\s*=/i,
    'SEC-07 requires addEventListener/delegation instead of event-handler properties', name);
  rejectPattern(source, /javascript\s*:/i,
    'SEC-07 forbids javascript: URLs', name);
  rejectPattern(source, /\bjsq\s*\(/,
    'SEC-07 forbids embedding JavaScript argument strings into generated HTML', name);
}

rejectPattern(indexHtml, /<script(?![^>]*\bsrc\s*=)[^>]*>[\s\S]*?<\/script>/i,
  'SEC-07 requires all executable JavaScript to come from external same-origin files', 'index.html');
requireToken(indexHtml, '/webui/control-room.js', 'SEC-07 must externalize the control-room script');
requireToken(indexHtml, '/webui/workspace-overrides.js', 'SEC-07 must externalize workspace/delete overrides');

requireToken(program, 'WebSecurityHeadersMiddleware', 'Agent pipeline must install the WebUI security-header middleware');
requireToken(securityHeaders, 'Content-Security-Policy', 'Agent must emit a Content-Security-Policy header');
requireToken(securityHeaders, "script-src 'self'", 'Agent CSP must restrict scripts to same-origin files');
requireToken(securityHeaders, "script-src-attr 'none'", 'Agent CSP must disable inline event-handler attributes');
requireToken(securityHeaders, "object-src 'none'", 'Agent CSP must disable plugin/object execution');
requireToken(securityHeaders, "base-uri 'none'", 'Agent CSP must disable injected base URLs');
requireToken(securityHeaders, "frame-ancestors 'none'", 'Agent CSP must prevent framing');
requireToken(securityHeaders, 'X-Content-Type-Options', 'Agent must emit nosniff protection');
requireToken(securityHeaders, 'Referrer-Policy', 'Agent must emit a restrictive referrer policy');

requireToken(cloudWorker, 'Content-Security-Policy', 'Cloud gateway must emit the same CSP protections');
requireToken(cloudWorker, "script-src 'self'", 'Cloud CSP must restrict scripts to same-origin files');
requireToken(cloudWorker, "script-src-attr 'none'", 'Cloud CSP must disable inline event-handler attributes');
requireToken(cloudWorker, "frame-ancestors 'none'", 'Cloud CSP must prevent framing');
requireToken(cloudWorker, 'X-Content-Type-Options', 'Cloud gateway must emit nosniff protection');

console.log('Audit SEC-07 WebUI XSS/CSP contract OK.');
