import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { packages, prepareRelease, verifyRelease } from './prepare-release.mjs';

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'contoso-release-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const repository = join(root, 'repository');
  const artifacts = join(root, 'packages');
  mkdirSync(repository);
  mkdirSync(artifacts);
  const git = (...args) => execFileSync('git', args, { cwd: repository, stdio: 'pipe' }).toString().trim();
  const commit = () => {
    git('add', '.');
    git('-c', 'user.name=Contoso Test', '-c', 'user.email=test@contoso.example', 'commit', '-qm', 'Synthetic fixture');
    return git('rev-parse', 'HEAD');
  };
  const put = (path, text) => {
    const fullPath = join(repository, path);
    mkdirSync(dirname(fullPath), { recursive: true });
    writeFileSync(fullPath, text);
  };
  git('init', '-q');
  git('config', 'core.autocrlf', 'true');
  put('.gitattributes', '*.sql text eol=lf\n');
  commit();
  git('tag', 'stable');
  for (const name of packages) writeFileSync(join(artifacts, name), `Synthetic ${name}`);
  return { repository, artifacts, output: join(root, 'staged'), baseline: 'stable', git, commit, put, root };
}

test('release staging attaches changed SQL, preserves git-blob bytes and verifies downloads', t => {
  const f = fixture(t);
  const migration = 'src/AnalyticsEngine/Common/Entities/Migrations/202610010000001_ContosoExample';
  const sql = "SELECT N'\u039a\u03b1\u03bb\u03b7\u03bc\u03ad\u03c1\u03b1';\n";
  f.put(`${migration}.cs`, 'class ContosoExample {}\n');
  f.put(`${migration}.manual.sql`, sql);
  f.put('src/AnalyticsEngine/SQL/repair_contoso_example.sql', 'SELECT 1;\n');
  const commit = f.commit();
  // The working tree deliberately differs from the committed LF/Unicode bytes.
  f.put(`${migration}.manual.sql`, sql.replaceAll('\n', '\r\n'));
  const manifest = prepareRelease({ ...f, commit });
  assert.equal(manifest.commit, commit);
  assert.equal(manifest.assets.length, packages.length + 2);
  assert.deepEqual(readFileSync(join(f.output, '202610010000001_ContosoExample.manual.sql')), Buffer.from(sql));
  const downloaded = join(f.root, 'downloaded');
  mkdirSync(downloaded);
  for (const name of readdirSync(f.output)) copyFileSync(join(f.output, name), join(downloaded, name));
  verifyRelease(f.output, downloaded);
  writeFileSync(join(downloaded, 'Website.zip'), 'Altered');
  assert.throws(() => verifyRelease(f.output, downloaded), /checksum mismatch: Website.zip/);
});

test('a migration without its manual SQL prevents release staging', t => {
  const f = fixture(t);
  f.put('src/AnalyticsEngine/Common/Entities/Migrations/202610010000001_ContosoExample.cs', 'class ContosoExample {}\n');
  assert.throws(() => prepareRelease({ ...f, commit: f.commit() }), /New migration requires a matching manual script/);
});

test('edits to historical migrations allow absent legacy scripts and attach existing ones', t => {
  const f = fixture(t);
  const legacy = 'src/AnalyticsEngine/Common/Entities/Migrations/202601010000001_ContosoLegacy';
  const scripted = 'src/AnalyticsEngine/Common/Entities/Migrations/202601010000002_ContosoScripted';
  f.put(`${legacy}.cs`, 'class ContosoLegacy {}\n');
  f.put(`${scripted}.cs`, 'class ContosoScripted {}\n');
  f.put(`${scripted}.manual.sql`, 'SELECT 1;\n');
  const baseline = f.commit();
  f.put(`${legacy}.cs`, '// Documentation change\nclass ContosoLegacy {}\n');
  f.put(`${scripted}.cs`, '// Documentation change\nclass ContosoScripted {}\n');
  const manifest = prepareRelease({ ...f, baseline, commit: f.commit() });
  assert.deepEqual(manifest.assets.filter(asset => asset.source).map(asset => asset.source), [`${scripted}.manual.sql`]);
});

test('missing packages and duplicate script basenames cannot produce a releasable manifest', t => {
  const f = fixture(t);
  rmSync(join(f.artifacts, 'Website.zip'));
  assert.throws(() => prepareRelease({ ...f, commit: 'HEAD' }), /ENOENT/);
  const other = fixture(t);
  other.put('src/one/repair_contoso.sql', 'SELECT 1;\n');
  other.put('src/two/repair_contoso.sql', 'SELECT 2;\n');
  assert.throws(() => prepareRelease({ ...other, commit: other.commit() }), /Duplicate release asset/);
});

test('unexpected downloaded assets are rejected even when every expected checksum matches', t => {
  const f = fixture(t);
  prepareRelease({ ...f, commit: 'HEAD' });
  const downloaded = join(f.root, 'downloaded');
  mkdirSync(downloaded);
  for (const name of readdirSync(f.output)) copyFileSync(join(f.output, name), join(downloaded, name));
  writeFileSync(join(downloaded, 'unexpected.txt'), 'Not an approved asset');
  assert.throws(() => verifyRelease(f.output, downloaded), /unexpected asset set/);
});

test('PowerShell package and TRX helpers reject incomplete results', () => {
  execFileSync('pwsh', ['-NoLogo', '-NoProfile', '-File', fileURLToPath(new URL('./Test-CIHelpers.ps1', import.meta.url))], {
    stdio: 'pipe',
  });
});
