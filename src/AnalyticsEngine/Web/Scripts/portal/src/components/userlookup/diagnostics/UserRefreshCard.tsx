import { useEffect, useRef, useState } from 'react';
import {
  Button,
  Card,
  CardHeader,
  MessageBar,
  MessageBarBody,
  Subtitle2,
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
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchUserRefresh, requestUserRefresh } from '../../../api/userLookupApi';
import type { UserRefreshCapability, UserRefreshRequest } from '../../../types/userDiagnostics';
import Spinner from '../../Spinner';
import { useT, type TFunction, type TranslationKey } from '../../../i18n';
import { formatUtc } from './diagnosticsText';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  actions: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  confirm: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  changes: {
    display: 'grid',
    gridTemplateColumns: 'minmax(200px, max-content) 1fr',
    columnGap: '24px',
    rowGap: '6px',
    alignItems: 'baseline',
  },
  label: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  tableWrap: { overflowX: 'auto' },
});

/** How often a waiting or running request is re-read. The importer drains at most once per cycle, so faster is waste. */
export const REFRESH_POLL_MS = 10_000;

const OUTCOME_KEYS: Record<string, TranslationKey> = {
  updated: 'userDiagnostics.refresh.outcome.updated',
  unchanged: 'userDiagnostics.refresh.outcome.unchanged',
  partial: 'userDiagnostics.refresh.outcome.partial',
  scopeExcluded: 'userDiagnostics.refresh.outcome.scopeExcluded',
  scopeUnknown: 'userDiagnostics.refresh.outcome.scopeUnknown',
  notStored: 'userDiagnostics.refresh.outcome.notStored',
  userNotFoundInDirectory: 'userDiagnostics.refresh.outcome.userNotFoundInDirectory',
  identityMismatch: 'userDiagnostics.refresh.outcome.identityMismatch',
  permissionDenied: 'userDiagnostics.refresh.outcome.permissionDenied',
  graphUnavailable: 'userDiagnostics.refresh.outcome.graphUnavailable',
  importDisabled: 'userDiagnostics.refresh.outcome.importDisabled',
  interrupted: 'userDiagnostics.refresh.outcome.interrupted',
  error: 'userDiagnostics.refresh.outcome.error',
};

const NOTE_KEYS: Record<string, TranslationKey> = {
  incompleteDirectoryAnswer: 'userDiagnostics.refresh.note.incompleteDirectoryAnswer',
  ambiguousLicenceKept: 'userDiagnostics.refresh.note.ambiguousLicenceKept',
  userPrincipalNameChanged: 'userDiagnostics.refresh.note.userPrincipalNameChanged',
  noLicenceHistory: 'userDiagnostics.refresh.note.noLicenceHistory',
  reconciledAfterInterruption: 'userDiagnostics.refresh.note.reconciledAfterInterruption',
  profileNotUpdated: 'userDiagnostics.refresh.note.profileNotUpdated',
  reportCachesNotRefreshed: 'userDiagnostics.refresh.note.reportCachesNotRefreshed',
};

const UNAVAILABLE_KEYS: Record<string, TranslationKey> = {
  storageNotConfigured: 'userDiagnostics.refresh.unavailable.storageNotConfigured',
  storageUnavailable: 'userDiagnostics.refresh.unavailable.storageUnavailable',
  importDisabled: 'userDiagnostics.refresh.unavailable.importDisabled',
  notStored: 'userDiagnostics.refresh.unavailable.notStored',
  scopeExcluded: 'userDiagnostics.refresh.unavailable.scopeExcluded',
  scopeUnknown: 'userDiagnostics.refresh.unavailable.scopeUnknown',
  queueFull: 'userDiagnostics.refresh.unavailable.queueFull',
};

const FIELD_KEYS: Record<string, TranslationKey> = {
  accountEnabled: 'userDiagnostics.refresh.field.accountEnabled',
  mail: 'userDiagnostics.refresh.field.mail',
  postalCode: 'userDiagnostics.refresh.field.postalCode',
  objectId: 'userDiagnostics.refresh.field.objectId',
  department: 'userDiagnostics.refresh.field.department',
  jobTitle: 'userDiagnostics.refresh.field.jobTitle',
  officeLocation: 'userDiagnostics.refresh.field.officeLocation',
  usageLocation: 'userDiagnostics.refresh.field.usageLocation',
  country: 'userDiagnostics.refresh.field.country',
  stateOrProvince: 'userDiagnostics.refresh.field.stateOrProvince',
  companyName: 'userDiagnostics.refresh.field.companyName',
  manager: 'userDiagnostics.refresh.field.manager',
};

const SUCCESS = new Set(['updated', 'unchanged', 'partial']);

function isFinished(request: UserRefreshRequest): boolean {
  return request.state === 'completed' || request.state === 'failed' || request.state === 'expired';
}

/** A short status for the recent-requests table. */
function statusText(t: TFunction, request: UserRefreshRequest): string {
  if (request.state === 'queued') return t('userDiagnostics.refresh.status.queued');
  if (request.state === 'running') return t('userDiagnostics.refresh.status.running');
  if (request.state === 'expired') return t('userDiagnostics.refresh.status.expired');
  if (request.state === 'failed') return t('userDiagnostics.refresh.status.failed');
  if (request.outcome === 'updated') return t('userDiagnostics.refresh.status.updated');
  if (request.outcome === 'unchanged') return t('userDiagnostics.refresh.status.unchanged');
  if (request.outcome === 'partial') return t('userDiagnostics.refresh.status.partial');
  return t('userDiagnostics.refresh.status.notRefreshed');
}

/** Where a request is now, or what came of it, as a sentence. */
function progressText(t: TFunction, request: UserRefreshRequest): string {
  if (request.state === 'queued') {
    return t('userDiagnostics.refresh.state.queued', { when: formatUtc(request.requestedUtc), expires: formatUtc(request.expiresUtc) });
  }
  if (request.state === 'running') {
    return t('userDiagnostics.refresh.state.running', { when: formatUtc(request.startedUtc ?? request.requestedUtc) });
  }
  if (request.state === 'expired') return t('userDiagnostics.refresh.state.expired');
  const key = request.outcome ? OUTCOME_KEYS[request.outcome] : undefined;
  return key ? t(key) : t('userDiagnostics.refresh.outcome.error');
}

function RequestResult({ request }: { request: UserRefreshRequest }) {
  const styles = useStyles();
  const t = useT();
  const intent = !isFinished(request) ? 'info' : request.outcome && SUCCESS.has(request.outcome) ? (request.outcome === 'partial' ? 'warning' : 'success') : 'error';
  const notes = request.notes
    .map((note) => NOTE_KEYS[note])
    .filter((key): key is TranslationKey => key !== undefined);
  // A field this build has no name for is left out rather than shown as a raw key.
  const fields = request.profileChanges
    .map((field) => FIELD_KEYS[field])
    .filter((key): key is TranslationKey => key !== undefined)
    .map((key) => t(key));

  return (
    <div className={styles.stack}>
      <MessageBar intent={intent}>
        <MessageBarBody>
          <span role="status" aria-live="polite">
            {progressText(t, request)}
          </span>
        </MessageBarBody>
      </MessageBar>
      {isFinished(request) && request.observedUtc && (
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.refresh.observedAt', { when: formatUtc(request.observedUtc) })}
        </Text>
      )}
      {(request.added.length > 0 || request.removed.length > 0 || request.heldBack.length > 0 || fields.length > 0) && (
        <div className={styles.changes}>
          {request.added.length > 0 && (
            <>
              <Text className={styles.label}>{t('userDiagnostics.refresh.added')}</Text>
              <Text>{request.added.join(', ')}</Text>
            </>
          )}
          {request.removed.length > 0 && (
            <>
              <Text className={styles.label}>{t('userDiagnostics.refresh.removed')}</Text>
              <Text>{request.removed.join(', ')}</Text>
            </>
          )}
          {request.heldBack.length > 0 && (
            <>
              <Text className={styles.label}>{t('userDiagnostics.refresh.heldBack')}</Text>
              <Text>{request.heldBack.join(', ')}</Text>
            </>
          )}
          {fields.length > 0 && (
            <>
              <Text className={styles.label}>{t('userDiagnostics.refresh.profileChanges')}</Text>
              <Text>{fields.join(', ')}</Text>
            </>
          )}
        </div>
      )}
      {notes.map((key) => (
        <Text key={key} size={200}>
          {t(key)}
        </Text>
      ))}
    </div>
  );
}

/**
 * Queues a one-person refresh of the profile and licences for the importer, then follows it to its result. The
 * importer does the work - the browser never waits on Graph - and reports pick up any change on their own.
 */
export default function UserRefreshCard({
  upn,
  capability,
  onFinished,
}: {
  upn: string;
  capability: UserRefreshCapability;
  /** Called once a request this card is following finishes, so the page can reload what it changed. */
  onFinished?: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const [confirming, setConfirming] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rejection, setRejection] = useState<string | null>(null);
  const [current, setCurrent] = useState<UserRefreshRequest | null>(capability.active ?? null);
  const onFinishedRef = useRef(onFinished);
  onFinishedRef.current = onFinished;

  const followingId = current && !isFinished(current) ? current.id : null;

  useEffect(() => {
    if (!followingId) return undefined;
    const controller = new AbortController();
    const poll = async () => {
      try {
        const latest = await fetchUserRefresh(followingId, controller.signal);
        setCurrent(latest);
        if (isFinished(latest)) onFinishedRef.current?.();
      } catch {
        // A missed poll is retried on the next tick; the request itself is durable.
      }
    };
    const timer = window.setInterval(() => void poll(), REFRESH_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [followingId]);

  const submit = async () => {
    setSubmitting(true);
    setError(null);
    setRejection(null);
    try {
      const result = await requestUserRefresh(upn);
      if (result.request) {
        setCurrent(result.request);
      } else {
        setRejection(result.rejection);
      }
      setConfirming(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  };

  const checkNow = async () => {
    if (!current) return;
    try {
      const latest = await fetchUserRefresh(current.id);
      setCurrent(latest);
      if (isFinished(latest)) onFinished?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  const unavailableKey = !capability.available && capability.reason ? UNAVAILABLE_KEYS[capability.reason] : undefined;
  const rejectionKey = rejection ? UNAVAILABLE_KEYS[rejection] : undefined;
  const busy = followingId !== null;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('userDiagnostics.refresh.title')}</Subtitle2>} />
      <div className={styles.stack}>
        <Text>{t('userDiagnostics.refresh.description')}</Text>
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.refresh.when')}
        </Text>

        {!capability.available ? (
          <MessageBar intent="info">
            <MessageBarBody>{unavailableKey ? t(unavailableKey) : t('userDiagnostics.refresh.unavailable.storageUnavailable')}</MessageBarBody>
          </MessageBar>
        ) : confirming ? (
          <div className={styles.confirm} role="group" aria-label={t('userDiagnostics.refresh.title')}>
            <Text>{t('userDiagnostics.refresh.confirmText', { upn })}</Text>
            <div className={styles.actions}>
              <Button appearance="primary" icon={<ArrowClockwise16Regular />} onClick={() => void submit()} disabled={submitting}>
                {t('userDiagnostics.refresh.confirm')}
              </Button>
              <Button appearance="secondary" onClick={() => setConfirming(false)} disabled={submitting}>
                {t('userDiagnostics.refresh.cancel')}
              </Button>
              {submitting && <Spinner size={20} label={t('userDiagnostics.refresh.submitting')} />}
            </div>
          </div>
        ) : (
          <div className={styles.actions}>
            <Button appearance="secondary" icon={<ArrowClockwise16Regular />} onClick={() => setConfirming(true)} disabled={busy}>
              {t('userDiagnostics.refresh.button')}
            </Button>
            {busy && (
              <Button appearance="subtle" onClick={() => void checkNow()}>
                {t('userDiagnostics.refresh.checkAgain')}
              </Button>
            )}
          </div>
        )}

        {rejectionKey && (
          <MessageBar intent="warning">
            <MessageBarBody>{t(rejectionKey)}</MessageBarBody>
          </MessageBar>
        )}
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}

        {current && <RequestResult request={current} />}

        {capability.recent.length > 0 && (
          <div className={styles.stack}>
            <Text weight="semibold">{t('userDiagnostics.refresh.recent')}</Text>
            <div className={styles.tableWrap}>
              <Table size="small" aria-label={t('userDiagnostics.refresh.recent')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('userDiagnostics.refresh.columnRequested')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.refresh.columnBy')}</TableHeaderCell>
                    <TableHeaderCell>{t('userDiagnostics.refresh.columnResult')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {capability.recent.map((request) => (
                    <TableRow key={request.id}>
                      <TableCell>{formatUtc(request.requestedUtc)}</TableCell>
                      <TableCell>{request.requestedBy ?? '\u2014'}</TableCell>
                      <TableCell>{statusText(t, request)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}
