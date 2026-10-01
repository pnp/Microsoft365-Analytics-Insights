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
  ProgressBar,
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
import { ArrowClockwise16Regular, Delete16Regular, Dismiss16Regular } from '@fluentui/react-icons';
import {
  cancelUserScopePurge,
  fetchUserScope,
  fetchUserScopePurge,
  refreshUserScope,
  startUserScopePurge,
} from '../api/userScopeApi';
import { formatUtc } from '../components/health/healthShared';
import Spinner from '../components/Spinner';
import { formatNumber, useT, type TranslationKey } from '../i18n';
import type {
  UserScopeFailureKind,
  UserScopePurgeJob,
  UserScopePurgePhase,
  UserScopePurgeState,
  UserScopePurgeUnavailableReason,
  UserScopeResolutionStatus,
  UserScopeStatus,
} from '../types/userScope';

const POLL_MS = 3000;

const PHASE_KEYS: Record<UserScopePurgePhase, TranslationKey> = {
  snapshot: 'admin.userScope.phase.snapshot',
  auditEvents: 'admin.userScope.phase.auditEvents',
  webActivity: 'admin.userScope.phase.webActivity',
  calls: 'admin.userScope.phase.calls',
  pageComments: 'admin.userScope.phase.pageComments',
  sentEmails: 'admin.userScope.phase.sentEmails',
  teams: 'admin.userScope.phase.teams',
  usageReports: 'admin.userScope.phase.usageReports',
  copilotInteractions: 'admin.userScope.phase.copilotInteractions',
  licencesAndCredits: 'admin.userScope.phase.licencesAndCredits',
  sharedWith: 'admin.userScope.phase.sharedWith',
  managers: 'admin.userScope.phase.managers',
  users: 'admin.userScope.phase.users',
  done: 'admin.userScope.phase.done',
};

const STATE_KEYS: Record<UserScopePurgeState, TranslationKey> = {
  queued: 'admin.userScope.state.queued',
  running: 'admin.userScope.state.running',
  completed: 'admin.userScope.state.completed',
  failed: 'admin.userScope.state.failed',
  cancelled: 'admin.userScope.state.cancelled',
};

const UNAVAILABLE_KEYS: Record<UserScopePurgeUnavailableReason, TranslationKey> = {
  notFiltered: 'admin.userScope.unavailable.notFiltered',
  scopeUnavailable: 'admin.userScope.unavailable.scopeUnavailable',
  scopeEmpty: 'admin.userScope.unavailable.scopeEmpty',
  nothingToPurge: 'admin.userScope.unavailable.nothingToPurge',
  jobActive: 'admin.userScope.unavailable.jobActive',
  storageUnavailable: 'admin.userScope.unavailable.storageUnavailable',
};

const FAILURE_KEYS: Record<UserScopeFailureKind, TranslationKey> = {
  directoryRead: 'admin.userScope.failure.directoryRead',
  budgetExhausted: 'admin.userScope.failure.budgetExhausted',
  clientUnavailable: 'admin.userScope.failure.clientUnavailable',
  unexpected: 'admin.userScope.failure.unexpected',
};

const RESOLUTION_KEYS: Record<UserScopeResolutionStatus, TranslationKey> = {
  unfiltered: 'admin.userScope.filter.statusUnfiltered',
  resolved: 'admin.userScope.filter.statusResolved',
  unavailable: 'admin.userScope.filter.statusUnavailable',
};

const JOB_ERROR_KEYS: Record<NonNullable<UserScopePurgeJob['errorCode']>, TranslationKey> = {
  scopeUnavailable: 'admin.userScope.jobError.scopeUnavailable',
  scopeEmpty: 'admin.userScope.jobError.scopeEmpty',
  filterChanged: 'admin.userScope.jobError.filterChanged',
  filterChangedWhileRunning: 'admin.userScope.jobError.filterChangedWhileRunning',
  databaseError: 'admin.userScope.jobError.databaseError',
  stateUnavailable: 'admin.userScope.jobError.stateUnavailable',
  unexpected: 'admin.userScope.jobError.unexpected',
};

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
  inlineList: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
  },
  dangerButton: {
    backgroundColor: tokens.colorPaletteRedBackground3,
    color: tokens.colorNeutralForegroundInverted,
    ':hover': {
      backgroundColor: tokens.colorPaletteRedForeground1,
      color: tokens.colorNeutralForegroundInverted,
    },
  },
  progress: {
    maxWidth: '520px',
  },
  dialogText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
});

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function isActive(job: UserScopePurgeJob | null | undefined): job is UserScopePurgeJob {
  return job?.state === 'queued' || job?.state === 'running';
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

function DataList({ values, emptyText }: { values: string[]; emptyText?: string }) {
  const styles = useStyles();
  if (values.length === 0) return emptyText ? <Text>{emptyText}</Text> : null;
  return (
    <span className={styles.inlineList}>
      {values.map((value) => (
        <code className={styles.key} key={value}>
          {value}
        </code>
      ))}
    </span>
  );
}

function FailureMessage({ status }: { status: UserScopeStatus }) {
  const t = useT();
  const failureKind = status.resolution.failureKind;
  if (status.resolution.status !== 'unavailable' || failureKind === null) return null;

  const key =
    failureKind === 'directoryRead' && status.resolution.httpStatus === 403
      ? 'admin.userScope.failure.directoryReadForbidden'
      : FAILURE_KEYS[failureKind];

  return (
    <MessageBar intent="error">
      <MessageBarBody>{t(key, { status: formatNumber(status.resolution.httpStatus ?? 0) })}</MessageBarBody>
    </MessageBar>
  );
}

function FilterCard({ status }: { status: UserScopeStatus }) {
  const styles = useStyles();
  const t = useT();
  const resolution = status.resolution;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userScope.filter.title')}</Subtitle2>} />
      <div className={styles.section}>
        {!status.filtered ? (
          <MessageBar intent="info">
            <MessageBarBody>{t('admin.userScope.filter.unfiltered')}</MessageBarBody>
          </MessageBar>
        ) : (
          <>
            <Table aria-label={t('admin.userScope.filter.ariaLabel')} size="small">
              <TableBody>
                <StateRow label={t('admin.userScope.filter.patterns')}>
                  <DataList values={status.filterPatterns} emptyText={t('admin.userScope.filter.noPatterns')} />
                </StateRow>
                <StateRow label={t('admin.userScope.filter.resolution')}>
                  {t(RESOLUTION_KEYS[resolution.status])}
                </StateRow>
                <StateRow label={t('admin.userScope.filter.resolvedUtc')}>
                  {resolution.resolvedUtc ? formatUtc(resolution.resolvedUtc) : t('admin.common.unknown')}
                </StateRow>
                <StateRow label={t('admin.userScope.filter.peopleInScope')}>
                  {formatNumber(resolution.memberCount)}
                </StateRow>
              </TableBody>
            </Table>
            <FailureMessage status={status} />
            {resolution.status === 'unavailable' && (
              <MessageBar intent="warning">
                <MessageBarBody>{t('admin.userScope.filter.unavailableImportsContinue')}</MessageBarBody>
              </MessageBar>
            )}
            {resolution.matchedNoGroup && (
              <MessageBar intent="warning">
                <MessageBarBody>{t('admin.userScope.filter.matchedNoGroup')}</MessageBarBody>
              </MessageBar>
            )}
            {resolution.unmatchedPatterns.length > 0 && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  {t('admin.userScope.filter.unmatchedPatterns')}{' '}
                  <DataList values={resolution.unmatchedPatterns} />
                </MessageBarBody>
              </MessageBar>
            )}
          </>
        )}
      </div>
    </Card>
  );
}

function GroupsCard({ status }: { status: UserScopeStatus }) {
  const styles = useStyles();
  const t = useT();
  if (status.resolution.groups.length === 0) return null;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userScope.groups.title')}</Subtitle2>} />
      <Table aria-label={t('admin.userScope.groups.ariaLabel')} size="small">
        <TableBody>
          <TableRow>
            <TableCell className={styles.label}>{t('admin.userScope.groups.name')}</TableCell>
            <TableCell className={styles.label}>{t('admin.userScope.groups.objectId')}</TableCell>
            <TableCell className={styles.label}>{t('admin.userScope.groups.members')}</TableCell>
            <TableCell className={styles.label}>{t('admin.userScope.groups.patterns')}</TableCell>
          </TableRow>
          {status.resolution.groups.map((group) => (
            <TableRow key={group.id}>
              <TableCell>{group.displayName}</TableCell>
              <TableCell>
                <code className={styles.key}>{group.id}</code>
              </TableCell>
              <TableCell>{formatNumber(group.userMemberCount)}</TableCell>
              <TableCell>
                <DataList values={group.matchedPatterns} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Card>
  );
}

function DatabaseCard({ status }: { status: UserScopeStatus }) {
  const t = useT();
  if (!status.database) return null;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userScope.database.title')}</Subtitle2>} />
      <Table aria-label={t('admin.userScope.database.ariaLabel')} size="small">
        <TableBody>
          <StateRow label={t('admin.userScope.database.total')}>{formatNumber(status.database.totalUsers)}</StateRow>
          <StateRow label={t('admin.userScope.database.inScope')}>{formatNumber(status.database.inScopeUsers)}</StateRow>
          <StateRow label={t('admin.userScope.database.outOfScope')}>
            {formatNumber(status.database.outOfScopeUsers)}
          </StateRow>
        </TableBody>
      </Table>
    </Card>
  );
}

function PurgeExplanation({ durable }: { durable: boolean }) {
  const t = useT();
  return (
    <>
      <Text block>{t('admin.userScope.purge.intro')}</Text>
      <ul>
        <li>{t('admin.userScope.purge.deleteList')}</li>
        <li>{t('admin.userScope.purge.anonymiseList')}</li>
        <li>{t('admin.userScope.purge.operationalList')}</li>
        <li>{t(durable ? 'admin.userScope.purge.restartDurable' : 'admin.userScope.purge.restartMemory')}</li>
      </ul>
      <Text block weight="semibold">
        {t('admin.userScope.purge.irreversible')}
      </Text>
    </>
  );
}

function PurgeCard({
  status,
  starting,
  startError,
  onOpen,
}: {
  status: UserScopeStatus;
  starting: boolean;
  startError: unknown;
  onOpen: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const outOfScope = status.database?.outOfScopeUsers ?? 0;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userScope.purge.title')}</Subtitle2>} />
      <div className={styles.section}>
        <PurgeExplanation durable={status.purgeStateDurable} />
        {status.purgeUnavailableReason && (
          <MessageBar intent="warning">
            <MessageBarBody>{t(UNAVAILABLE_KEYS[status.purgeUnavailableReason])}</MessageBarBody>
          </MessageBar>
        )}
        {startError !== null && (
          <MessageBar intent="error">
            <MessageBarBody>{errorText(startError, t('admin.userScope.purge.startFailed'))}</MessageBarBody>
          </MessageBar>
        )}
        <div className={styles.actions}>
          <Button
            appearance="primary"
            className={styles.dangerButton}
            icon={<Delete16Regular />}
            onClick={onOpen}
            disabled={status.purgeUnavailableReason !== null || starting}
          >
            {t('admin.userScope.purge.button', { count: formatNumber(outOfScope) })}
          </Button>
        </div>
      </div>
    </Card>
  );
}

function ProgressCard({
  job,
  cancelling,
  cancelError,
  onCancel,
}: {
  job: UserScopePurgeJob;
  cancelling: boolean;
  cancelError: unknown;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const progressValue = job.state === 'completed' ? 1 : job.stepCount > 0 ? job.stepIndex / job.stepCount : undefined;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userScope.progress.title')}</Subtitle2>} />
      <div className={styles.section}>
        <Text block>{t(STATE_KEYS[job.state])}</Text>
        <Text block>
          {t('admin.userScope.progress.phase', {
            phase: t(PHASE_KEYS[job.phase]),
            completed: formatNumber(job.stepIndex),
            total: formatNumber(job.stepCount),
          })}
        </Text>
        <ProgressBar className={styles.progress} value={progressValue} thickness="large" />
        <Table aria-label={t('admin.userScope.progress.ariaLabel')} size="small">
          <TableBody>
            <StateRow label={t('admin.userScope.progress.peopleFound')}>{formatNumber(job.candidateCount)}</StateRow>
            <StateRow label={t('admin.userScope.progress.peopleRemoved')}>{formatNumber(job.usersDeleted)}</StateRow>
            <StateRow label={t('admin.userScope.progress.peopleSkipped')}>{formatNumber(job.usersSkipped)}</StateRow>
            <StateRow label={t('admin.userScope.progress.requestedBy')}>
              {job.requestedBy ?? t('admin.common.unknown')}
            </StateRow>
            <StateRow label={t('admin.userScope.progress.started')}>
              {job.startedUtc ? formatUtc(job.startedUtc) : t('admin.common.unknown')}
            </StateRow>
            <StateRow label={t('admin.userScope.progress.updated')}>{formatUtc(job.updatedUtc)}</StateRow>
            <StateRow label={t('admin.userScope.progress.completed')}>
              {job.completedUtc ? formatUtc(job.completedUtc) : t('admin.common.unknown')}
            </StateRow>
          </TableBody>
        </Table>
        {job.usersSkipped > 0 && (
          <MessageBar intent="warning">
            <MessageBarBody>{t('admin.userScope.progress.skippedWarning')}</MessageBarBody>
          </MessageBar>
        )}
        {job.errorCode && (
          <MessageBar intent="error">
            <MessageBarBody>{t(JOB_ERROR_KEYS[job.errorCode] ?? 'admin.userScope.jobError.unexpected')}</MessageBarBody>
          </MessageBar>
        )}
        {job.cancelRequested && isActive(job) && (
          <MessageBar intent="warning">
            <MessageBarBody>{t('admin.userScope.progress.cancelRequested')}</MessageBarBody>
          </MessageBar>
        )}
        {cancelError !== null && (
          <MessageBar intent="error">
            <MessageBarBody>{errorText(cancelError, t('admin.userScope.progress.cancelFailed'))}</MessageBarBody>
          </MessageBar>
        )}
        {job.rowsAffected.length > 0 && (
          <>
            <Table aria-label={t('admin.userScope.progress.rowsAffected')} size="small">
              <TableBody>
                <TableRow>
                  <TableCell className={styles.label}>{t('admin.userScope.progress.table')}</TableCell>
                  <TableCell className={styles.label}>{t('admin.userScope.progress.rows')}</TableCell>
                </TableRow>
                {job.rowsAffected.map((row) => (
                  <TableRow key={row.table}>
                    <TableCell>
                      <code className={styles.key}>{row.table}</code>
                    </TableCell>
                    <TableCell>{formatNumber(row.rows)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Text block className={styles.muted}>
              {t('admin.userScope.progress.rowsHelp')}
            </Text>
          </>
        )}
        {isActive(job) && (
          <>
            <Text block className={styles.muted}>
              {t('admin.userScope.progress.stopHelp')}
            </Text>
            <div className={styles.actions}>
              <Button icon={<Dismiss16Regular />} onClick={onCancel} disabled={cancelling}>
                {cancelling ? t('admin.userScope.progress.stopping') : t('admin.userScope.progress.stop')}
              </Button>
            </div>
          </>
        )}
      </div>
    </Card>
  );
}

/**
 * Administration > User scope.
 *
 * `UserGroupsFilter` limits every importer to direct members of configured Entra ID groups. This page shows the
 * current resolution and drives the irreversible background purge for database users outside that scope.
 */
export default function UserScopePage() {
  const styles = useStyles();
  const t = useT();
  const [status, setStatus] = useState<UserScopeStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState<unknown>(null);
  const [refreshSucceeded, setRefreshSucceeded] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<unknown>(null);
  const [pollError, setPollError] = useState<unknown>(null);
  const [cancelling, setCancelling] = useState(false);
  const [cancelError, setCancelError] = useState<unknown>(null);

  const load = useCallback(async (showSpinner = true, isCancelled: () => boolean = () => false) => {
    if (showSpinner) setLoading(true);
    setLoadError(null);
    try {
      const next = await fetchUserScope();
      if (!isCancelled()) setStatus(next);
    } catch (err) {
      if (!isCancelled()) setLoadError(err ?? new Error());
    } finally {
      if (!isCancelled() && showSpinner) setLoading(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    void load(true, () => cancelled);
    return () => {
      cancelled = true;
    };
  }, [load]);

  const latestJob = status?.latestJob ?? null;

  useEffect(() => {
    if (!isActive(latestJob)) return undefined;

    let cancelled = false;
    let inFlight = false;
    let timer: number | undefined;

    const poll = async () => {
      if (cancelled || inFlight) return;
      inFlight = true;
      try {
        const job = await fetchUserScopePurge(latestJob.id);
        if (cancelled) return;
        setPollError(null);
        setStatus((current) =>
          current
            ? {
                ...current,
                latestJob: job,
                purgeUnavailableReason: isActive(job) ? 'jobActive' : current.purgeUnavailableReason,
              }
            : current,
        );
        if (!isActive(job)) {
          await load(false, () => cancelled);
          return;
        }
      } catch (err) {
        if (!cancelled) setPollError(err ?? new Error());
      } finally {
        inFlight = false;
      }
      if (!cancelled) timer = window.setTimeout(poll, POLL_MS);
    };

    timer = window.setTimeout(poll, POLL_MS);
    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [latestJob?.id, latestJob?.state, load]);

  async function refresh() {
    setRefreshing(true);
    setRefreshError(null);
    setRefreshSucceeded(false);
    try {
      const next = await refreshUserScope();
      setStatus(next);
      setRefreshSucceeded(true);
    } catch (err) {
      setRefreshError(err ?? new Error());
    } finally {
      setRefreshing(false);
    }
  }

  async function startPurge() {
    setStarting(true);
    setStartError(null);
    try {
      const job = await startUserScopePurge();
      setStatus((current) =>
        current ? { ...current, latestJob: job, purgeUnavailableReason: isActive(job) ? 'jobActive' : null } : current,
      );
      setConfirmOpen(false);
      setAcknowledged(false);
    } catch (err) {
      setStartError(err ?? new Error());
    } finally {
      setStarting(false);
    }
  }

  async function cancelPurge() {
    if (!latestJob) return;
    setCancelling(true);
    setCancelError(null);
    try {
      const job = await cancelUserScopePurge(latestJob.id);
      setStatus((current) => (current ? { ...current, latestJob: job } : current));
    } catch (err) {
      setCancelError(err ?? new Error());
    } finally {
      setCancelling(false);
    }
  }

  return (
    <div>
      <div className={styles.header}>
        <Title3 block>{t('admin.userScope.title')}</Title3>
        <Button
          appearance="subtle"
          icon={<ArrowClockwise16Regular />}
          onClick={() => void refresh()}
          disabled={loading || refreshing}
        >
          {refreshing ? t('admin.userScope.refreshing') : t('common.action.refresh')}
        </Button>
      </div>
      <Body1 block style={{ marginTop: '8px' }}>
        {t('admin.userScope.description')}
      </Body1>

      {refreshSucceeded && (
        <MessageBar intent="success" style={{ marginTop: '16px' }}>
          <MessageBarBody>{t('admin.userScope.refreshSucceeded')}</MessageBarBody>
        </MessageBar>
      )}
      {refreshError !== null && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{errorText(refreshError, t('admin.userScope.refreshFailed'))}</MessageBarBody>
        </MessageBar>
      )}
      {loadError !== null && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{errorText(loadError, t('admin.userScope.loadFailed'))}</MessageBarBody>
        </MessageBar>
      )}
      {pollError !== null && (
        <MessageBar intent="warning" style={{ marginTop: '16px' }}>
          <MessageBarBody>{errorText(pollError, t('admin.userScope.pollFailed'))}</MessageBarBody>
        </MessageBar>
      )}

      {loading && !status && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label={t('admin.userScope.loading')} />
        </div>
      )}

      {status && (
        <div className={styles.cards}>
          <FilterCard status={status} />
          <GroupsCard status={status} />
          <DatabaseCard status={status} />
          <PurgeCard
            status={status}
            starting={starting}
            startError={startError}
            onOpen={() => {
              setStartError(null);
              setAcknowledged(false);
              setConfirmOpen(true);
            }}
          />
          {status.latestJob && (
            <ProgressCard
              job={status.latestJob}
              cancelling={cancelling}
              cancelError={cancelError}
              onCancel={() => void cancelPurge()}
            />
          )}
        </div>
      )}

      <Dialog
        open={confirmOpen}
        onOpenChange={(_, data) => {
          if (!starting) setConfirmOpen(data.open);
        }}
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('admin.userScope.confirm.title')}</DialogTitle>
            <DialogContent className={styles.dialogText}>
              <PurgeExplanation durable={status?.purgeStateDurable ?? false} />
              <Checkbox
                checked={acknowledged}
                onChange={(_, data) => setAcknowledged(data.checked === true)}
                label={t('admin.userScope.confirm.acknowledge', {
                  count: formatNumber(status?.database?.outOfScopeUsers ?? 0),
                })}
              />
              {startError !== null && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorText(startError, t('admin.userScope.purge.startFailed'))}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button
                appearance="primary"
                className={styles.dangerButton}
                onClick={() => void startPurge()}
                disabled={!acknowledged || starting}
              >
                {starting ? t('admin.userScope.confirm.starting') : t('admin.userScope.confirm.start')}
              </Button>
              <Button appearance="secondary" onClick={() => setConfirmOpen(false)} disabled={starting}>
                {t('common.action.cancel')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
