import { useEffect, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Field,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
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
import { fetchImportJob, importCsv, previewCsv } from '../../api/userOrgsApi';
import { formatNumber, plural, useT, type TFunction } from '../../i18n';
import { STATUS_KEYS } from './userOrgShared';
import type {  UserOrgCsvPreview,
  UserOrgImportJob,
  UserOrgImportMode,
  UserOrgType,
} from '../../types/userOrgs';

const useStyles = makeStyles({
  panel: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
  counts: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '4px' },
});

/** How often import progress is polled. Frequent enough to feel live, not so frequent it is noisy. */
const POLL_INTERVAL_MS = 2000;

export interface CsvImportPanelProps {
  orgType: UserOrgType;
  /** Called when an import finishes, so the page can refresh its counts. */
  onImportFinished: () => void;
}

/**
 * Upload, preview and import a CSV for one org type.
 *
 * The preview is deliberately between choosing the file and importing it. A Replace against a
 * mis-exported file would clear every user's value for this dimension, and the ten parsed rows -
 * with whether each UPN actually matches a user - are what let an admin notice a wrong column or a
 * stale UPN format before that happens.
 */
export default function CsvImportPanel({ orgType, onImportFinished }: CsvImportPanelProps) {
  const styles = useStyles();
  const t = useT();
  const fileInput = useRef<HTMLInputElement>(null);

  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<UserOrgCsvPreview | null>(null);
  const [mode, setMode] = useState<UserOrgImportMode>('merge');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [job, setJob] = useState<UserOrgImportJob | null>(null);

  // Seeded from the type's last import so a refresh, or coming back to the page later, still shows an
  // import that is in flight or was interrupted. Without this the panel looks idle, the good "upload
  // it again" copy is only ever visible to the session that started the import, and a second upload
  // is rejected with "already in progress" next to a panel showing nothing happening.
  const [seeded, setSeeded] = useState(false);
  useEffect(() => {
    if (seeded) return;
    setSeeded(true);

    const last = orgType.lastImport;
    if (last && (last.status === 'pending' || last.status === 'running' || last.status === 'interrupted')) {
      setJob(last);
    }
  }, [orgType.lastImport, seeded]);

  // Re-armed whenever the file or the mode changes, so a confirmation can never carry over to a
  // different file or a different blast radius.
  const [confirmedClear, setConfirmedClear] = useState(false);

  const running = job !== null && (job.status === 'pending' || job.status === 'running');

  // Poll while the import is in flight. setTimeout-after-settle rather than setInterval: an interval
  // fires another request every two seconds whether or not the previous one has come back, so under
  // SQL latency or an outage the requests pile up on a page that is already struggling. The effect
  // owns the timer and an AbortController, so a component unmounted mid-import (the admin navigating
  // away) stops polling and cancels the request in flight instead of leaking both and setting state
  // on a dead component - the import itself carries on server-side regardless.
  useEffect(() => {
    if (!running || job === null) return;

    let cancelled = false;
    let timer = 0;
    const controller = new AbortController();

    const poll = async () => {
      try {
        const latest = await fetchImportJob(job.id, controller.signal);
        if (cancelled) return;
        setJob(latest);
        if (latest.status !== 'pending' && latest.status !== 'running') {
          onImportFinished();
          return;
        }
      } catch {
        // A transient poll failure is not worth surfacing; the next tick will retry.
        if (cancelled) return;
      }
      timer = window.setTimeout(poll, POLL_INTERVAL_MS);
    };

    timer = window.setTimeout(poll, POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [running, job, onImportFinished]);

  const chooseFile = async (chosen: File | null) => {
    setFile(chosen);
    setPreview(null);
    setError(null);
    setJob(null);
    setConfirmedClear(false);
    if (!chosen) return;

    setBusy(true);
    try {
      setPreview(await previewCsv(orgType.id, chosen));
    } catch (e) {
      setError(e instanceof Error ? e.message : t('errors.userOrgs.fileUnreadable'));
    } finally {
      setBusy(false);
    }
  };

  const startImport = async () => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const queued = await importCsv(orgType.id, mode, file, confirmedClear);
      setJob({
        id: queued.jobId,
        orgTypeId: orgType.id,
        mode,
        status: 'pending',
        fileName: file.name,
        startedBy: null,
        queuedUtc: new Date().toISOString(),
        finishedUtc: null,
        rowsTotal: queued.rowsQueued,
        rowsApplied: 0,
        rowsCleared: 0,
        rowsUnknownUpn: 0,
        rowsInvalid: queued.rowsInvalid,
        errorMessage: null,
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : t('errors.userOrgs.importNotStarted'));
    } finally {
      setBusy(false);
    }
  };

  const reset = () => {
    setFile(null);
    setPreview(null);
    setJob(null);
    setError(null);
    if (fileInput.current) fileInput.current.value = '';
  };

  return (
    <div className={styles.panel}>
      <div className={styles.row}>
        <input
          ref={fileInput}
          type="file"
          accept=".csv,.txt,text/csv,text/plain"
          onChange={(e) => chooseFile(e.target.files?.[0] ?? null)}
          disabled={busy || running}
        />
        {busy && <Spinner size="tiny" />}
        {(file || job) && (
          <Button appearance="subtle" size="small" onClick={reset} disabled={running}>
            {t('userOrgs.csv.clear')}
          </Button>
        )}
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {preview && !job && <PreviewTable preview={preview} styles={styles} t={t} />}

      {preview && !job && (
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
              <Radio
                value="replace"
                label={t('userOrgs.csv.modeReplace', { name: orgType.name })}
              />
            </RadioGroup>
          </Field>

          {mode === 'replace' && preview.wouldClearCount > 0 && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <strong>
                  {t(
                    plural(
                      preview.wouldClearCount,
                      'userOrgs.csv.clearWarning.one',
                      'userOrgs.csv.clearWarning.other',
                    ),
                    { count: formatNumber(preview.wouldClearCount), name: orgType.name },
                  )}
                </strong>{' '}
                {t('userOrgs.csv.clearWarning.keeps', {
                  kept: formatNumber(preview.currentlyAssignedCount - preview.wouldClearCount),
                  assigned: formatNumber(preview.currentlyAssignedCount),
                })}
                {preview.unknownUpnCount > 0 && (
                  <>
                    {' '}
                    {t(
                      plural(
                        preview.unknownUpnCount,
                        'userOrgs.csv.clearWarning.unknown.one',
                        'userOrgs.csv.clearWarning.unknown.other',
                      ),
                      { count: formatNumber(preview.unknownUpnCount) },
                    )}
                  </>
                )}
                <Checkbox
                  checked={confirmedClear}
                  onChange={(_e, d) => setConfirmedClear(d.checked === true)}
                  label={t('userOrgs.csv.confirmClear')}
                />
              </MessageBarBody>
            </MessageBar>
          )}

          <div>
            <Button
              appearance="primary"
              onClick={startImport}
              disabled={
                busy ||
                preview.rows.length === 0 ||
                (mode === 'replace' && preview.wouldClearCount > 0 && !confirmedClear)
              }
            >
              {t(
                plural(preview.totalRows, 'userOrgs.csv.import.one', 'userOrgs.csv.import.other'),
                { count: formatNumber(preview.totalRows) },
              )}
            </Button>
          </div>
        </>
      )}

      {job && <JobProgress job={job} styles={styles} t={t} />}
    </div>
  );
}

function PreviewTable({
  preview,
  styles,
  t,
}: {
  preview: UserOrgCsvPreview;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
}) {
  // Deliberately the whole-file counts, not the ten rows on screen. A truncated export whose first
  // ten user principal names happen to exist looks perfectly clean in the table, and a Merge of it
  // then quietly applies a handful of rows - the counts are the only thing that reveals it.
  const matchedRows = preview.totalRows - preview.unknownUpnCount;

  return (
    <div>
      <Text size={200} className={styles.muted} block>
        {preview.headerDetected
          ? t('userOrgs.csv.headerFound', {
              upnColumn: preview.upnColumnName ?? '',
              orgColumn: preview.orgColumnName ?? '',
              delimiter: preview.delimiter,
            })
          : t('userOrgs.csv.headerMissing', { delimiter: preview.delimiter })}
      </Text>

      <Text size={200} className={styles.muted} block>
        {t('userOrgs.csv.matchSummary', {
          matched: formatNumber(matchedRows),
          total: formatNumber(preview.totalRows),
        })}
      </Text>

      {preview.unknownUpnCount > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t(
              plural(
                preview.unknownUpnCount,
                'userOrgs.csv.unknownRows.one',
                'userOrgs.csv.unknownRows.other',
              ),
              { count: formatNumber(preview.unknownUpnCount) },
            )}
          </MessageBarBody>
        </MessageBar>
      )}

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

      {preview.moreRowsExist && (
        <Text size={200} className={styles.muted} block>
          {t('userOrgs.csv.showingFirst', { count: formatNumber(preview.rows.length) })}
        </Text>
      )}

      {preview.truncatedValueCount > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t(
              plural(
                preview.truncatedValueCount,
                'userOrgs.csv.truncated.one',
                'userOrgs.csv.truncated.other',
              ),
              { count: formatNumber(preview.truncatedValueCount), max: formatNumber(preview.maxValueLength) },
            )}
          </MessageBarBody>
        </MessageBar>
      )}

      {preview.problems.length > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t('userOrgs.csv.problems', {
              problems: preview.problems
                .map((p) =>
                  t('userOrgs.csv.problemLine', { line: p.lineNumber, reason: p.reason }),
                )
                .join('; '),
            })}
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

function JobProgress({
  job,
  styles,
  t,
}: {
  job: UserOrgImportJob;
  styles: ReturnType<typeof useStyles>;
  t: TFunction;
}) {
  if (job.status === 'pending' || job.status === 'running') {
    return (
      <div className={styles.row}>
        <Spinner size="tiny" />
        <Text>{t('userOrgs.job.importing', { count: formatNumber(job.rowsTotal) })}</Text>
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
        <MessageBarBody>
          {t('userOrgs.job.failed', {
            status: t(STATUS_KEYS[job.status]),
            message: job.errorMessage ?? '',
          })}
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <MessageBar intent="success">
        <MessageBarBody>{t('userOrgs.job.finished')}</MessageBarBody>
      </MessageBar>
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
    </div>
  );
}
