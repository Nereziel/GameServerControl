import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const propsPath = path.join(repoRoot, 'Directory.Build.props');
const stableSemVer = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

function fail(message) {
  throw new Error(message);
}

function readVersionSource() {
  const content = fs.readFileSync(propsPath, 'utf8');
  const matches = [...content.matchAll(/<Version>([^<]+)<\/Version>/g)];
  if (matches.length !== 1) {
    fail(`Directory.Build.props must contain exactly one <Version>; found ${matches.length}.`);
  }

  for (const redundant of ['VersionPrefix', 'VersionSuffix', 'AssemblyVersion', 'FileVersion', 'InformationalVersion']) {
    if (content.includes(`<${redundant}>`)) {
      fail(`Directory.Build.props must use <Version> as the single source of truth; remove <${redundant}>.`);
    }
  }

  const version = matches[0][1].trim();
  const parsed = stableSemVer.exec(version);
  if (!parsed) {
    fail(`Version '${version}' must be stable SemVer in MAJOR.MINOR.PATCH form without leading zeroes.`);
  }

  return {
    content,
    version,
    major: Number(parsed[1]),
    minor: Number(parsed[2]),
    patch: Number(parsed[3])
  };
}

function expectedTag(version) {
  return `v${version}`;
}

function checkTag(version, tag) {
  const expected = expectedTag(version);
  if (tag !== expected) {
    fail(`Release tag '${tag}' must match the repository version exactly: ${expected}.`);
  }
}

function bumpVersion(source, part) {
  let { major, minor, patch } = source;
  switch (part) {
    case 'major':
      major += 1;
      minor = 0;
      patch = 0;
      break;
    case 'minor':
      minor += 1;
      patch = 0;
      break;
    case 'patch':
      patch += 1;
      break;
    default:
      fail(`Unknown bump '${part ?? ''}'. Use major, minor, or patch.`);
  }

  const next = `${major}.${minor}.${patch}`;
  const updated = source.content.replace(/<Version>[^<]+<\/Version>/, `<Version>${next}</Version>`);
  fs.writeFileSync(propsPath, updated, 'utf8');
  return next;
}

const command = process.argv[2] ?? 'check';
const source = readVersionSource();

switch (command) {
  case 'check': {
    const tag = process.argv[3];
    if (tag) checkTag(source.version, tag);
    console.log(`Version contract OK: ${source.version}`);
    break;
  }
  case 'current':
    console.log(source.version);
    break;
  case 'tag':
    console.log(expectedTag(source.version));
    break;
  case 'bump':
    console.log(bumpVersion(source, process.argv[3]));
    break;
  default:
    fail(`Unknown command '${command}'. Use check, current, tag, or bump.`);
}
