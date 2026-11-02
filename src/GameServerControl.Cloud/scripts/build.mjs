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

const bootstrap = `<script>\nwindow.GSC_CLOUD_MODE=true;\ntry{localStorage.setItem('gsc.connection.v2',JSON.stringify({url:location.origin.replace(/\\\/$/,''),token:'cloud-gateway'}));}catch{}\n</script>\n`;
const adapterTag = '<script src="/webui/cloud-adapter.js"></script>\n';

let index = fs.readFileSync(path.join(sourceDir, 'index.html'), 'utf8');
const coreTag = '<script src="/webui/core.js"></script>';
if (!index.includes(coreTag)) throw new Error('Agent Web UI index no longer contains the expected core.js script tag.');
index = index.replace(coreTag, bootstrap + coreTag);
if (!index.includes('</body>')) throw new Error('Agent Web UI index has no closing body tag.');
index = index.replace('</body>', adapterTag + '</body>');
fs.writeFileSync(path.join(distDir, 'index.html'), index, 'utf8');

fs.copyFileSync(
  path.join(cloudDir, 'static/cloud-adapter.js'),
  path.join(webUiDist, 'cloud-adapter.js')
);

fs.writeFileSync(path.join(distDir, '_headers'), `/*\n  X-Content-Type-Options: nosniff\n  Referrer-Policy: no-referrer\n  X-Frame-Options: DENY\n  Permissions-Policy: camera=(), microphone=(), geolocation=()\n\n/api/*\n  Cache-Control: no-store\n\n/hubs/*\n  Cache-Control: no-store\n`, 'utf8');

console.log(`Cloud Web UI staged from ${path.relative(repoRoot, sourceDir)} into ${path.relative(repoRoot, distDir)}.`);
