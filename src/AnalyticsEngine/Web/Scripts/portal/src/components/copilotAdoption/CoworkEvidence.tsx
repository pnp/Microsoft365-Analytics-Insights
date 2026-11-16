import { Button, Card, MessageBar, MessageBarBody, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { useT, type TranslationKey } from '../../i18n';
import { formatCount, formatDate } from '../shared/KpiGrid';
import { coworkAdoptionReason } from './coworkTimeSaved';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '8px' },
  note: { color: tokens.colorNeutralForeground2 },
  figures: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
    gap: '16px',
  },
});

const REASONS: Record<string, TranslationKey> = {
  auditMissing: 'copilotAdoptionCowork.coverage.auditMissing',
  incomplete: 'copilotAdoptionCowork.coverage.incomplete',
  historyUnknown: 'copilotAdoptionCowork.coverage.historyUnknown',
  historyLimited: 'copilotAdoptionCowork.coverage.historyLimited',
  notAssessed: 'copilotAdoptionCowork.coverage.notAssessed',
  workloadMissing: 'copilotAdoptionCowork.timeSaved.model.noActivity',
};

export function CoworkEvidenceNote({
  summary, model = true,
}: { summary: CopilotAdoptionSummary; model?: boolean }) {
  const t = useT();
  const styles = useStyles();
  const source = summary.dataSources;
  return (
    <div className={styles.stack}>
      <Text size={200}>{t('copilotAdoptionCowork.coverage.period', {
        from: formatDate(summary.fromUtc), to: formatDate(summary.toUtc),
      })}</Text>
      {summary.coworkReadinessAvailable && <Text size={200}>{t('copilotAdoptionCowork.coverage.assessed', {
        users: formatCount(summary.coworkScoredUsers ?? 0),
      })}</Text>}
      <Text size={200} className={styles.note}>{source?.auditHistoryStartUtc
        ? t('copilotAdoptionCowork.coverage.historyStart', { date: formatDate(source.auditHistoryStartUtc) })
        : t('copilotAdoptionCowork.coverage.historyUnknown')}</Text>
      <Text size={200} className={styles.note}>{t('copilotAdoptionCowork.coverage.refresh', {
        report: source?.m365UsageReportDate ? formatDate(source.m365UsageReportDate) : t('common.state.notAvailable'),
        analysis: formatDate(summary.generatedUtc),
      })}</Text>
      <Text size={200} className={styles.note}>{t('copilotAdoptionCowork.coverage.limitations')}</Text>
      {model && <Text size={200}>{t('copilotAdoptionCowork.coverage.baseline')}</Text>}
    </div>
  );
}

export function CoworkAdoptionUnavailable({ summary }: { summary: CopilotAdoptionSummary }) {
  const t = useT();
  const reason = coworkAdoptionReason(summary);
  const knownEmpty = reason == null
    && summary.coworkNonUserCeilingEstimate?.cohortUsers === 0;
  return (
    <Card>
      <Text weight="semibold">{t(knownEmpty
        ? 'copilotAdoptionCowork.coverage.emptyTitle'
        : 'copilotAdoptionCowork.coverage.unavailable')}</Text>
      <Text>{t(knownEmpty ? 'copilotAdoptionCowork.coverage.empty'
        : summary.coworkNewAdoptionWithheld || summary.coworkNonUserCeilingWithheld
        ? 'copilotAdoptionCowork.coverage.withheld'
        : REASONS[reason ?? 'notAssessed']
          ?? 'copilotAdoptionCowork.coverage.notAssessed')}</Text>
      <CoworkEvidenceNote summary={summary} />
    </Card>
  );
}

export function CoworkObservedUsagePanel({
  summary, onShowPeople,
}: { summary: CopilotAdoptionSummary; onShowPeople?: () => void }) {
  const t = useT();
  const styles = useStyles();
  const observed = summary.coworkObservedUsage;
  const available = observed?.available && observed.assessedUsers != null && observed.users != null
    && observed.interactions != null && observed.activeUserDays != null;
  return (
    <Card className={styles.stack}>
      <Text as="h2" size={400} weight="semibold">{t('copilotAdoptionCowork.observed.title')}</Text>
      {available ? (
        <>
          <Text>{t('copilotAdoptionCowork.observed.population', { users: formatCount(observed.assessedUsers ?? 0) })}</Text>
          <div className={styles.figures}>
            <div><Text block>{t('copilotAdoptionCowork.observed.users')}</Text>
              <Text size={600} weight="semibold">{formatCount(observed.users ?? 0)}</Text></div>
            <div><Text block>{t('copilotAdoptionCowork.observed.interactions')}</Text>
              <Text size={600} weight="semibold">{formatCount(observed.interactions ?? 0)}</Text></div>
            <div><Text block>{t('copilotAdoptionCowork.observed.activeUserDays')}</Text>
              <Text size={600} weight="semibold">{formatCount(observed.activeUserDays ?? 0)}</Text></div>
          </div>
          <Text size={200}>{t('copilotAdoptionCowork.observed.daysBasis')}</Text>
          <Text size={200}>{t('copilotAdoptionCowork.observed.recent', {
            date: observed.lastInteractionUtc ? formatDate(observed.lastInteractionUtc) : t('common.state.notAvailable'),
          })}</Text>
          {observed.partial && <MessageBar intent="warning"><MessageBarBody>
            {t('copilotAdoptionCowork.observed.partial')}
          </MessageBarBody></MessageBar>}
          {onShowPeople && <Button onClick={onShowPeople}>{t('copilotAdoptionCowork.observed.seePeople')}</Button>}
        </>
      ) : <Text>{t(observed?.withheldReason
        ? 'copilotAdoptionCowork.coverage.withheld'
        : 'copilotAdoptionCowork.observed.unavailable')}</Text>}
      <Text size={400} weight="semibold">{t('copilotAdoptionCowork.observed.notEstimated')}</Text>
      <Text>{t('copilotAdoptionCowork.observed.benefitExplanation')}</Text>
      <CoworkEvidenceNote summary={summary} model={false} />
    </Card>
  );
}
