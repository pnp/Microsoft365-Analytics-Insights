#!/usr/bin/env node
// Builds the ARM parameters file the drift check feeds to `az deployment group what-if`.
//
// The repository variable holds the readable {"name": value} form, because a human has to set it;
// ARM wants {"name": {"value": ...}}. Doing the conversion here rather than in a jq one-liner keeps
// it unit-testable - a malformed parameters file otherwise fails inside the Azure job, which is the
// slowest and least informative place to find out.
//
// Usage:
//   node build-parameters.mjs <output-path>
//
// Reads TELEMETRY_INFRA_PARAMETERS (JSON object) and AZURE_TENANT_ID from the environment.
// Exit codes: 0 = written, 2 = bad input.

import { writeFileSync } from 'node:fs';

/**
 * The @secure() telemetrySecret parameter. Its real value lives only in Key Vault, and what-if
 * cannot read the deployed secret to compare against in any case, so a placeholder is passed and
 * the resulting difference is suppressed by drift-ignore.json.
 */
export const SECRET_PLACEHOLDER = 'placeholder-not-compared';

export function buildParameters(rawInfraParameters, tenantId) {
  if (!rawInfraParameters || !rawInfraParameters.trim()) {
    throw new Error('TELEMETRY_INFRA_PARAMETERS is empty.');
  }

  let supplied;
  try {
    supplied = JSON.parse(rawInfraParameters);
  } catch (error) {
    throw new Error(`TELEMETRY_INFRA_PARAMETERS is not valid JSON: ${error.message}`);
  }

  if (supplied === null || typeof supplied !== 'object' || Array.isArray(supplied)) {
    throw new Error('TELEMETRY_INFRA_PARAMETERS must be a JSON object of parameter names to values.');
  }

  if (!tenantId) {
    throw new Error('AZURE_TENANT_ID is not set.');
  }

  const merged = {
    ...supplied,
    azureAdTenantId: tenantId,
    telemetrySecret: SECRET_PLACEHOLDER,
  };

  const parameters = {};
  for (const [name, value] of Object.entries(merged)) {
    parameters[name] = { value };
  }

  return {
    $schema: 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#',
    contentVersion: '1.0.0.0',
    parameters,
  };
}

function main() {
  const [outputPath] = process.argv.slice(2);
  if (!outputPath) {
    console.error('Usage: node build-parameters.mjs <output-path>');
    process.exit(2);
  }

  try {
    const built = buildParameters(process.env.TELEMETRY_INFRA_PARAMETERS, process.env.AZURE_TENANT_ID);
    writeFileSync(outputPath, JSON.stringify(built, null, 2), 'utf8');
    // Names only - the values identify the environment and this runs in a public repository.
    console.log(`Wrote ${Object.keys(built.parameters).length} parameter(s): ${Object.keys(built.parameters).sort().join(', ')}`);
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(2);
  }
}

if (process.argv[1] && import.meta.url === `file://${process.argv[1].replace(/\\/g, '/')}`) {
  main();
}
