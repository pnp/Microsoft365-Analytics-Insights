import {
  Card,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { formatList, formatNumber, useT } from '../../i18n';
import type { CopilotInvestmentSummary, InvestmentScenario } from '../../types/copilotInvestment';
import {
  BAND_LABEL_KEYS,
  FIELD_LABEL_KEYS,
  SCENARIO_LABEL_KEYS,
  SectionHeading,
  formatCredits,
  formatDecimal,
  formatMinutes,
  formatMoney,
  formatMultiple,
  formatMultipleDelta,
  formatShare,
} from './investmentShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px', overflowX: 'auto' },
  columns: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: '16px', alignItems: 'start' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  grid: { display: 'grid', gridTemplateColumns: 'auto repeat(3, minmax(80px, 1fr))', gap: '4px' },
  gridHead: { padding: '6px 8px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  gridCell: {
    padding: '10px 8px',
    borderRadius: tokens.borderRadiusMedium,
    textAlign: 'center',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    fontVariantNumeric: 'tabular-nums',
  },
  paysBack: { backgroundColor: tokens.colorPaletteGreenBackground2, color: tokens.colorNeutralForeground1 },
  short: { backgroundColor: tokens.colorPaletteRedBackground2, color: tokens.colorNeutralForeground1 },
  baseCell: { outline: `2px solid ${tokens.colorBrandStroke1}` },
  facts: { display: 'grid', gridTemplateColumns: 'minmax(200px, 1fr) auto', gap: '6px 16px', alignItems: 'baseline' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const SCENARIOS: InvestmentScenario[] = ['conservative', 'base', 'upside'];

/** The value model laid out for an analyst: every scenario, every combination and the break-even in full. */
export default function ValuePanel({ summary }: { summary: CopilotInvestmentSummary }) {
  const styles = useStyles();
  const t = useT();
  const currency = summary.investment.currency;
  const value = summary.value;

  if (!value.available) {
    const missing = value.missing.map((f) => (FIELD_LABEL_KEYS[f] ? t(FIELD_LABEL_KEYS[f]) : f));
    return (
      <div className={styles.stack}>
        <Card className={styles.card}>
          {value.unavailableReason === 'noMatchedHolders' ? (
            <>
              <SectionHeading title={t('copilotInvestment.value.unmatched.title')} />
              <Text>{t('copilotInvestment.value.unmatched.body')}</Text>
            </>
          ) : (
            <>
              <SectionHeading title={t('copilotInvestment.value.missing.title')} />
              <Text>{t('copilotInvestment.value.missing.body', { fields: formatList(missing) })}</Text>
            </>
          )}
        </Card>
        <Cohorts summary={summary} />
      </div>
    );
  }

  const grid = summary.sensitivity.grid;
  const cell = (minutes: InvestmentScenario, realisation: InvestmentScenario) =>
    grid.find((c) => c.minutesScenario === minutes && c.realisationScenario === realisation);
  const minutesOf = (s: InvestmentScenario) => summary.assumptions.values?.minutesPerActiveDay[s] ?? null;
  const realisationOf = (s: InvestmentScenario) => summary.assumptions.values?.realisation[s] ?? null;
  const breakEven = summary.breakEven;
  const sensitivity = summary.sensitivity;
  const baseRoi = value.scenarios.find((s) => s.scenario === 'base')?.roiMultiple ?? null;
  // Each lever with how far it moves the base case, so a small move is still visible as a move.
  const lever = (roi: number | null): string => {
    const delta = formatMultipleDelta(t, roi, baseRoi);
    return delta === null ? formatMultiple(t, roi) : t('copilotInvestment.value.levers.delta', { value: formatMultiple(t, roi), delta });
  };

  return (
    <div className={styles.stack}>
      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotInvestment.value.scenarios.title')}
          description={t('copilotInvestment.value.scenarios.description')}
        />
        <Table size="small" aria-label={t('copilotInvestment.value.scenarios.title')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('copilotInvestment.value.col.scenario')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.minutes')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.realisation')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.assistedHours')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.realisedHours')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.value')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.monthly')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.roi')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.net')}</TableHeaderCell>
              <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.atOrAbove')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {value.scenarios.map((s) => (
              <TableRow key={s.scenario}>
                <TableCell>
                  <Text weight={s.scenario === 'base' ? 'semibold' : 'regular'}>{t(SCENARIO_LABEL_KEYS[s.scenario])}</Text>
                </TableCell>
                <TableCell className={styles.num}>{formatDecimal(s.minutesPerActiveDay)}</TableCell>
                <TableCell className={styles.num}>{formatShare(s.realisation)}</TableCell>
                <TableCell className={styles.num}>{formatNumber(s.assistedHours, { maximumFractionDigits: 0 })}</TableCell>
                <TableCell className={styles.num}>{formatNumber(s.realisedHours, { maximumFractionDigits: 0 })}</TableCell>
                <TableCell className={styles.num}>{formatMoney(s.value, currency)}</TableCell>
                <TableCell className={styles.num}>{formatMoney(s.monthlyValue, currency)}</TableCell>
                <TableCell className={styles.num}>{formatMultiple(t, s.roiMultiple)}</TableCell>
                <TableCell className={styles.num}>{formatMoney(s.net, currency)}</TableCell>
                <TableCell className={styles.num}>{formatNumber(s.holdersAtOrAboveBreakEven)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      <div className={styles.columns}>
        <Card className={styles.card}>
          <SectionHeading
            title={t('copilotInvestment.value.sensitivity.title')}
            description={t('copilotInvestment.value.sensitivity.description')}
          />
          <div className={styles.grid} role="table" aria-label={t('copilotInvestment.value.sensitivity.title')}>
            <div role="row" style={{ display: 'contents' }}>
              <span role="columnheader" className={styles.gridHead}>
                {t('copilotInvestment.value.sensitivity.minutes')}
              </span>
              {SCENARIOS.map((r) => (
                <span key={r} role="columnheader" className={styles.gridHead}>
                  {t(SCENARIO_LABEL_KEYS[r])}
                  <br />
                  {formatShare(realisationOf(r))}
                </span>
              ))}
            </div>
            {SCENARIOS.map((m) => (
              <div key={m} role="row" style={{ display: 'contents' }}>
                <span role="rowheader" className={styles.gridHead}>
                  {t(SCENARIO_LABEL_KEYS[m])}
                  <br />
                  {formatMinutes(t, minutesOf(m))}
                </span>
                {SCENARIOS.map((r) => {
                  const c = cell(m, r);
                  const roi = c?.roiMultiple ?? null;
                  const pays = roi !== null && roi >= 1;
                  return (
                    <span
                      key={r}
                      role="cell"
                      className={`${styles.gridCell} ${pays ? styles.paysBack : styles.short} ${m === 'base' && r === 'base' ? styles.baseCell : ''}`}
                      aria-label={t('copilotInvestment.value.sensitivity.cell', {
                        minutes: formatMinutes(t, minutesOf(m)),
                        realisation: formatShare(realisationOf(r)),
                        roi: formatMultiple(t, roi),
                      })}
                    >
                      <Text weight="semibold">{formatMultiple(t, roi)}</Text>
                      <Text size={100}>{t(pays ? 'copilotInvestment.value.sensitivity.paysBack' : 'copilotInvestment.value.sensitivity.short')}</Text>
                    </span>
                  );
                })}
              </div>
            ))}
          </div>
          <Text size={100} className={styles.muted}>
            {t('copilotInvestment.value.sensitivity.realisation')}
          </Text>
        </Card>

        <Card className={styles.card}>
          <SectionHeading title={t('copilotInvestment.value.levers.title')} />
          <div className={styles.facts}>
            <Text size={200}>{t('copilotInvestment.value.levers.hourlyMinus')}</Text>
            <Text weight="semibold">{lever(sensitivity.roiBaseHourlyMinus20)}</Text>
            <Text size={200}>{t('copilotInvestment.value.levers.hourlyPlus')}</Text>
            <Text weight="semibold">{lever(sensitivity.roiBaseHourlyPlus20)}</Text>
            {sensitivity.roiBaseAfterReclaim !== null && (
              <>
                <Text size={200}>{t('copilotInvestment.value.levers.afterReclaim')}</Text>
                <Text weight="semibold">{lever(sensitivity.roiBaseAfterReclaim)}</Text>
              </>
            )}
            {sensitivity.roiBaseIfBalancesMonthly !== null && (
              <>
                <Text size={200}>{t('copilotInvestment.value.levers.ifMonthly')}</Text>
                <Text weight="semibold">{lever(sensitivity.roiBaseIfBalancesMonthly)}</Text>
              </>
            )}
          </div>
          {sensitivity.roiBaseIfBalancesMonthly !== null && (
            <Text size={100} className={styles.muted}>
              {t('copilotInvestment.value.levers.ifMonthlyNote')}
            </Text>
          )}

          <SectionHeading title={t('copilotInvestment.value.breakEven.title')} />
          <div className={styles.facts}>
            <Text size={200}>{t('copilotInvestment.value.breakEven.minutes')}</Text>
            <Text weight="semibold">{formatMinutes(t, breakEven.minutesPerActiveDay)}</Text>
            <Text size={200}>{t('copilotInvestment.value.breakEven.margin')}</Text>
            <Text weight="semibold">{formatShare(breakEven.marginOfSafety)}</Text>
            <Text size={200}>{t('copilotInvestment.value.breakEven.daysPerSeat')}</Text>
            <Text weight="semibold">{formatDecimal(breakEven.activeDaysPerMonthPerSeat)}</Text>
            <Text size={200}>{t('copilotInvestment.value.breakEven.minutesPerWeekday')}</Text>
            <Text weight="semibold">{formatMinutes(t, breakEven.minutesPerWeekdayPerSeat)}</Text>
            <Text size={200}>{t('copilotInvestment.value.breakEven.atOrAbove')}</Text>
            <Text weight="semibold">{formatNumber(breakEven.holdersAtOrAbove)}</Text>
            <Text size={200}>{t('copilotInvestment.value.breakEven.below')}</Text>
            <Text weight="semibold">{formatNumber(breakEven.holdersBelow)}</Text>
            <Text size={200}>{t('copilotInvestment.value.breakEven.noEvidence')}</Text>
            <Text weight="semibold">{formatNumber(breakEven.holdersNoEvidence)}</Text>
          </div>
        </Card>
      </div>

      <Cohorts summary={summary} />
    </div>
  );
}

function Cohorts({ summary }: { summary: CopilotInvestmentSummary }) {
  const styles = useStyles();
  const t = useT();
  const currency = summary.investment.currency;
  const rows = summary.cohorts.filter((c) => c.holders > 0);
  // A month at a time, like the headline figures, so a 7- or 90-day period reads on the same scale.
  const months = summary.monthsInWindow > 0 ? summary.monthsInWindow : 1;
  const monthly = (value: number | null): number | null => (value === null ? null : value / months);
  return (
    <Card className={styles.card}>
      <SectionHeading title={t('copilotInvestment.value.cohorts.title')} description={t('copilotInvestment.value.cohorts.description')} />
      <Table size="small" aria-label={t('copilotInvestment.value.cohorts.title')}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('copilotInvestment.value.col.band')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.holders')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.share')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.medianDays')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.shareOfDays')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.cost')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.valueMonthly')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.roi')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.credits')}</TableHeaderCell>
            <TableHeaderCell className={styles.num}>{t('copilotInvestment.value.col.studio')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((c) => (
            <TableRow key={c.band}>
              <TableCell>{BAND_LABEL_KEYS[c.band] ? t(BAND_LABEL_KEYS[c.band]) : c.band}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.holders)}</TableCell>
              <TableCell className={styles.num}>{formatShare(c.shareOfHolders)}</TableCell>
              <TableCell className={styles.num}>{formatDecimal(c.medianActiveDays)}</TableCell>
              <TableCell className={styles.num}>{formatShare(c.shareOfActiveDays)}</TableCell>
              <TableCell className={styles.num}>{formatMoney(monthly(c.cost), currency)}</TableCell>
              <TableCell className={styles.num}>{formatMoney(monthly(c.valueBase), currency)}</TableCell>
              <TableCell className={styles.num}>{formatMultiple(t, c.roiBase)}</TableCell>
              <TableCell className={styles.num}>{formatCredits(c.credits)}</TableCell>
              <TableCell className={styles.num}>{formatCredits(c.studioCredits)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Card>
  );
}
