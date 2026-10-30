import assert from 'node:assert/strict';
import { test } from 'node:test';
import { deploymentResource, deploymentTarget } from './telemetry-deployment.mjs';

test('manual runs default to build-only and pull requests never deploy', () => {
  assert.equal(deploymentTarget('workflow_dispatch', 'refs/heads/dev'), 'none');
  assert.equal(deploymentTarget('workflow_dispatch', 'refs/heads/main'), 'none');
  assert.equal(deploymentTarget('pull_request', 'refs/pull/1/merge', 'production'), 'none');
  assert.equal(deploymentTarget('push', 'refs/heads/dev'), 'none');
  assert.equal(deploymentTarget('push', 'refs/heads/main'), 'production');
});

test('explicit deployments are restricted to the matching long-lived branch', () => {
  assert.equal(deploymentTarget('workflow_dispatch', 'refs/heads/dev', 'development'), 'development');
  assert.equal(deploymentTarget('workflow_dispatch', 'refs/heads/main', 'production'), 'production');
  for (const [ref, target] of [
    ['refs/heads/dev', 'production'], ['refs/heads/main', 'development'],
    ['refs/heads/feature/example', 'production'], ['refs/heads/dev', 'unexpected'],
  ]) {
    assert.throws(() => deploymentTarget('workflow_dispatch', ref, target), /deploys require/);
  }
});

test('development cannot fall back to, or alias, production', () => {
  const config = {
    productionApp: 'contoso-telemetry', productionGroup: 'contoso-production',
    developmentApp: 'contoso-telemetry-dev', developmentGroup: 'contoso-development',
  };
  assert.deepEqual(deploymentResource('development', config), { app: config.developmentApp, group: config.developmentGroup });
  assert.deepEqual(deploymentResource('production', config), { app: config.productionApp, group: config.productionGroup });
  assert.throws(() => deploymentResource('development', { ...config, developmentApp: '' }), /not configured/);
  assert.throws(() => deploymentResource('development', { ...config, developmentApp: 'CONTOSO-TELEMETRY' }), /different/);
  assert.throws(() => deploymentResource('none', config), /not configured/);
});
