import { describe, expect, it } from 'vitest';
import { formatCosts } from './CopilotAdoptionPage';

describe('Copilot Adoption idle spend formatting', () => {
  it('distinguishes unknown unassigned spend from not configured and zero', () => {
    expect(formatCosts([], true)).toBe('Unknown');
    expect(formatCosts([], false)).toBe('not configured');
    expect(formatCosts([{ currency: 'GBP', cost: 0 }], false)).toBe('GBP 0');
    expect(formatCosts([{ currency: 'GBP', cost: 30 }], true)).toBe('GBP 30; Unknown unassigned');
  });
});
