import { makeStyles, tokens, Text, Card, Badge } from '@fluentui/react-components';
import type { CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { UNSCOPED_SECTIONS } from '../../types/copilotAdoption';
import InfoTip from '../shared/InfoTip';
import SqlPopover from '../SqlPopover';
import { formatCount, formatDate } from '../shared/KpiGrid';
import { formatNumber, useT } from '../../i18n';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px 16px',
    borderLeftWidth: '4px',
    borderLeftStyle: 'solid',
    borderLeftColor: tokens.colorNeutralStroke1,
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '8px',
  },
  labelGroup: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
  },
  label: {
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  tools: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  figures: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'baseline',
    columnGap: '28px',
    rowGap: '4px',
  },
  figure: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '6px',
  },
  value: {
    fontSize: '22px',
    lineHeight: '30px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * Microsoft's own tenant figures, from the Microsoft 365 Copilot usage report the import already stores
 * (#642): one compact line, under the headline figures rather than among them.
 *
 * Kept out of the KPI grid on purpose. Every tile there is an audit-log figure about this page's period and
 * population; these are Microsoft's, about licensed users only, over Microsoft's own report period, and in
 * a different unit - a prompt is not an audit-log interaction. So the line names its source and states the
 * period and report date beside the numbers, carries its caveats on the surface rather than only behind the
 * "i", and is never summed with anything (#534). No external benchmark is shown beside it (#547).
 *
 * A figure Microsoft did not report is a dash with the reason, never a zero. Nothing is shown when no
 * summary has been imported - or by a server that predates these fields - rather than a row of blanks.
 */
export default function MicrosoftReportFigures({
  summary,
  sql,
}: {
  summary: CopilotAdoptionSummary;
  sql?: string | null;
}) {
  const styles = useStyles();
  const t = useT();

  const reportDate = summary.microsoftReportDate;
  const periodDays = summary.microsoftReportPeriodDays;
  if (!reportDate || periodDays == null) return null;

  const prompts = summary.microsoftReportPromptsSubmitted;
  const average = summary.microsoftReportAveragePromptsPerActiveUser;
  const anyMissing = prompts == null || average == null;
  const tenantWide = (summary.unscopedSections ?? []).includes(UNSCOPED_SECTIONS.microsoftReport);
  const title = t('copilotAdoption.page.microsoftReport.title');

  return (
    <Card className={styles.card} aria-label={title}>
      <div className={styles.head}>
        <span className={styles.labelGroup}>
          <Text size={200} weight="semibold" className={styles.label}>
            {title}
          </Text>
          <Text size={200} className={styles.muted}>
            {t('copilotAdoption.page.microsoftReport.period', {
              days: formatCount(periodDays),
              date: formatDate(reportDate),
            })}
          </Text>
          {tenantWide && (
            <Badge size="small" appearance="outline" color="informative">
              {t('copilotAdoption.page.microsoftReport.tenantWide')}
            </Badge>
          )}
        </span>
        <span className={styles.tools}>
          <InfoTip
            title={title}
            content={{
              what: t('copilotAdoption.page.microsoftReport.info.what'),
              how: t('copilotAdoption.page.microsoftReport.info.how'),
              source: t('copilotAdoption.page.microsoftReport.info.source'),
            }}
          />
          {sql && <SqlPopover sql={sql} title={t('copilotAdoption.page.sqlBehindTheseFigures')} />}
        </span>
      </div>
      <div className={styles.figures}>
        <span className={styles.figure}>
          <span className={styles.value}>{prompts == null ? '\u2014' : formatCount(prompts)}</span>
          <Text size={300}>{t('copilotAdoption.page.microsoftReport.promptsSubmitted')}</Text>
        </span>
        <span className={styles.figure}>
          <span className={styles.value}>
            {average == null ? '\u2014' : formatNumber(average, { maximumFractionDigits: 1 })}
          </span>
          <Text size={300}>{t('copilotAdoption.page.microsoftReport.averagePerActiveUser')}</Text>
        </span>
      </div>
      {anyMissing && (
        <Text size={200} className={styles.muted}>
          {summary.microsoftReportVersion === 'v1'
            ? t('copilotAdoption.page.microsoftReport.notInVersion1')
            : t('copilotAdoption.page.microsoftReport.notReported')}
        </Text>
      )}
      <Text size={200} className={styles.muted}>
        {t('copilotAdoption.page.microsoftReport.caveat')}
      </Text>
    </Card>
  );
}
