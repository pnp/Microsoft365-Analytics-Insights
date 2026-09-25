import { describe, expect, it } from 'vitest';

import { loadCatalog, translateStatic } from '../i18n';
import { agentCostDimensionLabel } from './AgentCostsPage';

describe('agentCostDimensionLabel', () => {
  it('translates the missing Azure cost dimension label in Spanish', async () => {
    await loadCatalog('es');

    expect(agentCostDimensionLabel(null, null, (key, values) => translateStatic('es', key, values))).toBe('No informado');
  });
});
