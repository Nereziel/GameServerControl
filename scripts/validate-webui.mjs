import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
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

function checkInlineScripts(html, tempDir, labelPrefix) {
  const scripts = [...html.matchAll(/<script(?![^>]*\bsrc\s*=)[^>]*>([\s\S]*?)<\/script>/gi)];
  scripts.forEach((match, index) => {
    const file = path.join(tempDir, `${labelPrefix.replace(/[^a-z0-9]+/gi, '-')}-${index + 1}.js`);
    fs.writeFileSync(file, match[1], 'utf8');
    check(file, `${labelPrefix} inline script #${index + 1}`);
  });
  return scripts.length;
}

for (const file of files) check(file);

// HTML must be revalidated, while JS/CSS URLs change with the deployed asset bytes.
const indexHtml = fs.readFileSync(path.join(webUiDir, 'index.html'), 'utf8');
const programSource = fs.readFileSync(path.resolve('src/GameServerControl.Agent/Program.cs'), 'utf8');
for (const name of ['styles.css', 'core.js', 'server-admin.js', 'jobs-mods.js', 'config-backups.js', 'server-workspace.js']) {
  if (!indexHtml.includes(`/webui/${name}?v=__GSC_ASSET_VERSION__`))
    throw new Error(`Missing content-versioned asset reference: ${name}`);
}
if (!programSource.includes('webUiHasher.AppendData(File.ReadAllBytes(assetPath))') ||
    !programSource.includes('Replace("__GSC_ASSET_VERSION__", webUiAssetVersion)') ||
    !programSource.includes('context.Response.Headers.CacheControl = "no-store"'))
  throw new Error('WebUI cache-busting or HTML revalidation contract missing');



/* Regression: a childList MutationObserver runs applyAuthUi -> ensureAuthRoleBadge.
 * Writing the same textContent again schedules another observer callback forever. */
const authUiSource = fs.readFileSync(path.join(webUiDir, 'server-admin.js'), 'utf8');
const badgeStart = authUiSource.indexOf('function ensureAuthRoleBadge(){');
const badgeEnd = authUiSource.indexOf('function delegatedActionNames(', badgeStart);
if (badgeStart < 0 || badgeEnd < 0) throw new Error('Auth role badge implementation not found');
const badgeFunction = authUiSource.slice(badgeStart, badgeEnd);
let textWrites = 0;
const badge = {
  _text: '',
  _title: '',
  get textContent() { return this._text; },
  set textContent(value) { textWrites++; this._text = value; },
  get title() { return this._title; },
  set title(value) { this._title = value; }
};
const dom = {
  getElementById(id) { return id === 'authRole' ? badge : null; }
};
const authContext = { document: dom, AUTH: { name: 'Test', role: 'Admin' } };
vm.runInNewContext(badgeFunction, authContext);
for (let i = 0; i < 5; i++) authContext.ensureAuthRoleBadge();
if (textWrites !== 1) throw new Error(`Auth badge generated ${textWrites} childList mutations on unchanged identity; observer may freeze the UI`);
authContext.AUTH = { name: 'Test', role: 'ReadOnly' };
authContext.ensureAuthRoleBadge();
if (textWrites !== 2) throw new Error('Auth badge did not update after role change');

// The login form must display errors outside #app, which is hidden until connected.
// Guard against routing login errors only to the control-room notice bar.
const coreSource = fs.readFileSync(path.join(webUiDir, 'core.js'), 'utf8');
for (const required of [
  'const showLoginToast=toast;',
  "if(!$('#login')?.classList.contains('hidden'))return showLoginToast(msg,kind);",
  'const timeout=setTimeout(()=>controller.abort(),12000);',
  "button.textContent='Connecting…'",
  "button.textContent='Connect'"
]) {
  if (!coreSource.includes(required)) {
    throw new Error(`Web UI login feedback regression: missing ${required}`);
  }
}

const htmlPath = path.join(webUiDir, 'index.html');
const html = fs.readFileSync(htmlPath, 'utf8');
const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gsc-webui-'));

try {
  const agentInlineCount = checkInlineScripts(html, tempDir, 'Agent index.html');

  const cloudRoot = path.resolve('src/GameServerControl.Cloud');
  const cloudWorker = path.join(cloudRoot, 'src/index.js');
  const cloudAdapter = path.join(cloudRoot, 'static/cloud-adapter.js');
  const cloudBuild = path.join(cloudRoot, 'scripts/build.mjs');
  let cloudInlineCount = 0;

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
    if (!stagedHtml.includes('/webui/cloud-adapter.js')) {
      console.error('Cloudflare Web UI build is missing its external cloud adapter.');
      process.exit(1);
    }
    cloudInlineCount = checkInlineScripts(stagedHtml, tempDir, 'Cloud index.html');
    if (cloudInlineCount !== 0) {
      console.error(`Cloudflare Web UI staging emitted ${cloudInlineCount} inline executable script(s).`);
      process.exit(1);
    }
  }

  runNode(path.resolve('scripts/validate-update-ux.mjs'), 'update/install UX contract');
  runNode(path.resolve('scripts/validate-server-admin.mjs'), 'server admin capability contract');
  runNode(path.resolve('scripts/validate-monitoring.mjs'), 'monitoring history contract');
  runNode(path.resolve('scripts/validate-profile-contract.mjs'), 'scalable game-profile contract');
  runNode(path.resolve('scripts/validate-sec07-ui-actions.mjs'), 'SEC-07 delegated UI action behavior');
  runNode(path.resolve('scripts/validate-audit-sec07.mjs'), 'audit SEC-07 WebUI XSS/CSP contract');
  console.log(`Validated ${files.length} Agent JavaScript files, ${agentInlineCount} Agent inline scripts, ${cloudInlineCount} Cloud inline scripts and the Cloudflare Web UI staging build.`);
} finally {
  fs.rmSync(tempDir, { recursive: true, force: true });
}
