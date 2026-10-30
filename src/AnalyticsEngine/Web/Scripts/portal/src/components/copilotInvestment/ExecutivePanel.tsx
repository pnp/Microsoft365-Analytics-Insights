import { useState } from 'react';
import { Button, Card, Link, Text, makeStyles, tokens } from '@fluentui/react-components';
import { CheckmarkCircle16Regular, Info16Regular, Lightbulb16Regular, Warning16Regular } from '@fluentui/react-icons';
import { formatList, formatNumber, useT, type TFunction } from '../../i18n';
import { KpiGrid, type KpiDefinition, type KpiTone } from '../shared/KpiGrid';
import type { CopilotInvestmentSummary, InvestmentAction, InvestmentFinding } from '../../types/copilotInvestment';
import {
  ACTION_EFFECT_KEYS,
  FIELD_LABEL_KEYS,
  LabelledBars,
  PROVENANCE_LABEL_KEYS,
  SectionHeading,
  TRAJECTORY_UNAVAILABLE_KEYS,
  TRUST_AREA_LABEL_KEYS,
  TRUST_AREAS,
  TRUST_LEVEL_LABEL_KEYS,
  ValueVsCost,
  actionText,
  bucketLabel,
  findingText,
  formatCredits,
  formatDecimal,
  formatMinutes,
  formatMoney,
  formatMultiple,
  formatPrice,
  formatShare,
  formatShortDate,
  formatSignedMoney,
  toneColour,
  trustText,
} from './investmentShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  kpiGroup: { display: 'flex', flexDirection: 'column', gap: '8px' },
  columns: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(340px, 1fr))', gap: '16px', alignItems: 'start' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px' },
  verdict: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '16px 20px',
    borderLeftWidth: '6px',
    borderLeftStyle: 'solid',
  },
  verdictText: { fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400, fontWeight: tokens.fontWeightSemibold },
  list: { display: 'flex', flexDirection: 'column', gap: '10px', margin: 0, padding: 0, listStyleType: 'none' },
  finding: {
    display: 'flex',
    gap: '8px',
    alignItems: 'flex-start',
    paddingLeft: '10px',
    borderLeftWidth: '3px',
    borderLeftStyle: 'solid',
  },
  icon: { flexShrink: 0, marginTop: '2px', color: tokens.colorNeutralForeground3 },
  actions: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: '12px' },
  action: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '14px 16px' },
  actionFooter: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginTop: '4px' },
  metric: { fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  muted: { color: tokens.colorNeutralForeground3 },
  provenance: { display: 'grid', gridTemplateColumns: 'minmax(140px, 1fr) auto auto', gap: '4px 12px', alignItems: 'baseline' },
  areas: { display: 'grid', gridTemplateColumns: 'minmax(140px, auto) auto 1fr', gap: '8px 12px', alignItems: 'baseline' },
  areaLevel: { fontWeight: tokens.fontWeightSemibold },
  weeks: { display: 'flex', gap: '4px', alignItems: 'flex-end', height: '96px', paddingTop: '4px' },
  week: { flex: '1 1 0', minWidth: '8px', backgroundColor: tokens.colorBrandBackground, borderRadius: '2px 2px 0 0' },
  weekMissing: {
    flex: '1 1 0',
    minWidth: '8px',
    height: '100%',
    backgroundImage: `repeating-linear-gradient(45deg, ${tokens.colorNeutralStroke2} 0 2px, transparent 2px 6px)`,
  },
  weekAxis: { display: 'flex', justifyContent: 'space-between' },
});

/** Where a reader goes from an action: the people it names (See PII), or the tab that explains it. */
export interface ExecutiveNavigation {
  seePeople?: (filter: { signal?: string; flag?: string }) => void;
  openTab: (tab: 'value' | 'organisation' | 'credits' | 'people' | 'method') => void;
  openWhatIf: () => void;
  settingsHref?: string;
}

/** The assumptions the landing page shows beside the figures; the Method tab lists them all. */
const HEADLINE_ASSUMPTIONS = ['seatPricePerMonth', 'studioCreditPrice', 'creditPrice', 'hourlyCost', 'minutesPerActiveDay.base', 'realisation.base'];

/** Findings the verdict, the path and the trend already say: not repeated in the list beneath them. */
const SHOWN_ELSEWHERE = new Set(['runRate', 'valueRange', 'breakEven', 'assumptionsMissing', 'noMatchedHolders', 'trajectory']);

/** How many findings show before "show all": the ones a reader acts on first. */
const TOP_FINDINGS = 5;

/** The headline figures that are the return itself; the rest say what it takes and how much the seats are used. */
const RETURN_KPIS = new Set(['cost', 'value', 'net', 'roi']);

const TONE_ORDER: Record<string, number> = { critical: 0, warning: 1, opportunity: 2, good: 3, neutral: 4 };

function roiTone(conservative: number | null, base: number | null): KpiTone {
  if (conservative !== null && conservative >= 1) return 'good';
  if (base !== null && base >= 1) return 'warning';
  return base === null ? 'neutral' : 'critical';
}

function perMonth(t: TFunction, value: string): string {
  return t('copilotInvestment.format.perMonth', { value });
}

/** The figures the investment case rests on: cost, value, net, return, break-even and how much the seats are used. */
function headline(summary: CopilotInvestmentSummary, t: TFunction): KpiDefinition[] {
  const currency = summary.investment.currency;
  const spend = summary.investment;
  const seats = spend.seats;
  const evidence = summary.evidence;
  const scenarios = Object.fromEntries(summary.value.scenarios.map((s) => [s.scenario, s]));
  const conservative = scenarios.conservative ?? null;
  const base = scenarios.base ?? null;
  const upside = scenarios.upside ?? null;
  const modelled = t('copilotInvestment.badge.modelled');
  const cost = spend.modelledMonthlyCost;
  const net = (monthly: number | undefined): number | null => (monthly === undefined || cost === null ? null : monthly - cost);
  const items: KpiDefinition[] = [];

  items.push({
    key: 'cost',
    label: t('copilotInvestment.kpi.cost.label'),
    value: cost !== null ? perMonth(t, formatMoney(cost, currency)) : '\u2014',
    hint:
      cost === null
        ? t('copilotInvestment.kpi.runRate.missing')
        : spend.monthlyStudioCost !== null
          ? t('copilotInvestment.kpi.cost.hint', { seats: formatMoney(spend.monthlySeatCost, currency), studio: formatMoney(spend.monthlyStudioCost, currency) })
          : t('copilotInvestment.kpi.cost.hintSeatsOnly', {
              holders: formatNumber(seats.analysedHolders),
              price: formatPrice(summary.assumptions.values?.seatPricePerMonth ?? null, currency),
            }),
    modelledBadge: modelled,
    info: {
      what: t('copilotInvestment.kpi.cost.what'),
      how: t('copilotInvestment.kpi.cost.how'),
      formula: t('copilotInvestment.kpi.cost.formula'),
    },
  });

  items.push({
    key: 'value',
    label: t('copilotInvestment.kpi.value.label'),
    value: base ? perMonth(t, formatMoney(base.monthlyValue, currency)) : '\u2014',
    hint:
      base && conservative && upside
        ? t('copilotInvestment.kpi.value.hint', { low: formatMoney(conservative.monthlyValue, currency), high: formatMoney(upside.monthlyValue, currency) })
        : t('copilotInvestment.kpi.value.missing'),
    modelledBadge: modelled,
    tone: base ? 'opportunity' : 'neutral',
    info: {
      what: t('copilotInvestment.kpi.value.what'),
      how: t('copilotInvestment.kpi.value.how'),
      formula: t('copilotInvestment.kpi.value.formula'),
    },
  });

  const netBase = net(base?.monthlyValue);
  items.push({
    key: 'net',
    label: t('copilotInvestment.kpi.net.label'),
    value: netBase !== null ? perMonth(t, formatSignedMoney(netBase, currency)) : '\u2014',
    hint:
      netBase !== null && conservative && upside
        ? t('copilotInvestment.kpi.net.hint', {
            low: formatSignedMoney(net(conservative.monthlyValue), currency),
            high: formatSignedMoney(net(upside.monthlyValue), currency),
          })
        : t('copilotInvestment.kpi.value.missing'),
    modelledBadge: modelled,
    tone: netBase === null ? 'neutral' : netBase >= 0 ? 'good' : (net(upside?.monthlyValue) ?? -1) >= 0 ? 'warning' : 'critical',
    info: {
      what: t('copilotInvestment.kpi.net.what'),
      how: t('copilotInvestment.kpi.net.how'),
      formula: t('copilotInvestment.kpi.net.formula'),
    },
  });

  items.push({
    key: 'roi',
    label: t('copilotInvestment.kpi.roi.label'),
    value: base ? formatMultiple(t, base.roiMultiple) : '\u2014',
    hint:
      base && conservative && upside
        ? t('copilotInvestment.kpi.roi.hint', { low: formatMultiple(t, conservative.roiMultiple), high: formatMultiple(t, upside.roiMultiple) })
        : t('copilotInvestment.kpi.value.missing'),
    modelledBadge: modelled,
    tone: base ? roiTone(conservative?.roiMultiple ?? null, base.roiMultiple) : 'neutral',
    info: {
      what: t('copilotInvestment.kpi.roi.what'),
      how: t('copilotInvestment.kpi.roi.how'),
      formula: t('copilotInvestment.kpi.roi.formula'),
    },
  });

  const breakEven = summary.breakEven;
  const assumedMinutes = summary.assumptions.values?.minutesPerActiveDay.base ?? null;
  items.push({
    key: 'breakEven',
    label: t('copilotInvestment.kpi.breakEven.label'),
    value: breakEven.available && breakEven.minutesPerActiveDay !== null ? formatMinutes(t, breakEven.minutesPerActiveDay) : '\u2014',
    hint:
      breakEven.available && breakEven.minutesPerActiveDay !== null
        ? t('copilotInvestment.kpi.breakEven.hint', { assumed: formatMinutes(t, assumedMinutes), margin: formatShare(breakEven.marginOfSafety) })
        : t('copilotInvestment.kpi.value.missing'),
    modelledBadge: modelled,
    tone:
      breakEven.marginOfSafety === null ? 'neutral' : breakEven.marginOfSafety >= 0.25 ? 'good' : breakEven.marginOfSafety >= 0 ? 'warning' : 'critical',
    info: {
      what: t('copilotInvestment.kpi.breakEven.what'),
      how: t('copilotInvestment.kpi.breakEven.how'),
      formula: t('copilotInvestment.kpi.breakEven.formula'),
    },
  });

  items.push({
    key: 'active',
    label: t('copilotInvestment.kpi.active.label'),
    value: formatShare(evidence.activeShare),
    hint: t('copilotInvestment.kpi.active.hint', { active: formatNumber(evidence.activeHolders), holders: formatNumber(evidence.analysedHolders) }),
    tone: evidence.activeShare === null ? 'neutral' : evidence.activeShare >= 0.8 ? 'good' : evidence.activeShare >= 0.6 ? 'warning' : 'critical',
    info: {
      what: t('copilotInvestment.kpi.active.what'),
      how: t('copilotInvestment.kpi.active.how'),
      formula: t('copilotInvestment.kpi.active.formula'),
    },
  });

  if (seats.costPerActiveHolderMonthly !== null) {
    items.push({
      key: 'costPerActive',
      label: t('copilotInvestment.kpi.costPerActive.label'),
      value: perMonth(t, formatMoney(seats.costPerActiveHolderMonthly, currency)),
      hint: t('copilotInvestment.kpi.costPerActive.hint'),
      modelledBadge: modelled,
      info: {
        what: t('copilotInvestment.kpi.costPerActive.what'),
        how: t('copilotInvestment.kpi.costPerActive.how'),
        formula: t('copilotInvestment.kpi.costPerActive.formula'),
      },
    });
  }
  return items;
}

/** What is being spent and consumed beside the return - facts from the billing sources, never added together. */
function spendSources(summary: CopilotInvestmentSummary, t: TFunction): KpiDefinition[] {
  const currency = summary.investment.currency;
  const seats = summary.investment.seats;
  const items: KpiDefinition[] = [
    {
      key: 'runRate',
      label: t('copilotInvestment.kpi.runRate.label'),
      value: seats.monthlyRunRate !== null ? perMonth(t, formatMoney(seats.monthlyRunRate, currency)) : '\u2014',
      hint:
        seats.monthlyRunRate !== null
          ? t('copilotInvestment.kpi.runRate.hint', {
              holders: formatNumber(seats.holders),
              price: formatPrice(summary.assumptions.values?.seatPricePerMonth ?? null, currency),
            })
          : t('copilotInvestment.kpi.runRate.missing'),
      modelledBadge: t('copilotInvestment.badge.modelled'),
      info: {
        what: t('copilotInvestment.kpi.runRate.what'),
        how: t('copilotInvestment.kpi.runRate.how'),
        formula: t('copilotInvestment.kpi.runRate.formula'),
      },
    },
  ];

  const credits = summary.investment.credits;
  if (summary.sources.credits.hasData) {
    items.push({
      key: 'balances',
      label: t('copilotInvestment.kpi.balances.label'),
      value: formatCredits(credits.credits),
      hint: t('copilotInvestment.kpi.balances.hint', { consumers: formatNumber(credits.consumingUsers) }),
      info: {
        what: t('copilotInvestment.kpi.balances.what'),
        how: t('copilotInvestment.kpi.balances.how'),
        source: t('copilotInvestment.kpi.balances.source'),
      },
    });
  }

  const studio = summary.investment.studio;
  if (summary.sources.studio.hasData || studio.credits > 0) {
    const days = formatNumber(summary.windowDays);
    items.push({
      key: 'studio',
      label: t('copilotInvestment.kpi.studio.label'),
      value: formatCredits(studio.credits),
      hint:
        studio.priorCredits === null
          ? t('copilotInvestment.kpi.studio.hintNotComparable', { days })
          : studio.priorCredits > 0
            ? t('copilotInvestment.kpi.studio.hint', { prior: formatCredits(studio.priorCredits), days })
            : t('copilotInvestment.kpi.studio.hintNoPrior', { days }),
      info: {
        what: t('copilotInvestment.kpi.studio.what'),
        how: t('copilotInvestment.kpi.studio.how'),
        source: t('copilotInvestment.kpi.studio.source'),
      },
    });
  }
  return items;
}

/** The answer in one sentence, coloured by it - and always worded, so the colour is never the only signal. */
function verdict(summary: CopilotInvestmentSummary, t: TFunction): { text: string; tone: string } {
  const value = summary.value;
  const currency = summary.investment.currency;
  if (!value.available) {
    return value.unavailableReason === 'noMatchedHolders'
      ? { text: t('copilotInvestment.exec.verdict.noMatched'), tone: 'warning' }
      : { text: t('copilotInvestment.exec.verdict.missing', { count: formatNumber(value.missing.length) }), tone: 'neutral' };
  }
  const scenarios = Object.fromEntries(value.scenarios.map((s) => [s.scenario, s]));
  const path = summary.path;
  const gap = path.monthlyGap ?? 0;
  const values = {
    roi: formatMultiple(t, scenarios.base?.roiMultiple),
    roiLow: formatMultiple(t, scenarios.conservative?.roiMultiple),
    roiHigh: formatMultiple(t, scenarios.upside?.roiMultiple),
    gap: formatMoney(Math.abs(gap), currency),
    needed: formatMinutes(t, summary.breakEven.minutesPerActiveDay),
    assumed: formatMinutes(t, path.minutesAssumed),
  };
  const tone = roiTone(scenarios.conservative?.roiMultiple ?? null, scenarios.base?.roiMultiple ?? null);
  return { text: t(gap > 0 ? 'copilotInvestment.exec.verdict.shortfall' : 'copilotInvestment.exec.verdict.surplus', values), tone };
}

function ToneIcon({ tone }: { tone: string }) {
  const styles = useStyles();
  if (tone === 'good') return <CheckmarkCircle16Regular className={styles.icon} aria-hidden="true" />;
  if (tone === 'warning' || tone === 'critical') return <Warning16Regular className={styles.icon} aria-hidden="true" />;
  if (tone === 'opportunity') return <Lightbulb16Regular className={styles.icon} aria-hidden="true" />;
  return <Info16Regular className={styles.icon} aria-hidden="true" />;
}

/** What an action's amount is, worded by kind: a freed seat cost, a value shortfall and an added cost never read alike. */
function effectText(t: TFunction, action: InvestmentAction, currency: string | null): string | null {
  if (action.monthlyAmount === null) return null;
  const key = action.effect ? ACTION_EFFECT_KEYS[action.effect] : null;
  return key ? t(key, { amount: formatMoney(action.monthlyAmount, currency) }) : null;
}

function ActionCard({ action, summary, navigation, canSeePii }: {
  action: InvestmentAction;
  summary: CopilotInvestmentSummary;
  navigation: ExecutiveNavigation;
  canSeePii: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const text = actionText(t, action);
  if (!text) return null;
  const currency = summary.investment.currency;
  const amount = effectText(t, action, currency);
  const drill = canSeePii && navigation.seePeople && action.people > 0 && (action.signal || action.flag);
  const warning = action.code === 'reclaimIdle' || action.code === 'reviewIdle' || action.code === 'reviewHighSpendThinEvidence';
  return (
    <Card className={styles.action} style={{ borderLeft: `4px solid ${toneColour(warning ? 'warning' : 'opportunity')}` }}>
      <Text weight="semibold">{text.title}</Text>
      <Text size={200}>{text.body}</Text>
      {amount && (
        <Text size={200} className={styles.metric}>
          {amount}
        </Text>
      )}
      {action.roiBaseAfter !== null && (
        <Text size={200}>{t('copilotInvestment.action.reclaimIdle.after', { roi: formatMultiple(t, action.roiBaseAfter) })}</Text>
      )}
      {action.assumesMonthlyBalances && !amount && (
        <Text size={100} className={styles.muted}>
          {t('copilotInvestment.action.monthlyBalanceCaveat')}
        </Text>
      )}
      <div className={styles.actionFooter} data-print="hide">
        {drill && (
          <Button size="small" appearance="secondary" onClick={() => navigation.seePeople?.({ signal: action.signal ?? undefined, flag: action.flag ?? undefined })}>
            {action.people === 1
              ? t('copilotInvestment.exec.actions.seePerson')
              : t('copilotInvestment.exec.actions.seePeople', { count: formatNumber(action.people) })}
          </Button>
        )}
        {action.code === 'setAssumptions' && navigation.settingsHref && (
          <Link href={navigation.settingsHref}>{t('copilotInvestment.controls.settings')}</Link>
        )}
        {action.code === 'setAssumptions' && (
          <Button size="small" appearance="subtle" onClick={navigation.openWhatIf}>
            {t('copilotInvestment.controls.whatIf')}
          </Button>
        )}
        {action.code === 'improveCoverage' && (
          <Button size="small" appearance="subtle" onClick={() => navigation.openTab('credits')}>
            {t('copilotInvestment.exec.actions.openTab')}
          </Button>
        )}
      </div>
    </Card>
  );
}

/** The levers that move the base case, each quantified a month on the same seat holders as the return. */
function PathCard({ summary, navigation }: { summary: CopilotInvestmentSummary; navigation: ExecutiveNavigation }) {
  const styles = useStyles();
  const t = useT();
  const path = summary.path;
  const currency = summary.investment.currency;
  const lines: { key: string; tone: string; text: string }[] = [];
  if (path.available) {
    const gap = path.monthlyGap ?? 0;
    if (gap > 0 && path.extraActiveDaysPerMonth !== null) {
      lines.push({
        key: 'gap',
        tone: 'critical',
        text: t('copilotInvestment.exec.path.shortfall', {
          gap: formatMoney(gap, currency),
          days: formatNumber(path.extraActiveDaysPerMonth, { maximumFractionDigits: 0 }),
          perHolder: formatDecimal(path.extraDaysPerActiveHolder),
        }),
      });
    } else if (gap <= 0) {
      lines.push({ key: 'gap', tone: 'good', text: t('copilotInvestment.exec.path.surplus', { gap: formatMoney(-gap, currency) }) });
    }
    if (path.minutesNeeded !== null) {
      lines.push({
        key: 'minutes',
        tone: 'neutral',
        text: t(gap > 0 ? 'copilotInvestment.exec.path.minutesShort' : 'copilotInvestment.exec.path.minutesClear', {
          needed: formatMinutes(t, path.minutesNeeded),
          assumed: formatMinutes(t, path.minutesAssumed),
        }),
      });
    }
    if (path.reclaimable > 0 && path.reclaimMonthly !== null) {
      const monthly = formatMoney(path.reclaimMonthly, currency);
      const roi = formatMultiple(t, path.roiAfterReclaim);
      lines.push({
        key: 'reclaim',
        tone: 'warning',
        text:
          path.reclaimable === 1
            ? t('copilotInvestment.exec.path.reclaimOne', { monthly, roi })
            : t('copilotInvestment.exec.path.reclaim', { count: formatNumber(path.reclaimable), monthly, roi }),
      });
    }
    if (path.evidencedWithoutSeat > 0) {
      const one = path.evidencedWithoutSeat === 1;
      const evidenced = formatNumber(path.evidencedWithoutSeat);
      const netNewMonthly = formatMoney(path.netNewMonthly, currency);
      let text: string;
      if (path.netNewSeats === 0) {
        text = one ? t('copilotInvestment.exec.path.rebalanceCoveredOne') : t('copilotInvestment.exec.path.rebalanceCovered', { evidenced });
      } else if (path.reassignable === 0) {
        text = one ? t('copilotInvestment.exec.path.rebalanceNoneOne', { netNewMonthly }) : t('copilotInvestment.exec.path.rebalanceNone', { evidenced, netNewMonthly });
      } else {
        text = t('copilotInvestment.exec.path.rebalance', {
          evidenced,
          reassignable: formatNumber(path.reassignable),
          netNew: formatNumber(path.netNewSeats),
          netNewMonthly,
        });
      }
      lines.push({ key: 'rebalance', tone: 'opportunity', text });
    }
  }
  return (
    <Card className={styles.card}>
      <SectionHeading title={t('copilotInvestment.exec.path.title')} description={t('copilotInvestment.exec.path.description')} />
      {lines.length === 0 ? (
        <Text size={200}>{t('copilotInvestment.exec.path.unavailable')}</Text>
      ) : (
        <ul className={styles.list}>
          {lines.map((line) => (
            <li key={line.key} className={styles.finding} style={{ borderLeftColor: toneColour(line.tone) }}>
              <ToneIcon tone={line.tone} />
              <Text size={300}>{line.text}</Text>
            </li>
          ))}
        </ul>
      )}
      <div className={styles.actionFooter} data-print="hide">
        <Button size="small" appearance="secondary" onClick={navigation.openWhatIf}>
          {t('copilotInvestment.controls.whatIf')}
        </Button>
        <Link as="button" onClick={() => navigation.openTab('value')}>
          {t('copilotInvestment.tab.value')}
        </Link>
      </div>
    </Card>
  );
}

/** Weekly active seat holders as a share of today's seats - whether use is building, holding or fading. */
function TrajectoryCard({ summary }: { summary: CopilotInvestmentSummary }) {
  const styles = useStyles();
  const t = useT();
  const trajectory = summary.trajectory;
  const finding = summary.findings.find((f) => f.code === 'trajectory');
  const sentence = finding ? findingText(t, finding, summary.investment.currency) : null;
  const reasonKey = TRAJECTORY_UNAVAILABLE_KEYS[trajectory.unavailableReason ?? ''] ?? TRAJECTORY_UNAVAILABLE_KEYS.noData;
  const max = Math.max(0.0001, ...trajectory.weeks.map((w) => w.share ?? 0));
  return (
    <Card className={styles.card}>
      <SectionHeading title={t('copilotInvestment.exec.trajectory.title')} description={t('copilotInvestment.exec.trajectory.description')} />
      {!trajectory.available ? (
        <Text size={200}>{t(reasonKey)}</Text>
      ) : (
        <>
          <div className={styles.weeks} role="list" aria-label={t('copilotInvestment.exec.trajectory.title')}>
            {trajectory.weeks.map((week) => {
              const date = formatShortDate(week.weekStart);
              const label =
                week.activeHolders === null
                  ? t('copilotInvestment.exec.trajectory.notCovered', { date })
                  : t('copilotInvestment.exec.trajectory.week', { date, count: formatNumber(week.activeHolders), share: formatShare(week.share) });
              return week.activeHolders === null ? (
                <span key={week.weekStart} role="listitem" aria-label={label} title={label} className={styles.weekMissing} />
              ) : (
                <span
                  key={week.weekStart}
                  role="listitem"
                  aria-label={label}
                  title={label}
                  className={styles.week}
                  style={{ height: `${Math.max(2, ((week.share ?? 0) / max) * 100)}%` }}
                />
              );
            })}
          </div>
          <div className={styles.weekAxis} aria-hidden="true">
            <Text size={100} className={styles.muted}>
              {formatShortDate(trajectory.weeks[0]?.weekStart)}
            </Text>
            <Text size={100} className={styles.muted}>
              {formatShortDate(trajectory.weeks[trajectory.weeks.length - 1]?.weekStart)}
            </Text>
          </div>
          {sentence && <Text size={300}>{sentence}</Text>}
        </>
      )}
    </Card>
  );
}

/** Confidence per question: evidence, cost, assumptions and balances, each with its own level and its first caveat. */
function TrustCard({ summary, navigation }: { summary: CopilotInvestmentSummary; navigation: ExecutiveNavigation }) {
  const styles = useStyles();
  const t = useT();
  const levels = Object.fromEntries((summary.trust.areas ?? []).map((a) => [a.area, a.level]));
  const reasons = summary.trust.reasons
    .map((r) => ({ reason: r, text: trustText(t, r) }))
    .filter((r): r is { reason: typeof r.reason; text: string } => r.text !== null);
  return (
    <Card className={styles.card}>
      <SectionHeading title={t('copilotInvestment.exec.trust.title')} description={t('copilotInvestment.exec.trust.description')} />
      <Text weight="semibold">
        {t(summary.trust.level === 'high' ? 'copilotInvestment.exec.trust.high' : summary.trust.level === 'low' ? 'copilotInvestment.exec.trust.low' : 'copilotInvestment.exec.trust.medium')}
      </Text>
      <div className={styles.areas}>
        {TRUST_AREAS.map((area) => {
          const level = levels[area] ?? 'high';
          const first = reasons.find((r) => r.reason.area === area && r.reason.impact !== 'info') ?? reasons.find((r) => r.reason.area === area);
          return (
            <div key={area} style={{ display: 'contents' }}>
              <Text size={200}>{t(TRUST_AREA_LABEL_KEYS[area])}</Text>
              <Text size={200} className={styles.areaLevel} style={{ color: level === 'low' ? tokens.colorPaletteRedForeground1 : undefined }}>
                {t(TRUST_LEVEL_LABEL_KEYS[level] ?? 'copilotInvestment.exec.trust.level.medium')}
              </Text>
              <Text size={200} className={styles.muted}>
                {first ? first.text : t('copilotInvestment.exec.trust.none')}
              </Text>
            </div>
          );
        })}
      </div>
      {reasons.length > 0 && (
        <Link as="button" onClick={() => navigation.openTab('method')} data-print="hide">
          {t('copilotInvestment.exec.trust.more', { count: formatNumber(reasons.length) })}
        </Link>
      )}
    </Card>
  );
}

/**
 * The executive landing, in the order an investment decision is made: the answer and its range, the figures it rests
 * on, what would change it, what to do, what the evidence shows and how far to trust it - each figure carrying its
 * definition, and every amount labelled as modelled.
 */
export default function ExecutivePanel({ summary, navigation, canSeePii }: {
  summary: CopilotInvestmentSummary;
  navigation: ExecutiveNavigation;
  canSeePii: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const [allFindings, setAllFindings] = useState(false);
  const currency = summary.investment.currency;
  const scenarios = Object.fromEntries(summary.value.scenarios.map((s) => [s.scenario, s]));
  const answer = verdict(summary, t);
  const kpis = headline(summary, t);
  const findings = summary.findings
    .filter((f) => !SHOWN_ELSEWHERE.has(f.code))
    .map((f) => ({ finding: f, text: findingText(t, f, currency) }))
    .filter((f): f is { finding: InvestmentFinding; text: string } => f.text !== null)
    .sort((a, b) => (TONE_ORDER[a.finding.tone] ?? 9) - (TONE_ORDER[b.finding.tone] ?? 9));
  const shownFindings = allFindings ? findings : findings.slice(0, TOP_FINDINGS);
  const assumptions = summary.assumptions;
  const missingFields = assumptions.missingForValue.map((f) => (FIELD_LABEL_KEYS[f] ? t(FIELD_LABEL_KEYS[f]) : f));
  const breakEvenDays = summary.breakEven.activeDaysPerMonthPerSeat;
  const distribution = summary.evidence.distribution;
  const above = distribution.filter((b) => b.belowBreakEven === false).reduce((sum, b) => sum + b.holders, 0);
  const noUse = distribution.filter((b) => b.key === 'none').reduce((sum, b) => sum + b.holders, 0);
  const below = distribution.filter((b) => b.belowBreakEven === true && b.key !== 'none').reduce((sum, b) => sum + b.holders, 0);

  return (
    <div className={styles.stack}>
      <Card className={styles.verdict} style={{ borderLeftColor: toneColour(answer.tone) }}>
        <Text size={200} weight="semibold" className={styles.muted}>
          {t('copilotInvestment.exec.verdict.label')}
        </Text>
        <Text className={styles.verdictText}>{answer.text}</Text>
        <Text size={200} className={styles.muted}>
          {t('copilotInvestment.exec.verdict.caveat')}
        </Text>
      </Card>

      <div className={styles.kpiGroup}>
        <Text size={300} weight="semibold">{t('copilotInvestment.exec.kpis.return')}</Text>
        <KpiGrid items={kpis.filter((k) => RETURN_KPIS.has(k.key))} />
      </div>
      <div className={styles.kpiGroup}>
        <Text size={300} weight="semibold">{t('copilotInvestment.exec.kpis.use')}</Text>
        <KpiGrid items={kpis.filter((k) => !RETURN_KPIS.has(k.key))} />
      </div>

      <div className={styles.columns}>
        <Card className={styles.card}>
          <SectionHeading title={t('copilotInvestment.exec.valueVsCost.title')} description={t('copilotInvestment.exec.valueVsCost.description')} />
          {summary.value.available && scenarios.base && scenarios.conservative && scenarios.upside && summary.investment.modelledMonthlyCost !== null ? (
            <ValueVsCost
              cost={summary.investment.modelledMonthlyCost}
              low={scenarios.conservative.monthlyValue}
              base={scenarios.base.monthlyValue}
              high={scenarios.upside.monthlyValue}
              currency={currency}
              t={t}
            />
          ) : (
            <Text size={200}>
              {summary.value.unavailableReason === 'noMatchedHolders' ? t('copilotInvestment.value.unmatched.body') : t('copilotInvestment.exec.valueVsCost.missing')}
            </Text>
          )}
        </Card>
        <PathCard summary={summary} navigation={navigation} />
      </div>

      <div className={styles.stack}>
        <SectionHeading title={t('copilotInvestment.exec.actions.title')} description={t('copilotInvestment.exec.actions.description')} />
        {summary.actions.length === 0 ? (
          <Text size={200}>{t('copilotInvestment.exec.actions.empty')}</Text>
        ) : (
          <div className={styles.actions}>
            {summary.actions.map((action) => (
              <ActionCard key={action.code} action={action} summary={summary} navigation={navigation} canSeePii={canSeePii} />
            ))}
          </div>
        )}
      </div>

      <div className={styles.columns}>
        <Card className={styles.card}>
          <SectionHeading title={t('copilotInvestment.exec.findings.title')} />
          {findings.length === 0 ? (
            <Text size={200}>{t('copilotInvestment.exec.findings.empty')}</Text>
          ) : (
            <ul className={styles.list}>
              {shownFindings.map(({ finding, text }) => (
                <li key={finding.code} className={styles.finding} style={{ borderLeftColor: toneColour(finding.tone) }}>
                  <ToneIcon tone={finding.tone} />
                  <Text size={300}>{text}</Text>
                </li>
              ))}
            </ul>
          )}
          {findings.length > TOP_FINDINGS && (
            <Link as="button" onClick={() => setAllFindings((v) => !v)} data-print="hide">
              {allFindings ? t('copilotInvestment.exec.findings.fewer') : t('copilotInvestment.exec.findings.all', { count: formatNumber(findings.length) })}
            </Link>
          )}
        </Card>
        <TrustCard summary={summary} navigation={navigation} />
      </div>

      <div className={styles.columns}>
        <Card className={styles.card}>
          <SectionHeading title={t('copilotInvestment.exec.distribution.title')} description={t('copilotInvestment.exec.distribution.description')} />
          <LabelledBars
            rows={distribution.map((bucket) => ({
              key: `${bucket.minDaysPerMonth}-${bucket.maxDaysPerMonth ?? 'up'}-${bucket.key}`,
              label: bucketLabel(t, bucket),
              value: bucket.holders,
              display: t('copilotInvestment.exec.distribution.holders', { count: formatNumber(bucket.holders) }),
              marked: bucket.belowBreakEven === true,
              note: bucket.belowBreakEven === true ? `\u00b7 ${t('copilotInvestment.exec.distribution.belowMarker')}` : undefined,
            }))}
          />
          {breakEvenDays !== null && (
            <>
              <Text size={200} weight="semibold">
                {t('copilotInvestment.exec.distribution.breakEven', { days: formatDecimal(breakEvenDays) })}
              </Text>
              <Text size={200}>
                {t('copilotInvestment.exec.distribution.summary', {
                  above: formatNumber(above),
                  holders: formatNumber(summary.evidence.analysedHolders),
                  below: formatNumber(below),
                  none: formatNumber(noUse),
                })}
              </Text>
            </>
          )}
          <Text size={100} className={styles.muted}>
            {t('copilotInvestment.exec.distribution.axis')}
          </Text>
        </Card>
        <TrajectoryCard summary={summary} />
      </div>

      <div className={styles.stack}>
        <SectionHeading title={t('copilotInvestment.kpi.secondary')} description={t('copilotInvestment.kpi.secondary.description')} />
        <KpiGrid items={spendSources(summary, t)} />
      </div>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.exec.assumptions.title')} />
        {assumptions.status === 'unavailable' ? (
          <Text size={200}>{t('copilotInvestment.exec.assumptions.unavailable')}</Text>
        ) : (
          <div className={styles.provenance}>
            {HEADLINE_ASSUMPTIONS.map((field) => (
              <AssumptionLine key={field} field={field} summary={summary} />
            ))}
          </div>
        )}
        {assumptions.scenarioApplied && <Text size={200}>{t('copilotInvestment.exec.assumptions.whatIfNote')}</Text>}
        {missingFields.length > 0 && assumptions.status !== 'unavailable' && (
          <Text size={200}>{t('copilotInvestment.exec.assumptions.missingNote', { fields: formatList(missingFields) })}</Text>
        )}
        <div className={styles.actionFooter} data-print="hide">
          <Button size="small" appearance="secondary" onClick={navigation.openWhatIf}>
            {t('copilotInvestment.controls.whatIf')}
          </Button>
          {navigation.settingsHref && <Link href={navigation.settingsHref}>{t('copilotInvestment.controls.settings')}</Link>}
        </div>
      </Card>
    </div>
  );
}

/** One assumption's label, the value in force and where it came from, as text. */
export function assumptionDisplay(t: TFunction, field: string, summary: CopilotInvestmentSummary): {
  label: string;
  value: string;
  source: string;
  provenance: string;
} {
  const values = summary.assumptions.values;
  const currency = summary.investment.currency;
  let display = '\u2014';
  if (values) {
    switch (field) {
      case 'currency':
        display = values.currency ?? '\u2014';
        break;
      case 'seatPricePerMonth':
        display = formatPrice(values.seatPricePerMonth, currency);
        break;
      case 'creditPrice':
        display = formatPrice(values.creditPrice, currency);
        break;
      case 'studioCreditPrice':
        display = formatPrice(values.studioCreditPrice, currency);
        break;
      case 'hourlyCost':
        display = formatPrice(values.hourlyCost, currency);
        break;
      case 'minutesPerActiveDay.conservative':
        display = values.minutesPerActiveDay.conservative === null ? '\u2014' : formatMinutes(t, values.minutesPerActiveDay.conservative);
        break;
      case 'minutesPerActiveDay.base':
        display = values.minutesPerActiveDay.base === null ? '\u2014' : formatMinutes(t, values.minutesPerActiveDay.base);
        break;
      case 'minutesPerActiveDay.upside':
        display = values.minutesPerActiveDay.upside === null ? '\u2014' : formatMinutes(t, values.minutesPerActiveDay.upside);
        break;
      case 'realisation.conservative':
        display = formatShare(values.realisation.conservative);
        break;
      case 'realisation.base':
        display = formatShare(values.realisation.base);
        break;
      case 'realisation.upside':
        display = formatShare(values.realisation.upside);
        break;
      default:
        break;
    }
  }
  const stated = summary.assumptions.provenance[field] ?? 'missing';
  const provenance = stated === 'missing' || display === '\u2014' ? 'missing' : stated;
  return {
    label: FIELD_LABEL_KEYS[field] ? t(FIELD_LABEL_KEYS[field]) : field,
    value: provenance === 'missing' ? t('copilotInvestment.value.notSet') : display,
    source: t(PROVENANCE_LABEL_KEYS[provenance] ?? 'copilotInvestment.provenance.missing'),
    provenance,
  };
}

/** One assumption, its value in force and where it came from. */
export function AssumptionLine({ field, summary }: { field: string; summary: CopilotInvestmentSummary }) {
  const t = useT();
  const line = assumptionDisplay(t, field, summary);
  return (
    <>
      <Text size={200}>{line.label}</Text>
      <Text size={200} weight="semibold">
        {line.value}
      </Text>
      <Text size={100} style={{ color: line.provenance === 'scenario' ? tokens.colorBrandForeground1 : tokens.colorNeutralForeground3 }}>
        {line.source}
      </Text>
    </>
  );
}
