// Unit tests for the ARM parameters builder. Run with: node --test

import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { buildParameters, SECRET_PLACEHOLDER } from './build-parameters.mjs';

const TENANT = '00000000-0000-0000-0000-000000000000';

// Synthetic values only.
const SUPPLIED = JSON.stringify({
  location: 'westeurope',
  webAppName: 'app-contoso-telemetry',
  namePrefix: 'contoso-tel',
  vnetAddressPrefix: '10.0.0.0/16',
  appIntegrationSubnetPrefix: '10.0.1.0/24',
  privateEndpointSubnetPrefix: '10.0.2.0/24',
  azureAdClientId: '11111111-1111-1111-1111-111111111111',
});

describe('buildParameters', () => {
  test('produces an ARM parameters file', () => {
    const built = buildParameters(SUPPLIED, TENANT);

    assert.equal(built.contentVersion, '1.0.0.0');
    assert.match(built.$schema, /deploymentParameters\.json#$/);
    assert.deepEqual(built.parameters.location, { value: 'westeurope' });
    assert.deepEqual(built.parameters.vnetAddressPrefix, { value: '10.0.0.0/16' });
  });

  test('adds the tenant id from the environment rather than the repository', () => {
    const built = buildParameters(SUPPLIED, TENANT);
    assert.deepEqual(built.parameters.azureAdTenantId, { value: TENANT });
  });

  test('supplies a placeholder for the secure telemetrySecret parameter', () => {
    const built = buildParameters(SUPPLIED, TENANT);
    assert.deepEqual(built.parameters.telemetrySecret, { value: SECRET_PLACEHOLDER });
  });

  test('covers every non-secure parameter resources.bicep declares', () => {
    // Guards against a parameter being added to the template and the drift check silently failing
    // on every run afterwards.
    const here = dirname(fileURLToPath(import.meta.url));
    const bicep = readFileSync(join(here, '..', 'resources.bicep'), 'utf8');

    const declared = [...bicep.matchAll(/^param\s+(\w+)\s/gm)].map((m) => m[1]);
    const provided = Object.keys(buildParameters(SUPPLIED, TENANT).parameters);

    // `tags` has a default in the template only via main.bicep, so it is optional at RG scope.
    const missing = declared.filter((name) => name !== 'tags' && !provided.includes(name));

    assert.deepEqual(missing, [],
      `TELEMETRY_INFRA_PARAMETERS (and drift/README.md) must be updated for: ${missing.join(', ')}`);
  });

  test('rejects input that is not JSON', () => {
    assert.throws(() => buildParameters('{not json', TENANT), /not valid JSON/);
  });

  test('rejects input that is not an object', () => {
    assert.throws(() => buildParameters('["a"]', TENANT), /must be a JSON object/);
    assert.throws(() => buildParameters('"a"', TENANT), /must be a JSON object/);
    assert.throws(() => buildParameters('null', TENANT), /must be a JSON object/);
  });

  test('rejects empty input', () => {
    assert.throws(() => buildParameters('', TENANT), /empty/);
    assert.throws(() => buildParameters('   ', TENANT), /empty/);
  });

  test('rejects a missing tenant id', () => {
    assert.throws(() => buildParameters(SUPPLIED, ''), /AZURE_TENANT_ID/);
    assert.throws(() => buildParameters(SUPPLIED, undefined), /AZURE_TENANT_ID/);
  });
});
