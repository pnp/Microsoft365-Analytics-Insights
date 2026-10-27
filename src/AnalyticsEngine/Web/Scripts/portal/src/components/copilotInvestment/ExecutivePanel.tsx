import { Button, Card, Link, Text, makeStyles, tokens } from '@fluentui/react-components';
import { CheckmarkCircle16Regular, Info16Regular, Lightbulb16Regular, Warning16Regular } from '@fluentui/react-icons';
import { formatList, formatNumber, useT, type TFunction } from '../../i18n';
import { KpiGrid, type KpiDefinition, type KpiTone } from '../shared/KpiGrid';
import type { CopilotInvestmentSummary, InvestmentAction } from '../../types/copilotInvestment';
import {
  DISTRIBUTION_BUCKET_KEYS,
  FIELD_LABEL_KEYS,
  LabelledBars,
  PROVENANCE_LABEL_KEYS,
  SectionHeading,
  ValueVsCost,
  actionText,
  findingText,
  formatCredits,
  formatDecimal,
  formatMinutes,
  formatMoney,
  formatMultiple,
  formatPrice,
  formatShare,
  toneColour,
  trustText,
} from './investmentShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  columns: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(340px, 1fr))', gap: '16px', alignItems: 'start' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px' },
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
  trustLevel: { fontWeight: tokens.fontWeightSemibold },
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

function roiTone(conservative: number | null, base: number | null): KpiTone {
  if (conservative !== null && conservative >= 1) return 'good';
  if (base !== null && base >= 1) return 'warning';
  return base === null ? 'neutral' : 'critical';
}

function headline(summary: CopilotInvestmentSummary, t: TFunction): KpiDefinition[] {
  const currency = summary.investment.currency;
  const seats = summary.investment.seats;
  const evidence = summary.evidence;
  const scenarios = Object.fromEntries(summary.value.scenarios.map((s) => [s.scenario, s]));
  const conservative = scenarios.conservative ?? null;
  const base = scenarios.base ?? null;
  const upside = scenarios.upside ?? null;
  const modelled = t('copilotInvestment.badge.modelled');
  const items: KpiDefinition[] = [];

  items.push({
    key: 'runRate',
    label: t('copilotInvestment.kpi.runRate.label'),
    value: seats.monthlyRunRate !== null ? t('copilotInvestment.format.perMonth', { value: formatMoney(seats.monthlyRunRate, currency) }) : '\u2014',
    hint:
      seats.monthlyRunRate !== null
        ? t('copilotInvestment.kpi.runRate.hint', {
            holders: formatNumber(seats.holders),
            price: formatPrice(summary.assumptions.values?.seatPricePerMonth ?? null, currency),
          })
        : t('copilotInvestment.kpi.runRate.missing'),
    modelledBadge: modelled,
    info: {
      what: t('copilotInvestment.kpi.runRate.what'),
      how: t('copilotInvestment.kpi.runRate.how'),
      formula: t('copilotInvestment.kpi.runRate.formula'),
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
      value: t('copilotInvestment.format.perMonth', { value: formatMoney(seats.costPerActiveHolderMonthly, currency) }),
      hint: t('copilotInvestment.kpi.costPerActive.hint'),
      modelledBadge: modelled,
      info: {
        what: t('copilotInvestment.kpi.costPerActive.what'),
        how: t('copilotInvestment.kpi.costPerActive.how'),
        formula: t('copilotInvestment.kpi.costPerActive.formula'),
      },
    });
  }

  items.push({
    key: 'value',
    label: t('copilotInvestment.kpi.value.label'),
    value: base ? t('copilotInvestment.format.perMonth', { value: formatMoney(base.monthlyValue, currency) }) : '\u2014',
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
    items.push({
      key: 'studio',
      label: t('copilotInvestment.kpi.studio.label'),
      value: formatCredits(studio.credits),
      hint:
        studio.priorCredits > 0
          ? t('copilotInvestment.kpi.studio.hint', { prior: formatCredits(studio.priorCredits), days: formatNumber(summary.windowDays) })
          : t('copilotInvestment.kpi.studio.hintNoPrior', { days: formatNumber(summary.windowDays) }),
      info: {
        what: t('copilotInvestment.kpi.studio.what'),
        how: t('copilotInvestment.kpi.studio.how'),
        source: t('copilotInvestment.kpi.studio.source'),
      },
    });
  }
  return items;
}

function ToneIcon({ tone }: { tone: string }) {
  const styles = useStyles();
  if (tone === 'good') return <CheckmarkCircle16Regular className={styles.icon} aria-hidden="true" />;
  if (tone === 'warning' || tone === 'critical') return <Warning16Regular className={styles.icon} aria-hidden="true" />;
  if (tone === 'opportunity') return <Lightbulb16Regular className={styles.icon} aria-hidden="true" />;
  return <Info16Regular className={styles.icon} aria-hidden="true" />;
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
  const drill = canSeePii && navigation.seePeople && action.people > 0 && (action.signal || action.flag);
  return (
    <Card className={styles.action} style={{ borderLeft: `4px solid ${toneColour(action.code === 'reclaimIdle' || action.code === 'reviewHighSpendThinEvidence' ? 'warning' : 'opportunity')}` }}>
      <Text weight="semibold">{text.title}</Text>
      <Text size={200}>{text.body}</Text>
      {action.monthlyAmount !== null && (
        <Text size={200} className={styles.metric}>
          {t('copilotInvestment.exec.actions.monthly', { amount: formatMoney(action.monthlyAmount, currency) })}
        </Text>
      )}
      {action.roiBaseAfter !== null && (
        <Text size={200}>{t('copilotInvestment.action.reclaimIdle.after', { roi: formatMultiple(t, action.roiBaseAfter) })}</Text>
      )}
      {action.assumesMonthlyBalances && (
        <Text size={100} className={styles.muted}>
          {t('copilotInvestment.action.monthlyBalanceCaveat')}
        </Text>
      )}
      <div className={styles.actionFooter} data-print="hide">
        {drill && (
          <Button size="small" appearance="secondary" onClick={() => navigation.seePeople?.({ signal: action.signal ?? undefined, flag: action.flag ?? undefined })}>
            {t('copilotInvestment.exec.actions.seePeople', { count: formatNumber(action.people) })}
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

/**
 * The executive landing: what is being invested, what the evidence supports, what break-even requires, how far
 * to trust it and what to do next - in that order, each figure carrying its definition.
 */
export default function ExecutivePanel({ summary, navigation, canSeePii }: {
  summary: CopilotInvestmentSummary;
  navigation: ExecutiveNavigation;
  canSeePii: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const currency = summary.investment.currency;
  const scenarios = Object.fromEntries(summary.value.scenarios.map((s) => [s.scenario, s]));
  const findings = summary.findings
    .map((f) => ({ finding: f, text: findingText(t, f, currency) }))
    .filter((f): f is { finding: typeof f.finding; text: string } => f.text !== null);
  const reasons = summary.trust.reasons
    .map((r) => ({ reason: r, text: trustText(t, r) }))
    .filter((r): r is { reason: typeof r.reason; text: string } => r.text !== null);
  const assumptions = summary.assumptions;
  const missingFields = assumptions.missingForValue.map((f) => (FIELD_LABEL_KEYS[f] ? t(FIELD_LABEL_KEYS[f]) : f));
  const breakEvenDays = summary.breakEven.activeDaysPerMonthPerSeat;

  return (
    <div className={styles.stack}>
      <KpiGrid items={headline(summary, t)} />

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
            <Text size={200}>{t('copilotInvestment.exec.valueVsCost.missing')}</Text>
          )}
        </Card>

        <Card className={styles.card}>
          <SectionHeading title={t('copilotInvestment.exec.trust.title')} />
          <Text className={styles.trustLevel}>
            {t(summary.trust.level === 'high' ? 'copilotInvestment.exec.trust.high' : summary.trust.level === 'low' ? 'copilotInvestment.exec.trust.low' : 'copilotInvestment.exec.trust.medium')}
          </Text>
          {reasons.length === 0 ? (
            <Text size={200}>{t('copilotInvestment.exec.trust.none')}</Text>
          ) : (
            <ul className={styles.list}>
              {reasons.slice(0, 3).map(({ reason, text }) => (
                <li key={reason.code} className={styles.finding} style={{ borderLeftColor: toneColour(reason.impact === 'low' ? 'critical' : reason.impact === 'medium' ? 'warning' : 'neutral') }}>
                  <Text size={200}>{text}</Text>
                </li>
              ))}
            </ul>
          )}
          {reasons.length > 3 && (
            <Link as="button" onClick={() => navigation.openTab('method')} data-print="hide">
              {t('copilotInvestment.exec.trust.more', { count: formatNumber(reasons.length) })}
            </Link>
          )}
        </Card>
      </div>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.exec.findings.title')} />
        {findings.length === 0 ? (
          <Text size={200}>{t('copilotInvestment.exec.findings.empty')}</Text>
        ) : (
          <ul className={styles.list}>
            {findings.map(({ finding, text }) => (
              <li key={finding.code} className={styles.finding} style={{ borderLeftColor: toneColour(finding.tone) }}>
                <ToneIcon tone={finding.tone} />
                <Text size={300}>{text}</Text>
              </li>
            ))}
          </ul>
        )}
      </Card>

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
          <SectionHeading title={t('copilotInvestment.exec.distribution.title')} description={t('copilotInvestment.exec.distribution.description')} />
          <LabelledBars
            rows={summary.evidence.distribution.map((bucket) => ({
              key: bucket.key,
              label: t(DISTRIBUTION_BUCKET_KEYS[bucket.key] ?? 'copilotInvestment.value.unknown'),
              value: bucket.holders,
              display: t('copilotInvestment.exec.distribution.holders', { count: formatNumber(bucket.holders) }),
              marked: bucket.breakEvenBucket,
            }))}
          />
          {breakEvenDays !== null && (
            <Text size={200} weight="semibold">
              {t('copilotInvestment.exec.distribution.breakEven', { days: formatDecimal(breakEvenDays) })}
            </Text>
          )}
          <Text size={100} className={styles.muted}>
            {t('copilotInvestment.exec.distribution.axis')}
          </Text>
        </Card>

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
