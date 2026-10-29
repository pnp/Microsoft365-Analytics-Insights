// The CI is held together by two things that are written down twice and checked nowhere else: the protected
// status checks (branch protection waits for those exact names) and the path lists the workflows start on
// (ci-paths.json says which changes need which tests; ci.yml and pr.yml repeat the paths as triggers). When one
// side moves the symptom is far from the cause - a pull request that can never merge, or a change that ships
// without its build. This reads the workflow files without a YAML library, because workflow_checks runs these
// tests straight after checkout and setup-node, and reports every way the written-down contract has broken.

const meaningful = line => line.trim() !== '' && !line.trimStart().startsWith('#');
const indentOf = line => line.length - line.trimStart().length;
const unquote = value => value.replace(/\s+#.*$/, '').trim().replace(/^(['"])(.*)\1$/, '$2');
const flowList = value => value.replace(/^\[|\]$/g, '').split(',').map(unquote).filter(Boolean);

/** The direct children of a block ({ start, end } line indexes), each with the extent of its own body. */
function children(lines, range) {
  const found = [];
  let indent = null;
  for (let i = range.start; i < range.end; i++) {
    if (!meaningful(lines[i])) continue;
    indent ??= indentOf(lines[i]);
    if (indentOf(lines[i]) !== indent) continue;
    const match = /^([\w.-]+):(.*)$/.exec(lines[i].trim());
    if (!match) continue;
    let end = i + 1;
    // A list may sit at its key's own indentation, so a "- " line there still belongs to the key.
    while (end < range.end && (!meaningful(lines[end]) || indentOf(lines[end]) > indent
      || (indentOf(lines[end]) === indent && lines[end].trim().startsWith('- ')))) end++;
    found.push({ key: match[1], value: match[2].trim(), start: i + 1, end });
  }
  return found;
}

const section = (lines, range, key) => children(lines, range).find(child => child.key === key) ?? null;

function listItems(lines, range) {
  const items = [];
  for (let i = range.start; i < range.end; i++) {
    const match = /^\s*-\s+(.+)$/.exec(lines[i]);
    if (match) items.push(unquote(match[1]));
  }
  return items;
}

const split = text => text.split(/\r?\n/);

/** Each event a workflow starts on: its path list (null when it has none) and whether it is path-filtered at all. */
export function triggers(text) {
  const lines = split(text);
  const on = section(lines, { start: 0, end: lines.length }, 'on');
  if (!on) throw new Error('Could not find a block-style "on:" in the workflow.');
  return Object.fromEntries(children(lines, on).map(event => {
    const paths = section(lines, event, 'paths');
    const ignored = section(lines, event, 'paths-ignore');
    return [event.key, { paths: paths ? listItems(lines, paths) : null, filtered: Boolean(paths || ignored) }];
  }));
}

/** Each job of a workflow: the status check it reports (its name, else its id), its condition and what it waits for. */
export function jobs(text) {
  const lines = split(text);
  const all = section(lines, { start: 0, end: lines.length }, 'jobs');
  if (!all) throw new Error('Could not find "jobs:" in the workflow.');
  return Object.fromEntries(children(lines, all).map(job => {
    const field = key => section(lines, job, key);
    const needs = field('needs');
    return [job.key, {
      check: unquote(field('name')?.value ?? '') || job.key,
      condition: unquote(field('if')?.value ?? ''),
      needs: !needs ? [] : needs.value.startsWith('[') ? flowList(needs.value) : needs.value ? [unquote(needs.value)] : listItems(lines, needs),
    }];
  }));
}

/** The status checks `dev` and `main` require. Branch protection matches these names exactly. */
export const requiredChecks = [
  ['tests.yml', 'test_dotnet (Release)'],
  ['tests.yml', 'test_aitracker'],
  ['secret-scan.yml', 'gitleaks'],
];

/**
 * ci-paths.json patterns that the package and release builds deliberately do not start on. Only the .NET tests read
 * these: a pull request that changes them still runs every test (tests.yml has no path trigger), but nothing in a
 * package comes from them. Anything else ci-paths.json selects tests for has to start both builds too.
 */
export const testOnlyInputs = ['scripts/CopilotAdoption/**'];

const covers = (trigger, pattern) => trigger === pattern || (trigger.endsWith('/**') && pattern.startsWith(trigger.slice(0, -2)));

/** Every way the workflows and ci-paths.json have drifted apart. Empty when the contract holds. */
export function contractProblems({ ciPaths, workflows }) {
  const problems = [];
  const release = triggers(workflows['ci.yml']).push;
  const pullRequest = triggers(workflows['pr.yml']).pull_request;
  if (!release) problems.push('ci.yml does not start on a push, so merging to main would build no release.');
  if (!pullRequest) problems.push('pr.yml does not start on a pull_request, so a pull request would build no packages.');

  // A null path list means unfiltered, which already covers everything.
  const builds = [['ci.yml', release?.paths], ['pr.yml', pullRequest?.paths]].filter(([, paths]) => paths);
  const patterns = [...new Set(Object.values(ciPaths).flat())].filter(pattern => !testOnlyInputs.includes(pattern));
  for (const [file, paths] of builds) {
    for (const pattern of patterns) {
      if (!paths.some(trigger => covers(trigger, pattern))) {
        problems.push(`${file} does not start on "${pattern}", which ci-paths.json selects tests for. Add it to the paths of ci.yml and pr.yml, or to testOnlyInputs with the reason.`);
      }
    }
  }
  if (release?.paths && pullRequest?.paths) {
    for (const trigger of release.paths) {
      if (!pullRequest.paths.some(path => covers(path, trigger))) {
        problems.push(`pr.yml does not start on "${trigger}", which ci.yml does: a change there would merge without its packages ever being built.`);
      }
    }
  }

  const tests = jobs(workflows['tests.yml']);
  for (const [file, check] of requiredChecks) {
    if (!Object.values(jobs(workflows[file])).some(job => job.check === check)) {
      problems.push(`${file} no longer reports the required status check "${check}". Branch protection waits for that exact name.`);
    }
  }
  for (const file of new Set(requiredChecks.map(([name]) => name))) {
    const onPullRequest = triggers(workflows[file]).pull_request;
    if (!onPullRequest) problems.push(`${file} does not run on pull_request, so its required checks would never report.`);
    else if (onPullRequest.filtered) problems.push(`${file} filters its pull_request trigger by path, so its required checks would never report on an unrelated pull request and would block it for ever. Gate the work inside the workflow instead.`);
  }

  const aggregator = Object.values(tests).find(job => job.check === 'test_dotnet (Release)');
  if (aggregator) {
    if (!/\balways\(\)/.test(aggregator.condition)) {
      problems.push('"test_dotnet (Release)" has no always() in its condition, so a failed or cancelled job upstream would skip it, and GitHub counts a skipped required check as passed.');
    }
    const required = new Set(requiredChecks.filter(([file]) => file === 'tests.yml').map(([, check]) => check));
    for (const [id, job] of Object.entries(tests)) {
      if (required.has(job.check) || aggregator.needs.includes(id)) continue;
      problems.push(`The tests.yml job "${id}" is not in the needs of "test_dotnet (Release)", so nothing requires it. Add it there, or make it a required check of its own.`);
    }
  }
  return problems;
}
