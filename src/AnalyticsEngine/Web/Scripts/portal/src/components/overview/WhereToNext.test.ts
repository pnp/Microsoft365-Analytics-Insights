import { describe, it, expect } from 'vitest';
import { visiblePointers } from './WhereToNext';

describe('visiblePointers', () => {
  it('always offers the pages that work off whatever is imported', () => {
    const keys = visiblePointers([]).map((p) => p.key);
    expect(keys).toEqual(['reports', 'licence-activity', 'health', 'configuration']);
  });

  it('offers Copilot Adoption for either Copilot source', () => {
    expect(visiblePointers(['copilotInteractions']).map((p) => p.key)).toContain('copilot-adoption');
    expect(visiblePointers(['copilotAiInteractions']).map((p) => p.key)).toContain('copilot-adoption');
  });

  it('offers Agent costs for either billing source', () => {
    expect(visiblePointers(['copilotStudioCreditDays']).map((p) => p.key)).toContain('agent-costs');
    expect(visiblePointers(['azureCostDays']).map((p) => p.key)).toContain('agent-costs');
  });

  it('keeps the declared order when several gates open', () => {
    const keys = visiblePointers(['copilotInteractions', 'dlpMatches', 'teams']).map((p) => p.key);
    expect(keys).toEqual([
      'reports',
      'copilot-adoption',
      'licence-activity',
      'dlp',
      'teams-permissions',
      'health',
      'configuration',
    ]);
  });
});
