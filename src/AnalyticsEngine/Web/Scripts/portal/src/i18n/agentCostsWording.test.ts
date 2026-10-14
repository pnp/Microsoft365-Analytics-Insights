import { describe, expect, it } from 'vitest';
import { agentCosts as en } from './catalog/en/agentCosts';
import es from './catalog/es/agentCosts';

describe('Consumed credits are a reference total, not an allowance', () => {
  const key = 'agentCosts.availability.message.perAgentFromPerUser';

  it('keeps the English consumption explanation authoritative', () => {
    expect(en[key]).toContain('total consumed credits');
    expect(en[key]).toContain('the authoritative total');
  });

  it('describes the same consumption reference in Spanish, not an authorised quota', () => {
    // "Autorizado" means permitted, not authoritative. Placeholder/key parity cannot catch this.
    expect(es[key]).toContain('total de créditos consumidos del inquilino');
    expect(es[key]).toContain('es el total de referencia');
    expect(es[key]).not.toMatch(/total autorizado/i);
  });
});
