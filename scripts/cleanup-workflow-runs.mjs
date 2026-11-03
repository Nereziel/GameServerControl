// One-time GitHub Actions run cleanup for this repository.
// GitHub Actions workflow_dispatch provides a scoped GITHUB_TOKEN (actions:write).
// Defaults to dry-run, targets only completed successful PR runs, and retains
// recent records plus the latest N per workflow.
const repository = process.env.GITHUB_REPOSITORY;
if (repository !== 'Nereziel/GameServerControl')
  throw new Error('Cleanup is restricted to Nereziel/GameServerControl.');

const token = process.env.GH_TOKEN;
if (!token) throw new Error('GH_TOKEN with actions:write is required.');

function integer(name, fallback, minimum, maximum) {
  const raw = process.env[name] || String(fallback);
  const n = Number(raw);
  if (!Number.isSafeInteger(n) || n < minimum || n > maximum)
    throw new Error(name + ' must be an integer in ' + minimum + '..' + maximum);
  return n;
}
const olderThanHours = integer('OLDER_THAN_HOURS', 24, 1, 8760);
const keepPerWorkflow = integer('KEEP_PER_WORKFLOW', 5, 1, 100);
const maxDeletions = integer('MAX_DELETIONS', 400, 1, 1000);
const dryRun = process.env.DRY_RUN !== 'false';
const cutoff = Date.now() - olderThanHours * 60 * 60 * 1000;
const base = 'https://api.github.com/repos/' + repository;
const headers = {
  authorization: 'Bearer ' + token,
  accept: 'application/vnd.github+json',
  'x-github-api-version': '2022-11-28',
  'user-agent': 'GameServerControl-actions-cleanup'
};

async function api(path, options = {}) {
  const res = await fetch(base + path, { ...options, headers });
  if (!res.ok) throw new Error('GitHub API ' + res.status + ' on ' + path);
  return res.status === 204 ? null : res.json();
}

// Fetch every page BEFORE deleting anything (deletions shift pagination).
const runs = [];
for (let page = 1; page <= 100; page++) {
  const data = await api('/actions/runs?per_page=100&page=' + page);
  const batch = data.workflow_runs || [];
  runs.push(...batch);
  if (batch.length < 100) break;
  if (page === 100) throw new Error('Too many runs for a safe complete scan.');
}
runs.sort((a, b) => new Date(b.created_at) - new Date(a.created_at));
const seenByWorkflow = new Map();
const candidates = [];
for (const run of runs) {
  const key = run.workflow_id || run.name;
  const count = seenByWorkflow.get(key) || 0;
  seenByWorkflow.set(key, count + 1);
  if (count < keepPerWorkflow) continue;
  // Preserve in-progress, failures, cancelled, tag, main, releases, manual runs.
  if (run.event !== 'pull_request' || run.status !== 'completed' ||
      run.conclusion !== 'success' || Date.parse(run.created_at) >= cutoff) continue;
  candidates.push(run);
}
const selected = candidates.slice(0, maxDeletions);
console.log('Scanned ' + runs.length + ' workflow runs.');
console.log('Eligible old successful PR runs: ' + candidates.length);
console.log('Selected this invocation: ' + selected.length);
console.log('Dry run: ' + dryRun);
for (const run of selected.slice(0, 25))
  console.log((dryRun ? 'Would delete ' : 'Deleting ') + run.id +
    ' | ' + run.name + ' | ' + run.created_at);
if (selected.length > 25) console.log('... plus ' + (selected.length - 25) + ' more');
if (dryRun) {
  console.log('Nothing deleted. Run again with dry_run=false to apply.');
  process.exit(0);
}
let removed = 0;
for (const run of selected) {
  await api('/actions/runs/' + run.id, { method: 'DELETE' });
  removed++;
  if (removed % 25 === 0) console.log('Deleted ' + removed + '/' + selected.length);
}
console.log('Successfully deleted ' + removed + ' old successful PR workflow runs.');
