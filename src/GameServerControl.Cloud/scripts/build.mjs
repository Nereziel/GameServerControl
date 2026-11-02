import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../..');
const sourceDir = path.join(repoRoot, 'src/GameServerControl.Agent/WebUi');
const cloudDir = path.join(repoRoot, 'src/GameServerControl.Cloud');
const distDir = path.join(cloudDir, 'dist');
const webUiDist = path.join(distDir, 'webui');

fs.rmSync(distDir, { recursive: true, force: true });
fs.mkdirSync(webUiDist, { recursive: true });

for (const entry of fs.readdirSync(sourceDir, { withFileTypes: true })) {
  if (!entry.isFile() || entry.name === 'index.html') continue;
  fs.copyFileSync(path.join(sourceDir, entry.name), path.join(webUiDist, entry.name));
}

const adapterTag = '<script src="/webui/cloud-adapter.js"></script>\n';
let index = fs.readFileSync(path.join(sourceDir, 'index.html'), 'utf8');
if (!index.includes('</body>')) throw new Error('Agent Web UI index has no closing body tag.');
index = index.replace('</body>', adapterTag + '</body>');
fs.writeFileSync(path.join(distDir, 'index.html'), index, 'utf8');

fs.copyFileSync(
  path.join(cloudDir, 'static/cloud-adapter.js'),
  path.join(webUiDist, 'cloud-adapter.js')
);

const csp = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; script-src-attr 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws: wss:";
fs.writeFileSync(path.join(distDir, '_headers'), `/*\n  Content-Security-Policy: ${csp}\n  X-Content-Type-Options: nosniff\n  Referrer-Policy: no-referrer\n  X-Frame-Options: DENY\n  Permissions-Policy: camera=(), microphone=(), geolocation=()\n\n/api/*\n  Cache-Control: no-store\n\n/hubs/*\n  Cache-Control: no-store\n`, 'utf8');

console.log(`Cloud Web UI staged from ${path.relative(repoRoot, sourceDir)} into ${path.relative(repoRoot, distDir)}.`);
