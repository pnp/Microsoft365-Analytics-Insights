import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { contractProblems, jobs, triggers } from './workflow-contract.mjs';

const read = relative => readFileSync(new URL(relative, import.meta.url), 'utf8');
const real = () => ({
  ciPaths: JSON.parse(read('../ci-paths.json')),
  workflows: Object.fromEntries(['ci.yml', 'pr.yml', 'tests.yml', 'secret-scan.yml'].map(name => [name, read(`../workflows/${name}`)])),
});

// The real files with one workflow's text changed. The change must really change it: a fixture that stops matching
// the file would otherwise keep "proving" a drift that is no longer there.
function drifted(workflow, from, to) {
  const { ciPaths, workflows } = real();
  const changed = workflows[workflow].replace(from, to);
  assert.notEqual(changed, workflows[workflow], `The fixture no longer matches ${workflow}.`);
  return { ciPaths, workflows: { ...workflows, [workflow]: changed } };
}

function problemsMatching(input, expected, total = 1) {
  const problems = contractProblems(input);
  assert.equal(problems.length, total, problems.join('\n'));
  assert.ok(problems.some(problem => expected.test(problem)), problems.join('\n'));
}

const onlyProblem = (input, expected) => problemsMatching(input, expected, 1);

test('the real workflows and ci-paths.json hold the contract', () => {
  assert.deepEqual(contractProblems(real()), []);
});

test('the parser reads what the contract relies on from the real files', () => {
  const { workflows } = real();
  assert.ok(triggers(workflows['ci.yml']).push.paths.includes('.nvmrc'));
  assert.ok(triggers(workflows['pr.yml']).pull_request.paths.includes('src/AnalyticsEngine/**'));
  assert.equal(triggers(workflows['tests.yml']).pull_request.filtered, false);
  assert.equal(triggers(workflows['secret-scan.yml']).pull_request.filtered, false);
  const tests = jobs(workflows['tests.yml']);
  assert.equal(tests.test_dotnet.check, 'test_dotnet (Release)');
  assert.equal(tests.test_aitracker.check, 'test_aitracker');
  assert.deepEqual(tests.test_dotnet.needs, ['setup_tests', 'workflow_checks', 'build_tests', 'test_shards', 'test_portal']);
  assert.equal(jobs(workflows['secret-scan.yml']).gitleaks.check, 'gitleaks');
});

test('a path that ci-paths.json selects tests for but a build does not start on is reported for each build', () => {
  const input = real();
  input.ciPaths.dotnet.push('deploy/**');
  const problems = contractProblems(input);
  assert.equal(problems.length, 2, problems.join('\n'));
  assert.match(problems[0], /^ci\.yml does not start on "deploy\/\*\*"/);
  assert.match(problems[1], /^pr\.yml does not start on "deploy\/\*\*"/);
});

test('a pattern is covered by a broader trigger, but a narrower trigger does not cover a broader pattern', () => {
  const broader = real();
  broader.ciPaths.spfx.push('src/AnalyticsEngine/Web/Scripts/portal/**');
  assert.deepEqual(contractProblems(broader), []);
  const narrower = real();
  narrower.ciPaths.portal.push('src/**');
  assert.equal(contractProblems(narrower).length, 2);
});

test('the pull request build must start on everything the release build starts on', () => {
  // Reported twice, by the ci-paths.json check and by the comparison with ci.yml; either alone would catch it.
  problemsMatching(drifted('pr.yml', /^ +- \.nvmrc\r?\n/m, ''), /^pr\.yml does not start on "\.nvmrc", which ci\.yml does/, 2);
  // A trigger ci-paths.json knows nothing about is only caught by the comparison.
  problemsMatching(drifted('ci.yml', /^( +- \.nvmrc\r?\n)/m, '$1      - docs/**\n'), /^pr\.yml does not start on "docs\/\*\*", which ci\.yml does/, 1);
});

test('losing a build trigger altogether is reported', () => {
  onlyProblem(drifted('ci.yml', /^ {2}push:/m, '  schedule:'), /^ci\.yml does not start on a push/);
});

test('a renamed required check is reported, because branch protection waits for the exact name', () => {
  onlyProblem(drifted('tests.yml', 'name: test_dotnet (Release)', 'name: test_dotnet (Debug)'), /no longer reports the required status check "test_dotnet \(Release\)"/);
  // Renaming a job's id renames its check, and leaves the job outside the aggregate's needs as well.
  problemsMatching(drifted('tests.yml', /^ {2}test_aitracker:/m, '  test_ai_tracker:'), /required status check "test_aitracker"/, 2);
  onlyProblem(drifted('secret-scan.yml', 'name: gitleaks', 'name: secret scan'), /required status check "gitleaks"/);
});

test('a path filter on a required workflow is reported, because the check would never report on other pull requests', () => {
  const filtered = drifted('tests.yml', /(^ {2}pull_request:\r?\n {4}types: .*\r?\n)/m, '$1    paths:\n      - src/**\n');
  onlyProblem(filtered, /^tests\.yml filters its pull_request trigger by path/);
  onlyProblem(drifted('secret-scan.yml', /^ {2}pull_request:/m, '  pull_request:\n    paths-ignore:\n      - docs/**'), /^secret-scan\.yml filters its pull_request trigger by path/);
  onlyProblem(drifted('tests.yml', /^ {2}pull_request:/m, '  pull_request_target:'), /^tests\.yml does not run on pull_request/);
});

test('the aggregate check cannot be skipped into a pass, and waits for every job', () => {
  onlyProblem(drifted('tests.yml', 'if: always() && needs.setup_tests.result', 'if: needs.setup_tests.result'), /no always\(\) in its condition/);
  onlyProblem(drifted('tests.yml', ', test_portal]', ']'), /job "test_portal" is not in the needs of "test_dotnet \(Release\)"/);
  onlyProblem(drifted('tests.yml', /^ {2}test_aitracker:/m, '  test_spfx:\n    runs-on: ubuntu-latest\n    steps: []\n\n  test_aitracker:'), /job "test_spfx" is not in the needs/);
});
