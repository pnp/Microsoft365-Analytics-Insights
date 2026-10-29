import { Card, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, makeStyles, tokens } from '@fluentui/react-components';
import { formatNumber, useT } from '../../i18n';
import type { CopilotInvestmentSummary } from '../../types/copilotInvestment';
import { assumptionDisplay } from './ExecutivePanel';
import { IMPACT_LABEL_KEYS, SectionHeading, TRUST_AREAS, TRUST_AREA_LABEL_KEYS, TRUST_LEVEL_LABEL_KEYS, formatShortDate, toneColour, trustText } from './investmentShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px' },
  area: { display: 'flex', flexDirection: 'column', gap: '8px' },
  formula: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusSmall,
    padding: '8px 10px',
    whiteSpace: 'pre-wrap',
  },
  rules: { margin: 0, paddingLeft: '20px', display: 'flex', flexDirection: 'column', gap: '6px' },
  provenance: { display: 'grid', gridTemplateColumns: 'minmax(200px, 1fr) auto auto', gap: '6px 16px', alignItems: 'baseline' },
  reason: { paddingLeft: '10px', borderLeftWidth: '3px', borderLeftStyle: 'solid', display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  note: { whiteSpace: 'pre-wrap' },
});

const FORMULA_KEYS = [
  'copilotInvestment.method.formula.value',
  'copilotInvestment.method.formula.cost',
  'copilotInvestment.method.formula.roi',
  'copilotInvestment.method.formula.breakEven',
  'copilotInvestment.method.formula.daysPerSeat',
  'copilotInvestment.method.formula.personBreakEven',
  'copilotInvestment.method.formula.path',
] as const;

const RULE_KEYS = [
  'copilotInvestment.method.rule.activeDays',
  'copilotInvestment.method.rule.population',
  'copilotInvestment.method.rule.balances',
  'copilotInvestment.method.rule.studio',
  'copilotInvestment.method.rule.sources',
  'copilotInvestment.method.rule.seats',
  'copilotInvestment.method.rule.noCausality',
] as const;

const ASSUMPTION_FIELDS = [
  'currency',
  'seatPricePerMonth',
  'creditPrice',
  'studioCreditPrice',
  'hourlyCost',
  'minutesPerActiveDay.conservative',
  'minutesPerActiveDay.base',
  'minutesPerActiveDay.upside',
  'realisation.conservative',
  'realisation.base',
  'realisation.upside',
];

/** How every figure is worked out, the assumptions in force and every caveat - the tab an analyst checks first. */
export default function MethodPanel({ summary }: { summary: CopilotInvestmentSummary }) {
  const styles = useStyles();
  const t = useT();
  const assumptions = summary.assumptions;
  const reasons = summary.trust.reasons
    .map((r) => ({ reason: r, text: trustText(t, r) }))
    .filter((r): r is { reason: typeof r.reason; text: string } => r.text !== null);

  return (
    <div className={styles.stack}>
      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.method.title')} description={t('copilotInvestment.method.description')} />
        <Text weight="semibold">{t('copilotInvestment.method.formulas')}</Text>
        {FORMULA_KEYS.map((key) => (
          <div key={key} className={styles.formula}>
            {t(key)}
          </div>
        ))}
        <Text weight="semibold">{t('copilotInvestment.method.rules')}</Text>
        <ul className={styles.rules}>
          {RULE_KEYS.map((key) => (
            <li key={key}>
              <Text size={200}>{t(key)}</Text>
            </li>
          ))}
        </ul>
      </Card>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.method.assumptions')} />
        <Table size="small" aria-label={t('copilotInvestment.method.assumptions')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('copilotInvestment.method.col.assumption')}</TableHeaderCell>
              <TableHeaderCell>{t('copilotInvestment.method.col.value')}</TableHeaderCell>
              <TableHeaderCell>{t('copilotInvestment.method.col.source')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {ASSUMPTION_FIELDS.map((field) => (
              <TableRow key={field}>
                <AssumptionCells field={field} summary={summary} />
              </TableRow>
            ))}
          </TableBody>
        </Table>
        {assumptions.updatedUtc && (
          <Text size={200}>
            {t('copilotInvestment.method.updated', { when: formatShortDate(assumptions.updatedUtc), version: formatNumber(assumptions.version) })}
          </Text>
        )}
        {assumptions.note && (
          <>
            <Text weight="semibold" size={200}>
              {t('copilotInvestment.method.note')}
            </Text>
            {/* The administrator's own words: shown verbatim, never translated. */}
            <Text size={200} className={styles.note}>
              {assumptions.note}
            </Text>
          </>
        )}
        {!assumptions.storageDurable && (
          <Text size={200} className={styles.muted}>
            {t('copilotInvestment.method.notDurable')}
          </Text>
        )}
      </Card>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.method.trust')} description={t('copilotInvestment.exec.trust.description')} />
        {reasons.length === 0 ? (
          <Text size={200}>{t('copilotInvestment.method.trustEmpty')}</Text>
        ) : (
          TRUST_AREAS.filter((area) => reasons.some((r) => r.reason.area === area)).map((area) => (
            <div key={area} className={styles.area}>
              <Text weight="semibold" size={300}>
                {t(TRUST_AREA_LABEL_KEYS[area])}
                {' \u00b7 '}
                {t(TRUST_LEVEL_LABEL_KEYS[summary.trust.areas.find((a) => a.area === area)?.level ?? 'high'] ?? 'copilotInvestment.exec.trust.level.medium')}
              </Text>
              {reasons
                .filter((r) => r.reason.area === area)
                .map(({ reason, text }) => (
                  <div
                    key={reason.code}
                    className={styles.reason}
                    style={{ borderLeftColor: toneColour(reason.impact === 'low' ? 'critical' : reason.impact === 'medium' ? 'warning' : 'neutral') }}
                  >
                    <Text size={100} className={styles.muted}>
                      {t(IMPACT_LABEL_KEYS[reason.impact] ?? 'copilotInvestment.impact.info')}
                    </Text>
                    <Text size={200}>{text}</Text>
                  </div>
                ))}
            </div>
          ))
        )}
      </Card>
    </div>
  );
}

/** The assumption line as table cells: the same value and provenance the summary shows. */
function AssumptionCells({ field, summary }: { field: string; summary: CopilotInvestmentSummary }) {
  const t = useT();
  const line = assumptionDisplay(t, field, summary);
  return (
    <>
      <TableCell>{line.label}</TableCell>
      <TableCell>
        <Text weight="semibold">{line.value}</Text>
      </TableCell>
      <TableCell>
        <Text style={{ color: line.provenance === 'scenario' ? tokens.colorBrandForeground1 : tokens.colorNeutralForeground3 }}>{line.source}</Text>
      </TableCell>
    </>
  );
}
