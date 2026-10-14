import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Body1, Button, Card, CardHeader, Field, Input, MessageBar, MessageBarBody, Table, TableBody, TableCell, TableRow, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowClockwise16Regular, Delete16Regular, Save16Regular } from '@fluentui/react-icons';
import { fetchLeadershipCohort, refreshLeadershipCohort, saveLeadershipCohort } from '../api/leadershipCohortApi';
import { PortalPermissionError, SessionExpiredError } from '../api/http';
import Spinner from '../components/Spinner';
import { formatUtc } from '../components/health/healthShared';
import { formatNumber, useT, type TranslationKey } from '../i18n';
import type { LeadershipCohortErrorCode, LeadershipCohortFailureKind, LeadershipCohortRefreshStatus, LeadershipCohortStatus } from '../types/leadershipCohort';

/** `LeadershipCohortRefreshStatuses` in Common.Entities. */
export const LEADERSHIP_REFRESH_STATUS_KEYS: Record<LeadershipCohortRefreshStatus, TranslationKey> = {
  ready: 'admin.leadershipCohort.refreshStatus.ready',
  groupNotFound: 'admin.leadershipCohort.refreshStatus.groupNotFound',
  permissionMissing: 'admin.leadershipCohort.refreshStatus.permissionMissing',
  tooLarge: 'admin.leadershipCohort.refreshStatus.tooLarge',
  failed: 'admin.leadershipCohort.refreshStatus.failed',
};

/** `LeadershipCohortFailureKinds` in Common.Entities. */
export const LEADERSHIP_FAILURE_KIND_KEYS: Record<LeadershipCohortFailureKind, TranslationKey> = {
  graphError: 'admin.leadershipCohort.failureKind.graphError',
  graphClient: 'admin.leadershipCohort.failureKind.graphClient',
  sqlError: 'admin.leadershipCohort.failureKind.sqlError',
};

/** `LeadershipCohortErrorCodes` in the web app. */
export const LEADERSHIP_ERROR_KEYS: Record<LeadershipCohortErrorCode, TranslationKey> = {
  invalidGroupId: 'admin.leadershipCohort.error.invalidGroupId',
  stateNotDurable: 'admin.leadershipCohort.error.stateNotDurable',
  stateUnavailable: 'admin.leadershipCohort.error.stateUnavailable',
  notConfigured: 'admin.leadershipCohort.error.notConfigured',
  refreshInProgress: 'admin.leadershipCohort.error.refreshInProgress',
};

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const useStyles = makeStyles({
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'end' },
  cards: { display: 'flex', flexDirection: 'column', gap: '16px', marginTop: '16px' },
  groupInput: { minWidth: '340px' },
  label: { fontWeight: tokens.fontWeightSemibold, width: '250px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function Row({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return <TableRow><TableCell className={styles.label}>{label}</TableCell><TableCell>{children}</TableCell></TableRow>;
}

export default function LeadershipCohortPage() {
  const t = useT();
  const styles = useStyles();
  const [status, setStatus] = useState<LeadershipCohortStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [groupId, setGroupId] = useState('');

  const describeError = useCallback((e: unknown, fallback: TranslationKey) => {
    if (e instanceof SessionExpiredError || e instanceof PortalPermissionError) return e.message;
    const code = e instanceof Error ? e.message : '';
    const key = LEADERSHIP_ERROR_KEYS[code as LeadershipCohortErrorCode];
    return t(key ?? fallback);
  }, [t]);

  const apply = (next: LeadershipCohortStatus) => {
    setStatus(next);
    setGroupId(next.groupId ?? '');
  };

  const load = useCallback(async () => {
    setError(null);
    try { apply(await fetchLeadershipCohort()); }
    catch (e) { setError(describeError(e, 'admin.leadershipCohort.failed.load')); }
    finally { setLoading(false); }
  }, [describeError]);

  useEffect(() => { void load(); }, [load]);

  const run = async (action: () => Promise<LeadershipCohortStatus>, fallback: TranslationKey) => {
    setBusy(true);
    setError(null);
    try { apply(await action()); }
    catch (e) { setError(describeError(e, fallback)); }
    finally { setBusy(false); }
  };

  if (loading) return <Spinner label={t('admin.leadershipCohort.loading')} />;

  const trimmed = groupId.trim();
  const validId = trimmed === '' || GUID.test(trimmed);
  const refresh = status?.refresh ?? null;
  const durable = status?.stateDurable ?? false;

  return <div>
    <Title3>{t('admin.leadershipCohort.title')}</Title3>
    <Body1 block>{t('admin.leadershipCohort.description')}</Body1>
    <div className={styles.cards}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {status && !durable && <MessageBar intent="warning"><MessageBarBody>{t('admin.leadershipCohort.stateNotDurable')}</MessageBarBody></MessageBar>}
      <MessageBar intent="info"><MessageBarBody>
        {t('admin.leadershipCohort.privacy', { min: formatNumber(status?.minimumCohort ?? 10) })}
      </MessageBarBody></MessageBar>
      <Card>
        <CardHeader header={<Text weight="semibold">{t('admin.leadershipCohort.group.title')}</Text>} />
        <div className={styles.actions}>
          <Field
            label={t('admin.leadershipCohort.group.label')}
            hint={t('admin.leadershipCohort.group.hint')}
            validationState={validId ? 'none' : 'error'}
            validationMessage={validId ? undefined : t('admin.leadershipCohort.error.invalidGroupId')}
          >
            <Input className={styles.groupInput} value={groupId} placeholder="00000000-0000-0000-0000-000000000000" onChange={(_, data) => setGroupId(data.value)} />
          </Field>
          <Button icon={<Save16Regular />} appearance="primary" disabled={busy || !durable || !validId || trimmed === ''}
            onClick={() => run(() => saveLeadershipCohort(trimmed), 'admin.leadershipCohort.failed.save')}>
            {t('admin.leadershipCohort.save')}
          </Button>
          <Button icon={<Delete16Regular />} disabled={busy || !durable || !status?.configured}
            onClick={() => run(() => saveLeadershipCohort(''), 'admin.leadershipCohort.failed.save')}>
            {t('admin.leadershipCohort.clear')}
          </Button>
          <Button icon={<ArrowClockwise16Regular />} disabled={busy || !durable || !status?.configured}
            onClick={() => run(refreshLeadershipCohort, 'admin.leadershipCohort.failed.refresh')}>
            {t('admin.leadershipCohort.refreshNow')}
          </Button>
          <Button disabled={busy} onClick={() => run(fetchLeadershipCohort, 'admin.leadershipCohort.failed.load')}>
            {t('admin.leadershipCohort.status.check')}
          </Button>
        </div>
        <Text size={200} className={styles.muted}>{t('admin.leadershipCohort.permissionHint')}</Text>
      </Card>
      <Card>
        <CardHeader header={<Text weight="semibold">{t('admin.leadershipCohort.status.title')}</Text>} />
        {!status?.configured
          ? <Text className={styles.muted}>{t('admin.leadershipCohort.status.notConfigured')}</Text>
          : <Table size="small" aria-label={t('admin.leadershipCohort.status.title')}><TableBody>
            <Row label={t('admin.leadershipCohort.status.groupId')}>{status.groupId}</Row>
            {/* Tenant data: the group's display name is shown exactly as Entra ID stores it. */}
            <Row label={t('admin.leadershipCohort.status.groupName')}>{refresh?.groupDisplayName ?? t('admin.common.unknown')}</Row>
            <Row label={t('admin.leadershipCohort.status.updated')}>{status.updatedUtc ? formatUtc(status.updatedUtc) : t('admin.common.unknown')}</Row>
            <Row label={t('admin.leadershipCohort.status.refresh')}>
              {refresh
                ? t(LEADERSHIP_REFRESH_STATUS_KEYS[refresh.status as LeadershipCohortRefreshStatus] ?? 'admin.leadershipCohort.refreshStatus.failed')
                : t('admin.leadershipCohort.status.pending')}
            </Row>
            {refresh?.failureKind && <Row label={t('admin.leadershipCohort.status.failureKind')}>
              {t(LEADERSHIP_FAILURE_KIND_KEYS[refresh.failureKind as LeadershipCohortFailureKind] ?? 'admin.leadershipCohort.failureKind.graphError')}
              {refresh.httpStatus ? ` ${t('admin.leadershipCohort.status.httpStatus', { status: refresh.httpStatus })}` : ''}
            </Row>}
            {refresh && <Row label={t('admin.leadershipCohort.status.attempted')}>{formatUtc(refresh.attemptedUtc)}</Row>}
            {refresh && <Row label={t('admin.leadershipCohort.status.refreshed')}>{refresh.refreshedUtc ? formatUtc(refresh.refreshedUtc) : t('admin.leadershipCohort.status.never')}</Row>}
            {refresh?.status === 'ready' && <Row label={t('admin.leadershipCohort.status.members')}>
              {t('admin.leadershipCohort.status.membersValue', { members: formatNumber(refresh.directMembers), matched: formatNumber(refresh.matchedUsers) })}
            </Row>}
            {refresh?.stale && <Row label={t('admin.leadershipCohort.status.freshness')}>
              {t('admin.leadershipCohort.status.stale', { hours: formatNumber(status.staleAfterHours) })}
            </Row>}
          </TableBody></Table>}
        <Text size={200} className={styles.muted}>
          {t('admin.leadershipCohort.status.schedule', { hours: formatNumber(status?.refreshAfterSuccessHours ?? 6), max: formatNumber(status?.maxMembers ?? 10000) })}
        </Text>
      </Card>
    </div>
  </div>;
}
