import { useEffect, useRef, useState } from 'react';
import {
  Badge,
  Button,
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
import type {
  UserOrgCsvPreview,
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
  const fileInput = useRef<HTMLInputElement>(null);

  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<UserOrgCsvPreview | null>(null);
  const [mode, setMode] = useState<UserOrgImportMode>('merge');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [job, setJob] = useState<UserOrgImportJob | null>(null);

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
    if (!chosen) return;

    setBusy(true);
    try {
      setPreview(await previewCsv(chosen));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The file could not be read.');
    } finally {
      setBusy(false);
    }
  };

  const startImport = async () => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const queued = await importCsv(orgType.id, mode, file);
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
      setError(e instanceof Error ? e.message : 'The import could not be started.');
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
            Clear
          </Button>
        )}
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {preview && !job && <PreviewTable preview={preview} styles={styles} />}

      {preview && !job && (
        <>
          <Field label="What should happen to users who are not in the file?">
            <RadioGroup value={mode} onChange={(_e, d) => setMode(d.value as UserOrgImportMode)}>
              <Radio value="merge" label="Merge - leave them exactly as they are" />
              <Radio
                value="replace"
                label={`Replace - clear their ${orgType.name} value (the file is the complete list)`}
              />
            </RadioGroup>
          </Field>

          {mode === 'replace' && orgType.assignedUserCount > 0 && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <strong>This will clear values.</strong> {orgType.assignedUserCount.toLocaleString()} user
                {orgType.assignedUserCount === 1 ? ' has' : 's have'} a {orgType.name} value today. Anyone
                not given a value by this file will lose theirs.
              </MessageBarBody>
            </MessageBar>
          )}

          <div>
            <Button appearance="primary" onClick={startImport} disabled={busy || preview.rows.length === 0}>
              Import {preview.moreRowsExist ? 'the whole file' : `${preview.rows.length} row(s)`}
            </Button>
          </div>
        </>
      )}

      {job && <JobProgress job={job} styles={styles} />}
    </div>
  );
}

function PreviewTable({
  preview,
  styles,
}: {
  preview: UserOrgCsvPreview;
  styles: ReturnType<typeof useStyles>;
}) {
  const unmatched = preview.rows.filter((r) => !r.userExists).length;

  return (
    <div>
      <Text size={200} className={styles.muted} block>
        {preview.headerDetected
          ? `Header row found: "${preview.upnColumnName}" and "${preview.orgColumnName}", ${preview.delimiter}-separated.`
          : `No header row recognised, so the first column is treated as the user and the second as the organisation. ${preview.delimiter}-separated.`}
      </Text>

      {unmatched > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {unmatched} of the {preview.rows.length} rows shown below do not match a user in this database.
            Check the file uses the same user principal names the product imports.
          </MessageBarBody>
        </MessageBar>
      )}

      <Table size="small" aria-label="File preview">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Line</TableHeaderCell>
            <TableHeaderCell>User</TableHeaderCell>
            <TableHeaderCell>Organisation</TableHeaderCell>
            <TableHeaderCell>Matches a user</TableHeaderCell>
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
                    clears the value
                  </Badge>
                ) : (
                  row.orgValue
                )}
              </TableCell>
              <TableCell>
                {row.userExists ? (
                  <Badge appearance="tint" color="success">
                    yes
                  </Badge>
                ) : (
                  <Badge appearance="tint" color="danger">
                    no
                  </Badge>
                )}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      {preview.moreRowsExist && (
        <Text size={200} className={styles.muted} block>
          Showing the first {preview.rows.length} rows. The whole file is imported.
        </Text>
      )}

      {preview.problems.length > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Some rows cannot be used:{' '}
            {preview.problems.map((p) => `line ${p.lineNumber} (${p.reason})`).join('; ')}. They are
            skipped and counted; the rest of the file still imports.
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

function JobProgress({
  job,
  styles,
}: {
  job: UserOrgImportJob;
  styles: ReturnType<typeof useStyles>;
}) {
  if (job.status === 'pending' || job.status === 'running') {
    return (
      <div className={styles.row}>
        <Spinner size="tiny" />
        <Text>
          Importing {job.rowsTotal.toLocaleString()} row(s)... this page will update when it finishes.
        </Text>
      </div>
    );
  }

  if (job.status === 'interrupted') {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          This import stopped reporting progress, which usually means the web app restarted while it was
          running. Some rows may have been applied. Upload the file again to be sure.
        </MessageBarBody>
      </MessageBar>
    );
  }

  if (job.status !== 'succeeded') {
    return (
      <MessageBar intent="error">
        <MessageBarBody>
          The import {job.status}. {job.errorMessage ?? ''}
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <MessageBar intent="success">
        <MessageBarBody>Import finished.</MessageBarBody>
      </MessageBar>
      <div className={styles.counts}>
        <Badge appearance="tint" color="brand">
          {job.rowsApplied.toLocaleString()} set
        </Badge>
        <Badge appearance="tint" color="informative">
          {job.rowsCleared.toLocaleString()} cleared
        </Badge>
        {job.rowsUnknownUpn > 0 && (
          <Badge appearance="tint" color="warning">
            {job.rowsUnknownUpn.toLocaleString()} unknown user(s)
          </Badge>
        )}
        {job.rowsInvalid > 0 && (
          <Badge appearance="tint" color="danger">
            {job.rowsInvalid.toLocaleString()} unusable row(s)
          </Badge>
        )}
      </div>
    </div>
  );
}
