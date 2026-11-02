import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');

function requireCondition(condition, message) {
  if (!condition) throw new Error(message);
}

function read(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), 'utf8');
}

const cloudDir = path.join(root, 'src', 'GameServerControl.Cloud');
const lockPath = path.join(cloudDir, 'package-lock.json');
requireCondition(fs.existsSync(lockPath), 'SUP-01: src/GameServerControl.Cloud/package-lock.json must be committed.');
const lock = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
requireCondition(lock.lockfileVersion >= 3, 'SUP-01: Cloud Worker lockfile must use npm lockfileVersion 3 or newer.');
requireCondition(lock.packages?.['']?.name === 'gameservercontrol-cloud', 'SUP-01: Cloud Worker lockfile root package does not match package.json.');

const dependabot = read('.github/dependabot.yml');
requireCondition(!dependabot.includes('directory: "/worker"'), 'SUP-01: Dependabot still targets removed /worker directory.');
requireCondition(
  /package-ecosystem:\s*"npm"[\s\S]*?directory:\s*"\/src\/GameServerControl\.Cloud"/.test(dependabot),
  'SUP-01: Dependabot npm updates must target /src/GameServerControl.Cloud.'
);

const workflows = [
  '.github/workflows/build.yml',
  '.github/workflows/cloudflare-deploy.yml',
  '.github/workflows/release.yml'
];

for (const workflow of workflows) {
  const text = read(workflow);
  const jobsIndex = text.indexOf('\njobs:');
  requireCondition(jobsIndex > 0, `${workflow}: jobs section not found.`);
  const workflowHeader = text.slice(0, jobsIndex);
  requireCondition(
    /permissions:\s*\n\s{2}contents:\s*read\b/.test(workflowHeader),
    `SUP-02: ${workflow} must default GITHUB_TOKEN to contents: read.`
  );

  requireCondition(!/\bnpm install\b/.test(text), `SUP-01: ${workflow} still uses npm install instead of npm ci.`);
  if (text.includes('src/GameServerControl.Cloud')) {
    requireCondition(
      text.includes('npm ci --ignore-scripts --no-audit --no-fund'),
      `SUP-01: ${workflow} must install Cloud Worker dependencies with npm ci --ignore-scripts --no-audit --no-fund.`
    );
  }

  const uses = [...text.matchAll(/^\s*-?\s*uses:\s*([^\s#]+)(?:\s+#.*)?$/gm)].map(match => match[1]);
  for (const action of uses) {
    const at = action.lastIndexOf('@');
    requireCondition(at > 0, `SUP-02: ${workflow} has malformed action reference ${action}.`);
    const ref = action.slice(at + 1);
    requireCondition(/^[0-9a-f]{40}$/.test(ref), `SUP-02: ${workflow} action ${action} is not pinned to a full commit SHA.`);
  }
}

const release = read('.github/workflows/release.yml');
const releaseJobIndex = release.indexOf('\n  release:\n');
requireCondition(releaseJobIndex > 0, 'SUP-02: release job not found.');
const beforeReleaseJob = release.slice(0, releaseJobIndex);
const releaseJob = release.slice(releaseJobIndex);
requireCondition(!/contents:\s*write\b/.test(beforeReleaseJob), 'SUP-02: contents: write must not be granted globally or to non-release jobs.');
requireCondition(
  /\n    permissions:\s*\n      contents:\s*write\b/.test(releaseJob),
  'SUP-02: only the release job should receive contents: write.'
);

console.log('SUP-01/SUP-02 dependency reproducibility and CI least-privilege contract OK.');
