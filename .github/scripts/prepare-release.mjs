import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { pathToFileURL } from 'node:url';

export const packages = [
  'AITrackerInstaller.zip', 'AppInsightsImporter.zip', 'ControlPanelApp.zip',
  'Office365ActivityImporter.zip', 'Website.zip',
];
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

export function prepareRelease({ repository, baseline, commit, artifacts, output }) {
  const git = (...args) => execFileSync('git', args, { cwd: repository, stdio: 'pipe' });
  const resolvedCommit = git('rev-parse', '--verify', `${commit}^{commit}`).toString().trim();
  const resolvedBaseline = git('rev-parse', '--verify', `${baseline}^{commit}`).toString().trim();
  const changed = git('diff', '--name-only', '-z', '--diff-filter=AMR', resolvedBaseline, resolvedCommit)
    .toString().split('\0').filter(Boolean);
  const scripts = new Set(changed.filter(path => path.endsWith('.manual.sql') || /\/repair_[^/]+\.sql$/.test(path)));
  const migrations = changed.filter(path => /\/Migrations\/\d{15}_[^/]+\.cs$/.test(path) && !path.endsWith('.Designer.cs'));
  for (const migration of migrations) {
    const script = migration.replace(/\.cs$/, '.manual.sql');
    const hasScript = git('ls-tree', '--name-only', resolvedCommit, '--', script).length > 0;
    const isNew = git('ls-tree', '--name-only', resolvedBaseline, '--', migration).length === 0;
    if (isNew && !hasScript) throw new Error(`New migration requires a matching manual script: ${migration}`);
    if (hasScript) scripts.add(script);
  }

  const names = new Set(packages);
  for (const script of scripts) {
    if (names.has(basename(script))) throw new Error(`Duplicate release asset name: ${basename(script)}`);
    names.add(basename(script));
    git('cat-file', '-e', `${resolvedCommit}:${script}`);
  }
  mkdirSync(output, { recursive: true });
  if (readdirSync(output).length) throw new Error('Release staging directory must be empty.');
  const assets = [];
  for (const name of packages) {
    const source = join(artifacts, name);
    const bytes = readFileSync(source);
    if (!bytes.length) throw new Error(`Empty release package: ${name}`);
    copyFileSync(source, join(output, name));
    assets.push({ name, sha256: sha256(bytes) });
  }
  for (const source of [...scripts].sort()) {
    // Export the git blob, never a Windows checkout that may have CRLF conversion.
    const bytes = git('cat-file', 'blob', `${resolvedCommit}:${source}`);
    const name = basename(source);
    writeFileSync(join(output, name), bytes);
    assets.push({ name, source, sha256: sha256(bytes) });
  }
  const manifest = { commit: resolvedCommit, baseline: resolvedBaseline, migrations, assets };
  writeFileSync(join(output, 'release-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return manifest;
}

export function verifyRelease(expectedDirectory, downloadedDirectory) {
  const manifestBytes = readFileSync(join(expectedDirectory, 'release-manifest.json'));
  assertIdentical(manifestBytes, readFileSync(join(downloadedDirectory, 'release-manifest.json')), 'release-manifest.json');
  const manifest = JSON.parse(manifestBytes);
  for (const asset of manifest.assets) {
    if (sha256(readFileSync(join(downloadedDirectory, asset.name))) !== asset.sha256) {
      throw new Error(`Release asset checksum mismatch: ${asset.name}`);
    }
  }
  const expected = [...manifest.assets.map(asset => asset.name), 'release-manifest.json'].sort();
  if (JSON.stringify(readdirSync(downloadedDirectory).sort()) !== JSON.stringify(expected)) {
    throw new Error('Downloaded release has an unexpected asset set.');
  }
}

function assertIdentical(expected, actual, name) {
  if (!expected.equals(actual)) throw new Error(`Release asset checksum mismatch: ${name}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [mode, ...args] = process.argv.slice(2);
  if (mode === 'prepare' && args.length === 4) {
    const [baseline, commit, artifacts, output] = args;
    const manifest = prepareRelease({ repository: process.cwd(), baseline, commit, artifacts, output });
    console.log(`Prepared ${manifest.assets.length} verified assets and a checksum manifest.`);
  } else if (mode === 'verify' && args.length === 2) {
    verifyRelease(...args);
    console.log('Downloaded release assets match the staged bytes.');
  } else {
    throw new Error('Use prepare <baseline> <commit> <artifacts> <output> or verify <expected> <downloaded>.');
  }
}
