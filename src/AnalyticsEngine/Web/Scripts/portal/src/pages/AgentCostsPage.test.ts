import { describe, expect, it } from 'vitest';

import { loadCatalog, translateStatic } from '../i18n';
import { agentCostDimensionLabel, importFailureWarning } from './AgentCostsPage';

describe('agentCostDimensionLabel', () => {
  it('translates the missing Azure cost dimension label in Spanish', async () => {
    await loadCatalog('es');

    expect(agentCostDimensionLabel(null, null, (key, values) => translateStatic('es', key, values))).toBe('No informado');
  });

  describe('importFailureWarning', () => {
    it('renders the known import-failing conditions from the catalog', async () => {
      await loadCatalog('es');

      const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
        translateStatic('es', key, values);
      expect(importFailureWarning(es, 'copilotStudio')).toBe('La importación de créditos de Copilot Studio está fallando.');
      expect(importFailureWarning(es, 'azure')).toBe('La importación de costes de Azure está fallando.');
    });
  });
});
