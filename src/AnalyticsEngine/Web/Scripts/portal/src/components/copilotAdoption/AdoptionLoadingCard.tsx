import {
  Accordion, AccordionHeader, AccordionItem, AccordionPanel,
  Button, Card, MessageBar, MessageBarBody, ProgressBar, Text, makeStyles, tokens,
} from '@fluentui/react-components';
import type { AdoptionProgress } from '../../types/copilotAdoption';
import { adoptionFailureText, type AdoptionFailure } from '../../api/copilotAdoptionApi';
import { formatNumber, useT, type TranslationKey } from '../../i18n';
import { reportQueryFailureText } from '../shared/reportQueryFailure';

const STEP_KEYS: Record<string, TranslationKey> = {
  LicenceTypes: 'copilotAdoption.loading.step.licences',
  DataSourceProbes: 'copilotAdoption.loading.step.sources',
  LicensedUsers: 'copilotAdoption.loading.step.users',
  UsageByApp: 'copilotAdoption.loading.step.apps',
  SeatHolderTimeSaved: 'copilotAdoption.loading.step.timeSaved',
  WeeklyTrend: 'copilotAdoption.loading.step.trend',
  LicenceOpportunities: 'copilotAdoption.loading.step.opportunities',
  CoworkReadiness: 'copilotAdoption.loading.step.cowork',
  AgentEstate: 'copilotAdoption.loading.step.agents',
  AgentReach: 'copilotAdoption.loading.step.agentReach',
  AgentGrowth: 'copilotAdoption.loading.step.agentGrowth',
  UnlicensedPopulation: 'copilotAdoption.loading.step.unlicensed',
  ResourceTypes: 'copilotAdoption.loading.step.resources',
  MicrosoftReportFigures: 'copilotAdoption.loading.step.microsoft',
  Scoring: 'copilotAdoption.loading.step.scoring',
  Publishing: 'copilotAdoption.loading.step.publishing',
};
const STATE_KEYS: Record<string, TranslationKey> = {
  pending: 'copilotAdoption.loading.state.pending',
  running: 'copilotAdoption.loading.state.running',
  loaded: 'copilotAdoption.loading.state.loaded',
  failed: 'copilotAdoption.loading.state.failed',
  skipped: 'copilotAdoption.loading.state.skipped',
  blocked: 'copilotAdoption.loading.state.blocked',
};
const PHASE_KEYS: Record<string, TranslationKey> = {
  starting: 'copilotAdoption.loading.phase.starting',
  queued: 'copilotAdoption.loading.phase.queued',
  analysing: 'copilotAdoption.loading.phase.analysing',
  scoring: 'copilotAdoption.loading.phase.scoring',
  publishing: 'copilotAdoption.loading.phase.publishing',
};

export type OptionalAdoptionLoad = {
  state: 'pending' | 'running' | 'loaded' | 'failed';
  error?: AdoptionFailure;
  retryable?: boolean;
};

const useStyles = makeStyles({
  card: { marginTop: '16px', gap: '12px' },
  heading: { display: 'flex', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground2 },
  rows: { listStyleType: 'none', padding: 0, margin: 0 },
  row: {
    display: 'flex', justifyContent: 'space-between', alignItems: 'center',
    flexWrap: 'wrap', gap: '8px', paddingBlock: '6px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  failed: { color: tokens.colorPaletteRedForeground1 },
  actions: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '12px' },
});

export default function AdoptionLoadingCard({
  progress, busy, keepingReport, error, filters, sql, onCheckAgain, onRebuild,
  onRetryFilters, onRetrySql, cooldownSeconds,
}: {
  progress: AdoptionProgress | null;
  busy: boolean;
  keepingReport: boolean;
  error: AdoptionFailure | null;
  filters: OptionalAdoptionLoad;
  sql: OptionalAdoptionLoad;
  onCheckAgain?: () => void;
  onRebuild?: () => void;
  onRetryFilters: () => void;
  onRetrySql: () => void;
  cooldownSeconds: number;
}) {
  const styles = useStyles();
  const t = useT();
  const failed = progress?.steps.some(step => step.state === 'failed') ?? false;
  const needsAttention = failed || filters.state === 'failed' || sql.state === 'failed';
  const optionalBusy = filters.state === 'running' || sql.state === 'running';
  const preparing = (busy && progress?.phase === 'completed') || (!busy && optionalBusy);
  const title = preparing ? t('copilotAdoption.loading.preparingTitle') : busy
    ? t(keepingReport ? 'copilotAdoption.loading.rebuilding' : 'copilotAdoption.page.loading.label')
    : t(error || failed || filters.error || sql.error ? 'copilotAdoption.loading.attention' : 'copilotAdoption.loading.ready');
  const percent = progress?.percentComplete;

  return (
    <Card className={styles.card} data-print="hide" aria-busy={busy || optionalBusy}>
      <div className={styles.heading} role="status" aria-live="polite">
        <Text weight="semibold">{title}</Text>
        {progress && (
          <Text className={styles.muted}>
            {percent != null && <>{t('copilotAdoption.loading.percent', { percent: formatNumber(percent) })}{' · '}</>}
            {t('copilotAdoption.loading.elapsed', { seconds: formatNumber(Math.floor(progress.elapsedMs / 1000)) })}
          </Text>
        )}
      </div>
      {(busy || optionalBusy) && (
        <>
          <ProgressBar
            value={!preparing && percent != null ? percent / 100 : undefined}
            aria-label={t('copilotAdoption.loading.progressLabel')}
          />
          <Text size={200} className={styles.muted}>
            {preparing || (!busy && optionalBusy)
              ? t('copilotAdoption.loading.preparing')
              : t(PHASE_KEYS[progress?.phase ?? 'starting'] ?? 'copilotAdoption.loading.phase.starting')}
            {' '}
            {t('copilotAdoption.loading.notTimeEstimate')}
          </Text>
        </>
      )}
      {keepingReport && busy && <Text>{t('copilotAdoption.loading.previousReport')}</Text>}
      {error && <MessageBar intent="error"><MessageBarBody>{adoptionFailureText(t, error)}</MessageBarBody></MessageBar>}
      <Accordion key={`${progress?.generationId ?? 'starting'}:${needsAttention}`} collapsible defaultOpenItems={busy || needsAttention ? ['steps'] : []}>
        <AccordionItem value="steps">
          <AccordionHeader>{t('copilotAdoption.loading.details')}</AccordionHeader>
          <AccordionPanel>
            <ul className={styles.rows}>
              {progress?.steps.map(step => (
                <li key={step.step} className={styles.row}>
                  <div>
                    <Text>{t(STEP_KEYS[step.step] ?? 'copilotAdoption.loading.step.other')}</Text>
                    {step.failure && (
                      <Text block size={200} className={styles.failed}>
                        {reportQueryFailureText(t, step.failure, step.reference ?? null, '')}
                      </Text>
                    )}
                  </div>
                  <Text className={step.state === 'failed' ? styles.failed : styles.muted}>
                    {t(STATE_KEYS[step.state] ?? 'copilotAdoption.loading.state.pending')}
                    {(step.state === 'running' || step.durationMs > 0) && (
                      <> · {t('copilotAdoption.loading.elapsed', { seconds: formatNumber(Math.floor(step.durationMs / 1000)) })}</>
                    )}
                  </Text>
                </li>
              ))}
              {([
                ['copilotAdoption.loading.filters', filters, onRetryFilters],
                ['copilotAdoption.loading.sql', sql, onRetrySql],
              ] as const).map(([key, load, retry]) => (
                <li key={key} className={styles.row}>
                  <div>
                    <Text>{t(key)}</Text>
                    {load.error && <Text block size={200} className={styles.failed}>{adoptionFailureText(t, load.error)}</Text>}
                  </div>
                  {load.state === 'failed' && load.retryable
                    ? <Button onClick={retry} disabled={busy}>{t('copilotAdoption.loading.retryRequest')}</Button>
                    : <Text className={styles.muted}>{t(STATE_KEYS[load.state])}</Text>}
                </li>
              ))}
            </ul>
          </AccordionPanel>
        </AccordionItem>
      </Accordion>
      {(onCheckAgain || onRebuild) && (
        <div className={styles.actions}>
          {onCheckAgain && (
            <Button onClick={onCheckAgain} disabled={busy}>{t('copilotAdoption.loading.checkAgain')}</Button>
          )}
          {onRebuild && (
            <>
              <Button appearance="primary" onClick={onRebuild} disabled={busy || cooldownSeconds > 0}>
                {t('copilotAdoption.loading.retryAnalysis')}
              </Button>
              <Text size={200} className={styles.muted}>
                {cooldownSeconds > 0
                  ? t('copilotAdoption.loading.cooldown', { seconds: formatNumber(cooldownSeconds) })
                  : t('copilotAdoption.loading.rebuildCost')}
              </Text>
            </>
          )}
        </div>
      )}
    </Card>
  );
}
