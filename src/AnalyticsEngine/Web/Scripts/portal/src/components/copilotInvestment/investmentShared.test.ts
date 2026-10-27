import { afterEach, describe, expect, it } from 'vitest';
import { setActiveLanguage, translateActive } from '../../i18n/runtime';
import { loadCatalog, type TFunction } from '../../i18n';
import type { InvestmentAction, InvestmentFinding } from '../../types/copilotInvestment';
import { actionText, findingText, formatMoney } from './investmentShared';

const t = translateActive as unknown as TFunction;

function action(code: string, people: number, extra: Partial<InvestmentAction> = {}): InvestmentAction {
  return {
    code,
    people,
    monthlyAmount: null,
    credits: 1200,
    roiBaseAfter: null,
    metric: 2.5,
    signal: null,
    flag: null,
    effect: 'none',
    assumesMonthlyBalances: false,
    ...extra,
  };
}

function finding(code: string, values: Record<string, number | null>): InvestmentFinding {
  return { code, tone: 'warning', values };
}

describe('investment wording for one person and for none', () => {
  afterEach(() => setActiveLanguage('en'));

  it('speaks of one person in the singular, in both languages', async () => {
    expect(actionText(t, action('raiseLimitEvidenced', 1))?.body).toBe(
      'One person with strong, regular Copilot use is at or near their credit limit (1,200 credits).',
    );
    expect(actionText(t, action('raiseLimitEvidenced', 3))?.body).toBe(
      '3 people with strong, regular Copilot use are at or near their credit limit (1,200 credits).',
    );
    expect(findingText(t, finding('highSpendThinEvidence', { people: 1, credits: 900 }), 'EUR')).toMatch(/^One person holds one of the highest/);

    await loadCatalog('es');
    setActiveLanguage('es');
    expect(actionText(t, action('reclaimIdle', 1))?.body).toMatch(/^Un titular no mostró uso/);
    expect(findingText(t, finding('limitBlocked', { people: 1, evidenced: 0 }), 'EUR')).toBe(
      'Una persona ha agotado su límite de créditos sin un uso propio intenso y regular.',
    );
  });

  it('leaves out a count of nobody rather than printing a zero clause', () => {
    expect(findingText(t, finding('limitBlocked', { people: 7, evidenced: 0 }), 'EUR')).toBe(
      '7 people have used their whole credit limit; none of them shows strong, regular use.',
    );
    expect(findingText(t, finding('limitBlocked', { people: 7, evidenced: 1 }), 'EUR')).toBe(
      '7 people have used their whole credit limit; one of them shows strong, regular use.',
    );
    expect(findingText(t, finding('idleSeats', { idle: 61, share: 0.17, reclaimable: 0, review: 61, tooNew: 0, monthly: null, roiAfter: null }), 'EUR')).toBe(
      '61 seat holders (17%) did not use Copilot at all in the period; 61 of them need a review before any seat is reassigned.',
    );
  });

  it('shows whole currency units from 100 up and cents below it', () => {
    expect(formatMoney(480, 'EUR')).toBe('\u20ac480');
    expect(formatMoney(43.72, 'EUR')).toBe('\u20ac43.72');
  });
});
