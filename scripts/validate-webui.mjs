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
  console.log(`Validated ${files.length} JavaScript files and ${scripts.length} inline script blocks.`);
} finally {
  fs.rmSync(tempDir, { recursive: true, force: true });
}
