import { Button, Card, MessageBar, MessageBarBody, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import { useId, useState, type ReactNode } from 'react';
import type { CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { useT, type TranslationKey } from '../../i18n';
import { formatCount, formatDate } from '../shared/KpiGrid';
import { coworkAdoptionReason } from './coworkTimeSaved';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '8px' },
  note: { color: tokens.colorNeutralForeground2 },
  evidence: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: '16px',
    marginTop: '8px',
  },
  facts: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
    gap: '12px 24px',
    margin: 0,
  },
  factValue: { margin: '4px 0 0', fontVariantNumeric: 'tabular-nums' },
  disclosureToggle: {
    justifyContent: 'flex-start',
    textAlign: 'start',
    minHeight: '44px',
  },
  disclosureBody: {
    padding: '4px 12px 8px',
    color: tokens.colorNeutralForeground2,
    '@media print': { display: 'block' },
  },
  limits: {
    margin: 0,
    paddingLeft: '20px',
  },
  limit: { marginBottom: '8px' },
  benefit: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '16px',
    marginTop: '8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  action: { alignSelf: 'flex-start' },
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

function EvidenceDisclosure({ labelKey, children }: { labelKey: TranslationKey; children: ReactNode }) {
  const t = useT();
  const styles = useStyles();
  const id = useId();
  const [expanded, setExpanded] = useState(false);
  return (
    <div>
      <Button
        id={`${id}-button`}
        appearance="subtle"
        className={styles.disclosureToggle}
        icon={expanded ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
        data-print="hide"
        aria-expanded={expanded}
        aria-controls={id}
        onClick={() => setExpanded(!expanded)}
      >
        {t(labelKey)}
      </Button>
      <Text data-print="only" weight="semibold">{t(labelKey)}</Text>
      {/* Keep caveats on paper even when the reader leaves them collapsed on screen. */}
      <div id={id} role="region" aria-labelledby={`${id}-button`} hidden={!expanded} className={styles.disclosureBody}>
        <div className={styles.stack}>{children}</div>
      </div>
    </div>
  );
}

export function CoworkEvidenceNote({
  summary, model = true,
}: { summary: CopilotAdoptionSummary; model?: boolean }) {
  const t = useT();
  const styles = useStyles();
  const source = summary.dataSources;
  return (
    <div className={styles.evidence}>
      <dl className={styles.facts}>
        <div>
          <dt><Text size={200} className={styles.note}>{t('copilotAdoptionCowork.coverage.periodLabel')}</Text></dt>
          <dd className={styles.factValue}><Text weight="semibold">{t('copilotAdoptionCowork.coverage.dateRange', {
            from: formatDate(summary.fromUtc), to: formatDate(summary.toUtc),
          })}</Text></dd>
        </div>
        {model && summary.coworkReadinessAvailable && <div>
          <dt><Text size={200} className={styles.note}>{t('copilotAdoptionCowork.coverage.assessedLabel')}</Text></dt>
          <dd className={styles.factValue}><Text weight="semibold">{formatCount(summary.coworkScoredUsers ?? 0)}</Text></dd>
        </div>}
        <div>
          <dt><Text size={200} className={styles.note}>{t('copilotAdoptionCowork.coverage.reportLabel')}</Text></dt>
          <dd className={styles.factValue}><Text weight="semibold">{source?.m365UsageReportDate
            ? formatDate(source.m365UsageReportDate) : t('common.state.notAvailable')}</Text></dd>
        </div>
        <div>
          <dt><Text size={200} className={styles.note}>{t('copilotAdoptionCowork.coverage.analysisLabel')}</Text></dt>
          <dd className={styles.factValue}><Text weight="semibold">{formatDate(summary.generatedUtc)}</Text></dd>
        </div>
      </dl>
      {model && <Text size={200}>{t('copilotAdoptionCowork.coverage.modelBasis')}</Text>}
      <EvidenceDisclosure labelKey="copilotAdoptionCowork.coverage.details">
        <Text size={200}>{source?.auditHistoryStartUtc
          ? t('copilotAdoptionCowork.coverage.historyStart', { date: formatDate(source.auditHistoryStartUtc) })
          : t('copilotAdoptionCowork.coverage.historyUnknown')}</Text>
        <ul className={styles.limits}>
          <li className={styles.limit}><Text size={200}>{t('copilotAdoptionCowork.coverage.observationLimits')}</Text></li>
          <li className={styles.limit}><Text size={200}>{t('copilotAdoptionCowork.coverage.noUseLimits')}</Text></li>
          <li className={styles.limit}><Text size={200}>{t('copilotAdoptionCowork.coverage.policyLimits')}</Text></li>
          {model && summary.coworkReadinessAvailable && <li className={styles.limit}><Text size={200}>{t('copilotAdoptionCowork.coverage.assessed', {
            users: formatCount(summary.coworkScoredUsers ?? 0),
          })}</Text></li>}
          {model && <li className={styles.limit}><Text size={200}>{t('copilotAdoptionCowork.coverage.baseline')}</Text></li>}
        </ul>
      </EvidenceDisclosure>
    </div>
  );
}

export function CoworkAdoptionUnavailable({
  summary, children, showEvidence = true,
}: { summary: CopilotAdoptionSummary; children?: ReactNode; showEvidence?: boolean }) {
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
      {showEvidence && <CoworkEvidenceNote summary={summary} />}
      {children}
    </Card>
  );
}

export function CoworkObservedUsagePanel({
  summary, onShowPeople, showEvidence = false,
}: { summary: CopilotAdoptionSummary; onShowPeople?: () => void; showEvidence?: boolean }) {
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
          <Text size={200}>{t('copilotAdoptionCowork.observed.recent', {
            date: observed.lastInteractionUtc ? formatDate(observed.lastInteractionUtc) : t('common.state.notAvailable'),
          })}</Text>
          {observed.partial && <MessageBar intent="warning"><MessageBarBody>
            {t('copilotAdoptionCowork.observed.partial')}
          </MessageBarBody></MessageBar>}
          {onShowPeople && <Button className={styles.action} data-print="hide" onClick={onShowPeople}>{t('copilotAdoptionCowork.observed.seePeople')}</Button>}
        </>
      ) : <Text>{t(observed?.withheldReason
        ? 'copilotAdoptionCowork.coverage.withheld'
        : 'copilotAdoptionCowork.observed.unavailable')}</Text>}
      <div className={styles.benefit}>
        <Text size={400} weight="semibold">{t('copilotAdoptionCowork.observed.notEstimated')}</Text>
        <Text size={200}>{t('copilotAdoptionCowork.observed.benefitSummary')}</Text>
        <EvidenceDisclosure labelKey="copilotAdoptionCowork.observed.details">
          <Text size={200}>{t('copilotAdoptionCowork.observed.daysBasis')}</Text>
          <Text size={200}>{t('copilotAdoptionCowork.observed.benefitExplanation')}</Text>
        </EvidenceDisclosure>
      </div>
      {showEvidence && <CoworkEvidenceNote summary={summary} model={false} />}
    </Card>
  );
}
