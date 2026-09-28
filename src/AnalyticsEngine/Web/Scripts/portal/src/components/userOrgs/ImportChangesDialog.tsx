import { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
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
import { SearchRegular } from '@fluentui/react-icons';
import Spinner from '../Spinner';
import { MAX_CHANGE_PAGE_SIZE, fetchImportChanges } from '../../api/userOrgsApi';
import { formatDateParts, formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';
import { csvDocument, derivedFileName, downloadCsv } from './csvUnusableRows';
import type {
  UserOrgChange,
  UserOrgChangeKind,
  UserOrgChangeLogPage,
  UserOrgChangeLogSummary,
  UserOrgImportJob,
} from '../../types/userOrgs';

/** Changes per page on screen. */
const PAGE_SIZE = 50;

const KIND_KEYS: Record<UserOrgChangeKind, TranslationKey> = {
  added: 'userOrgs.changes.kind.added',
  changed: 'userOrgs.changes.kind.changed',
  cleared: 'userOrgs.changes.kind.cleared',
};

const KIND_COLOURS: Record<UserOrgChangeKind, 'success' | 'informative' | 'warning'> = {
  added: 'success',
  changed: 'informative',
  cleared: 'warning',
};

const useStyles = makeStyles({
  surface: { maxWidth: '960px', width: '95vw' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  searchRow: { display: 'flex', gap: '8px' },
  grow: { flexGrow: 1, minWidth: 0 },
  upn: { wordBreak: 'break-all' },
  value: { wordBreak: 'break-word' },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
});

export interface ImportChangesDialogProps {
  /** The import whose changes to show; the dialog is open while this is set. */
  job: UserOrgImportJob | null;
  onDismiss: () => void;
}

interface Loaded {
  page: UserOrgChangeLogPage | null;
  items: UserOrgChange[];
  continuation: string | null;
  loading: boolean;
  failed: boolean;
}

const EMPTY: Loaded = { page: null, items: [], continuation: null, loading: true, failed: false };

/**
 * What one import changed, user by user: the value before and after, alphabetically by user principal
 * name, searchable by the start of the UPN, and downloadable in full. Every UPN and value is tenant
 * data, shown exactly as stored.
 */
export default function ImportChangesDialog({ job, onDismiss }: ImportChangesDialogProps) {
  const styles = useStyles();
  return (
    <Dialog open={job !== null} onOpenChange={(_e, data) => !data.open && onDismiss()}>
      <DialogSurface mountNode={undefined} className={styles.surface}>
        {job && <ChangesBody key={job.id} job={job} onDismiss={onDismiss} />}
      </DialogSurface>
    </Dialog>
  );
}

function ChangesBody({ job, onDismiss }: { job: UserOrgImportJob; onDismiss: () => void }) {
  const styles = useStyles();
  const t = useT();

  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  // Bumped by Search and "Try again", so asking again with nothing else changed still asks again.
  const [attempt, setAttempt] = useState(0);
  const [loaded, setLoaded] = useState<Loaded>(EMPTY);
  const [exporting, setExporting] = useState<number | null>(null);
  const [exportFailed, setExportFailed] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setLoaded(EMPTY);
    fetchImportChanges(job.id, { search, pageSize: PAGE_SIZE }, controller.signal)
      .then((page) =>
        setLoaded({ page, items: page.items, continuation: page.continuation, loading: false, failed: false }),
      )
      .catch(() => {
        if (!controller.signal.aborted) setLoaded({ ...EMPTY, loading: false, failed: true });
      });
    return () => controller.abort();
  }, [job.id, search, attempt]);

  const loadMore = async () => {
    const continuation = loaded.continuation;
    if (!continuation) return;
    setLoaded((s) => ({ ...s, loading: true, failed: false }));
    try {
      const next = await fetchImportChanges(job.id, { search, continuation, pageSize: PAGE_SIZE });
      setLoaded((s) => ({ ...s, items: [...s.items, ...next.items], continuation: next.continuation, loading: false }));
    } catch {
      setLoaded((s) => ({ ...s, loading: false, failed: true }));
    }
  };

  const commitSearch = () => {
    setSearch(draft.trim());
    setAttempt((n) => n + 1);
  };

  // Every change, in pages of the most the server returns, into one CSV worded in the reader's language.
  const downloadAll = async () => {
    setExporting(0);
    setExportFailed(false);
    const rows: string[][] = [
      [
        t('userOrgs.changes.column.user'),
        t('userOrgs.changes.column.before'),
        t('userOrgs.changes.column.after'),
        t('userOrgs.changes.column.change'),
      ],
    ];
    try {
      let continuation: string | null = null;
      do {
        const page: UserOrgChangeLogPage = await fetchImportChanges(job.id, { continuation, pageSize: MAX_CHANGE_PAGE_SIZE });
        if (page.status !== 'available') throw new Error(page.status);
        for (const change of page.items) {
          rows.push([change.upn, change.before ?? '', change.after ?? '', kindLabel(change.kind, t)]);
        }
        setExporting(rows.length - 1);
        continuation = page.continuation;
      } while (continuation);

      downloadCsv(
        csvDocument(rows),
        derivedFileName(job.fileName, t('userOrgs.changes.defaultFileName'), `changes-${job.id}`),
      );
    } catch {
      setExportFailed(true);
    } finally {
      setExporting(null);
    }
  };

  const page = loaded.page;
  const summary = page?.status === 'available' ? page.summary : null;

  return (
    <DialogBody>
      <DialogTitle>{t('userOrgs.changes.title')}</DialogTitle>
      <DialogContent>
        <div className={styles.body}>
          {summary && <SummaryLines summary={summary} storage={page?.storage ?? null} t={t} />}

          {!page && loaded.loading && <Spinner size={40} label={t('userOrgs.changes.loading')} />}

          {loaded.failed && (
            <MessageBar intent="error">
              <MessageBarBody>{t('userOrgs.changes.loadFailed')}</MessageBarBody>
              <MessageBarActions>
                <Button size="small" onClick={() => setAttempt((n) => n + 1)}>
                  {t('userOrgs.changes.retry')}
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}

          {page && page.status !== 'available' && (
            <MessageBar intent={page.status === 'pending' ? 'info' : 'warning'}>
              <MessageBarBody>{statusMessage(page, t)}</MessageBarBody>
              {(page.status === 'pending' || page.status === 'unavailable') && (
                <MessageBarActions>
                  <Button size="small" onClick={() => setAttempt((n) => n + 1)}>
                    {t('userOrgs.changes.retry')}
                  </Button>
                </MessageBarActions>
              )}
            </MessageBar>
          )}

          {summary && (
            <>
              <div className={styles.searchRow}>
                <Input
                  className={styles.grow}
                  value={draft}
                  maxLength={250}
                  placeholder={t('userOrgs.changes.searchPlaceholder')}
                  aria-label={t('userOrgs.changes.searchPlaceholder')}
                  onChange={(_: unknown, d: { value: string }) => setDraft(d.value)}
                  onKeyDown={(e: { key: string }) => {
                    if (e.key === 'Enter') commitSearch();
                  }}
                />
                <Button icon={<SearchRegular />} onClick={commitSearch}>
                  {t('common.action.search')}
                </Button>
              </div>

              {loaded.items.length === 0 && !loaded.loading && (
                <Text className={styles.muted}>
                  {search ? t('userOrgs.changes.noMatches', { search }) : t('userOrgs.changes.empty')}
                </Text>
              )}

              {loaded.items.length > 0 && (
                <Table size="small" aria-label={t('userOrgs.changes.tableLabel')}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>{t('userOrgs.changes.column.user')}</TableHeaderCell>
                      <TableHeaderCell>{t('userOrgs.changes.column.before')}</TableHeaderCell>
                      <TableHeaderCell>{t('userOrgs.changes.column.after')}</TableHeaderCell>
                      <TableHeaderCell>{t('userOrgs.changes.column.change')}</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {loaded.items.map((change, index) => (
                      <TableRow key={`${index}:${change.upn}`}>
                        <TableCell className={styles.upn}>{change.upn}</TableCell>
                        <TableCell className={styles.value}>
                          {change.before ?? <span className={styles.muted}>{t('userOrgs.changes.noValue')}</span>}
                        </TableCell>
                        <TableCell className={styles.value}>
                          {change.after ?? <span className={styles.muted}>{t('userOrgs.changes.noValue')}</span>}
                        </TableCell>
                        <TableCell>
                          <Badge appearance="tint" color={isKind(change.kind) ? KIND_COLOURS[change.kind] : 'informative'}>
                            {kindLabel(change.kind, t)}
                          </Badge>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}

              <div className={styles.footer}>
                {!search && loaded.items.length > 0 && (
                  <Text size={200} className={styles.muted}>
                    {t('userOrgs.changes.showing', {
                      count: formatNumber(loaded.items.length),
                      total: formatNumber(summary.storedChanges),
                    })}
                  </Text>
                )}
                {loaded.continuation && (
                  <Button onClick={loadMore} disabled={loaded.loading}>
                    {t('userOrgs.changes.loadMore')}
                  </Button>
                )}
                {summary.storedChanges > 0 && (
                  <Button onClick={downloadAll} disabled={exporting !== null}>
                    {t('userOrgs.changes.download')}
                  </Button>
                )}
                {exporting !== null && (
                  <Text size={200}>{t('userOrgs.changes.downloading', { count: formatNumber(exporting) })}</Text>
                )}
              </div>

              {exportFailed && (
                <MessageBar intent="error">
                  <MessageBarBody>{t('userOrgs.changes.downloadFailed')}</MessageBarBody>
                </MessageBar>
              )}
            </>
          )}
        </div>
      </DialogContent>
      <DialogActions>
        <Button appearance="secondary" onClick={onDismiss}>
          {t('userOrgs.changes.close')}
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

function SummaryLines({
  summary,
  storage,
  t,
}: {
  summary: UserOrgChangeLogSummary;
  storage: 'tableStorage' | 'memory' | null;
  t: TFunction;
}) {
  const styles = useStyles();
  const date = formatDateParts(new Date(summary.finishedUtc ?? summary.queuedUtc), { dateStyle: 'medium', timeStyle: 'short' });
  const who = summary.startedBy ?? t('userOrgs.source.unknownUser');
  const mode = t(summary.mode === 'replace' ? 'userOrgs.history.mode.replace' : 'userOrgs.history.mode.merge');

  return (
    <div>
      <Text block>
        {summary.fileName
          ? t('userOrgs.changes.importedBy', { date, who, mode, file: summary.fileName })
          : t('userOrgs.changes.importedByNoFile', { date, who, mode })}
      </Text>
      <Text block weight="semibold">
        {t('userOrgs.changes.counts', {
          added: formatNumber(summary.added),
          changed: formatNumber(summary.changed),
          cleared: formatNumber(summary.cleared),
        })}
      </Text>
      {storage === 'memory' ? (
        <MessageBar intent="warning">
          <MessageBarBody>{t('userOrgs.changes.storage.memory')}</MessageBarBody>
        </MessageBar>
      ) : (
        <Text size={200} block className={styles.muted}>
          {t('userOrgs.changes.storage.tableStorage')}
        </Text>
      )}
      {summary.storedChanges < summary.changeCount && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t('userOrgs.changes.truncated', {
              stored: formatNumber(summary.storedChanges),
              count: formatNumber(summary.changeCount),
            })}
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

function statusMessage(page: UserOrgChangeLogPage, t: TFunction): string {
  switch (page.status) {
    case 'pending':
      return t('userOrgs.changes.status.pending');
    case 'none':
      return t('userOrgs.changes.status.none');
    case 'missing':
      return t(page.storage === 'memory' ? 'userOrgs.changes.status.missingMemory' : 'userOrgs.changes.status.missingTable');
    default:
      return t('userOrgs.changes.status.unavailable');
  }
}

function isKind(kind: string): kind is UserOrgChangeKind {
  return Object.prototype.hasOwnProperty.call(KIND_KEYS, kind);
}

function kindLabel(kind: string, t: TFunction): string {
  return isKind(kind) ? t(KIND_KEYS[kind]) : kind;
}
