import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { posix } from 'node:path';
import { test } from 'node:test';
import { testShards } from './ci.mjs';

const paths = JSON.parse(readFileSync(new URL('../ci-paths.json', import.meta.url), 'utf8'));
const affected = path => Object.entries(paths)
  .filter(([, patterns]) => patterns.some(pattern => posix.matchesGlob(path, pattern)))
  .map(([name]) => name);

test('toolchain and CI implementation changes exercise the dependent workloads', () => {
  for (const path of ['.nvmrc', 'build/NodeVersion.targets', '.github/workflows/tests.yml',
    '.github/workflows/build-packages.yml', '.github/actions/test-runtime/action.yml',
    '.github/scripts/prepare-release.mjs', '.github/ci-paths.json']) {
    assert.ok(affected(path).includes('dotnet'), path);
    assert.ok(affected(path).includes('portal'), path);
    assert.ok(affected(path).includes('aitracker'), path);
  }
});

test('workload changes select their own checks without dropping server-authored translation checks', () => {
  assert.deepEqual(affected('src/AnalyticsEngine/Web/Controllers/ReportsAPIController.cs'), ['dotnet', 'portal']);
  assert.deepEqual(affected('src/SPO/AITracker/TypeScript/src/AITracker.ts'), ['aitracker']);
  assert.deepEqual(affected('src/SPO/ModernPagesAITrackerExtension/src/extension.ts'), ['spfx']);
  assert.deepEqual(affected('src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg'), ['aitracker', 'spfx']);
  assert.deepEqual(affected('scripts/CopilotAdoption/Compare-Workbooks.ps1'), ['dotnet']);
  assert.deepEqual(affected('README.md'), []);
});

function matches(filter, name) {
  return filter.split('&').every(part => part.replace(/^\(|\)$/g, '').split('|').some(predicate => {
    const [, operator, value] = /^FullyQualifiedName(!~|~)(.+)$/.exec(predicate);
    const contains = name.toLowerCase().includes(value.toLowerCase());
    return operator === '~' ? contains : !contains;
  }));
}

test('shards are exhaustive and disjoint, including overlapping names and future namespaces', () => {
  const cases = [
    ['Tests.UnitTests.DemoGeneratorTests.Generates', 'demo'],
    ['Tests.UnitTests.DemoCopilotMigrationTests.Upgrades', 'demo'],
    ['Tests.UnitTests.CopilotMigrationTests.Upgrades', 'migrations'],
    ['Tests.UnitTests.UpgradeFromStableTests.Upgrades', 'migrations'],
    ['Tests.UnitTests.CopilotAdoptionTests.Scores', 'reports'],
    ['Tests.UnitTests.LicenceActivityTests.Loads', 'reports'],
    ['Tests.UnitTests.TeamsTests.Loads', 'core'],
    ['Future.Namespace.NewFeatureTests.Works', 'core'],
  ];
  for (const [name, expected] of cases) {
    assert.deepEqual(testShards().filter(shard => matches(shard.filter, name)).map(shard => shard.name), [expected]);
  }
});
