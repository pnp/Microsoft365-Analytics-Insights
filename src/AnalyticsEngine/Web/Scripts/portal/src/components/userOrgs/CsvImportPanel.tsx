import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Field,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Select,
  Spinner,
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
import { SessionExpiredError } from '../../api/http';
import { UserOrgApiError, fetchImportHistory, fetchImportJob, importCsv, previewCsv } from '../../api/userOrgsApi';
import {
  formatDateParts,
  formatNumber,
  plural,
  useT,
  type TFunction,
  type TranslationKey,
} from '../../i18n';
import { STATUS_KEYS, userOrgErrorMessage } from './userOrgShared';
import { buildUnusableRowsCsv, downloadCsv, unusableRowsFileName } from './csvUnusableRows';
import ImportChangesDialog from './ImportChangesDialog';
import type {
  UserOrgCsvBlockingCode,
  UserOrgCsvColumnChoice,
  UserOrgCsvPreview,
  UserOrgCsvRowProblemCode,
  UserOrgImportErrorCode,
  UserOrgImportJob,
  UserOrgImportMode,
  UserOrgType,
} from '../../types/userOrgs';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  columnChooser: { display: 'grid', gap: '8px', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  counts: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '4px' },
  history: { display: 'flex', flexDirection: 'column', gap: '8px' },
  summary: {
    cursor: 'pointer',
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
});

const INITIAL_POLL_DELAY_MS = 2000;
const MAX_POLL_DELAY_MS = 30000;
const HISTORY_TAKE = 10;

/** Each row problem the server can report (`UserOrgCsvProblemCodes`), as a catalogue key. */
export const ROW_PROBLEM_KEYS: Record<UserOrgCsvRowProblemCode, TranslationKey> = {
  missingUserColumn: 'userOrgs.csv.problem.missingUserColumn',
  userEmptyOrTooLong: 'userOrgs.csv.problem.userEmptyOrTooLong',
  notAValidUpn: 'userOrgs.csv.problem.notAValidUpn',
  tooManyValues: 'userOrgs.csv.problem.tooManyValues',
  unknownUser: 'userOrgs.csv.problem.unknownUser',
};

/** Each reason the server can refuse a whole file (`UserOrgCsvBlockingCodes`), as a catalogue key. */
export const BLOCKING_KEYS: Record<UserOrgCsvBlockingCode, TranslationKey> = {
  notUtf8: 'userOrgs.csv.blocking.notUtf8',
  excelWorkbook: 'userOrgs.csv.blocking.excelWorkbook',
  notText: 'userOrgs.csv.blocking.notText',
  unterminatedQuote: 'userOrgs.csv.blocking.unterminatedQuote',
  rowSpansLines: 'userOrgs.csv.blocking.rowSpansLines',
  chooseColumns: 'userOrgs.csv.blocking.chooseColumns',
  oneColumn: 'userOrgs.csv.blocking.oneColumn',
  tooManyRows: 'userOrgs.csv.blocking.tooManyRows',
  noRows: 'userOrgs.csv.blocking.noRows',
  noUsableRows: 'userOrgs.csv.blocking.noUsableRows',
};

const API_ERROR_KEYS: Record<string, TranslationKey> = {
  importInProgress: 'userOrgs.csv.apiError.importInProgress',
  draftNotFound: 'userOrgs.csv.apiError.draftNotFound',
  typeChanged: 'userOrgs.csv.apiError.typeChanged',
  typeNotFound: 'userOrgs.csv.apiError.typeNotFound',
  typeNotCsv: 'userOrgs.csv.apiError.typeNotCsv',
  typeDisabled: 'userOrgs.csv.apiError.typeDisabled',
  noMatchingUsers: 'userOrgs.csv.apiError.noMatchingUsers',
  clearExceedsConfirmed: 'userOrgs.csv.apiError.clearExceedsConfirmed',
  noFile: 'userOrgs.csv.apiError.noFile',
  uploadUnreadable: 'userOrgs.csv.apiError.uploadUnreadable',
  uploadTooLarge: 'userOrgs.csv.apiError.uploadTooLarge',
  invalidMode: 'userOrgs.csv.apiError.invalidMode',
  invalidColumns: 'userOrgs.csv.apiError.invalidColumns',
};

const JOB_ERROR_KEYS: Record<UserOrgImportErrorCode, TranslationKey> = {
  failed: 'userOrgs.job.error.failed',
  superseded: 'userOrgs.job.error.superseded',
  typeChanged: 'userOrgs.job.error.typeChanged',
  clearExceedsConfirmed: 'userOrgs.job.error.clearExceedsConfirmed',
  interruptedRepeatedly: 'userOrgs.job.error.interruptedRepeatedly',
};

/** The separator the server detected, as a token (`UserOrgAdminService.DescribeDelimiter`). */
export const CSV_DELIMITER_KEYS: Record<string, TranslationKey> = {
  comma: 'userOrgs.csv.delimiter.comma',
  semicolon: 'userOrgs.csv.delimiter.semicolon',
  tab: 'userOrgs.csv.delimiter.tab',
  pipe: 'userOrgs.csv.delimiter.pipe',
};

function delimiterName(token: string, t: TFunction): string {
  return Object.prototype.hasOwnProperty.call(CSV_DELIMITER_KEYS, token) ? t(CSV_DELIMITER_KEYS[token]) : token;
}

export interface CsvImportPanelProps {
  orgType: UserOrgType;
  /** Called when an import finishes, so the page can refresh its counts. */
  onImportFinished: () => void;
}

export default function CsvImportPanel({ orgType, onImportFinished }: CsvImportPanelProps) {
  const styles = useStyles();
  const t = useT();
  const fileInput = useRef<HTMLInputElement>(null);

  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<UserOrgCsvPreview | null>(null);
  const [columns, setColumns] = useState<UserOrgCsvColumnChoice>({});
  const [mode, setMode] = useState<UserOrgImportMode>('merge');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [job, setJob] = useState<UserOrgImportJob | null>(null);
  const [lastImport, setLastImport] = useState<UserOrgImportJob | null>(orgType.lastImport);
  const [confirmedClear, setConfirmedClear] = useState(false);
  const [pollFailures, setPollFailures] = useState(0);
  const [pollTerminalError, setPollTerminalError] = useState<string | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [historyLoaded, setHistoryLoaded] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [history, setHistory] = useState<UserOrgImportJob[]>([]);
  const [changesFor, setChangesFor] = useState<UserOrgImportJob | null>(null);
  // Which preview request is the latest; see runPreview.
  const previewRun = useRef(0);

  const running = job !== null && (job.status === 'pending' || job.status === 'running');
  const matchedRows = preview ? preview.totalRows - preview.unknownUpnCount : 0;
  const noMatchingRows = preview !== null && preview.totalRows > 0 && matchedRows === 0;
  const clearCountForMode = preview ? (mode === 'replace' ? preview.wouldClearCount : preview.mergeWouldClearCount) : 0;
  const needsClearConfirmation = clearCountForMode > 0;
  const importDisabled =
    busy ||
    preview === null ||
    preview.blocking !== null ||
    preview.draftId === null ||
    noMatchingRows ||
    (needsClearConfirmation && !confirmedClear);

  useEffect(() => {
    const last = orgType.lastImport;
    setLastImport(last);
    if (last && (last.status === 'pending' || last.status === 'running')) {
      setJob(last);
    }
  }, [orgType.lastImport]);

  useEffect(() => {
    if (!running || job === null) return;

    let cancelled = false;
    let timer = 0;
    const controller = new AbortController();

    const poll = async (delay: number, failures: number) => {
      try {
        const latest = await fetchImportJob(job.id, controller.signal);
        if (cancelled) return;
        setJob(latest);
        setLastImport(latest);
        setPollFailures(0);
        setPollTerminalError(null);
        if (latest.status !== 'pending' && latest.status !== 'running') {
          onImportFinished();
          refreshHistory();
          return;
        }
        timer = window.setTimeout(() => poll(INITIAL_POLL_DELAY_MS, 0), INITIAL_POLL_DELAY_MS);
      } catch (e) {
        if (cancelled) return;
        if (e instanceof UserOrgApiError && e.status === 404) {
          setPollTerminalError(t('userOrgs.job.poll.notFound'));
          return;
        }
        if (e instanceof SessionExpiredError) {
          setPollTerminalError(t('userOrgs.job.poll.sessionExpired'));
          return;
        }

        const nextFailures = failures + 1;
        const nextDelay = Math.min(delay * 2, MAX_POLL_DELAY_MS);
        setPollFailures(nextFailures);
        timer = window.setTimeout(() => poll(nextDelay, nextFailures), nextDelay);
      }
    };

    timer = window.setTimeout(() => poll(INITIAL_POLL_DELAY_MS, 0), INITIAL_POLL_DELAY_MS);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [running, job?.id, onImportFinished, t]);

  const refreshHistory = async () => {
    // Always fetched, even while the list is closed: the import that just finished must be in it the
    // next time it is opened, and it is ten rows.
    setHistoryLoading(true);
    setHistoryError(null);
    const controller = new AbortController();
    try {
      setHistory(await fetchImportHistory(orgType.id, HISTORY_TAKE, controller.signal));
      setHistoryLoaded(true);
    } catch (e) {
      setHistoryError(userOrgErrorMessage(e, t, 'errors.userOrgs.loadFailed'));
    } finally {
      setHistoryLoading(false);
    }
  };

  const runPreview = async (chosen: File, chosenColumns: UserOrgCsvColumnChoice, previous: UserOrgCsvPreview | null = null) => {
    // Only the latest preview may land. Clear, or another file chosen, while a slow one is reading
    // makes this one stale - and applying it anyway would bring back the file the admin just cleared,
    // with its draft ready to import.
    const run = ++previewRun.current;
    setBusy(true);
    setError(null);
    setConfirmedClear(false);
    try {
      const nextPreview = await previewCsv(orgType.id, chosen, chosenColumns);
      if (run !== previewRun.current) return;
      setPreview(nextPreview);
      setColumns({
        userColumn: nextPreview.userColumnIndex ?? chosenColumns.userColumn,
        valueColumn: nextPreview.valueColumnIndex ?? chosenColumns.valueColumn,
      });
    } catch (e) {
      if (run !== previewRun.current) return;
      if (previous) {
        // A failed re-read with other columns keeps the preview the admin already has, and puts the
        // column choice back to what that preview actually used - so what is shown, and what an
        // import would commit, still agree.
        setPreview(previous);
        setColumns({
          userColumn: previous.userColumnIndex ?? undefined,
          valueColumn: previous.valueColumnIndex ?? undefined,
        });
      } else {
        setPreview(null);
      }
      setError(apiErrorMessage(e, t));
    } finally {
      if (run === previewRun.current) setBusy(false);
    }
  };

  const chooseFile = async (chosen: File | null) => {
    previewRun.current++;
    setBusy(false);
    setFile(chosen);
    setPreview(null);
    setError(null);
    setJob(null);
    setColumns({});
    setConfirmedClear(false);
    setPollFailures(0);
    setPollTerminalError(null);
    if (!chosen) return;
    await runPreview(chosen, {});
  };

  const chooseColumn = async (kind: keyof UserOrgCsvColumnChoice, value: number) => {
    if (!file || !preview) return;
    // Both columns are always sent together: the one the admin did not touch is the one already
    // chosen, or the one the server read. When neither has been settled yet, wait for it - half a
    // choice is refused by the server, and guessing the other column is exactly what this avoids.
    const next: UserOrgCsvColumnChoice = {
      userColumn: columns.userColumn ?? preview.userColumnIndex ?? undefined,
      valueColumn: columns.valueColumn ?? preview.valueColumnIndex ?? undefined,
      [kind]: value,
    };
    setColumns(next);
    if (next.userColumn === undefined || next.valueColumn === undefined) {
      setError(null);
      return;
    }
    if (next.userColumn === next.valueColumn) {
      setError(t('userOrgs.csv.apiError.invalidColumns'));
      return;
    }
    await runPreview(file, next, preview);
  };

  const startImport = async () => {
    if (!preview?.draftId) return;
    setBusy(true);
    setError(null);
    try {
      const confirmedCount = confirmedClear ? clearCountForMode : 0;
      const queued = await importCsv(orgType.id, preview.draftId, mode, confirmedCount);
      const queuedJob: UserOrgImportJob = {
        id: queued.jobId,
        orgTypeId: orgType.id,
        mode,
        status: 'pending',
        fileName: file?.name ?? preview.fileName,
        startedBy: null,
        queuedUtc: new Date().toISOString(),
        startedUtc: null,
        finishedUtc: null,
        attempts: 0,
        rowsTotal: queued.rowsQueued,
        rowsApplied: 0,
        rowsCleared: 0,
        rowsUnknownUpn: 0,
        rowsInvalid: queued.rowsInvalid,
        errorCode: null,
        errorMessage: null,
        changeLog: null,
      };
      setJob(queuedJob);
      setLastImport(queuedJob);
    } catch (e) {
      setError(apiErrorMessage(e, t));
    } finally {
      setBusy(false);
    }
  };

  const reset = () => {
    // Abandons a preview still being read: see runPreview.
    previewRun.current++;
    setBusy(false);
    setFile(null);
    setPreview(null);
    setJob(null);
    setError(null);
    setColumns({});
    setConfirmedClear(false);
    setPollFailures(0);
    setPollTerminalError(null);
    if (fileInput.current) fileInput.current.value = '';
  };

  return (
    <div className={styles.panel}>
      <div className={styles.row}>
        <input
          ref={fileInput}
          type="file"
          accept=".csv,.txt,text/csv,text/plain"
          aria-label={t('userOrgs.csv.fileLabel')}
          onChange={(e) => chooseFile(e.target.files?.[0] ?? null)}
          disabled={busy || running}
        />
        {busy && (
          <div className={styles.row}>
            <Spinner size="tiny" />
            <Text>{t('userOrgs.csv.previewing')}</Text>
          </div>
        )}
        {(file || job) && (
          <Button appearance="subtle" size="small" onClick={reset} disabled={running}>
            {t('userOrgs.csv.clear')}
          </Button>
        )}
      </div>

      {error && <ErrorBar>{error}</ErrorBar>}

      {pollTerminalError && <ErrorBar>{pollTerminalError}</ErrorBar>}
      {!pollTerminalError && pollFailures >= 3 && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('userOrgs.job.poll.warning')}</MessageBarBody>
        </MessageBar>
      )}

      {preview?.blocking && (
        <MessageBar intent="error">
          <MessageBarBody>{blockingMessage(preview, t)}</MessageBarBody>
        </MessageBar>
      )}

      {preview && !job && shouldShowColumnChooser(preview) && (
        <ColumnChooser
          preview={preview}
          choice={columns}
          onChoose={chooseColumn}
          busy={busy}
          styles={styles}
          t={t}
        />
      )}

      {preview && !job && <PreviewTable preview={preview} orgType={orgType} styles={styles} t={t} />}

      {preview && !job && !preview.blocking && (
        <>
          <Field label={t('userOrgs.csv.modeLabel')}>
            <RadioGroup
              value={mode}
              onChange={(_e, d) => {
                setMode(d.value as UserOrgImportMode);
                setConfirmedClear(false);
              }}
            >
              <Radio value="merge" label={t('userOrgs.csv.modeMerge')} />
              <Radio value="replace" label={t('userOrgs.csv.modeReplace', { name: orgType.name })} />
            </RadioGroup>
          </Field>

          {mode === 'merge' && preview.mergeWouldClearCount > 0 && (
            <ClearWarning
              clearCount={preview.mergeWouldClearCount}
              name={orgType.name}
              checkboxLabel={t('userOrgs.csv.confirmMergeClear')}
              confirmed={confirmedClear}
              onConfirmed={setConfirmedClear}
              t={t}
            />
          )}

          {mode === 'replace' && preview.wouldClearCount > 0 && (
            <ReplaceWarning
              preview={preview}
              name={orgType.name}
              confirmed={confirmedClear}
              onConfirmed={setConfirmedClear}
              t={t}
            />
          )}

          <div>
            <Button appearance="primary" onClick={startImport} disabled={importDisabled}>
              {t(plural(preview.totalRows, 'userOrgs.csv.import.one', 'userOrgs.csv.import.other'), {
                count: formatNumber(preview.totalRows),
              })}
            </Button>
          </div>
        </>
      )}

      {job && <JobProgress job={job} styles={styles} t={t} onShowChanges={setChangesFor} />}

      {lastImport && <LastImportLine job={lastImport} t={t} />}

      <ImportHistory
        open={historyOpen}
        loading={historyLoading}
        loaded={historyLoaded}
        error={historyError}
        jobs={history}
        styles={styles}
        t={t}
        onShowChanges={setChangesFor}
        onToggle={async (open) => {
          setHistoryOpen(open);
          if (open && !historyLoaded) {
            await refreshHistory();
          }
        }}
      />

      <ImportChangesDialog job={changesFor} onDismiss={() => setChangesFor(null)} />
    </div>
  );
}

function ErrorBar({ children }: { children: string }) {
  return (
    <MessageBar intent="error">
      <MessageBarBody>{children}</MessageBarBody>
    </MessageBar>
  );
}

function ColumnChooser({
  preview,
  choice,
  onChoose,
  busy,
  styles,
  t,
}: {
  preview: UserOrgCsvPreview;
  choice: UserOrgCsvColumnChoice;
  onChoose: (kind: keyof UserOrgCsvColumnChoice, value: number) => void;
  busy: boolean;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
}) {
  const selectedUser = choice.userColumn ?? preview.userColumnIndex ?? null;
  const selectedValue = choice.valueColumn ?? preview.valueColumnIndex ?? null;

  return (
    <div>
      <Text size={200} className={styles.muted} block>
        {t('userOrgs.csv.columnChooser.hint', { count: formatNumber(preview.columnCount) })}
      </Text>
      <div className={styles.columnChooser}>
        <Field label={t('userOrgs.csv.columnChooser.user')}>
          <Select
            value={selectedUser === null ? '' : String(selectedUser)}
            disabled={busy}
            onChange={(e) => onChoose('userColumn', Number(e.currentTarget.value))}
          >
            {selectedUser === null && (
              <option value="" disabled>
                {t('userOrgs.csv.columnChooser.placeholder')}
              </option>
            )}
            {columnOptions(preview, t)}
          </Select>
        </Field>
        <Field label={t('userOrgs.csv.columnChooser.value')}>
          <Select
            value={selectedValue === null ? '' : String(selectedValue)}
            disabled={busy}
            onChange={(e) => onChoose('valueColumn', Number(e.currentTarget.value))}
          >
            {selectedValue === null && (
              <option value="" disabled>
                {t('userOrgs.csv.columnChooser.placeholder')}
              </option>
            )}
            {columnOptions(preview, t)}
          </Select>
        </Field>
      </div>
    </div>
  );
}

function columnOptions(preview: UserOrgCsvPreview, t: TFunction) {
  return Array.from({ length: Math.max(0, preview.columnCount) }, (_unused, i) => (
    <option key={i} value={i}>
      {preview.columns?.[i]?.trim() || t('userOrgs.csv.columnChooser.fallback', { number: formatNumber(i + 1) })}
    </option>
  ));
}

function PreviewTable({
  preview,
  orgType,
  styles,
  t,
}: {
  preview: UserOrgCsvPreview;
  orgType: UserOrgType;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
}) {
  const matchedRows = preview.totalRows - preview.unknownUpnCount;

  return (
    <div>
      {!preview.blocking && (
        <>
          <Text size={200} className={styles.muted} block>
            {preview.headerDetected
              ? t('userOrgs.csv.headerFound', {
                  upnColumn: preview.upnColumnName ?? '',
                  orgColumn: preview.orgColumnName ?? '',
                  delimiter: delimiterName(preview.delimiter, t),
                })
              : t('userOrgs.csv.headerMissing', { delimiter: delimiterName(preview.delimiter, t) })}
          </Text>

          <Text size={200} className={styles.muted} block>
            {t('userOrgs.csv.matchSummary', {
              matched: formatNumber(matchedRows),
              total: formatNumber(preview.totalRows),
            })}
          </Text>
        </>
      )}

      {preview.totalRows > 0 && matchedRows === 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('userOrgs.csv.noMatches')}</MessageBarBody>
        </MessageBar>
      )}

      {preview.unknownUpnCount > 0 && matchedRows > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t(plural(preview.unknownUpnCount, 'userOrgs.csv.unknownRows.one', 'userOrgs.csv.unknownRows.other'), {
              count: formatNumber(preview.unknownUpnCount),
            })}
          </MessageBarBody>
        </MessageBar>
      )}

      {preview.rows.length > 0 && (
        <Table size="small" aria-label={t('userOrgs.csv.previewAriaLabel')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('userOrgs.csv.column.line')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.csv.column.user')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.csv.column.organisation')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.csv.column.matches')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {preview.rows.map((row) => (
              <TableRow key={row.lineNumber}>
                <TableCell>{row.lineNumber}</TableCell>
                <TableCell className={styles.mono}>{row.upn}</TableCell>
                <TableCell>
                  {row.clearsValue ? (
                    <Badge appearance="tint" color="warning">
                      {t('userOrgs.csv.clearsValue')}
                    </Badge>
                  ) : (
                    row.orgValue
                  )}
                </TableCell>
                <TableCell>
                  {row.userExists ? (
                    <Badge appearance="tint" color="success">
                      {t('admin.common.yes')}
                    </Badge>
                  ) : (
                    <Badge appearance="tint" color="danger">
                      {t('admin.common.no')}
                    </Badge>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      {preview.moreRowsExist && (
        <Text size={200} className={styles.muted} block>
          {t('userOrgs.csv.showingFirst', { count: formatNumber(preview.rows.length) })}
        </Text>
      )}

      {preview.truncatedValueCount > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t(plural(preview.truncatedValueCount, 'userOrgs.csv.truncated.one', 'userOrgs.csv.truncated.other'), {
              count: formatNumber(preview.truncatedValueCount),
              max: formatNumber(preview.maxValueLength),
            })}
          </MessageBarBody>
        </MessageBar>
      )}

      {preview.problems.length > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t('userOrgs.csv.problems', {
              problems: preview.problems
                .map((p) =>
                  t('userOrgs.csv.problemLine', {
                    line: formatNumber(p.lineNumber),
                    reason: rowProblemMessage(p.code, p.reason, t),
                  }),
                )
                .join('; '),
            })}
          </MessageBarBody>
        </MessageBar>
      )}

      {preview.unusableRowCount > 0 && (
        <UnusableRowsDownload preview={preview} orgType={orgType} t={t} />
      )}
    </div>
  );
}

function UnusableRowsDownload({
  preview,
  orgType,
  t,
}: {
  preview: UserOrgCsvPreview;
  orgType: UserOrgType;
  t: TFunction;
}) {
  const truncated = preview.unusableRows.length < preview.unusableRowCount;

  const download = () => {
    const csv = buildUnusableRowsCsv(
      preview.unusableRows,
      {
        lineHeader: t('userOrgs.csv.unusable.column.line'),
        userHeader: t('userOrgs.csv.unusable.column.user'),
        valueHeader: t('userOrgs.csv.unusable.column.value', { name: orgType.name }),
        reasonHeader: t('userOrgs.csv.unusable.column.reason'),
      },
      (code) => rowProblemMessage(code, code, t),
    );
    downloadCsv(csv, unusableRowsFileName(preview.fileName, t('userOrgs.csv.unusable.defaultFileName')));
  };

  return (
    <div>
      <Button onClick={download}>
        {t(plural(preview.unusableRowCount, 'userOrgs.csv.unusable.download.one', 'userOrgs.csv.unusable.download.other'), {
          count: formatNumber(preview.unusableRowCount),
        })}
      </Button>
      {truncated && (
        <Text size={200} className="csv-unusable-truncated" block>
          {t('userOrgs.csv.unusable.truncated', {
            shown: formatNumber(preview.unusableRows.length),
            count: formatNumber(preview.unusableRowCount),
          })}
        </Text>
      )}
    </div>
  );
}

function ReplaceWarning({
  preview,
  name,
  confirmed,
  onConfirmed,
  t,
}: {
  preview: UserOrgCsvPreview;
  name: string;
  confirmed: boolean;
  onConfirmed: (confirmed: boolean) => void;
  t: TFunction;
}) {
  return (
    <MessageBar intent="warning">
      <MessageBarBody>
        <strong>
          {t(plural(preview.wouldClearCount, 'userOrgs.csv.clearWarning.one', 'userOrgs.csv.clearWarning.other'), {
            count: formatNumber(preview.wouldClearCount),
            name,
          })}
        </strong>{' '}
        {t('userOrgs.csv.clearWarning.keeps', {
          kept: formatNumber(preview.currentlyAssignedCount - preview.wouldClearCount),
          assigned: formatNumber(preview.currentlyAssignedCount),
        })}
        {preview.unknownUpnCount > 0 && (
          <>
            {' '}
            {t(plural(preview.unknownUpnCount, 'userOrgs.csv.clearWarning.unknown.one', 'userOrgs.csv.clearWarning.unknown.other'), {
              count: formatNumber(preview.unknownUpnCount),
            })}
          </>
        )}
        <Checkbox
          checked={confirmed}
          onChange={(_e, d) => onConfirmed(d.checked === true)}
          label={t('userOrgs.csv.confirmClear')}
        />
      </MessageBarBody>
    </MessageBar>
  );
}

function ClearWarning({
  clearCount,
  name,
  checkboxLabel,
  confirmed,
  onConfirmed,
  t,
}: {
  clearCount: number;
  name: string;
  checkboxLabel: string;
  confirmed: boolean;
  onConfirmed: (confirmed: boolean) => void;
  t: TFunction;
}) {
  return (
    <MessageBar intent="warning">
      <MessageBarBody>
        <strong>
          {t(plural(clearCount, 'userOrgs.csv.mergeClearWarning.one', 'userOrgs.csv.mergeClearWarning.other'), {
            count: formatNumber(clearCount),
            name,
          })}
        </strong>
        <Checkbox
          checked={confirmed}
          onChange={(_e, d) => onConfirmed(d.checked === true)}
          label={checkboxLabel}
        />
      </MessageBarBody>
    </MessageBar>
  );
}

function JobProgress({
  job,
  styles,
  t,
  onShowChanges,
}: {
  job: UserOrgImportJob;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
  onShowChanges: (job: UserOrgImportJob) => void;
}) {
  if (job.status === 'pending' || job.status === 'running') {
    return (
      <div>
        <div className={styles.row}>
          <Spinner size="tiny" />
          <Text>
            {job.status === 'pending' || !job.startedUtc
              ? t('userOrgs.job.waiting')
              : t('userOrgs.job.importing', {
                  count: formatNumber(job.rowsTotal),
                  time: formatJobDate(job.startedUtc),
                })}
          </Text>
        </div>
        {job.attempts > 1 && (
          <Text size={200} className={styles.muted} block>
            {t('userOrgs.job.resumed')}
          </Text>
        )}
      </div>
    );
  }

  if (job.status === 'interrupted') {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>{t('userOrgs.job.interrupted')}</MessageBarBody>
      </MessageBar>
    );
  }

  if (job.status !== 'succeeded') {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{jobErrorMessage(job, t)}</MessageBarBody>
      </MessageBar>
    );
  }

  const nothingChanged = job.rowsApplied + job.rowsCleared === 0;

  return (
    <div>
      <MessageBar intent={nothingChanged ? 'warning' : 'success'}>
        <MessageBarBody>
          {nothingChanged
            ? t('userOrgs.job.nothingChanged.detail', {
                reason:
                  job.rowsUnknownUpn > 0
                    ? t(plural(job.rowsUnknownUpn, 'userOrgs.job.nothingChanged.unknown.one', 'userOrgs.job.nothingChanged.unknown.other'), {
                        count: formatNumber(job.rowsUnknownUpn),
                      })
                    : t('userOrgs.job.nothingChanged.sameValues'),
              })
            : t('userOrgs.job.finished')}
        </MessageBarBody>
      </MessageBar>
      <JobCounts job={job} styles={styles} t={t} />
      {job.changeLog && (
        <Button size="small" onClick={() => onShowChanges(job)}>
          {t('userOrgs.changes.openAfterImport')}
        </Button>
      )}
    </div>
  );
}

function JobCounts({ job, styles, t }: { job: UserOrgImportJob; styles: ReturnType<typeof useStyles>; t: TFunction }) {
  return (
    <div className={styles.counts}>
      <Badge appearance="tint" color="brand">
        {t('userOrgs.job.changed', { count: formatNumber(job.rowsApplied) })}
      </Badge>
      <Badge appearance="tint" color="informative">
        {t('userOrgs.job.cleared', { count: formatNumber(job.rowsCleared) })}
      </Badge>
      {job.rowsUnknownUpn > 0 && (
        <Badge appearance="tint" color="warning">
          {t('userOrgs.job.unknownUsers', { count: formatNumber(job.rowsUnknownUpn) })}
        </Badge>
      )}
      {job.rowsInvalid > 0 && (
        <Badge appearance="tint" color="danger">
          {t('userOrgs.job.unusableRows', { count: formatNumber(job.rowsInvalid) })}
        </Badge>
      )}
    </div>
  );
}

function LastImportLine({ job, t }: { job: UserOrgImportJob; t: TFunction }) {
  const outcome = lastImportOutcome(job, t);

  return (
    <Text size={200}>
      {t('userOrgs.lastImport.line', {
        date: formatJobDate(job.finishedUtc ?? job.startedUtc ?? job.queuedUtc),
        who: job.startedBy ?? t('userOrgs.source.unknownUser'),
        outcome,
        counts: importCounts(job, t),
      })}
    </Text>
  );
}

function lastImportOutcome(job: UserOrgImportJob, t: TFunction): string {
  if (job.status === 'succeeded' && job.rowsApplied + job.rowsCleared === 0) {
    return t('userOrgs.job.nothingChanged.detail', {
      reason:
        job.rowsUnknownUpn > 0
          ? t(plural(job.rowsUnknownUpn, 'userOrgs.job.nothingChanged.unknown.one', 'userOrgs.job.nothingChanged.unknown.other'), {
              count: formatNumber(job.rowsUnknownUpn),
            })
          : t('userOrgs.job.nothingChanged.sameValues'),
    });
  }
  if (job.status === 'succeeded') return t('userOrgs.history.outcome.succeeded');
  if (job.status === 'pending' || job.status === 'running') return t(STATUS_KEYS[job.status]);
  return jobErrorMessage(job, t);
}

function ImportHistory({
  open,
  loading,
  loaded,
  error,
  jobs,
  styles,
  t,
  onToggle,
  onShowChanges,
}: {
  open: boolean;
  loading: boolean;
  loaded: boolean;
  error: string | null;
  jobs: UserOrgImportJob[];
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
  onToggle: (open: boolean) => void;
  onShowChanges: (job: UserOrgImportJob) => void;
}) {
  return (
    <details className={styles.history} open={open} onToggle={(e) => onToggle(e.currentTarget.open)}>
      <summary className={styles.summary}>{t('userOrgs.history.title')}</summary>
      {loading && (
        <div className={styles.row}>
          <Spinner size="tiny" />
          <Text>{t('userOrgs.history.loading')}</Text>
        </div>
      )}
      {error && <ErrorBar>{error}</ErrorBar>}
      {loaded && jobs.length === 0 && <Text size={200}>{t('userOrgs.history.empty')}</Text>}
      {jobs.length > 0 && (
        <Table size="small" aria-label={t('userOrgs.history.title')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('userOrgs.history.column.date')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.history.column.who')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.history.column.mode')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.history.column.status')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.history.column.counts')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.history.column.reason')}</TableHeaderCell>
              <TableHeaderCell>{t('userOrgs.history.column.changes')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {jobs.map((historyJob) => (
              <TableRow key={historyJob.id}>
                <TableCell>{formatJobDate(historyJob.finishedUtc ?? historyJob.startedUtc ?? historyJob.queuedUtc)}</TableCell>
                <TableCell>{historyJob.startedBy ?? t('userOrgs.source.unknownUser')}</TableCell>
                <TableCell>{t(historyJob.mode === 'merge' ? 'userOrgs.history.mode.merge' : 'userOrgs.history.mode.replace')}</TableCell>
                <TableCell>{t(STATUS_KEYS[historyJob.status])}</TableCell>
                <TableCell>{importCounts(historyJob, t)}</TableCell>
                <TableCell>{historyJob.status === 'succeeded' ? t('userOrgs.history.reason.none') : jobErrorMessage(historyJob, t)}</TableCell>
                <TableCell>
                  {historyJob.status === 'succeeded' && historyJob.changeLog && (
                    <Button size="small" appearance="subtle" onClick={() => onShowChanges(historyJob)}>
                      {t('userOrgs.changes.open')}
                    </Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </details>
  );
}

function shouldShowColumnChooser(preview: UserOrgCsvPreview): boolean {
  // Also for a two-column file in which no row could be read: the usual cause is the columns the
  // other way round, with no header to say so.
  return (
    preview.columnCount > 2 ||
    preview.blocking?.code === 'chooseColumns' ||
    (preview.blocking?.code === 'noUsableRows' && preview.columnCount >= 2)
  );
}

function blockingMessage(preview: UserOrgCsvPreview, t: TFunction): string {
  const blocking = preview.blocking;
  if (!blocking) return '';
  const key = isBlockingCode(blocking.code) ? BLOCKING_KEYS[blocking.code] : null;
  if (!key) return t('userOrgs.csv.blocking.generic');
  return t(key, {
    line: formatNumber(blocking.line ?? 0),
    lastLine: formatNumber(blocking.lastLine ?? 0),
    max: formatNumber(blocking.max ?? 0),
  });
}

function rowProblemMessage(code: string, fallback: string, t: TFunction): string {
  return isRowProblemCode(code) ? t(ROW_PROBLEM_KEYS[code]) : fallback;
}

function apiErrorMessage(error: unknown, t: TFunction): string {
  if (error instanceof UserOrgApiError && error.code && Object.prototype.hasOwnProperty.call(API_ERROR_KEYS, error.code)) {
    return t(API_ERROR_KEYS[error.code], {
      name: String(error.values.name ?? ''),
      maxMb: formatNumber(Number(error.values.maxMb ?? 0)),
      count: formatNumber(Number(error.values.count ?? 0)),
      confirmed: formatNumber(Number(error.values.confirmed ?? 0)),
    });
  }
  // The API's general codes - a type deleted elsewhere, a fault - are worded like everywhere else.
  return userOrgErrorMessage(error, t, 'errors.userOrgs.importNotStarted');
}

function jobErrorMessage(job: UserOrgImportJob, t: TFunction): string {
  if (job.errorCode && isJobErrorCode(job.errorCode)) {
    return t(JOB_ERROR_KEYS[job.errorCode]);
  }
  return job.errorMessage || t('userOrgs.job.error.failed');
}

function importCounts(job: UserOrgImportJob, t: TFunction): string {
  return t('userOrgs.history.counts', {
    changed: formatNumber(job.rowsApplied),
    cleared: formatNumber(job.rowsCleared),
    unknown: formatNumber(job.rowsUnknownUpn),
    unusable: formatNumber(job.rowsInvalid),
  });
}

function formatJobDate(iso: string): string {
  return formatDateParts(new Date(iso), { dateStyle: 'short', timeStyle: 'short' });
}

function isRowProblemCode(code: string): code is UserOrgCsvRowProblemCode {
  return Object.prototype.hasOwnProperty.call(ROW_PROBLEM_KEYS, code);
}

function isBlockingCode(code: string): code is UserOrgCsvBlockingCode {
  return Object.prototype.hasOwnProperty.call(BLOCKING_KEYS, code);
}

function isJobErrorCode(code: string): code is UserOrgImportErrorCode {
  return Object.prototype.hasOwnProperty.call(JOB_ERROR_KEYS, code);
}
