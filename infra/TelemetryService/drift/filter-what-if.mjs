#!/usr/bin/env node
// Turns `az deployment group what-if --no-pretty-print -o json` output into a drift verdict.
//
// Exists because raw what-if output is unusable as a signal: it always reports a handful of
// differences that are correct and expected (a Key Vault secret value it cannot read, app settings
// the platform injects at deploy time). Without suppressing those, a drift check is red on day one
// and ignored by day two - so the two real defects that motivated this (an empty EasyAuth
// allowedApplications array, and an alwaysOn that had been flipped to false) would still hide in
// the noise.
//
// Usage:
//   node filter-what-if.mjs <what-if.json> <ignore-rules.json> [summary.md]
//
// Exit codes: 0 = no drift, 1 = drift found, 2 = could not evaluate.

import { readFileSync, writeFileSync } from 'node:fs';

/** what-if change types that mean the deployed resource group does not match the template. */
const DRIFT_CHANGE_TYPES = new Set(['Create', 'Delete', 'Modify']);

/**
 * Change types that are reported but are not drift.
 * - NoChange / Ignore: what-if's own "nothing to do here" verdicts. `Ignore` means the resource is
 *   outside the template, which is expected: the template does not own the whole resource group.
 * - Deploy / Unsupported: ARM could not diff the resource, so it says so rather than guessing.
 *   Treating "I don't know" as drift would make the check permanently red.
 */
const INFORMATIONAL_CHANGE_TYPES = new Set(['NoChange', 'Ignore', 'Deploy', 'Unsupported']);

/**
 * Derives the ARM resource type from a resource id.
 * `/subscriptions/s/resourceGroups/r/providers/Microsoft.Web/sites/a/config/appsettings`
 *   -> `Microsoft.Web/sites/config`
 */
export function resourceTypeOf(resourceId) {
  if (typeof resourceId !== 'string') {
    return '';
  }

  const marker = '/providers/';
  const index = resourceId.toLowerCase().lastIndexOf(marker);
  if (index === -1) {
    return '';
  }

  const segments = resourceId.slice(index + marker.length).split('/').filter(Boolean);
  if (segments.length < 2) {
    return '';
  }

  // namespace, then every other segment: type/name/type/name...
  const parts = [segments[0]];
  for (let i = 1; i < segments.length; i += 2) {
    parts.push(segments[i]);
  }
  return parts.join('/');
}

/**
 * Flattens the nested delta tree into one entry per changed leaf property.
 * Child `path` values from ARM are relative to their parent, so they are joined on the way down.
 */
export function flattenDelta(delta, parentPath = '') {
  if (!Array.isArray(delta)) {
    return [];
  }

  const leaves = [];
  for (const node of delta) {
    if (!node || typeof node !== 'object') {
      continue;
    }

    // "NoEffect" means the template sets the value but ARM will not act on it. Not drift, and its
    // children are not drift either.
    if (node.propertyChangeType === 'NoEffect') {
      continue;
    }

    const path = node.path === undefined || node.path === null || node.path === ''
      ? parentPath
      : (parentPath ? `${parentPath}.${node.path}` : String(node.path));

    const children = Array.isArray(node.children) ? node.children : [];
    if (children.length > 0) {
      leaves.push(...flattenDelta(children, path));
    } else {
      leaves.push({
        path,
        propertyChangeType: node.propertyChangeType ?? 'Modify',
        before: node.before,
        after: node.after,
      });
    }
  }

  return leaves;
}

function ruleMatchesResource(rule, resourceType) {
  if (!rule.resourceType) {
    return true;
  }
  return rule.resourceType.toLowerCase() === resourceType.toLowerCase();
}

/** A rule with no propertyPath suppresses the whole resource. */
export function isWholeResourceRule(rule) {
  return !rule.propertyPath;
}

function ruleMatchesProperty(rule, leaf) {
  if (rule.propertyChangeType && rule.propertyChangeType !== leaf.propertyChangeType) {
    return false;
  }
  return new RegExp(rule.propertyPath).test(leaf.path);
}

/**
 * @returns {{drift: object[], suppressed: object[], informational: object[]}}
 */
export function evaluate(whatIf, ignoreRules) {
  const changes = Array.isArray(whatIf) ? whatIf : (whatIf?.changes ?? []);
  const rules = ignoreRules?.rules ?? [];

  const drift = [];
  const suppressed = [];
  const informational = [];

  for (const change of changes) {
    const changeType = change?.changeType ?? 'Modify';
    const resourceId = change?.resourceId ?? '';
    const resourceType = resourceTypeOf(resourceId);

    if (INFORMATIONAL_CHANGE_TYPES.has(changeType) || !DRIFT_CHANGE_TYPES.has(changeType)) {
      informational.push({ resourceId, resourceType, changeType });
      continue;
    }

    const wholeResourceRule = rules
      .filter(isWholeResourceRule)
      .find((rule) => ruleMatchesResource(rule, resourceType));

    if (wholeResourceRule) {
      suppressed.push({ resourceId, resourceType, changeType, reason: wholeResourceRule.reason });
      continue;
    }

    // Create/Delete of a whole resource has no useful delta - the resource itself is the finding.
    if (changeType !== 'Modify') {
      drift.push({ resourceId, resourceType, changeType, properties: [] });
      continue;
    }

    const propertyRules = rules.filter(
      (rule) => !isWholeResourceRule(rule) && ruleMatchesResource(rule, resourceType));
    const remaining = [];

    for (const leaf of flattenDelta(change.delta)) {
      const rule = propertyRules.find((candidate) => ruleMatchesProperty(candidate, leaf));
      if (rule) {
        suppressed.push({ resourceId, resourceType, changeType, path: leaf.path, reason: rule.reason });
      } else {
        remaining.push(leaf);
      }
    }

    // If every difference on this resource was expected, the resource is not drifted.
    if (remaining.length > 0) {
      drift.push({ resourceId, resourceType, changeType, properties: remaining });
    }
  }

  return { drift, suppressed, informational };
}

function format(value) {
  if (value === undefined) {
    return '_(absent)_';
  }
  if (value === null) {
    return '`null`';
  }
  const text = typeof value === 'object' ? JSON.stringify(value) : String(value);
  const trimmed = text.length > 120 ? `${text.slice(0, 117)}...` : text;
  return `\`${trimmed.replace(/\|/g, '\\|')}\``;
}

/**
 * Resource ids carry the subscription id and resource-group name. This repository is public and the
 * job summary is world-readable on a public repo, so only the provider-relative part is published.
 */
export function shortenResourceId(resourceId) {
  const marker = '/providers/';
  const index = resourceId.toLowerCase().lastIndexOf(marker);
  return index === -1 ? resourceId : resourceId.slice(index + marker.length);
}

export function buildSummary({ drift, suppressed, informational }) {
  const lines = ['## Azure configuration drift', ''];

  if (drift.length === 0) {
    lines.push('No drift. The deployed resource group matches the committed Bicep template.', '');
  } else {
    lines.push(
      `**${drift.length} resource(s) differ from the committed template.**`,
      '',
      'The template is the source of truth. Either redeploy it, or update the template if the',
      'deployed value is the one you actually want.',
      '',
    );

    for (const item of drift) {
      lines.push(`### \`${shortenResourceId(item.resourceId)}\``, '');
      lines.push(`Change type: **${item.changeType}**`, '');

      if (item.properties.length > 0) {
        lines.push('| Property | Deployed | Template |', '| --- | --- | --- |');
        for (const property of item.properties) {
          lines.push(`| \`${property.path}\` | ${format(property.before)} | ${format(property.after)} |`);
        }
        lines.push('');
      }
    }
  }

  lines.push(
    `<sub>${suppressed.length} known-benign difference(s) suppressed, ` +
    `${informational.length} resource(s) reported as no-change or outside the template.</sub>`,
    '',
  );

  return lines.join('\n');
}

function main() {
  const [whatIfPath, rulesPath, summaryPath] = process.argv.slice(2);

  if (!whatIfPath || !rulesPath) {
    console.error('Usage: node filter-what-if.mjs <what-if.json> <ignore-rules.json> [summary.md]');
    process.exit(2);
  }

  let whatIf;
  let rules;
  try {
    whatIf = JSON.parse(readFileSync(whatIfPath, 'utf8'));
    rules = JSON.parse(readFileSync(rulesPath, 'utf8'));
  } catch (error) {
    console.error(`Could not read what-if output or ignore rules: ${error.message}`);
    process.exit(2);
  }

  // A what-if that failed server-side must never be read as "no drift".
  if (whatIf?.status && whatIf.status !== 'Succeeded') {
    console.error(`what-if did not succeed (status: ${whatIf.status}).`);
    if (whatIf.error) {
      console.error(JSON.stringify(whatIf.error, null, 2));
    }
    process.exit(2);
  }

  const result = evaluate(whatIf, rules);
  const summary = buildSummary(result);

  if (summaryPath) {
    writeFileSync(summaryPath, summary, 'utf8');
  }
  console.log(summary);

  process.exit(result.drift.length > 0 ? 1 : 0);
}

// Only run when executed directly, so the exported functions stay unit-testable.
if (process.argv[1] && import.meta.url === `file://${process.argv[1].replace(/\\/g, '/')}`) {
  main();
}
