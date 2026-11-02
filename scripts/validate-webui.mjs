import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const webUiDir = path.resolve('src/GameServerControl.Agent/WebUi');
const files = fs.readdirSync(webUiDir)
  .filter(name => name.endsWith('.js'))
  .sort()
  .map(name => path.join(webUiDir, name));

function check(file, label = file) {
  console.log(`Checking ${label}`);
  const r = spawnSync(process.execPath, ['--check', file], { stdio: 'inherit' });
  if (r.status !== 0) process.exit(r.status ?? 1);
}

function runNode(file, label = file) {
  console.log(`Running ${label}`);
  const r = spawnSync(process.execPath, [file], { stdio: 'inherit' });
  if (r.status !== 0) process.exit(r.status ?? 1);
}

for (const file of files) check(file);

const htmlPath = path.join(webUiDir, 'index.html');
const html = fs.readFileSync(htmlPath, 'utf8');
const scripts = [...html.matchAll(/<script(?![^>]*\bsrc\s*=)[^>]*>([\s\S]*?)<\/script>/gi)];
const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gsc-webui-'));

try {
  scripts.forEach((match, index) => {
    const file = path.join(tempDir, `inline-${index + 1}.js`);
    fs.writeFileSync(file, match[1], 'utf8');
    check(file, `index.html inline script #${index + 1}`);
  });

  const cloudRoot = path.resolve('src/GameServerControl.Cloud');
  const cloudWorker = path.join(cloudRoot, 'src/index.js');
  const cloudAdapter = path.join(cloudRoot, 'static/cloud-adapter.js');
  const cloudBuild = path.join(cloudRoot, 'scripts/build.mjs');

  if (fs.existsSync(cloudWorker)) check(cloudWorker, 'Cloudflare Worker');
  if (fs.existsSync(cloudAdapter)) check(cloudAdapter, 'Cloudflare Web UI adapter');
  if (fs.existsSync(cloudBuild)) {
    check(cloudBuild, 'Cloudflare Web UI build script');
    runNode(cloudBuild, 'Cloudflare Web UI staging build');

    const stagedIndex = path.join(cloudRoot, 'dist/index.html');
    const stagedAdapter = path.join(cloudRoot, 'dist/webui/cloud-adapter.js');
    if (!fs.existsSync(stagedIndex) || !fs.existsSync(stagedAdapter)) {
      console.error('Cloudflare Web UI build did not stage the expected assets.');
      process.exit(1);
    }

    const stagedHtml = fs.readFileSync(stagedIndex, 'utf8');
    if (!stagedHtml.includes('window.GSC_CLOUD_MODE=true') || !stagedHtml.includes('/webui/cloud-adapter.js')) {
      console.error('Cloudflare Web UI build is missing its cloud bootstrap/adapter.');
      process.exit(1);
    }
  }

  console.log(`Validated ${files.length} Agent JavaScript files, ${scripts.length} inline script blocks and the Cloudflare Web UI staging build.`);
} finally {
  fs.rmSync(tempDir, { recursive: true, force: true });
}
