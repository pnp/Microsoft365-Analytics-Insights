import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Badge, Body1, Button, Card, CardHeader, Field, Input, MessageBar, MessageBarBody, ProgressBar, Table, TableBody, TableCell, TableRow, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowClockwise16Regular, Dismiss16Regular, Play16Regular } from '@fluentui/react-icons';
import { cancelCopilotAuditBackfill, fetchCopilotAuditBackfill, startCopilotAuditBackfill } from '../api/copilotAuditBackfillApi';
import Spinner from '../components/Spinner';
import { formatUtc } from '../components/health/healthShared';
import { formatNumber, useT, type TranslationKey } from '../i18n';
import type { CopilotAuditBackfillJob, CopilotAuditBackfillPermissionStatus, CopilotAuditBackfillState } from '../types/copilotAuditBackfill';

const POLL_MS = 5000;

const STATE_KEYS: Record<CopilotAuditBackfillState, TranslationKey> = {
  queued: 'admin.copilotAuditBackfill.state.queued',
  running: 'admin.copilotAuditBackfill.state.running',
  completed: 'admin.copilotAuditBackfill.state.completed',
  completedWithGaps: 'admin.copilotAuditBackfill.state.completedWithGaps',
  failed: 'admin.copilotAuditBackfill.state.failed',
  cancelled: 'admin.copilotAuditBackfill.state.cancelled',
};

const PERMISSION_KEYS: Record<CopilotAuditBackfillPermissionStatus, TranslationKey> = {
  unknown: 'admin.copilotAuditBackfill.permission.unknown',
  granted: 'admin.copilotAuditBackfill.permission.granted',
  missing: 'admin.copilotAuditBackfill.permission.missing',
  noIdentity: 'admin.copilotAuditBackfill.permission.noIdentity',
};

const ERROR_KEYS: Record<string, TranslationKey> = {
  missingPermission: 'admin.copilotAuditBackfill.error.missingPermission',
  copilotImportOff: 'admin.copilotAuditBackfill.error.copilotImportOff',
  jobActive: 'admin.copilotAuditBackfill.error.jobActive',
  jobNotActive: 'admin.copilotAuditBackfill.error.jobNotActive',
  jobNotFound: 'admin.copilotAuditBackfill.error.jobNotFound',
  queryFailed: 'admin.copilotAuditBackfill.error.queryFailed',
  queryRejected: 'admin.copilotAuditBackfill.error.queryRejected',
  queryThrottled: 'admin.copilotAuditBackfill.error.queryThrottled',
  queryTruncated: 'admin.copilotAuditBackfill.error.queryTruncated',
  graphAccessDenied: 'admin.copilotAuditBackfill.error.graphAccessDenied',
  unrecognisedAuditData: 'admin.copilotAuditBackfill.error.unrecognisedAuditData',
  stateNotDurable: 'admin.copilotAuditBackfill.error.stateNotDurable',
  stateUnavailable: 'admin.copilotAuditBackfill.error.stateUnavailable',
  unexpected: 'admin.copilotAuditBackfill.error.unexpected',
};

const useStyles = makeStyles({
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'end' },
  cards: { display: 'flex', flexDirection: 'column', gap: '16px', marginTop: '16px' },
  label: { fontWeight: tokens.fontWeightSemibold, width: '250px' },
  muted: { color: tokens.colorNeutralForeground3 },
  previewNote: { color: tokens.colorNeutralForeground3, marginTop: '4px' },
  progress: { maxWidth: '520px' },
  titleRow: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
});

function isActive(job: CopilotAuditBackfillJob | null | undefined): job is CopilotAuditBackfillJob {
  return job?.state === 'queued' || job?.state === 'running';
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return <TableRow><TableCell className={styles.label}>{label}</TableCell><TableCell>{children}</TableCell></TableRow>;
}

export default function CopilotAuditBackfillPage() {
  const t = useT();
  const styles = useStyles();
  const [status, setStatus] = useState<Awaited<ReturnType<typeof fetchCopilotAuditBackfill>> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [startDate, setStartDate] = useState('');

  const load = useCallback(async () => {
    setError(null);
    try { setStatus(await fetchCopilotAuditBackfill()); }
    catch (e) {
      const code = e instanceof Error ? e.message : null;
      setError(code && ERROR_KEYS[code] ? t(ERROR_KEYS[code]) : t('admin.copilotAuditBackfill.error.load'));
    }
    finally { setLoading(false); }
  }, [t]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (!isActive(status?.latestJob)) return undefined;
    const id = window.setInterval(() => { void load(); }, POLL_MS);
    return () => window.clearInterval(id);
  }, [load, status?.latestJob]);

  const start = async () => {
    setError(null);
    try {
      const iso = startDate ? new Date(`${startDate}T00:00:00Z`).toISOString() : null;
      const job = await startCopilotAuditBackfill(iso);
      setStatus((prev) => ({ stateDurable: prev?.stateDurable ?? false, copilotImportEnabled: prev?.copilotImportEnabled ?? true, latestJob: job }));
    } catch (e) {
      const code = e instanceof Error ? e.message : null;
      setError(code && ERROR_KEYS[code] ? t(ERROR_KEYS[code]) : t('admin.copilotAuditBackfill.error.start'));
    }
  };

  const cancel = async () => {
    if (!status?.latestJob) return;
    setError(null);
    try {
      const job = await cancelCopilotAuditBackfill(status.latestJob.id);
      setStatus((prev) => prev ? { ...prev, latestJob: job } : prev);
    } catch (e) {
      const code = e instanceof Error ? e.message : null;
      setError(code && ERROR_KEYS[code] ? t(ERROR_KEYS[code]) : t('admin.copilotAuditBackfill.error.cancel'));
    }
  };

  if (loading) return <Spinner label={t('admin.copilotAuditBackfill.loading')} />;
  const job = status?.latestJob ?? null;
  const totalSlices = job ? job.pendingSlices + job.inFlightSlices + job.slicesCompleted : 0;
  const progress = totalSlices > 0 ? job!.slicesCompleted / totalSlices : undefined;

  return <div>
    <div className={styles.titleRow}>
      <Title3>{t('admin.copilotAuditBackfill.title')}</Title3>
      <Badge appearance="tint" color="brand" size="medium">{t('admin.copilotAuditBackfill.preview')}</Badge>
    </div>
    <Body1>{t('admin.copilotAuditBackfill.description')}</Body1>
    <Text role="note" block size={200} className={styles.previewNote}>{t('admin.copilotAuditBackfill.previewNote')}</Text>
    <div className={styles.cards}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {!status?.copilotImportEnabled && <MessageBar intent="warning"><MessageBarBody>{t('admin.copilotAuditBackfill.copilotOff')}</MessageBarBody></MessageBar>}
      {status && !status.stateDurable && <MessageBar intent="warning"><MessageBarBody>{t('admin.copilotAuditBackfill.stateNotDurable')}</MessageBarBody></MessageBar>}
      <Card>
        <CardHeader header={<Text weight="semibold">{t('admin.copilotAuditBackfill.start.title')}</Text>} />
        <div className={styles.actions}>
          <Field label={t('admin.copilotAuditBackfill.start.startDate')} hint={t('admin.copilotAuditBackfill.start.hint')}>
            <Input type="date" value={startDate} onChange={(_, data) => setStartDate(data.value)} />
          </Field>
          <Button icon={<Play16Regular />} appearance="primary" onClick={start} disabled={isActive(job) || !status?.copilotImportEnabled || !status?.stateDurable}>{t('admin.copilotAuditBackfill.start.button')}</Button>
          <Button icon={<ArrowClockwise16Regular />} onClick={load}>{t('admin.copilotAuditBackfill.refresh')}</Button>
          <Button icon={<Dismiss16Regular />} onClick={cancel} disabled={!isActive(job)}>{t('admin.copilotAuditBackfill.cancel')}</Button>
        </div>
      </Card>
      <Card>
        <CardHeader header={<Text weight="semibold">{t('admin.copilotAuditBackfill.status.title')}</Text>} />
        {job ? <>
          {isActive(job) && <ProgressBar className={styles.progress} value={progress} />}
          <Table size="small" aria-label={t('admin.copilotAuditBackfill.status.title')}><TableBody>
            <Row label={t('admin.copilotAuditBackfill.status.state')}>{t(STATE_KEYS[job.state])}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.window')}>{formatUtc(job.startUtc)} - {formatUtc(job.endUtc)}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.permission')}>{t(PERMISSION_KEYS[job.permissionStatus])}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.slices')}>{formatNumber(job.slicesCompleted)} / {formatNumber(totalSlices)}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.inFlight')}>{formatNumber(job.inFlightSlices)}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.records')}>{formatNumber(job.recordsImported)} / {formatNumber(job.recordsSeen)}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.recordsAlreadyPresent')}>{formatNumber(job.recordsAlreadyPresent)}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.currentSlice')}>{job.currentSliceStartUtc ? `${formatUtc(job.currentSliceStartUtc)} - ${formatUtc(job.currentSliceEndUtc ?? job.currentSliceStartUtc)}` : t('admin.common.unknown')}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.completedDays')}>{job.completedDays.length > 0 ? job.completedDays.slice(0, 12).join(', ') : t('admin.common.unknown')}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.failedDays')}>{job.failedDays.length > 0 ? job.failedDays.join(', ') : t('admin.common.unknown')}</Row>
            <Row label={t('admin.copilotAuditBackfill.status.incompleteDays')}>{job.incompleteDays.length > 0 ? job.incompleteDays.join(', ') : t('admin.common.unknown')}</Row>
            {job.lastErrorCode && <Row label={t('admin.copilotAuditBackfill.status.lastError')}>{t(ERROR_KEYS[job.lastErrorCode] ?? 'admin.copilotAuditBackfill.error.unexpected')}</Row>}
          </TableBody></Table>
        </> : <Text className={styles.muted}>{t('admin.copilotAuditBackfill.status.none')}</Text>}
      </Card>
    </div>
  </div>;
}
