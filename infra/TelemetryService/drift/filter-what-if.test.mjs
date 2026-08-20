// Unit tests for the what-if drift filter. Run with: node --test
//
// These matter more than they look: the filter is the only thing standing between a useful drift
// signal and a permanently-red check that everyone mutes. The two regression guards worth reading
// are "a WEBSITE_ setting that is MODIFIED is still reported" and the two real-world cases the
// feature was created for (alwaysOn, and an EasyAuth allowedApplications array that emptied itself).

import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { evaluate, flattenDelta, resourceTypeOf, shortenResourceId, buildSummary } from './filter-what-if.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const shippedRules = JSON.parse(readFileSync(join(here, 'drift-ignore.json'), 'utf8'));

// Synthetic ids only - never paste a real subscription, resource group or resource name in here.
const RG = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-contoso-telemetry';
const SITE = `${RG}/providers/Microsoft.Web/sites/app-contoso-telemetry`;
const APPSETTINGS = `${SITE}/config/appsettings`;
const AUTHSETTINGS = `${SITE}/config/authsettingsV2`;
const KV_SECRET = `${RG}/providers/Microsoft.KeyVault/vaults/kvcontoso/secrets/telemetry-upload-signing-key`;

describe('resourceTypeOf', () => {
  test('reads a top-level type', () => {
    assert.equal(resourceTypeOf(SITE), 'Microsoft.Web/sites');
  });

  test('reads a nested child type', () => {
    assert.equal(resourceTypeOf(APPSETTINGS), 'Microsoft.Web/sites/config');
    assert.equal(resourceTypeOf(KV_SECRET), 'Microsoft.KeyVault/vaults/secrets');
  });

  test('returns empty for something that is not a resource id', () => {
    assert.equal(resourceTypeOf('not-an-id'), '');
    assert.equal(resourceTypeOf(undefined), '');
  });
});

describe('flattenDelta', () => {
  test('joins nested child paths onto their parent', () => {
    const leaves = flattenDelta([
      {
        path: 'properties.siteConfig',
        propertyChangeType: 'Modify',
        children: [
          { path: 'alwaysOn', propertyChangeType: 'Modify', before: false, after: true },
        ],
      },
    ]);

    assert.deepEqual(leaves.map((l) => l.path), ['properties.siteConfig.alwaysOn']);
    assert.equal(leaves[0].before, false);
    assert.equal(leaves[0].after, true);
  });

  test('drops NoEffect nodes and everything under them', () => {
    const leaves = flattenDelta([
      { path: 'properties.provisioningState', propertyChangeType: 'NoEffect', after: 'Succeeded' },
      {
        path: 'properties.ignored',
        propertyChangeType: 'NoEffect',
        children: [{ path: 'child', propertyChangeType: 'Modify', before: 1, after: 2 }],
      },
      { path: 'properties.real', propertyChangeType: 'Modify', before: 1, after: 2 },
    ]);

    assert.deepEqual(leaves.map((l) => l.path), ['properties.real']);
  });

  test('tolerates a missing or non-array delta', () => {
    assert.deepEqual(flattenDelta(undefined), []);
    assert.deepEqual(flattenDelta(null), []);
  });
});

describe('evaluate - the cases this feature exists for', () => {
  test('alwaysOn flipped away from the template is reported as drift', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: SITE,
          delta: [
            {
              path: 'properties.siteConfig',
              propertyChangeType: 'Modify',
              children: [
                { path: 'alwaysOn', propertyChangeType: 'Modify', before: false, after: true },
              ],
            },
          ],
        },
      ],
    };

    const { drift } = evaluate(whatIf, shippedRules);

    assert.equal(drift.length, 1);
    assert.equal(drift[0].resourceType, 'Microsoft.Web/sites');
    assert.deepEqual(drift[0].properties.map((p) => p.path), ['properties.siteConfig.alwaysOn']);
  });

  test('an emptied EasyAuth allowedApplications array is reported as drift', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: AUTHSETTINGS,
          delta: [
            {
              path: 'properties.identityProviders.azureActiveDirectory.validation',
              propertyChangeType: 'Modify',
              children: [
                {
                  path: 'defaultAuthorizationPolicy.allowedApplications',
                  propertyChangeType: 'Array',
                  before: [],
                  after: ['11111111-1111-1111-1111-111111111111'],
                },
              ],
            },
          ],
        },
      ],
    };

    const { drift } = evaluate(whatIf, shippedRules);

    assert.equal(drift.length, 1);
    assert.equal(
      drift[0].properties[0].path,
      'properties.identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications');
  });
});

describe('evaluate - suppression', () => {
  test("a Key Vault secret's unreadable value is suppressed", () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: KV_SECRET,
          delta: [{ path: 'properties.value', propertyChangeType: 'Modify', before: null, after: 'placeholder' }],
        },
      ],
    };

    const { drift, suppressed } = evaluate(whatIf, shippedRules);

    assert.equal(drift.length, 0, 'what-if can never read a secret value, so this is not drift');
    assert.equal(suppressed.length, 1);
    assert.match(suppressed[0].reason, /cannot read/i);
  });

  test('a platform-injected WEBSITE_ app setting being removed is suppressed', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: APPSETTINGS,
          delta: [
            { path: 'properties.WEBSITE_ENABLE_SYNC_UPDATE_SITE', propertyChangeType: 'Delete', before: '1' },
          ],
        },
      ],
    };

    const { drift } = evaluate(whatIf, shippedRules);
    assert.equal(drift.length, 0);
  });

  test('a WEBSITE_ app setting that the template owns being MODIFIED is still reported', () => {
    // Regression guard. The Delete-only rule above must not widen into "ignore all WEBSITE_*",
    // or a drifted WEBSITE_RUN_FROM_PACKAGE / WEBSITE_AAD_ENABLE_MISE would go unnoticed.
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: APPSETTINGS,
          delta: [
            { path: 'properties.WEBSITE_AAD_ENABLE_MISE', propertyChangeType: 'Modify', before: 'false', after: 'true' },
          ],
        },
      ],
    };

    const { drift } = evaluate(whatIf, shippedRules);

    assert.equal(drift.length, 1);
    assert.equal(drift[0].properties[0].path, 'properties.WEBSITE_AAD_ENABLE_MISE');
  });

  test('a resource whose every difference is suppressed is not drift', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: KV_SECRET,
          delta: [
            { path: 'properties.value', propertyChangeType: 'Modify', before: null, after: 'x' },
            { path: 'properties.attributes.enabled', propertyChangeType: 'Modify', before: true, after: true },
          ],
        },
      ],
    };

    const { drift, suppressed } = evaluate(whatIf, shippedRules);
    assert.equal(drift.length, 0);
    assert.equal(suppressed.length, 2);
  });

  test('NoChange and Ignore are informational, not drift', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        { changeType: 'NoChange', resourceId: SITE },
        { changeType: 'Ignore', resourceId: `${RG}/providers/Microsoft.Insights/components/appi-contoso` },
      ],
    };

    const { drift, informational } = evaluate(whatIf, shippedRules);
    assert.equal(drift.length, 0);
    assert.equal(informational.length, 2);
  });

  test('a resource the template declares but that does not exist is drift', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [{ changeType: 'Create', resourceId: `${RG}/providers/Microsoft.Web/sites/app-missing` }],
    };

    const { drift } = evaluate(whatIf, shippedRules);
    assert.equal(drift.length, 1);
    assert.equal(drift[0].changeType, 'Create');
  });

  test('accepts a bare array of changes as well as the full what-if envelope', () => {
    const changes = [
      {
        changeType: 'Modify',
        resourceId: SITE,
        delta: [{ path: 'properties.siteConfig.alwaysOn', propertyChangeType: 'Modify', before: false, after: true }],
      },
    ];

    assert.equal(evaluate(changes, shippedRules).drift.length, 1);
  });

  test('no rules at all means nothing is suppressed', () => {
    const whatIf = {
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: KV_SECRET,
          delta: [{ path: 'properties.value', propertyChangeType: 'Modify', before: null, after: 'x' }],
        },
      ],
    };

    assert.equal(evaluate(whatIf, {}).drift.length, 1);
    assert.equal(evaluate(whatIf, { rules: [] }).drift.length, 1);
  });
});

describe('summary output', () => {
  test('does not publish the subscription id or resource group name', () => {
    // The job summary is world-readable on this public repository.
    const summary = buildSummary(evaluate({
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: SITE,
          delta: [{ path: 'properties.siteConfig.alwaysOn', propertyChangeType: 'Modify', before: false, after: true }],
        },
      ],
    }, shippedRules));

    assert.doesNotMatch(summary, /subscriptions/);
    assert.doesNotMatch(summary, /resourceGroups/);
    assert.match(summary, /alwaysOn/);
  });

  test('publishes boolean values, because they cannot carry a secret and they are the finding', () => {
    const summary = buildSummary(evaluate({
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: SITE,
          delta: [{ path: 'properties.siteConfig.alwaysOn', propertyChangeType: 'Modify', before: false, after: true }],
        },
      ],
    }, shippedRules));

    assert.match(summary, /`false`/);
    assert.match(summary, /`true`/);
  });

  test('REDACTS string values - what-if returns full resource payloads', () => {
    // Regression guard. FullResourcePayloads includes app settings, so an unredacted summary would
    // publish the Application Insights connection string (instrumentation key and all) to anyone.
    const connectionString =
      'InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/';

    const summary = buildSummary(evaluate({
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: APPSETTINGS,
          delta: [{
            path: 'properties.APPLICATIONINSIGHTS_CONNECTION_STRING',
            propertyChangeType: 'Modify',
            before: connectionString,
            after: 'InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://example.invalid/',
          }],
        },
      ],
    }, shippedRules));

    assert.doesNotMatch(summary, /InstrumentationKey/,
      'a connection string must never reach the job summary');
    assert.doesNotMatch(summary, /IngestionEndpoint/);
    assert.match(summary, /APPLICATIONINSIGHTS_CONNECTION_STRING/,
      'the property path is still reported, so the drift is actionable');
    assert.match(summary, /redacted/);
  });

  test('redacts array and object values but reports their size', () => {
    const summary = buildSummary(evaluate({
      status: 'Succeeded',
      changes: [
        {
          changeType: 'Modify',
          resourceId: AUTHSETTINGS,
          delta: [{
            path: 'properties.identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications',
            propertyChangeType: 'Array',
            before: [],
            after: ['11111111-1111-1111-1111-111111111111'],
          }],
        },
      ],
    }, shippedRules));

    assert.doesNotMatch(summary, /11111111-1111-1111-1111-111111111111/,
      'a client id must not be published');
    assert.match(summary, /allowedApplications/);
    assert.match(summary, /0 items/);
    assert.match(summary, /1 item\b/);
  });

  test('shortenResourceId keeps only the provider-relative part', () => {
    assert.equal(shortenResourceId(SITE), 'Microsoft.Web/sites/app-contoso-telemetry');
  });

  test('says so plainly when there is no drift', () => {
    const summary = buildSummary(evaluate({ status: 'Succeeded', changes: [] }, shippedRules));
    assert.match(summary, /No drift/);
  });
});

describe('shipped ignore rules', () => {
  test('every rule carries a reason', () => {
    for (const rule of shippedRules.rules) {
      assert.ok(rule.reason && rule.reason.length > 20, `rule ${JSON.stringify(rule)} needs a real reason`);
    }
  });

  test('every propertyPath is a valid regular expression', () => {
    for (const rule of shippedRules.rules) {
      if (rule.propertyPath) {
        assert.doesNotThrow(() => new RegExp(rule.propertyPath), `bad regex in ${rule.reason}`);
      }
    }
  });

  test('no rule suppresses a whole resource type', () => {
    // A whole-resource rule is a blind spot. If one is ever genuinely needed, delete this test
    // deliberately rather than letting a rule slip in unnoticed.
    const blunt = shippedRules.rules.filter((r) => !r.propertyPath);
    assert.deepEqual(blunt, []);
  });
});
