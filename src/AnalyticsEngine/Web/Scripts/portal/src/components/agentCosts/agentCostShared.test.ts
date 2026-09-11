import { describe, expect, it } from 'vitest';

import {
  DASH,
  detailRowsToCsv,
  formatCount,
  formatCredits,
  formatMoney,
  windowOfDays,
} from './agentCostShared';
import type { AgentCostDetailRow } from '../../types/agentCosts';

describe('formatCredits', () => {
  it('keeps small fractional credits visible instead of rounding them to zero', () => {
    // A single billing slice can be a small fraction of one credit. Rounding to whole numbers would
    // display real spend as "0", which reads as "this agent cost nothing".
    expect(formatCredits(0.000125)).not.toBe('0');
    expect(formatCredits(0.5)).toBe('0.5');
  });

  it('drops the noise from large totals', () => {
    expect(formatCredits(12345.6789)).toBe((12346).toLocaleString());
  });

  it('shows a measured zero as zero, and an absent value as a dash', () => {
    // These are different facts and must not render the same.
    expect(formatCredits(0)).toBe('0');
    expect(formatCredits(null)).toBe(DASH);
    expect(formatCredits(undefined)).toBe(DASH);
  });
});

describe('formatMoney', () => {
  it('always shows the currency, so two amounts are never silently added', () => {
    expect(formatMoney(12.5, 'GBP')).toContain('GBP');
    expect(formatMoney(12.5, 'USD')).toContain('USD');
  });

  it('renders an absent amount as a dash rather than zero', () => {
    expect(formatMoney(null, 'GBP')).toBe(DASH);
  });
});

describe('formatCount', () => {
  it('distinguishes a measured zero from an unknown', () => {
    expect(formatCount(0)).toBe('0');
    expect(formatCount(null)).toBe(DASH);
  });
});

describe('windowOfDays', () => {
  it('returns an inclusive window ending today', () => {
    const now = new Date(Date.UTC(2026, 8, 9)); // 9 Sep 2026
    expect(windowOfDays(7, now)).toEqual({ from: '2026-09-03', to: '2026-09-09' });
  });

  it('handles a single-day window', () => {
    const now = new Date(Date.UTC(2026, 8, 9));
    expect(windowOfDays(1, now)).toEqual({ from: '2026-09-09', to: '2026-09-09' });
  });

  it('is computed in UTC, so the window does not shift with the browser time zone', () => {
    // A local-time implementation would return the previous day for anyone west of UTC late in the day.
    const lateUtc = new Date(Date.UTC(2026, 8, 9, 23, 30));
    expect(windowOfDays(1, lateUtc).to).toBe('2026-09-09');
  });
});

describe('detailRowsToCsv', () => {
  const row = (over: Partial<AgentCostDetailRow> = {}): AgentCostDetailRow => ({
    usageDate: '2026-09-03T00:00:00Z',
    environmentId: 'env-1',
    environmentName: 'Contoso Production',
    agentId: 'agent-1',
    agentName: 'Contoso Helpdesk',
    harness: 'StandardOrCopilotChat',
    featureName: 'Generative answer',
    channelId: null,
    llmModel: 'gpt-4o',
    toolInvoked: null,
    knowledgeSources: null,
    billedCredits: 12.5,
    nonBilledCredits: 1,
    distinctUsers: 4,
    ...over,
  });

  it('writes a header and one line per row', () => {
    const csv = detailRowsToCsv([row(), row({ agentId: 'agent-2' })]);
    const lines = csv.split('\r\n');
    expect(lines).toHaveLength(3);
    expect(lines[0]).toContain('Billed credits');
  });

  it('quotes a value containing a comma so the columns do not shift', () => {
    // Agent names are free text from a customer tenant and routinely contain commas.
    const csv = detailRowsToCsv([row({ agentName: 'Helpdesk, EMEA' })]);
    expect(csv).toContain('"Helpdesk, EMEA"');
  });

  it('escapes embedded quotes by doubling them', () => {
    const csv = detailRowsToCsv([row({ agentName: 'The "Big" Bot' })]);
    expect(csv).toContain('"The ""Big"" Bot"');
  });

  it('preserves non-Latin agent names', () => {
    const csv = detailRowsToCsv([row({ agentName: 'Καλημέρα κόσμε' })]);
    expect(csv).toContain('Καλημέρα κόσμε');
  });

  it('writes an absent dimension as an empty cell, not the word null', () => {
    const csv = detailRowsToCsv([row({ toolInvoked: null, distinctUsers: null })]);
    expect(csv).not.toContain('null');
  });

  it('exports the date as a plain day so a spreadsheet does not reinterpret it', () => {
    const csv = detailRowsToCsv([row()]);
    expect(csv).toContain('2026-09-03');
    expect(csv).not.toContain('2026-09-03T00:00:00Z');
  });
});
