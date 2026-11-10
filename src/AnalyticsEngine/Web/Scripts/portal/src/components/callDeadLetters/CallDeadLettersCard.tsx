import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Badge,
  Body1,
  Button,
  Card,
  CardHeader,
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
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSync16Regular, Delete16Regular } from '@fluentui/react-icons';
import { fetchCallDeadLetters, requestCallDeadLetterAction } from '../../api/callDeadLettersApi';
import type {
  CallDeadLetterAction,
  CallDeadLetterRequestView,
  CallDeadLetterRunResult,
  CallDeadLetterStatus,
} from '../../types/callDeadLetters';
import { usePortalAccess } from '../../access';
import Spinner from '../Spinner';
import { formatDateParts, formatNumber, parseApiDate, useT, type TFunction, type TranslationKey } from '../../i18n';

const useStyles = makeStyles({
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  facts: {
    display: 'grid',
    gridTemplateColumns: 'minmax(160px, 260px) 1fr',
    columnGap: '16px',
    rowGap: '8px',
    alignItems: 'start',
    '@media (max-width: 520px)': {
      gridTemplateColumns: '1fr',
      rowGap: '2px',
    },
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
  },
  value: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minWidth: 0,
    overflowWrap: 'anywhere',
    '@media (max-width: 520px)': {
      marginBottom: '8px',
    },
  },
  status: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
  },
  list: {
    margin: '0',
    paddingLeft: '20px',
  },
  reasons: {
    overflowX: 'auto',
  },
  reasonsTable: {
    // On a phone the table scrolls sideways inside the card rather than squeezing the reason column to nothing.
    minWidth: '420px',
  },
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    overflowWrap: 'anywhere',
  },
  number: {
    textAlign: 'right',
    whiteSpace: 'nowrap',
    width: '76px',
  },
  numberHeader: {
    justifyContent: 'flex-end',
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  dialogText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
});

/**
 * The dead-letter reasons Service Bus holds, as the importer (`CallQueueMessageHandler`, `CallQueueRetryPolicy`,
 * `CallRecordPayloadException`) and Service Bus itself give them. Maps, so a reason such as `constructor` can't find a match
 * on Object.prototype. A reason not listed is shown as the code it is. `src/i18n/lint/callDeadLetterServerCodes.test.ts`
 * reads the C# and fails when the importer gives a reason this list doesn't have.
 */
export const DEAD_LETTER_REASON_KEYS = new Map<string, TranslationKey>([
  ['InvalidCallNotification', 'callDeadLetters.reason.InvalidCallNotification'],
  ['MissingCallId', 'callDeadLetters.reason.MissingCallId'],
  ['CallImportNotRetryable', 'callDeadLetters.reason.CallImportNotRetryable'],
  ['CallImportRetriesExhausted', 'callDeadLetters.reason.CallImportRetriesExhausted'],
  ['CallImportSystemicRetriesExhausted', 'callDeadLetters.reason.CallImportSystemicRetriesExhausted'],
  ['CallImportRetryWindowExpired', 'callDeadLetters.reason.CallImportRetryWindowExpired'],
  ['MaxDeliveryCountExceeded', 'callDeadLetters.reason.MaxDeliveryCountExceeded'],
  ['NoOrganiserIdentity', 'callDeadLetters.reason.NoOrganiserIdentity'],
  ['NoSessions', 'callDeadLetters.reason.NoSessions'],
  ['NoUsableSessions', 'callDeadLetters.reason.NoUsableSessions'],
  ['NoCallType', 'callDeadLetters.reason.NoCallType'],
]);

/** `CallDeadLetterFailureCodes`: why a request didn't succeed. */
export const FAILURE_CODE_KEYS = new Map<string, TranslationKey>([
  ['queueUnavailable', 'callDeadLetters.failure.queueUnavailable'],
  ['readFailed', 'callDeadLetters.failure.readFailed'],
  ['sendFailures', 'callDeadLetters.failure.sendFailures'],
  ['sendFailed', 'callDeadLetters.failure.sendFailed'],
  ['deleteFailed', 'callDeadLetters.failure.deleteFailed'],
  ['lockedElsewhere', 'callDeadLetters.failure.lockedElsewhere'],
  ['locksLapsing', 'callDeadLetters.failure.locksLapsing'],
  ['browseRepeated', 'callDeadLetters.failure.browseRepeated'],
  ['cancelled', 'callDeadLetters.failure.cancelled'],
  ['timeBudget', 'callDeadLetters.failure.timeBudget'],
  ['scanLimit', 'callDeadLetters.failure.scanLimit'],
  ['unexpectedError', 'callDeadLetters.failure.unexpectedError'],
]);

/** `CallDeadLetterSkipCodes`: why a request left a call in the dead-letter queue on purpose. */
export const SKIP_CODE_KEYS = new Map<string, TranslationKey>([
  ['neverImportable', 'callDeadLetters.skipped.neverImportable'],
  ['callRecordNoLongerInGraph', 'callDeadLetters.skipped.callRecordNoLongerInGraph'],
  ['replayLimitReached', 'callDeadLetters.skipped.replayLimitReached'],
  ['replayedInThisRun', 'callDeadLetters.skipped.replayedInThisRun'],
  ['otherReason', 'callDeadLetters.skipped.otherReason'],
]);

/** `CallDeadLetterUnavailableReasons`: why the portal can't pass a request to the importer. */
export const UNAVAILABLE_REASON_KEYS = new Map<string, TranslationKey>([
  ['callsImportDisabled', 'callDeadLetters.unavailable.callsImportDisabled'],
  ['serviceBusNotConfigured', 'callDeadLetters.unavailable.serviceBusNotConfigured'],
  ['stateStoreInMemory', 'callDeadLetters.unavailable.stateStoreInMemory'],
]);

/** How long to wait before each status check while a request is pending; the last repeats. */
export const POLL_DELAYS_MS = [5_000, 10_000, 20_000, 30_000, 60_000];

const TIME_FORMAT: Intl.DateTimeFormatOptions = {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'UTC',
  timeZoneName: 'short',
};

/** A time the API sent, in the reader's language - in UTC, like the rest of Service configuration. */
export function formatRequestTime(value: string | null): string {
  if (!value) return '-';
  const date = parseApiDate(value);
  return Number.isNaN(date.getTime()) ? value : formatDateParts(date, TIME_FORMAT);
}

function isPending(latest: CallDeadLetterRequestView | null): boolean {
  return latest?.state === 'waiting' || latest?.state === 'running';
}

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

type BadgeColor = 'brand' | 'danger' | 'informative' | 'success' | 'warning';

function stateBadge(t: TFunction, latest: CallDeadLetterRequestView): { text: string; color: BadgeColor } {
  switch (latest.state) {
    case 'waiting':
      return { text: t('callDeadLetters.state.waiting'), color: 'informative' };
    case 'running':
      return { text: t('callDeadLetters.state.running'), color: 'brand' };
    case 'notPickedUp':
      return { text: t('callDeadLetters.state.notPickedUp'), color: 'warning' };
    case 'notFinished':
      return { text: t('callDeadLetters.state.notFinished'), color: 'warning' };
    case 'finished':
      if (latest.outcome === 'partial') return { text: t('callDeadLetters.state.partial'), color: 'warning' };
      if (latest.outcome === 'failed') return { text: t('callDeadLetters.state.failed'), color: 'danger' };
      return { text: t('callDeadLetters.state.succeeded'), color: 'success' };
    default:
      return { text: t('callDeadLetters.state.unknown', { state: latest.state }), color: 'informative' };
  }
}

function stateDetail(t: TFunction, status: CallDeadLetterStatus, latest: CallDeadLetterRequestView): string | null {
  switch (latest.state) {
    case 'waiting':
      return t('callDeadLetters.detail.waiting', { time: formatRequestTime(latest.requestedUtc) });
    case 'running':
      return t('callDeadLetters.detail.running', {
        time: formatRequestTime(latest.claimedUtc),
        minutes: formatNumber(status.runBudgetMinutes),
      });
    case 'notPickedUp':
      return t('callDeadLetters.detail.notPickedUp', {
        time: formatRequestTime(latest.requestedUtc),
        minutes: formatNumber(status.pickUpWithinMinutes),
      });
    case 'notFinished':
      return t('callDeadLetters.detail.notFinished', {
        time: formatRequestTime(latest.claimedUtc),
        hours: formatNumber(status.finishWithinMinutes / 60),
      });
    case 'finished':
      return t('callDeadLetters.detail.finished', { time: formatRequestTime(latest.finishedUtc) });
    default:
      return null;
  }
}

/** Why a finished request didn't succeed, as a sentence; a code this build doesn't know is shown as it is. */
export function failureText(t: TFunction, status: CallDeadLetterStatus, latest: CallDeadLetterRequestView, code: string): string {
  if (code === 'scanLimit' && latest.action === 'clear') return t('callDeadLetters.failure.scanLimitClear');
  const key = FAILURE_CODE_KEYS.get(code);
  return key ? t(key, { minutes: formatNumber(status.runBudgetMinutes) }) : t('callDeadLetters.failure.unknown', { code });
}

/** A dead-letter reason as text, or null for one this build doesn't know - which is then shown as its code alone. */
export function deadLetterReasonText(t: TFunction, reason: string | null): string | null {
  if (reason === null || reason === '') return t('callDeadLetters.reason.none');
  const key = DEAD_LETTER_REASON_KEYS.get(reason);
  return key ? t(key) : null;
}

function actionName(t: TFunction, action: string): string {
  if (action === 'clear') return t('callDeadLetters.actionName.clear');
  if (action === 'reprocess') return t('callDeadLetters.actionName.reprocess');
  return action;
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <>
      <Text className={styles.label}>{label}</Text>
      <div className={styles.value}>{children}</div>
    </>
  );
}

function ResultDetails({ result, action }: { result: CallDeadLetterRunResult; action: string }) {
  const styles = useStyles();
  const t = useT();
  const left = result.reasons.reduce((sum, reason) => sum + reason.left, 0);
  const skipped = Object.entries(result.skipped ?? {}).filter(([, count]) => count > 0);

  return (
    <div className={styles.section}>
      <ul className={styles.list}>
        {(action === 'reprocess' || result.replayed > 0) && (
          <li>{t('callDeadLetters.result.replayed', { count: formatNumber(result.replayed) })}</li>
        )}
        {(action === 'clear' || result.deleted > 0) && (
          <li>{t('callDeadLetters.result.deleted', { count: formatNumber(result.deleted) })}</li>
        )}
        {result.sendFailed > 0 && <li>{t('callDeadLetters.result.sendFailed', { count: formatNumber(result.sendFailed) })}</li>}
        {result.deleteFailed > 0 && (
          <li>{t('callDeadLetters.result.deleteFailed', { count: formatNumber(result.deleteFailed) })}</li>
        )}
        {left > 0 && (
          <li>
            {t('callDeadLetters.result.left', { count: formatNumber(left) })}
            {skipped.length > 0 && (
              <ul className={styles.list}>
                {skipped.map(([code, count]) => {
                  const key = SKIP_CODE_KEYS.get(code);
                  return (
                    <li key={code}>
                      {key
                        ? t(key, { count: formatNumber(count) })
                        : t('callDeadLetters.skipped.unknown', { code, count: formatNumber(count) })}
                    </li>
                  );
                })}
              </ul>
            )}
          </li>
        )}
      </ul>
      {result.replayedButNotRemoved > 0 && (
        <Text size={200} className={styles.muted}>
          {t('callDeadLetters.result.replayedButNotRemoved', { count: formatNumber(result.replayedButNotRemoved) })}
        </Text>
      )}
      {result.reasons.length > 0 && (
        <div className={styles.reasons}>
          <Text weight="semibold" block>
            {t('callDeadLetters.reasons.title')}
          </Text>
          <Table size="extra-small" className={styles.reasonsTable} aria-label={t('callDeadLetters.reasons.ariaLabel')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('callDeadLetters.reasons.reason')}</TableHeaderCell>
                <TableHeaderCell className={styles.number} button={{ className: styles.numberHeader }}>
                  {t('callDeadLetters.reasons.replayed')}
                </TableHeaderCell>
                <TableHeaderCell className={styles.number} button={{ className: styles.numberHeader }}>
                  {t('callDeadLetters.reasons.deleted')}
                </TableHeaderCell>
                <TableHeaderCell className={styles.number} button={{ className: styles.numberHeader }}>
                  {t('callDeadLetters.reasons.left')}
                </TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {result.reasons.map((reason) => {
                const text = deadLetterReasonText(t, reason.reason);
                return (
                  <TableRow key={reason.reason ?? ''}>
                    <TableCell>
                      {text && <Text block>{text}</Text>}
                      {reason.reason && <span className={styles.code}>{reason.reason}</span>}
                    </TableCell>
                    <TableCell className={styles.number}>{formatNumber(reason.replayed)}</TableCell>
                    <TableCell className={styles.number}>{formatNumber(reason.deleted)}</TableCell>
                    <TableCell className={styles.number}>{formatNumber(reason.left)}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}

function LatestRequest({ status, latest }: { status: CallDeadLetterStatus; latest: CallDeadLetterRequestView }) {
  const styles = useStyles();
  const t = useT();
  const badge = stateBadge(t, latest);
  const detail = stateDetail(t, status, latest);
  const failure =
    latest.state === 'finished' && latest.outcome !== 'succeeded' && latest.failureCode
      ? failureText(t, status, latest, latest.failureCode)
      : null;

  return (
    <>
      <div className={styles.status}>
        <Badge appearance="tint" color={badge.color}>
          {badge.text}
        </Badge>
        <Text weight="semibold">{actionName(t, latest.action)}</Text>
      </div>
      {detail && <Text>{detail}</Text>}
      {failure && <Text>{failure}</Text>}
      {latest.state === 'finished' && latest.result && <ResultDetails result={latest.result} action={latest.action} />}
    </>
  );
}

/**
 * Administration > Service configuration: the Teams calls queue's dead-lettered calls, and asking the importer to
 * reprocess or clear them.
 *
 * The portal only records the request (`POST api/CallDeadLetters/requests`); the importer, which owns the Service Bus
 * client and the replay, picks it up within a minute, carries it out and records the result, which this card follows
 * while it is pending - backing off to once a minute, and not while the tab is hidden. Administration only, like the
 * page it sits on and the API behind it.
 */
export default function CallDeadLettersCard() {
  const styles = useStyles();
  const t = useT();
  const access = usePortalAccess();
  const [status, setStatus] = useState<CallDeadLetterStatus | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState<CallDeadLetterAction | null>(null);
  const [submitError, setSubmitError] = useState<unknown>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(async () => {
    try {
      const next = await fetchCallDeadLetters();
      if (!mounted.current) return;
      setStatus(next);
      setLoadError(null);
    } catch (err) {
      if (mounted.current) setLoadError(err ?? new Error());
    }
  }, []);

  useEffect(() => {
    if (access.administration) void load();
  }, [access.administration, load]);

  const latest = status?.latest ?? null;
  const pending = isPending(latest);

  // Follows a pending request until the importer records it - or it goes stale, which the server decides.
  useEffect(() => {
    if (!pending) return undefined;
    let cancelled = false;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    const hidden = () => document.visibilityState === 'hidden';

    const schedule = () => {
      if (cancelled || inFlight || timer !== undefined || hidden()) return;
      const delay = POLL_DELAYS_MS[Math.min(attempt, POLL_DELAYS_MS.length - 1)];
      attempt += 1;
      timer = setTimeout(() => {
        timer = undefined;
        void poll();
      }, delay);
    };

    const poll = async () => {
      if (cancelled || inFlight) return;
      inFlight = true;
      try {
        const next = await fetchCallDeadLetters();
        if (cancelled) return;
        setStatus(next);
        setLoadError(null);
      } catch {
        // Tried again on the next tick; the card keeps showing what it last knew.
      } finally {
        inFlight = false;
      }
      schedule();
    };

    const onVisibilityChange = () => {
      if (hidden()) {
        if (timer !== undefined) clearTimeout(timer);
        timer = undefined;
      } else if (timer === undefined) {
        void poll();
      }
    };

    document.addEventListener('visibilitychange', onVisibilityChange);
    schedule();
    return () => {
      cancelled = true;
      if (timer !== undefined) clearTimeout(timer);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [pending, latest?.id, latest?.state]);

  if (!access.administration) return null;

  async function ask(action: CallDeadLetterAction) {
    setSubmitting(action);
    setSubmitError(null);
    try {
      const next = await requestCallDeadLetterAction(action);
      if (!mounted.current) return;
      setStatus(next);
      setConfirmOpen(false);
    } catch (err) {
      if (!mounted.current) return;
      setSubmitError(err ?? new Error());
      // Whatever the server refused for - a request already pending, say - is worth showing as it now stands.
      void load();
    } finally {
      if (mounted.current) setSubmitting(null);
    }
  }

  const empty = status?.deadLetterCount === 0;
  const canAsk = !!status && status.canRequest && !empty && submitting === null;
  const unavailableKey = status?.unavailableReason ? UNAVAILABLE_REASON_KEYS.get(status.unavailableReason) : undefined;
  const unavailableText = status?.unavailableReason
    ? unavailableKey
      ? t(unavailableKey)
      : t('callDeadLetters.unavailable.unknown', { reason: status.unavailableReason })
    : null;
  const showCount = !!status && (status.deadLetterCount !== null || status.countUnavailable);

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('callDeadLetters.title')}</Subtitle2>} />
      <div className={styles.section}>
        <Body1>{t('callDeadLetters.description')}</Body1>

        {loadError !== null && (
          <MessageBar intent="error">
            <MessageBarBody>{errorText(loadError, t('callDeadLetters.error.loadFailedGeneric'))}</MessageBarBody>
          </MessageBar>
        )}

        {!status && loadError === null && <Spinner size={24} label={t('callDeadLetters.loading')} />}

        {status && (
          <>
            {unavailableText && (
              <MessageBar intent="info">
                <MessageBarBody>{unavailableText}</MessageBarBody>
              </MessageBar>
            )}

            <div className={styles.facts} role="group" aria-label={t('callDeadLetters.ariaLabel')}>
              {showCount && (
                <Fact label={t('callDeadLetters.countLabel')}>
                  <Text>
                    {status.deadLetterCount !== null
                      ? formatNumber(status.deadLetterCount)
                      : t('callDeadLetters.countUnavailable')}
                  </Text>
                </Fact>
              )}
              {status.activeCount !== null && (
                <Fact label={t('callDeadLetters.activeLabel')}>
                  <Text>{formatNumber(status.activeCount)}</Text>
                </Fact>
              )}
              {status.available && (
                <Fact label={t('callDeadLetters.latestLabel')}>
                  {status.latestUnavailable ? (
                    <Text>{t('callDeadLetters.latestUnavailable')}</Text>
                  ) : latest ? (
                    <LatestRequest status={status} latest={latest} />
                  ) : (
                    <Text>{t('callDeadLetters.latestNone')}</Text>
                  )}
                </Fact>
              )}
            </div>

            {submitError !== null && !confirmOpen && (
              <MessageBar intent="error">
                <MessageBarBody>{errorText(submitError, t('callDeadLetters.error.requestFailedGeneric'))}</MessageBarBody>
              </MessageBar>
            )}

            <div className={styles.actions}>
              <Button
                appearance="primary"
                icon={<ArrowSync16Regular />}
                disabled={!canAsk}
                onClick={() => void ask('reprocess')}
              >
                {submitting === 'reprocess' ? t('callDeadLetters.action.requesting') : t('callDeadLetters.action.reprocess')}
              </Button>
              <Button
                icon={<Delete16Regular />}
                disabled={!canAsk}
                onClick={() => {
                  setSubmitError(null);
                  setConfirmOpen(true);
                }}
              >
                {t('callDeadLetters.action.clear')}
              </Button>
            </div>
            {status.available && !status.latestUnavailable && (
              <Text size={200} className={styles.muted}>
                {empty
                  ? t('callDeadLetters.empty')
                  : pending
                    ? t('callDeadLetters.pendingHint')
                    : t('callDeadLetters.reprocessHint')}
              </Text>
            )}
          </>
        )}
      </div>

      <Dialog
        open={confirmOpen}
        onOpenChange={(_, data) => {
          if (submitting === null) setConfirmOpen(data.open);
        }}
      >
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('callDeadLetters.confirm.title')}</DialogTitle>
            <DialogContent className={styles.dialogText}>
              <Text block>{t('callDeadLetters.confirm.body')}</Text>
              {status?.deadLetterCount != null && (
                <Text block>{t('callDeadLetters.confirm.count', { count: formatNumber(status.deadLetterCount) })}</Text>
              )}
              <Text block>{t('callDeadLetters.confirm.alternative')}</Text>
              {submitError !== null && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorText(submitError, t('callDeadLetters.error.requestFailedGeneric'))}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button
                appearance="primary"
                icon={<Delete16Regular />}
                onClick={() => void ask('clear')}
                disabled={submitting !== null}
              >
                {submitting === 'clear' ? t('callDeadLetters.action.requesting') : t('callDeadLetters.confirm.clear')}
              </Button>
              <Button appearance="secondary" onClick={() => setConfirmOpen(false)} disabled={submitting !== null}>
                {t('common.action.cancel')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Card>
  );
}
