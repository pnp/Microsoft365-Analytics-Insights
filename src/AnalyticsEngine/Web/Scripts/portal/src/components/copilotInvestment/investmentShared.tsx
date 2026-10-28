import { makeStyles, mergeClasses, tokens, Text } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { formatDateParts, formatNumber, type TFunction, type TranslationKey } from '../../i18n';
import type { InvestmentAction, InvestmentFinding, InvestmentTrustReason } from '../../types/copilotInvestment';

/**
 * Formatting, code-to-text maps and small visuals shared by the Copilot investment tabs.
 *
 * The server sends facts and stable codes; every sentence is written here from the catalog, so the page reads
 * the same in every language and a code the server adds without a sentence is caught by
 * `serverAuthoredText.test.ts` rather than shipping as an identifier.
 */

/** Whole units from 100 up, so amounts side by side read alike; cents only where they change the reading. */
function moneyDigits(value: number): number {
  return Math.abs(value) >= 100 ? 0 : 2;
}

/** Money in the assumptions' currency, in the reader's number conventions. */
export function formatMoney(value: number | null | undefined, currency: string | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '\u2014';
  const digits = moneyDigits(value);
  if (!currency) return formatNumber(value, { maximumFractionDigits: digits });
  try {
    return formatNumber(value, { style: 'currency', currency, maximumFractionDigits: digits, minimumFractionDigits: digits });
  } catch {
    // A well-formed code Intl does not know: the number, then the code, rather than no figure at all.
    return `${formatNumber(value, { maximumFractionDigits: digits })}\u00a0${currency}`;
  }
}

/** A price as entered, with enough decimals to show a fraction of a cent. */
export function formatPrice(value: number | null | undefined, currency: string | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '\u2014';
  if (!currency) return formatNumber(value, { maximumFractionDigits: 4 });
  try {
    return formatNumber(value, { style: 'currency', currency, maximumFractionDigits: 4 });
  } catch {
    return `${formatNumber(value, { maximumFractionDigits: 4 })}\u00a0${currency}`;
  }
}

/** Credits keep their fractions while small: a balance of 0.4 is not zero. */
export function formatCredits(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '\u2014';
  return formatNumber(value, { maximumFractionDigits: Math.abs(value) < 100 ? 2 : 0 });
}

export function formatShare(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '\u2014';
  return formatNumber(value, { style: 'percent', maximumFractionDigits: value < 0.1 && value > 0 ? 1 : 0 });
}

/** As {@link formatShare}, but a share above zero too small to show is written as below 0.1%, never rounded to 0%. */
export function formatShareAboveZero(value: number | null | undefined): string {
  if (value !== null && value !== undefined && value > 0 && value < 0.0005) return `<${formatShare(0.001)}`;
  return formatShare(value);
}

export function formatDecimal(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '\u2014';
  return formatNumber(value, { maximumFractionDigits: digits });
}

/**
 * A return multiple to two decimals: one decimal hides exactly the movements a reader is weighing - reclaiming
 * seats moving 0.80x to 0.84x would otherwise read as no change at all.
 */
export function formatMultiple(t: TFunction, value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return t('copilotInvestment.value.notAvailable');
  return t('copilotInvestment.format.multiple', { value: formatNumber(value, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) });
}

/** The change between two multiples, signed, for "what this lever does to the base case". */
export function formatMultipleDelta(t: TFunction, value: number | null | undefined, base: number | null | undefined): string | null {
  if (value === null || value === undefined || base === null || base === undefined || !Number.isFinite(value) || !Number.isFinite(base)) return null;
  return t('copilotInvestment.format.multiple', {
    value: formatNumber(value - base, { minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'exceptZero' }),
  });
}

/** Money with its sign shown, for a net figure that can be a surplus or a shortfall. */
export function formatSignedMoney(value: number | null | undefined, currency: string | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '\u2014';
  const digits = moneyDigits(value);
  if (!currency) return formatNumber(value, { maximumFractionDigits: digits, signDisplay: 'exceptZero' });
  try {
    return formatNumber(value, { style: 'currency', currency, maximumFractionDigits: digits, minimumFractionDigits: digits, signDisplay: 'exceptZero' });
  } catch {
    return `${formatNumber(value, { maximumFractionDigits: digits, signDisplay: 'exceptZero' })}\u00a0${currency}`;
  }
}

export function formatMinutes(t: TFunction, value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return t('copilotInvestment.value.notAvailable');
  return t('copilotInvestment.format.minutes', { value: formatNumber(value, { maximumFractionDigits: 1 }) });
}

/** A UTC date or instant as a short date in the reader's language. */
/**
 * A server date as a moment in UTC. The API sends the dates it reads from SQL without an offset ("2026-10-26T00:00:00"),
 * which a browser would otherwise read as its own local midnight: a day early anywhere east of UTC. Every date these
 * reports send is UTC, so one without an offset is read as UTC too.
 */
export function parseUtcDate(iso: string): Date {
  return new Date(/T[^Zz+-]*$/.test(iso) ? `${iso}Z` : iso);
}

export function formatShortDate(iso: string | null | undefined): string {
  if (!iso) return '\u2014';
  return formatDateParts(parseUtcDate(iso), { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
}

export const SIGNAL_LABEL_KEYS: Record<string, TranslationKey> = {
  supported: 'copilotInvestment.signal.supported',
  plausible: 'copilotInvestment.signal.plausible',
  belowBreakEven: 'copilotInvestment.signal.belowBreakEven',
  noEvidence: 'copilotInvestment.signal.noEvidence',
  tooNewToJudge: 'copilotInvestment.signal.tooNewToJudge',
  strongEvidence: 'copilotInvestment.signal.strongEvidence',
  developing: 'copilotInvestment.signal.developing',
  lightEvidence: 'copilotInvestment.signal.lightEvidence',
  expandCandidate: 'copilotInvestment.signal.expandCandidate',
  metered: 'copilotInvestment.signal.metered',
  observedUnlicensed: 'copilotInvestment.signal.observedUnlicensed',
  otherPeriod: 'copilotInvestment.signal.otherPeriod',
  notAnalysed: 'copilotInvestment.signal.notAnalysed',
};

export const FLAG_LABEL_KEYS: Record<string, TranslationKey> = {
  limitExhausted: 'copilotInvestment.flag.limitExhausted',
  nearLimit: 'copilotInvestment.flag.nearLimit',
  notEntitled: 'copilotInvestment.flag.notEntitled',
  highSpendThinEvidence: 'copilotInvestment.flag.highSpendThinEvidence',
  creditsPartial: 'copilotInvestment.flag.creditsPartial',
  otherUnit: 'copilotInvestment.flag.otherUnit',
  reclaimExcluded: 'copilotInvestment.flag.reclaimExcluded',
  accountDisabled: 'copilotInvestment.flag.accountDisabled',
  reclaimCandidate: 'copilotInvestment.flag.reclaimCandidate',
  idleReview: 'copilotInvestment.flag.idleReview',
  belowBaseBreakEven: 'copilotInvestment.flag.belowBaseBreakEven',
  expandEvidenced: 'copilotInvestment.flag.expandEvidenced',
  limitBlockedEvidenced: 'copilotInvestment.flag.limitBlockedEvidenced',
  meteredAboveSeat: 'copilotInvestment.flag.meteredAboveSeat',
};

export const BAND_LABEL_KEYS: Record<string, TranslationKey> = {
  NeverUsed: 'copilotInvestment.band.NeverUsed',
  Dormant: 'copilotInvestment.band.Dormant',
  Trialling: 'copilotInvestment.band.Trialling',
  Developing: 'copilotInvestment.band.Developing',
  Established: 'copilotInvestment.band.Established',
  Champion: 'copilotInvestment.band.Champion',
};

export const EVIDENCE_LABEL_KEYS: Record<string, TranslationKey> = {
  noActivity: 'copilotInvestment.person.evidence.noActivity',
  regularHabit: 'copilotInvestment.person.evidence.regularHabit',
  occasional: 'copilotInvestment.person.evidence.occasional',
  rare: 'copilotInvestment.person.evidence.rare',
  multiApp: 'copilotInvestment.person.evidence.multiApp',
  agentUse: 'copilotInvestment.person.evidence.agentUse',
  coworkUse: 'copilotInvestment.person.evidence.coworkUse',
  growing: 'copilotInvestment.person.evidence.growing',
  declining: 'copilotInvestment.person.evidence.declining',
  promptHistory: 'copilotInvestment.person.evidence.promptHistory',
  limitReached: 'copilotInvestment.person.evidence.limitReached',
  studioUsage: 'copilotInvestment.person.evidence.studioUsage',
};

export const REASON_LABEL_KEYS: Record<string, TranslationKey> = {
  allowed: 'copilotInvestment.reason.allowed',
  notEntitled: 'copilotInvestment.reason.notEntitled',
  limitExhausted: 'copilotInvestment.reason.limitExhausted',
  upstreamFailure: 'copilotInvestment.reason.upstreamFailure',
  noApplicablePolicy: 'copilotInvestment.reason.noApplicablePolicy',
};

export const AVAILABILITY_NOTE_KEYS: Record<string, TranslationKey> = {
  userMetadataDisabled: 'copilotInvestment.availability.userMetadataDisabled',
  copilotSourcesDisabled: 'copilotInvestment.availability.copilotSourcesDisabled',
  auditDisabled: 'copilotInvestment.availability.auditDisabled',
  studioDisabled: 'copilotInvestment.availability.studioDisabled',
  creditsDisabled: 'copilotInvestment.availability.creditsDisabled',
};

export const FIELD_LABEL_KEYS: Record<string, TranslationKey> = {
  currency: 'copilotInvestment.field.currency',
  seatPricePerMonth: 'copilotInvestment.field.seatPricePerMonth',
  creditPrice: 'copilotInvestment.field.creditPrice',
  studioCreditPrice: 'copilotInvestment.field.studioCreditPrice',
  hourlyCost: 'copilotInvestment.field.hourlyCost',
  'minutesPerActiveDay.conservative': 'copilotInvestment.field.minutesConservative',
  'minutesPerActiveDay.base': 'copilotInvestment.field.minutesBase',
  'minutesPerActiveDay.upside': 'copilotInvestment.field.minutesUpside',
  'realisation.conservative': 'copilotInvestment.field.realisationConservative',
  'realisation.base': 'copilotInvestment.field.realisationBase',
  'realisation.upside': 'copilotInvestment.field.realisationUpside',
  note: 'copilotInvestment.field.note',
};

export const PROVENANCE_LABEL_KEYS: Record<string, TranslationKey> = {
  tenant: 'copilotInvestment.provenance.tenant',
  scenario: 'copilotInvestment.provenance.scenario',
  missing: 'copilotInvestment.provenance.missing',
};

export const SCENARIO_LABEL_KEYS: Record<string, TranslationKey> = {
  conservative: 'copilotInvestment.scenario.conservative',
  base: 'copilotInvestment.scenario.base',
  upside: 'copilotInvestment.scenario.upside',
};

export const POLICY_STATUS_KEYS: Record<string, TranslationKey> = {
  enabled: 'copilotInvestment.policies.status.enabled',
  disabled: 'copilotInvestment.policies.status.disabled',
  provisioning: 'copilotInvestment.policies.status.provisioning',
  provisioningFailed: 'copilotInvestment.policies.status.provisioningFailed',
};

export const BILLING_METHOD_KEYS: Record<string, TranslationKey> = {
  prepaidCredits: 'copilotInvestment.policies.billing.prepaidCredits',
  payAsYouGo: 'copilotInvestment.policies.billing.payAsYouGo',
  notSpecified: 'copilotInvestment.policies.billing.notSpecified',
};

export const QUADRANT_LABEL_KEYS: Record<string, TranslationKey> = {
  highSpendStrongEvidence: 'copilotInvestment.quadrants.highSpendHighEvidence',
  highSpendThinEvidence: 'copilotInvestment.quadrants.highSpendLowEvidence',
  lowSpendStrongEvidence: 'copilotInvestment.quadrants.lowSpendHighEvidence',
  lowSpendThinEvidence: 'copilotInvestment.quadrants.lowSpendLowEvidence',
};

export const IMPACT_LABEL_KEYS: Record<string, TranslationKey> = {
  low: 'copilotInvestment.impact.low',
  medium: 'copilotInvestment.impact.medium',
  info: 'copilotInvestment.impact.info',
};

/** The questions confidence is answered for, in the order the page shows them. */
export const TRUST_AREAS = ['evidence', 'cost', 'assumptions', 'balances'] as const;

export const TRUST_AREA_LABEL_KEYS: Record<string, TranslationKey> = {
  evidence: 'copilotInvestment.exec.trust.area.evidence',
  cost: 'copilotInvestment.exec.trust.area.cost',
  assumptions: 'copilotInvestment.exec.trust.area.assumptions',
  balances: 'copilotInvestment.exec.trust.area.balances',
};

export const TRUST_LEVEL_LABEL_KEYS: Record<string, TranslationKey> = {
  high: 'copilotInvestment.exec.trust.level.high',
  medium: 'copilotInvestment.exec.trust.level.medium',
  low: 'copilotInvestment.exec.trust.level.low',
};

/** What each action's monthly amount is. `serverAuthoredText.test.ts` keeps this equal to the C# list. */
export const ACTION_EFFECT_KEYS: Record<string, TranslationKey | null> = {
  freesSeatCost: 'copilotInvestment.action.effect.freesSeatCost',
  idleSeatCost: 'copilotInvestment.action.effect.idleSeatCost',
  valueShortfall: 'copilotInvestment.action.effect.valueShortfall',
  addsSeatCost: 'copilotInvestment.action.effect.addsSeatCost',
  balanceIfMonthly: 'copilotInvestment.action.effect.balanceIfMonthly',
  meteredSpend: 'copilotInvestment.action.effect.meteredSpend',
  none: null,
};

/** Why there is no weekly trend. `serverAuthoredText.test.ts` keeps this equal to the C# list. */
export const TRAJECTORY_UNAVAILABLE_KEYS: Record<string, TranslationKey> = {
  filtered: 'copilotInvestment.exec.trajectory.filtered',
  noAudit: 'copilotInvestment.exec.trajectory.noAudit',
  noData: 'copilotInvestment.exec.trajectory.noData',
};

/** Why value is not modelled; the page words each differently. */
export const VALUE_UNAVAILABLE_REASONS = ['assumptions', 'noMatchedHolders'] as const;

/** A distribution bucket's label from its two edges, in the reader's number format. */
export function bucketLabel(t: TFunction, bucket: { key: string; minDaysPerMonth: number; maxDaysPerMonth: number | null }): string {
  if (bucket.key === 'none') return t('copilotInvestment.exec.distribution.bucket.none');
  if (bucket.maxDaysPerMonth === null) return t('copilotInvestment.exec.distribution.bucket.over', { min: formatDecimal(bucket.minDaysPerMonth) });
  return t('copilotInvestment.exec.distribution.bucket.range', {
    min: formatDecimal(bucket.minDaysPerMonth),
    max: formatDecimal(bucket.maxDaysPerMonth),
  });
}

/** Every action code the calculator can send. `serverAuthoredText.test.ts` keeps this equal to the C# list. */
export const ACTION_CODES = [
  'reclaimIdle',
  'reviewIdle',
  'enableBelowBreakEven',
  'enableLightEvidence',
  'expandEvidenced',
  'expandRecommended',
  'reviewHighSpendThinEvidence',
  'raiseLimitEvidenced',
  'licenceMeteredHeavy',
  'improveCoverage',
  'setAssumptions',
] as const;

/** Every finding code the calculator can send. */
export const FINDING_CODES = [
  'runRate',
  'valueRange',
  'breakEven',
  'idleSeats',
  'evidenceConcentration',
  'creditConcentration',
  'highSpendThinEvidence',
  'expandCandidates',
  'limitBlocked',
  'assumptionsMissing',
  'balancesUnverified',
  'studioTrend',
  'trajectory',
  'noMatchedHolders',
  'noSeats',
] as const;

/** Every trust code the calculator can send. */
export const TRUST_CODES = [
  'adoptionUnavailable',
  'auditMissing',
  'usageReportSignal',
  'figuresIncomplete',
  'holdersNotAnalysed',
  'assumptionsMissing',
  'assumptionsUnavailable',
  'scenarioApplied',
  'assumedTimeValue',
  'seatsCurrentAssignments',
  'balancesUnverified',
  'creditCoverage',
  'creditsStale',
  'creditsPartial',
  'creditsUnresolved',
  'creditsMissing',
  'creditsUnavailable',
  'shortWindow',
  'studioImportFailed',
  'studioUnavailable',
] as const;

function n(values: Record<string, number | null>, name: string): number | null {
  const value = values[name];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

const count = (value: number | null): string => formatNumber(Math.round(value ?? 0));

/** One finding as a sentence, written from its values. Null for a code this build does not know. */
export function findingText(t: TFunction, finding: InvestmentFinding, currency: string | null): string | null {
  const v = finding.values ?? {};
  const money = (name: string): string => formatMoney(n(v, name), currency);
  switch (finding.code) {
    case 'noSeats':
      return t('copilotInvestment.finding.noSeats');
    case 'runRate':
      return n(v, 'costPerActive') === null
        ? t('copilotInvestment.finding.runRateNoActive', { monthly: money('monthly'), holders: count(n(v, 'holders')) })
        : t('copilotInvestment.finding.runRate', { monthly: money('monthly'), holders: count(n(v, 'holders')), costPerActive: money('costPerActive') });
    case 'valueRange':
      return t('copilotInvestment.finding.valueRange', {
        low: money('low'),
        high: money('high'),
        base: money('base'),
        cost: money('cost'),
        roiLow: formatMultiple(t, n(v, 'roiLow')),
        roiHigh: formatMultiple(t, n(v, 'roiHigh')),
      });
    case 'breakEven': {
      const margin = n(v, 'margin');
      if (margin !== null && margin < 0) {
        return t('copilotInvestment.finding.breakEvenShort', { minutes: formatMinutes(t, n(v, 'minutes')), assumed: formatMinutes(t, n(v, 'assumed')) });
      }
      return t('copilotInvestment.finding.breakEven', {
        minutes: formatMinutes(t, n(v, 'minutes')),
        assumed: formatMinutes(t, n(v, 'assumed')),
        margin: formatShare(margin),
        share: formatShare(n(v, 'shareAtOrAbove')),
      });
    }
    case 'assumptionsMissing':
      return t('copilotInvestment.finding.assumptionsMissing', { count: count(n(v, 'missing')) });
    case 'noMatchedHolders':
      return t('copilotInvestment.finding.noMatchedHolders', { otherPeriod: count(n(v, 'otherPeriod')), notAnalysed: count(n(v, 'notAnalysed')) });
    case 'idleSeats':
      return (n(v, 'reclaimable') ?? 0) > 0 && n(v, 'monthly') !== null && n(v, 'roiAfter') !== null
        ? t('copilotInvestment.finding.idleSeatsReclaim', {
            idle: count(n(v, 'idle')),
            share: formatShare(n(v, 'share')),
            reclaimable: count(n(v, 'reclaimable')),
            monthly: money('monthly'),
            roiAfter: formatMultiple(t, n(v, 'roiAfter')),
            review: count(n(v, 'review')),
          })
        : (n(v, 'tooNew') ?? 0) === 0
          ? t('copilotInvestment.finding.idleSeatsNoneNew', { idle: count(n(v, 'idle')), share: formatShare(n(v, 'share')), review: count(n(v, 'review')) })
          : t('copilotInvestment.finding.idleSeats', {
              idle: count(n(v, 'idle')),
              share: formatShare(n(v, 'share')),
              review: count(n(v, 'review')),
              tooNew: count(n(v, 'tooNew')),
            });
    case 'evidenceConcentration':
      return t('copilotInvestment.finding.evidenceConcentration', { topFraction: formatShare(n(v, 'topFraction')), topShare: formatShare(n(v, 'topShare')) });
    case 'creditConcentration':
      return t('copilotInvestment.finding.creditConcentration', {
        topFraction: formatShare(n(v, 'topFraction')),
        topShare: formatShare(n(v, 'topShare')),
        consumers: count(n(v, 'consumers')),
      });
    case 'highSpendThinEvidence':
      return n(v, 'people') === 1
        ? t('copilotInvestment.finding.highSpendThinEvidenceOne', { credits: formatCredits(n(v, 'credits')) })
        : t('copilotInvestment.finding.highSpendThinEvidence', { people: count(n(v, 'people')), credits: formatCredits(n(v, 'credits')) });
    case 'expandCandidates':
      if (n(v, 'evidenced') === 1)
        return n(v, 'people') === 1
          ? t('copilotInvestment.finding.expandEvidencedOne', { monthly: money('monthly') })
          : t('copilotInvestment.finding.expandEvidenced', { people: count(n(v, 'people')), monthly: money('monthly') });
      return n(v, 'people') === 1
        ? t('copilotInvestment.finding.expandRecommendedOne')
        : t('copilotInvestment.finding.expandRecommended', { people: count(n(v, 'people')) });
    case 'limitBlocked':
      if (n(v, 'people') === 1)
        return (n(v, 'evidenced') ?? 0) > 0 ? t('copilotInvestment.finding.limitBlockedOneEvidenced') : t('copilotInvestment.finding.limitBlockedOneThin');
      if ((n(v, 'evidenced') ?? 0) === 0) return t('copilotInvestment.finding.limitBlockedNoneEvidenced', { people: count(n(v, 'people')) });
      return n(v, 'evidenced') === 1
        ? t('copilotInvestment.finding.limitBlockedOneOfThem', { people: count(n(v, 'people')) })
        : t('copilotInvestment.finding.limitBlocked', { people: count(n(v, 'people')), evidenced: count(n(v, 'evidenced')) });
    case 'studioTrend': {
      const prior = n(v, 'prior');
      if (prior === null) return t('copilotInvestment.finding.studioTrendNotComparable', { credits: formatCredits(n(v, 'credits')) });
      return prior > 0
        ? t('copilotInvestment.finding.studioTrend', {
            credits: formatCredits(n(v, 'credits')),
            prior: formatCredits(prior),
            change: formatNumber(n(v, 'change') ?? 0, { style: 'percent', maximumFractionDigits: 0, signDisplay: 'exceptZero' }),
          })
        : t('copilotInvestment.finding.studioTrendNoPrior', { credits: formatCredits(n(v, 'credits')) });
    }
    case 'trajectory':
      return t('copilotInvestment.finding.trajectory', {
        first: count(n(v, 'first')),
        last: count(n(v, 'last')),
        weeks: count(n(v, 'weeks')),
        change: n(v, 'change') === null ? '\u2014' : formatNumber(n(v, 'change') ?? 0, { style: 'percent', maximumFractionDigits: 0, signDisplay: 'exceptZero' }),
        share: formatShare(n(v, 'share')),
      });
    case 'balancesUnverified':
      return n(v, 'roiIfMonthly') === null
        ? t('copilotInvestment.finding.balancesUnverified', { consumers: count(n(v, 'consumers')), credits: formatCredits(n(v, 'credits')) })
        : t('copilotInvestment.finding.balancesUnverifiedIfMonthly', {
            consumers: count(n(v, 'consumers')),
            credits: formatCredits(n(v, 'credits')),
            roiIfMonthly: formatMultiple(t, n(v, 'roiIfMonthly')),
          });
    default:
      return null;
  }
}

/** One action's title and body. Null for a code this build does not know. */
export function actionText(t: TFunction, action: InvestmentAction): { title: string; body: string } | null {
  const people = count(action.people);
  const one = action.people === 1;
  switch (action.code) {
    case 'reclaimIdle':
      return { title: t('copilotInvestment.action.reclaimIdle.title'), body: one ? t('copilotInvestment.action.reclaimIdle.bodyOne') : t('copilotInvestment.action.reclaimIdle.body', { people }) };
    case 'reviewIdle':
      return { title: t('copilotInvestment.action.reviewIdle.title'), body: one ? t('copilotInvestment.action.reviewIdle.bodyOne') : t('copilotInvestment.action.reviewIdle.body', { people }) };
    case 'enableBelowBreakEven':
      return {
        title: t('copilotInvestment.action.enableBelowBreakEven.title'),
        body: one
          ? t('copilotInvestment.action.enableBelowBreakEven.bodyOne', { days: formatDecimal(action.metric) })
          : t('copilotInvestment.action.enableBelowBreakEven.body', { people, days: formatDecimal(action.metric) }),
      };
    case 'enableLightEvidence':
      return { title: t('copilotInvestment.action.enableLightEvidence.title'), body: one ? t('copilotInvestment.action.enableLightEvidence.bodyOne') : t('copilotInvestment.action.enableLightEvidence.body', { people }) };
    case 'expandEvidenced':
      return {
        title: t('copilotInvestment.action.expandEvidenced.title'),
        body: one
          ? t('copilotInvestment.action.expandEvidenced.bodyOne', { days: formatDecimal(action.metric) })
          : t('copilotInvestment.action.expandEvidenced.body', { people, days: formatDecimal(action.metric) }),
      };
    case 'expandRecommended':
      return { title: t('copilotInvestment.action.expandRecommended.title'), body: one ? t('copilotInvestment.action.expandRecommended.bodyOne') : t('copilotInvestment.action.expandRecommended.body', { people }) };
    case 'reviewHighSpendThinEvidence':
      return {
        title: t('copilotInvestment.action.reviewHighSpendThinEvidence.title'),
        body: one
          ? t('copilotInvestment.action.reviewHighSpendThinEvidence.bodyOne', { credits: formatCredits(action.credits) })
          : t('copilotInvestment.action.reviewHighSpendThinEvidence.body', { people, credits: formatCredits(action.credits) }),
      };
    case 'raiseLimitEvidenced':
      return {
        title: t('copilotInvestment.action.raiseLimitEvidenced.title'),
        body: one
          ? t('copilotInvestment.action.raiseLimitEvidenced.bodyOne', { credits: formatCredits(action.credits) })
          : t('copilotInvestment.action.raiseLimitEvidenced.body', { people, credits: formatCredits(action.credits) }),
      };
    case 'licenceMeteredHeavy':
      return { title: t('copilotInvestment.action.licenceMeteredHeavy.title'), body: one ? t('copilotInvestment.action.licenceMeteredHeavy.bodyOne') : t('copilotInvestment.action.licenceMeteredHeavy.body', { people }) };
    case 'improveCoverage':
      return { title: t('copilotInvestment.action.improveCoverage.title'), body: one ? t('copilotInvestment.action.improveCoverage.bodyOne') : t('copilotInvestment.action.improveCoverage.body', { people }) };
    case 'setAssumptions':
      return {
        title: t('copilotInvestment.action.setAssumptions.title'),
        body: t('copilotInvestment.action.setAssumptions.body', { count: count(action.metric) }),
      };
    default:
      return null;
  }
}

/** One caveat as a sentence. Null for a code this build does not know. */
export function trustText(t: TFunction, reason: InvestmentTrustReason): string | null {
  const v = reason.values ?? {};
  switch (reason.code) {
    case 'adoptionUnavailable':
      return t('copilotInvestment.trust.adoptionUnavailable');
    case 'auditMissing':
      return t('copilotInvestment.trust.auditMissing');
    case 'usageReportSignal':
      return t('copilotInvestment.trust.usageReportSignal', { holders: count(n(v, 'holders')) });
    case 'figuresIncomplete':
      return t('copilotInvestment.trust.figuresIncomplete');
    case 'holdersNotAnalysed':
      return t('copilotInvestment.trust.holdersNotAnalysed', { holders: count(n(v, 'holders')) });
    case 'assumptionsMissing':
      return t('copilotInvestment.trust.assumptionsMissing', { missing: count(n(v, 'missing')) });
    case 'assumptionsUnavailable':
      return t('copilotInvestment.trust.assumptionsUnavailable');
    case 'scenarioApplied':
      return t('copilotInvestment.trust.scenarioApplied');
    case 'assumedTimeValue':
      return t('copilotInvestment.trust.assumedTimeValue');
    case 'seatsCurrentAssignments':
      return t('copilotInvestment.trust.seatsCurrentAssignments');
    case 'balancesUnverified':
      return t('copilotInvestment.trust.balancesUnverified');
    case 'creditCoverage':
      return t('copilotInvestment.trust.creditCoverage', { coverage: formatShare(n(v, 'coverage')) });
    case 'creditsStale':
      return t('copilotInvestment.trust.creditsStale', { days: count(n(v, 'days')) });
    case 'creditsPartial':
      return t('copilotInvestment.trust.creditsPartial', { share: formatShare(n(v, 'share')) });
    case 'creditsUnresolved':
      return t('copilotInvestment.trust.creditsUnresolved', { credits: formatCredits(n(v, 'credits')), objects: count(n(v, 'objects')) });
    case 'creditsMissing':
      return t('copilotInvestment.trust.creditsMissing');
    case 'creditsUnavailable':
      return t('copilotInvestment.trust.creditsUnavailable');
    case 'shortWindow':
      return t('copilotInvestment.trust.shortWindow', { days: count(n(v, 'days')) });
    case 'studioImportFailed':
      return t('copilotInvestment.trust.studioImportFailed');
    case 'studioUnavailable':
      return t('copilotInvestment.trust.studioUnavailable');
    default:
      return null;
  }
}

/** Which tone colour a finding or caveat is drawn with. Never colour alone: the text always says it too. */
export function toneColour(tone: string): string {
  switch (tone) {
    case 'good':
      return tokens.colorPaletteGreenBorderActive;
    case 'warning':
      return tokens.colorPaletteYellowBorderActive;
    case 'critical':
      return tokens.colorPaletteRedBorderActive;
    case 'opportunity':
      return tokens.colorBrandStroke1;
    default:
      return tokens.colorNeutralStroke1;
  }
}

const useVisualStyles = makeStyles({
  section: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  rangeTrack: {
    position: 'relative',
    height: '44px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  rangeBand: {
    position: 'absolute',
    top: '10px',
    height: '24px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px dashed ${tokens.colorBrandStroke1}`,
  },
  rangeBase: {
    position: 'absolute',
    top: '6px',
    width: '3px',
    height: '32px',
    backgroundColor: tokens.colorBrandForeground1,
  },
  costLine: {
    position: 'absolute',
    top: '0',
    width: '0',
    height: '44px',
    borderLeft: `3px solid ${tokens.colorNeutralForeground1}`,
  },
  legend: { display: 'flex', gap: '16px', flexWrap: 'wrap', alignItems: 'center' },
  legendItem: { display: 'inline-flex', gap: '6px', alignItems: 'center' },
  swatchCost: { width: '3px', height: '14px', backgroundColor: tokens.colorNeutralForeground1 },
  swatchValue: { width: '18px', height: '10px', backgroundColor: tokens.colorBrandBackground2, border: `1px dashed ${tokens.colorBrandStroke1}` },
  bars: { display: 'grid', gridTemplateColumns: 'fit-content(160px) minmax(64px, 1fr) minmax(0, auto)', gap: '8px 12px', alignItems: 'center' },
  barTrack: { height: '16px', borderRadius: '8px', backgroundColor: tokens.colorNeutralBackground3, overflow: 'hidden' },
  bar: { height: '100%', borderRadius: '8px', backgroundColor: tokens.colorBrandBackground, minWidth: '2px' },
  barMarked: { backgroundColor: tokens.colorPaletteYellowBorderActive },
  barLabel: { color: tokens.colorNeutralForeground2 },
  barValue: { fontVariantNumeric: 'tabular-nums', textAlign: 'right' },
  marked: { fontWeight: tokens.fontWeightSemibold },
});

/**
 * Cost as a solid line, the value range as a dashed band with the base case marked: the investment case in one
 * picture. The sentence under it carries every figure, so the chart is never the only way to read them.
 */
export function ValueVsCost({ cost, low, base, high, currency, t }: {
  cost: number;
  low: number;
  base: number;
  high: number;
  currency: string | null;
  t: TFunction;
}) {
  const styles = useVisualStyles();
  const max = Math.max(cost, high, 1) * 1.1;
  const at = (value: number): string => `${Math.max(0, Math.min(100, (value / max) * 100))}%`;
  const summary = t('copilotInvestment.exec.valueVsCost.summary', {
    cost: formatMoney(cost, currency),
    low: formatMoney(low, currency),
    high: formatMoney(high, currency),
    base: formatMoney(base, currency),
  });
  return (
    <div className={styles.section}>
      <div className={styles.rangeTrack} role="img" aria-label={summary}>
        <div className={styles.rangeBand} style={{ left: at(low), width: `calc(${at(high)} - ${at(low)})` }} />
        <div className={styles.rangeBase} style={{ left: at(base) }} />
        <div className={styles.costLine} style={{ left: at(cost) }} />
      </div>
      <div className={styles.legend}>
        <span className={styles.legendItem}>
          <span className={styles.swatchCost} aria-hidden="true" />
          <Text size={200}>{t('copilotInvestment.exec.valueVsCost.cost')}</Text>
        </span>
        <span className={styles.legendItem}>
          <span className={styles.swatchValue} aria-hidden="true" />
          <Text size={200}>{t('copilotInvestment.exec.valueVsCost.value')}</Text>
        </span>
      </div>
      <Text size={300}>{summary}</Text>
    </div>
  );
}

export interface BarRowData {
  key: string;
  label: string;
  value: number;
  display: string;
  marked?: boolean;
  note?: string;
}

/** Horizontal bars with the value printed beside each one; a marked row is emphasised in weight as well as colour. */
export function LabelledBars({ rows }: { rows: BarRowData[] }) {
  const styles = useVisualStyles();
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <div className={styles.bars}>
      {rows.map((row) => (
        <BarRow key={row.key} row={row} max={max} />
      ))}
    </div>
  );
}

function BarRow({ row, max }: { row: BarRowData; max: number }) {
  const styles = useVisualStyles();
  return (
    <>
      <Text size={200} className={mergeClasses(styles.barLabel, row.marked && styles.marked)}>
        {row.label}
      </Text>
      <div className={styles.barTrack} aria-hidden="true">
        {row.value > 0 && (
          <div className={mergeClasses(styles.bar, row.marked && styles.barMarked)} style={{ width: `${(row.value / max) * 100}%` }} />
        )}
      </div>
      <Text size={200} className={mergeClasses(styles.barValue, row.marked && styles.marked)}>
        {row.display}
        {row.note ? <> {row.note}</> : null}
      </Text>
    </>
  );
}

/** A Lorenz curve against the line of equality, as an accessible image with its summary as the label. */
export function LorenzChart({ points, label, equalityLabel, curveLabel }: {
  points: number[];
  label: string;
  equalityLabel: string;
  curveLabel: string;
}) {
  const styles = useVisualStyles();
  const size = 150;
  const pad = 8;
  const plot = size - pad * 2;
  const step = points.length > 1 ? plot / (points.length - 1) : plot;
  const path = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${pad + i * step},${pad + plot - p * plot}`).join(' ');
  return (
    <figure style={{ margin: 0, display: 'flex', flexDirection: 'column', gap: '6px' }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={label}>
        <rect x={pad} y={pad} width={plot} height={plot} fill="none" stroke={tokens.colorNeutralStroke2} />
        <line x1={pad} y1={pad + plot} x2={pad + plot} y2={pad} stroke={tokens.colorNeutralStroke1} strokeDasharray="4 3" />
        <path d={path} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth={2.5} />
      </svg>
      <div className={styles.legend}>
        <span className={styles.legendItem}>
          <svg width="18" height="6" aria-hidden="true">
            <line x1="0" y1="3" x2="18" y2="3" stroke={tokens.colorNeutralStroke1} strokeDasharray="4 3" />
          </svg>
          <Text size={100}>{equalityLabel}</Text>
        </span>
        <span className={styles.legendItem}>
          <svg width="18" height="6" aria-hidden="true">
            <line x1="0" y1="3" x2="18" y2="3" stroke={tokens.colorBrandForeground1} strokeWidth="2.5" />
          </svg>
          <Text size={100}>{curveLabel}</Text>
        </span>
      </div>
      <Text size={200}>{label}</Text>
    </figure>
  );
}

/** A section heading with an optional badge, used by every tab so they read as one report. */
export function SectionHeading({ title, description, badge }: { title: string; description?: ReactNode; badge?: ReactNode }) {
  const styles = useVisualStyles();
  return (
    <div className={styles.section}>
      <div style={{ display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' }}>
        <Text as="h3" weight="semibold" size={400} style={{ margin: 0 }}>
          {title}
        </Text>
        {badge}
      </div>
      {description && (
        <Text size={200} className={styles.muted}>
          {description}
        </Text>
      )}
    </div>
  );
}
