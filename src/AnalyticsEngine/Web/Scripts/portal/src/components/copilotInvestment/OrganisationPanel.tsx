import {
  Card,
  Field,
  Select,
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
import { formatNumber, useT } from '../../i18n';
import type { CopilotInvestmentSummary, InvestmentGroupRow } from '../../types/copilotInvestment';
import type { UserFilterDimension } from '../../types/userFilter';
import { dimensionLabel } from '../userFilter/describeUserFilter';
import {
  LorenzChart,
  QUADRANT_LABEL_KEYS,
  SectionHeading,
  formatCredits,
  formatDecimal,
  formatMoney,
  formatMultiple,
  formatShare,
} from './investmentShared';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  card: { display: 'flex', flexDirection: 'column', gap: '12px', padding: '16px', overflowX: 'auto' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  charts: { display: 'flex', gap: '32px', flexWrap: 'wrap' },
  quadrants: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(160px, 1fr))', gap: '8px' },
  quadrant: { padding: '10px 12px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
  quadrantWarning: { outlineWidth: '2px', outlineStyle: 'solid', outlineColor: tokens.colorPaletteYellowBorderActive },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The directory attributes a breakdown can always use; managers and custom organisations are added for See PII. */
export const GROUPING_DIMENSIONS = ['department', 'companyName', 'country', 'jobTitle', 'officeLocation', 'emailDomain', 'usageLocation'];

export default function OrganisationPanel({ summary, dimension, onDimensionChange, canSeePii, customDimensions }: {
  summary: CopilotInvestmentSummary;
  dimension: string;
  onDimensionChange: (dimension: string) => void;
  canSeePii: boolean;
  customDimensions: UserFilterDimension[];
}) {
  const styles = useStyles();
  const t = useT();
  const currency = summary.investment.currency;
  const organisation = summary.organisation;
  const extra: string[] = canSeePii ? ['manager', ...customDimensions.filter((d) => d.kind === 'custom').map((d) => d.key)] : [];
  const options: string[] = [...GROUPING_DIMENSIONS, ...extra];
  const source = { dimensions: customDimensions };
  const concentration = summary.concentration;
  // A month at a time, like the headline figures, so every period reads on the same scale.
  const months = summary.monthsInWindow > 0 ? summary.monthsInWindow : 1;
  const monthly = (value: number | null): number | null => (value === null ? null : value / months);
  const withheld = t('copilotInvestment.org.withheld', { count: formatNumber(organisation.minGroupSize) });
  // A combined row of too few people shows a dash with its reason - never a zero, which would read as a measurement.
  const figure = (g: InvestmentGroupRow, text: string) =>
    g.withheld ? (
      <span role="img" aria-label={withheld} title={withheld}>
        {'\u2014'}
      </span>
    ) : (
      text
    );

  return (
    <div className={styles.stack}>
      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotInvestment.org.title')}
          description={t('copilotInvestment.org.description', { min: formatNumber(organisation.minGroupSize) })}
        />
        <div className={styles.toolbar} data-print="hide">
          <Field label={t('copilotInvestment.org.groupBy')}>
            <Select value={dimension} onChange={(_e, data) => onDimensionChange(data.value)}>
              {options.map((key) => (
                <option key={key} value={key}>
                  {dimensionLabel(t, key, source)}
                </option>
              ))}
            </Select>
          </Field>
        </div>
        {organisation.hidden ? (
          <Text>{t('copilotInvestment.org.hidden')}</Text>
        ) : organisation.groups.length === 0 ? (
          <Text>{t('copilotInvestment.org.empty')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotInvestment.org.title')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{dimensionLabel(t, organisation.dimension, source)}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.holders')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.active')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.daysPerHolder')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.cost')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.value')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.roi')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.credits')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.studio')}</TableHeaderCell>
                <TableHeaderCell className={styles.num}>{t('copilotInvestment.org.col.unlicensedActive')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {organisation.groups.map((g, index) => (
                <TableRow key={`${g.emptyKey ?? 'value'}-${g.label ?? ''}-${index}`}>
                  <TableCell>
                    {g.emptyKey === 'notSet'
                      ? t('copilotInvestment.value.notSet')
                      : g.emptyKey === 'otherSmallGroups'
                        ? t('copilotInvestment.value.otherSmallGroups')
                        : g.label}
                    {g.withheld && (
                      <Text size={100} block className={styles.muted}>
                        {withheld}
                      </Text>
                    )}
                  </TableCell>
                  <TableCell className={styles.num}>{figure(g, formatNumber(g.holders))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatShare(g.activeShare))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatDecimal(g.activeDaysPerHolderMonthly))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatMoney(monthly(g.cost), currency))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatMoney(monthly(g.valueBase), currency))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatMultiple(t, g.roiBase))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatCredits(g.credits))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatCredits(g.studioCredits))}</TableCell>
                  <TableCell className={styles.num}>{figure(g, formatNumber(g.unlicensedActiveUsers))}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {organisation.foldedGroups > 0 && (
          <Text size={200} className={styles.muted}>
            {t('copilotInvestment.org.folded', { count: formatNumber(organisation.foldedGroups) })}
          </Text>
        )}
      </Card>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotInvestment.concentration.title')} description={t('copilotInvestment.concentration.description')} />
        <div className={styles.charts}>
          {[
            { key: 'activeDays', title: t('copilotInvestment.concentration.activeDays'), data: concentration.activeDays },
            { key: 'credits', title: t('copilotInvestment.concentration.credits'), data: concentration.credits },
          ].map(({ key, title, data }) => (
            <div key={key} style={{ display: 'flex', flexDirection: 'column', gap: '6px', maxWidth: '260px' }}>
              <Text weight="semibold" size={300}>
                {title}
              </Text>
              {data && data.population > 1 ? (
                <LorenzChart
                  points={data.lorenz}
                  label={t('copilotInvestment.concentration.summary', {
                    fraction: formatShare(data.topFraction),
                    share: formatShare(data.topShare),
                    gini: formatNumber(data.gini, { maximumFractionDigits: 2, minimumFractionDigits: 2 }),
                  })}
                  equalityLabel={t('copilotInvestment.concentration.equality')}
                  curveLabel={t('copilotInvestment.concentration.curve')}
                />
              ) : (
                <Text size={200}>{t('copilotInvestment.concentration.none')}</Text>
              )}
            </div>
          ))}
        </div>
      </Card>

      {concentration.quadrants.length > 0 && (
        <Card className={styles.card}>
          <SectionHeading
            title={t('copilotInvestment.quadrants.title')}
            description={t('copilotInvestment.quadrants.description', {
              median: formatCredits(concentration.quadrantMedianCredits),
              days: formatDecimal(concentration.quadrantDaysPerMonth),
            })}
          />
          <div className={styles.quadrants}>
            {concentration.quadrants.map((q) => (
              <div key={q.key} className={`${styles.quadrant} ${q.key === 'highSpendThinEvidence' ? styles.quadrantWarning : ''}`}>
                <Text weight="semibold" size={300} block>
                  {QUADRANT_LABEL_KEYS[q.key] ? t(QUADRANT_LABEL_KEYS[q.key]) : q.key}
                </Text>
                <Text size={200}>
                  {t('copilotInvestment.quadrants.people', { count: formatNumber(q.people), credits: formatCredits(q.credits) })}
                </Text>
              </div>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}
