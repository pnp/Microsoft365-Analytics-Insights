import { useCallback, useEffect, useState, type ReactNode } from 'react';
import {
  Body1,
  Button,
  Card,
  CardHeader,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Table,
  TableBody,
  TableCell,
  TableRow,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, Delete16Regular } from '@fluentui/react-icons';
import { clearUserImportCheckpoint, fetchUserImportCheckpoint } from '../api/userImportCheckpointApi';
import type { UserImportCheckpointClearResult, UserImportCheckpointStatus } from '../types/userImportCheckpoint';
import { formatUtc } from '../components/health/healthShared';
import Spinner from '../components/Spinner';
import { formatNumber, useT, type TFunction } from '../i18n';

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
    width: '260px',
    verticalAlign: 'top',
  },
  value: {
    overflowWrap: 'anywhere',
    verticalAlign: 'top',
  },
  key: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    marginTop: '4px',
  },
  dialogText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
});

/**
 * When the next user import will run, as a sentence - or null when that can't honestly be said, because the
 * import is switched off. It runs on the next cycle when it is not cadence-gated, when there is no last-completed
 * stamp for the gate to wait on, or when the clear also removed that stamp.
 */
export function nextRunText(t: TFunction, status: UserImportCheckpointStatus, stampCleared: boolean): string | null {
  if (status.userImportEnabled === false) return null;
  if (stampCleared || status.intervalHours <= 0 || !status.lastCompletedUtc) {
    return t('admin.userImport.schedule.nextCycle');
  }
  return t('admin.userImport.schedule.afterInterval', { hours: formatNumber(status.intervalHours) });
}

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function StateRow({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <TableRow>
      <TableCell className={styles.label}>{label}</TableCell>
      <TableCell className={styles.value}>{children}</TableCell>
    </TableRow>
  );
}

function StateCard({ status }: { status: UserImportCheckpointStatus }) {
  const styles = useStyles();
  const t = useT();

  const importText =
    status.userImportEnabled === null
      ? t('admin.userImport.state.importUnknown')
      : status.userImportEnabled
        ? t('admin.userImport.state.importEnabled')
        : t('admin.userImport.state.importDisabled');

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userImport.state.title')}</Subtitle2>} />
      <Table aria-label={t('admin.userImport.state.ariaLabel')} size="small">
        <TableBody>
          <StateRow label={t('admin.userImport.state.importLabel')}>{importText}</StateRow>
          <StateRow label={t('admin.userImport.state.storageLabel')}>
            {status.redisConfigured ? t('admin.userImport.state.storageRedis') : t('admin.userImport.state.storageNone')}
          </StateRow>
          {status.redisConfigured && (
            <>
              <StateRow label={t('admin.userImport.state.checkpointLabel')}>
                {status.checkpointStored
                  ? t('admin.userImport.state.checkpointPresent')
                  : t('admin.userImport.state.checkpointAbsent')}
              </StateRow>
              <StateRow label={t('admin.userImport.state.keyLabel')}>
                <code className={styles.key}>{status.checkpointKey}</code>
              </StateRow>
              <StateRow label={t('admin.userImport.state.lastCompletedLabel')}>
                {status.lastCompletedUtc
                  ? formatUtc(status.lastCompletedUtc)
                  : t('admin.userImport.state.lastCompletedNone')}
              </StateRow>
            </>
          )}
          <StateRow label={t('admin.userImport.state.intervalLabel')}>
            {status.intervalHours > 0
              ? t('admin.userImport.state.intervalHours', { hours: formatNumber(status.intervalHours) })
              : t('admin.userImport.state.everyCycle')}
          </StateRow>
        </TableBody>
      </Table>
    </Card>
  );
}

interface ClearOutcome {
  cleared: UserImportCheckpointClearResult;
  /** The state the clear acted on, so the sentence about the next run is computed from facts at render time. */
  before: UserImportCheckpointStatus;
}

/**
 * Administration > User import (issue #664).
 *
 * The Graph user import saves a checkpoint - a `/users/delta` token - so each run reads only what changed. This
 * page shows whether one is stored and clears it, so the next run reads every user again: the in-product version
 * of deleting the Redis key by hand. Clearing is a POST through `apiFetch`, which the server's same-origin check
 * requires, and a confirmation comes first because a full read of a large tenant is expensive.
 */
export default function UserImportPage() {
  const styles = useStyles();
  const t = useT();
  const [status, setStatus] = useState<UserImportCheckpointStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [runOnNextCycle, setRunOnNextCycle] = useState(true);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [clearError, setClearError] = useState<unknown>(null);
  const [outcome, setOutcome] = useState<ClearOutcome | null>(null);

  const load = useCallback(async (isCancelled: () => boolean = () => false) => {
    setLoading(true);
    setLoadError(null);
    try {
      const next = await fetchUserImportCheckpoint();
      if (!isCancelled()) setStatus(next);
    } catch (err) {
      if (!isCancelled()) setLoadError(err ?? new Error());
    } finally {
      if (!isCancelled()) setLoading(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    void load(() => cancelled);
    return () => {
      cancelled = true;
    };
  }, [load]);

  // The next-cycle option only does something when the import is cadence-gated AND a last-completed stamp exists
  // for the gate to wait on; without one the import is already due on the next cycle.
  const cadenceGated = status !== null && status.intervalHours > 0 && status.lastCompletedUtc !== null;
  const resetCadence = cadenceGated && runOnNextCycle;

  async function confirmClear() {
    if (!status) return;
    setClearing(true);
    setClearError(null);
    try {
      const cleared = await clearUserImportCheckpoint(resetCadence);
      setOutcome({ cleared, before: status });
      setConfirmOpen(false);
      void load();
    } catch (err) {
      setClearError(err ?? new Error());
    } finally {
      setClearing(false);
    }
  }

  const outcomeNextRun = outcome ? nextRunText(t, outcome.before, outcome.cleared.lastCompletedCleared) : null;
  const plannedNextRun = status ? nextRunText(t, status, resetCadence) : null;

  return (
    <div>
      <div className={styles.header}>
        <Title3 block>{t('admin.userImport.title')}</Title3>
        <Button appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={() => void load()} disabled={loading}>
          {t('common.action.refresh')}
        </Button>
      </div>
      <Body1 block style={{ marginTop: '8px' }}>
        {t('admin.userImport.description')}
      </Body1>

      {outcome && (
        <MessageBar intent="success" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            {outcome.cleared.checkpointCleared
              ? t('admin.userImport.result.cleared')
              : t('admin.userImport.result.nothingStored')}
            {outcomeNextRun && (
              <>
                {' '}
                {outcomeNextRun}
              </>
            )}
          </MessageBarBody>
        </MessageBar>
      )}

      {loadError !== null && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{errorText(loadError, t('admin.userImport.loadFailed'))}</MessageBarBody>
        </MessageBar>
      )}

      {loading && !status && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label={t('admin.userImport.loading')} />
        </div>
      )}

      {status && (
        <div className={styles.cards}>
          <StateCard status={status} />

          <Card>
            <CardHeader header={<Subtitle2>{t('admin.userImport.clear.title')}</Subtitle2>} />
            <div className={styles.section}>
              <Text block>{t('admin.userImport.clear.description')}</Text>
              <Text block className={styles.muted}>
                {t('admin.userImport.clear.cost')}
              </Text>

              {!status.redisConfigured ? (
                <MessageBar intent="info">
                  <MessageBarBody>{t('admin.userImport.clear.noRedis')}</MessageBarBody>
                </MessageBar>
              ) : (
                <>
                  {status.userImportEnabled === false && (
                    <MessageBar intent="warning">
                      <MessageBarBody>{t('admin.userImport.clear.importOff')}</MessageBarBody>
                    </MessageBar>
                  )}
                  {cadenceGated && (
                    <div>
                      <Checkbox
                        checked={runOnNextCycle}
                        onChange={(_, data) => setRunOnNextCycle(data.checked === true)}
                        label={t('admin.userImport.clear.runOnNextCycle')}
                      />
                      <Text block size={200} className={styles.muted} style={{ marginLeft: '32px' }}>
                        {t('admin.userImport.clear.runOnNextCycleHint', { hours: formatNumber(status.intervalHours) })}
                      </Text>
                    </div>
                  )}
                  <div className={styles.actions}>
                    <Button
                      appearance="primary"
                      icon={<Delete16Regular />}
                      onClick={() => {
                        setClearError(null);
                        setConfirmOpen(true);
                      }}
                    >
                      {t('admin.userImport.clear.button')}
                    </Button>
                  </div>
                </>
              )}
            </div>
          </Card>
        </div>
      )}

      <Dialog
        open={confirmOpen}
        onOpenChange={(_, data) => {
          if (!clearing) setConfirmOpen(data.open);
        }}
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('admin.userImport.confirm.title')}</DialogTitle>
            <DialogContent className={styles.dialogText}>
              <Text block>{t('admin.userImport.confirm.body')}</Text>
              {plannedNextRun && <Text block>{plannedNextRun}</Text>}
              <Text block>{t('admin.userImport.confirm.running')}</Text>
              {clearError !== null && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorText(clearError, t('admin.userImport.clearFailed'))}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => void confirmClear()} disabled={clearing}>
                {clearing ? t('admin.userImport.confirm.clearing') : t('admin.userImport.confirm.clear')}
              </Button>
              <Button appearance="secondary" onClick={() => setConfirmOpen(false)} disabled={clearing}>
                {t('common.action.cancel')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
