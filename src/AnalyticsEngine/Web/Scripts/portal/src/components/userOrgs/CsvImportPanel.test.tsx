import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import CsvImportPanel from './CsvImportPanel';
import { buildUnusableRowsCsv, csvDocument, csvRows } from './csvUnusableRows';
import type { UserOrgCsvPreview, UserOrgImportJob, UserOrgType } from '../../types/userOrgs';

const mocks = vi.hoisted(() => ({
  previewCsv: vi.fn(),
  importCsv: vi.fn(),
  fetchImportJob: vi.fn(),
  fetchImportHistory: vi.fn(),
  fetchImportChanges: vi.fn(),
  UserOrgApiErrorCtor: undefined as unknown as typeof import('../../api/userOrgsApi').UserOrgApiError,
  SessionExpiredErrorCtor: undefined as unknown as typeof import('../../api/http').SessionExpiredError,
}));

const { previewCsv, importCsv, fetchImportJob, fetchImportHistory, fetchImportChanges } = mocks;

vi.mock('../../api/userOrgsApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/userOrgsApi')>();
  mocks.UserOrgApiErrorCtor = actual.UserOrgApiError;
  return {
    ...actual,
    previewCsv: (...args: unknown[]) => mocks.previewCsv(...args),
    importCsv: (...args: unknown[]) => mocks.importCsv(...args),
    fetchImportJob: (...args: unknown[]) => mocks.fetchImportJob(...args),
    fetchImportHistory: (...args: unknown[]) => mocks.fetchImportHistory(...args),
    fetchImportChanges: (...args: unknown[]) => mocks.fetchImportChanges(...args),
  };
});

vi.mock('../../api/http', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/http')>();
  mocks.SessionExpiredErrorCtor = actual.SessionExpiredError;
  return actual;
});

function orgType(over: Partial<UserOrgType> = {}): UserOrgType {
  return {
    id: 1,
    name: 'Cost Centre',
    source: 'csv',
    entraAttributeName: null,
    isEnabled: true,
    revision: 1,
    assignedUserCount: 1200,
    distinctValueCount: 40,
    createdUtc: '2026-01-01T00:00:00.000Z',
    modifiedUtc: null,
    lastRefreshedUtc: null,
    lastImport: null,
    ...over,
  };
}

function preview(over: Partial<UserOrgCsvPreview> = {}): UserOrgCsvPreview {
  return {
    fileName: 'orgs.csv',
    draftId: 42,
    blocking: null,
    delimiter: 'comma',
    headerDetected: true,
    columns: ['UPN', 'OrgName'],
    columnCount: 2,
    userColumnIndex: 0,
    valueColumnIndex: 1,
    upnColumnName: 'UPN',
    orgColumnName: 'OrgName',
    rows: [
      { lineNumber: 2, upn: 'alex.wilber@contoso.com', orgValue: 'Retail', userExists: true, clearsValue: false },
      { lineNumber: 3, upn: 'ghost@contoso.com', orgValue: 'Ops', userExists: false, clearsValue: false },
      { lineNumber: 4, upn: 'adele.vance@contoso.com', orgValue: null, userExists: true, clearsValue: true },
    ],
    problems: [],
    moreRowsExist: false,
    totalRows: 3,
    unknownUpnCount: 1,
    wouldClearCount: 0,
    mergeWouldClearCount: 0,
    currentlyAssignedCount: 1200,
    matchedUserCount: 2,
    truncatedValueCount: 0,
    maxValueLength: 848,
    unusableRows: [],
    unusableRowCount: 0,
    ...over,
  };
}

function job(over: Partial<UserOrgImportJob> = {}): UserOrgImportJob {
  return {
    id: 7,
    orgTypeId: 1,
    mode: 'merge',
    status: 'succeeded',
    fileName: 'orgs.csv',
    startedBy: 'admin@contoso.com',
    queuedUtc: '2026-01-01T00:00:00.000Z',
    startedUtc: '2026-01-01T00:00:01.000Z',
    finishedUtc: '2026-01-01T00:00:05.000Z',
    attempts: 1,
    rowsTotal: 3,
    rowsApplied: 2,
    rowsCleared: 1,
    rowsUnknownUpn: 1,
    rowsInvalid: 0,
    errorCode: null,
    errorMessage: null,
    changeLog: null,
    ...over,
  };
}

function csvFile(name = 'orgs.csv'): File {
  return new File(['UPN,OrgName\r\nalex.wilber@contoso.com,Retail\r\n'], name, { type: 'text/csv' });
}

async function chooseFile(file = csvFile()) {
  await userEvent.upload(screen.getByLabelText('Choose a CSV file'), file);
}

describe('CsvImportPanel', () => {
  beforeEach(() => {
    previewCsv.mockReset();
    importCsv.mockReset();
    fetchImportJob.mockReset();
    fetchImportHistory.mockReset();
    fetchImportChanges.mockReset();
    fetchImportHistory.mockResolvedValue([]);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('previews the chosen file and shows text while every user is checked', async () => {
    let resolvePreview!: (value: UserOrgCsvPreview) => void;
    previewCsv.mockReturnValue(new Promise<UserOrgCsvPreview>((resolve) => { resolvePreview = resolve; }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    expect(screen.getByText('Reading the file and checking every user...')).toBeInTheDocument();
    resolvePreview(preview());

    await waitFor(() => expect(screen.getByText('alex.wilber@contoso.com')).toBeInTheDocument());
    expect(screen.getByText(/1 row in the file matches no user/i)).toBeInTheDocument();
  });

  it('forgets a preview that was still being read when the file was cleared', async () => {
    // A slow preview landing after Clear used to bring the cleared file back, draft and all, ready to
    // import.
    let resolvePreview!: (value: UserOrgCsvPreview) => void;
    previewCsv.mockReturnValue(new Promise<UserOrgCsvPreview>((resolve) => { resolvePreview = resolve; }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    expect(screen.getByText('Reading the file and checking every user...')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(screen.queryByText('Reading the file and checking every user...')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Choose a CSV file')).toBeEnabled();

    await act(async () => { resolvePreview(preview()); });

    expect(screen.queryByText('alex.wilber@contoso.com')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Import/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clear' })).not.toBeInTheDocument();
  });

  it('shows only the latest file when an earlier preview finishes last', async () => {
    let resolveFirst!: (value: UserOrgCsvPreview) => void;
    previewCsv
      .mockReturnValueOnce(new Promise<UserOrgCsvPreview>((resolve) => { resolveFirst = resolve; }))
      .mockResolvedValueOnce(
        preview({
          fileName: 'second.csv',
          rows: [{ lineNumber: 2, upn: 'megan.bowen@contoso.com', orgValue: 'Finance', userExists: true, clearsValue: false }],
        }),
      );
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile(csvFile('first.csv'));
    await userEvent.click(screen.getByRole('button', { name: 'Clear' }));
    await chooseFile(csvFile('second.csv'));
    expect(await screen.findByText('megan.bowen@contoso.com')).toBeInTheDocument();

    await act(async () => { resolveFirst(preview()); });

    expect(screen.getByText('megan.bowen@contoso.com')).toBeInTheDocument();
    expect(screen.queryByText('alex.wilber@contoso.com')).not.toBeInTheDocument();
  });

  it.each([
    ['notUtf8', /isn't saved as UTF-8/i],
    ['excelWorkbook', /Excel workbook, not a CSV/i],
    ['notText', /doesn't look like a text CSV/i],
    ['unterminatedQuote', /line 7 is never closed/i],
    ['rowSpansLines', /Lines 7-9 were read as one row/i],
    ['oneColumn', /only one column/i],
    ['tooManyRows', /more than 10,000 rows/i],
    ['tooManyColumns', /Line 7 has more than 10,000 columns/i],
    ['noRows', /no rows to import/i],
    ['noUsableRows', /None of the rows in this file can be used/i],
  ])('shows translated blocking text for %s and no import controls', async (code, message) => {
    previewCsv.mockResolvedValue(
      preview({
        draftId: null,
        blocking: { code, line: 7, lastLine: 9, max: 10000 },
        totalRows: 0,
        rows: [],
      }),
    );
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.queryByText('What should happen to users who are not in the file?')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Import/ })).not.toBeInTheDocument();
  });

  it('shows a generic translated message for an unknown blocking code', async () => {
    previewCsv.mockResolvedValue(
      preview({
        draftId: null,
        blocking: { code: 'futureBlockingCode', line: null, lastLine: null, max: null },
        rows: [],
      }),
    );
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    expect(await screen.findByText("This file can't be imported.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Import/ })).not.toBeInTheDocument();
  });

  it('shows the column chooser and re-previews with both selected columns', async () => {
    previewCsv
      .mockResolvedValueOnce(
        preview({
          draftId: null,
          blocking: { code: 'chooseColumns', line: null, lastLine: null, max: null },
          columns: ['Display name', 'Department', 'UserPrincipalName'],
          columnCount: 3,
          userColumnIndex: 2,
          valueColumnIndex: 1,
        }),
      )
      .mockResolvedValueOnce(preview({ columns: ['Display name', 'Department', 'UserPrincipalName'], columnCount: 3, userColumnIndex: 2, valueColumnIndex: 0 }));

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();

    expect(await screen.findByText(/This file has several columns/i)).toBeInTheDocument();
    expect(screen.getByText(/This file has 3 columns/i)).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText('Value column'), '0');

    await waitFor(() => expect(previewCsv).toHaveBeenCalledTimes(2));
    expect(previewCsv).toHaveBeenLastCalledWith(1, expect.any(File), { userColumn: 2, valueColumn: 0 });
  });

  it('waits for both columns when the server settled only one, then sends both', async () => {
    // A chooseColumns preview settles only the user column. Guessing the value column is exactly
    // what the chooser exists to avoid, and the server refuses half a choice.
    previewCsv
      .mockResolvedValueOnce(
        preview({
          draftId: null,
          blocking: { code: 'chooseColumns', line: null, lastLine: null, max: null },
          columns: ['EmployeeId', 'Display name', 'UserPrincipalName', 'Centre'],
          columnCount: 4,
          userColumnIndex: 2,
          valueColumnIndex: null,
        }),
      )
      .mockResolvedValueOnce(preview({ columnCount: 4, userColumnIndex: 0, valueColumnIndex: 3 }));

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await screen.findByText(/This file has several columns/i);
    expect((screen.getByLabelText('Value column') as HTMLSelectElement).value).toBe('');
    expect(screen.getAllByRole('option', { name: 'Choose a column' }).length).toBe(1);

    await userEvent.selectOptions(screen.getByLabelText('User principal name column'), '0');
    expect(previewCsv).toHaveBeenCalledTimes(1);

    await userEvent.selectOptions(screen.getByLabelText('Value column'), '3');

    await waitFor(() => expect(previewCsv).toHaveBeenCalledTimes(2));
    expect(previewCsv).toHaveBeenLastCalledWith(1, expect.any(File), { userColumn: 0, valueColumn: 3 });
  });

  it('sends both columns when only one is changed and the other was already read', async () => {
    previewCsv
      .mockResolvedValueOnce(
        preview({ columns: ['UserPrincipalName', 'Centre', 'Region'], columnCount: 3, userColumnIndex: 0, valueColumnIndex: 1 }),
      )
      .mockResolvedValueOnce(preview({ columnCount: 3, userColumnIndex: 0, valueColumnIndex: 2 }));

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await screen.findByText('alex.wilber@contoso.com');

    await userEvent.selectOptions(screen.getByLabelText('Value column'), '2');

    await waitFor(() => expect(previewCsv).toHaveBeenCalledTimes(2));
    expect(previewCsv).toHaveBeenLastCalledWith(1, expect.any(File), { userColumn: 0, valueColumn: 2 });
  });

  it('asks for two different columns without re-reading the file', async () => {
    previewCsv.mockResolvedValueOnce(
      preview({
        draftId: null,
        blocking: { code: 'chooseColumns', line: null, lastLine: null, max: null },
        columns: ['EmployeeId', 'Display name', 'UserPrincipalName'],
        columnCount: 3,
        userColumnIndex: 2,
        valueColumnIndex: null,
      }),
    );

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await screen.findByText(/This file has several columns/i);

    await userEvent.selectOptions(screen.getByLabelText('Value column'), '2');

    expect(await screen.findByText(/Choose two different columns/i)).toBeInTheDocument();
    expect(previewCsv).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText('Value column')).toBeInTheDocument();
  });

  it('keeps the preview, and the columns it used, when the other columns are refused before anything is staged', async () => {
    previewCsv
      .mockResolvedValueOnce(
        preview({ columns: ['UserPrincipalName', 'Centre', 'Region'], columnCount: 3, userColumnIndex: 0, valueColumnIndex: 1 }),
      )
      .mockRejectedValueOnce(new mocks.UserOrgApiErrorCtor('server fallback', 400, 'typeDisabled', { name: 'Cost Centre' }));

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await screen.findByText('alex.wilber@contoso.com');

    await userEvent.selectOptions(screen.getByLabelText('Value column'), '2');

    expect(await screen.findByText('Cost Centre is disabled. Enable it before importing a file.')).toBeInTheDocument();
    expect(screen.getByText('alex.wilber@contoso.com')).toBeInTheDocument();
    expect((screen.getByLabelText('Value column') as HTMLSelectElement).value).toBe('1');
    expect(screen.getByRole('button', { name: /^Import/ })).toBeInTheDocument();
  });

  it.each([
    ['a network failure', () => new Error('Failed to reach the server.'), 'Failed to reach the server.'],
    [
      'the new draft vanishing after it was staged',
      () => new mocks.UserOrgApiErrorCtor('server fallback', 400, 'draftNotFound', null),
      'This preview has expired or was already imported. Choose the file again.',
    ],
  ])('drops the preview when re-reading fails with %s, which may have replaced its draft', async (_case, failure, message) => {
    // The server replaces this admin's earlier draft as soon as a newer preview is staged, so a preview
    // put back after a lost reply held a draft that no longer existed - and its Import button failed.
    previewCsv
      .mockResolvedValueOnce(
        preview({ columns: ['UserPrincipalName', 'Centre', 'Region'], columnCount: 3, userColumnIndex: 0, valueColumnIndex: 1 }),
      )
      .mockRejectedValueOnce(failure());

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await screen.findByText('alex.wilber@contoso.com');

    await userEvent.selectOptions(screen.getByLabelText('Value column'), '2');

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.queryByText('alex.wilber@contoso.com')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Import/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clear' })).toBeInTheDocument();
  });

  it('offers the column chooser for a two-column file in which no row could be read', async () => {
    // Usually the columns the other way round, with no header to say so.
    previewCsv.mockResolvedValueOnce(
      preview({
        draftId: null,
        blocking: { code: 'noUsableRows', line: null, lastLine: null, max: null },
        headerDetected: false,
        columns: null,
        columnCount: 2,
        rows: [],
        totalRows: 0,
      }),
    );

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();

    expect(await screen.findByLabelText('User principal name column')).toBeInTheDocument();
    expect(screen.getAllByRole('option', { name: 'Column 2' }).length).toBeGreaterThan(0);
  });

  it('translates row problem reasons and downloads unusable rows as UTF-8 CSV with formula neutralisation', async () => {
    let capturedBlob: Blob | undefined;
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
    });
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      capturedBlob = blob as Blob;
      return 'blob:csv';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    previewCsv.mockResolvedValue(
      preview({
        problems: [{ lineNumber: 9, code: 'notAValidUpn', reason: 'server fallback' }],
        unusableRowCount: 2,
        unusableRows: [
          { lineNumber: 9, upn: '=cmd|calc@contoso.com', orgValue: 'Ops, West', code: 'notAValidUpn' },
          { lineNumber: 10, upn: 'missing@contoso.com', orgValue: null, code: 'unknownUser' },
        ],
      }),
    );
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    expect(await screen.findByText(/not a valid user principal name/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Download the 2 rows/i }));

    expect(capturedBlob).toBeDefined();
    const csv = await capturedBlob!.text();
    expect(csv).toContain('Line,User,Cost Centre,Reason');
    expect(csv).toContain('\'=cmd|calc@contoso.com,"Ops, West",the user value is not a valid user principal name');
    expect(csv).toContain('missing@contoso.com,,the user principal name does not match a user in this database');
  });

  it('builds RFC 4180 unusable-row CSV in a pure helper', () => {
    const csv = buildUnusableRowsCsv(
      [{ lineNumber: 2, upn: '+person@contoso.com', orgValue: 'A "quoted"\r\nvalue', code: 'unknownUser' }],
      { lineHeader: 'Line', userHeader: 'User', valueHeader: 'Value', reasonHeader: 'Reason' },
      () => 'Unknown user',
    );

    expect(csv).toBe('\uFEFFLine,User,Value,Reason\r\n2,\'+person@contoso.com,"A ""quoted""\r\nvalue",Unknown user\r\n');
  });

  it('builds the same document a page at a time as all at once', () => {
    // How a long change list is downloaded without holding every row and the whole document at once.
    const header = ['Line', 'User'];
    const rows = [
      ['2', '=cmd@contoso.com'],
      ['3', 'A "quoted"\r\nvalue'],
      ['4', 'Καλημέρα κόσμε'],
    ];

    expect(csvDocument([header]) + csvRows(rows.slice(0, 2)) + csvRows(rows.slice(2)) + csvRows([])).toBe(
      csvDocument([header, ...rows]),
    );
  });

  it('disables import when no rows match a user', async () => {
    previewCsv.mockResolvedValue(preview({ totalRows: 2, unknownUpnCount: 2, matchedUserCount: 0 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    expect(await screen.findByText(/None of the rows match a user/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Import/ })).toBeDisabled();
  });

  it('gates a merge that clears existing values and passes the confirmed clear count', async () => {
    previewCsv.mockResolvedValue(preview({ mergeWouldClearCount: 3 }));
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    expect(await screen.findByText(/file lists them with an empty value/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Import/ })).toBeDisabled();
    await userEvent.click(screen.getByLabelText(/clear the values the file leaves empty/i));
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    expect(importCsv).toHaveBeenCalledWith(1, 42, 'merge', 3);
  });

  it('passes the confirmed replace clear count to the draft import', async () => {
    previewCsv.mockResolvedValue(preview({ wouldClearCount: 1199 }));
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    await userEvent.click(await screen.findByLabelText(/Replace/));
    await userEvent.click(screen.getByLabelText(/I understand/));
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    expect(importCsv).toHaveBeenCalledWith(1, 42, 'replace', 1199);
  });

  it('words API errors from stable codes with server-message fallback for unknown codes', async () => {
    previewCsv.mockRejectedValueOnce(new mocks.UserOrgApiErrorCtor('server fallback', 413, 'uploadTooLarge', { maxMb: 32 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    expect(await screen.findByText(/larger than the 32 MB upload limit/i)).toBeInTheDocument();

    previewCsv.mockRejectedValueOnce(new mocks.UserOrgApiErrorCtor('server fallback', 400, 'futureCode', null));
    await chooseFile(csvFile('second.csv'));
    expect(await screen.findByText('server fallback')).toBeInTheDocument();
  });

  it('shows pending and running progress with resume text', async () => {
    renderWithProvider(
      <CsvImportPanel
        orgType={orgType({ lastImport: job({ status: 'running', attempts: 2, rowsTotal: 200, startedUtc: '2026-01-01T10:00:00.000Z', finishedUtc: null }) })}
        onImportFinished={vi.fn()}
      />,
    );

    expect(await screen.findByText(/Importing 200 rows/i)).toBeInTheDocument();
    expect(screen.getByText('Resumed after the web app restarted.')).toBeInTheDocument();
  });

  it('backs off polling, warns after three failures, then resets after success', async () => {
    vi.useFakeTimers();
    fetchImportJob
      .mockRejectedValueOnce(new Error('network'))
      .mockRejectedValueOnce(new Error('network'))
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValueOnce(job());
    const onImportFinished = vi.fn();
    renderWithProvider(
      <CsvImportPanel orgType={orgType({ lastImport: job({ status: 'pending', startedUtc: null, finishedUtc: null }) })} onImportFinished={onImportFinished} />,
    );
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Waiting to start...')).toBeInTheDocument();

    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    await act(async () => { await vi.advanceTimersByTimeAsync(4000); });
    await act(async () => { await vi.advanceTimersByTimeAsync(8000); });

    expect(screen.getByText(/Can't reach the server/i)).toBeInTheDocument();

    await act(async () => { await vi.advanceTimersByTimeAsync(16000); });

    expect(onImportFinished).toHaveBeenCalled();
    expect(screen.queryByText(/Can't reach the server/i)).not.toBeInTheDocument();
  });

  it('stops polling on 404 and on session expiry', async () => {
    vi.useFakeTimers();
    fetchImportJob.mockRejectedValue(new mocks.UserOrgApiErrorCtor('missing', 404, null, null));
    const { unmount } = renderWithProvider(
      <CsvImportPanel orgType={orgType({ lastImport: job({ status: 'pending', startedUtc: null, finishedUtc: null }) })} onImportFinished={vi.fn()} />,
    );
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Waiting to start...')).toBeInTheDocument();

    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByText(/can no longer be found/i)).toBeInTheDocument();
    expect(fetchImportJob).toHaveBeenCalledTimes(1);
    unmount();

    vi.useRealTimers();
    vi.useFakeTimers();
    fetchImportJob.mockReset();
    fetchImportJob.mockRejectedValue(new mocks.SessionExpiredErrorCtor());
    renderWithProvider(
      <CsvImportPanel orgType={orgType({ lastImport: job({ status: 'pending', startedUtc: null, finishedUtc: null }) })} onImportFinished={vi.fn()} />,
    );
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Waiting to start...')).toBeInTheDocument();
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByText(/Your session has expired/i)).toBeInTheDocument();
  });

  it.each([
    ['failed', /could not be completed/i],
    ['superseded', /replaced by a later one/i],
    ['typeChanged', /type was changed after the file was previewed/i],
    ['clearExceedsConfirmed', /more users' values than you confirmed/i],
    ['interruptedRepeatedly', /restarted during this import several times/i],
  ])('words finished job error code %s', async (errorCode, expected) => {
    renderWithProvider(
      <CsvImportPanel
        orgType={orgType({ lastImport: job({ status: 'failed', errorCode, errorMessage: 'server fallback' }) })}
        onImportFinished={vi.fn()}
      />,
    );

    expect(await screen.findByText(expected)).toBeInTheDocument();
  });

  it('shows nothing-changed success as a warning with the reason', async () => {
    renderWithProvider(
      <CsvImportPanel
        orgType={orgType({ lastImport: job({ rowsApplied: 0, rowsCleared: 0, rowsUnknownUpn: 2 }) })}
        onImportFinished={vi.fn()}
      />,
    );

    expect(await screen.findByText(/Nothing changed. 2 rows matched no user/i)).toBeInTheDocument();
  });

  it('shows the last import outcome after reload even when it is finished', async () => {
    renderWithProvider(<CsvImportPanel orgType={orgType({ lastImport: job() })} onImportFinished={vi.fn()} />);

    expect(await screen.findByText(/Last import:/i)).toHaveTextContent('Succeeded.');
    expect(screen.getByText(/2 changed, 1 cleared, 1 unknown, 0 unusable/i)).toBeInTheDocument();
  });

  it('offers the change list for an import that applied, from the history and when it finishes', async () => {
    fetchImportChanges.mockResolvedValue({
      jobId: 7,
      status: 'pending',
      storage: null,
      summary: null,
      items: [],
      continuation: null,
    });
    fetchImportHistory.mockResolvedValue([
      job({ changeLog: 'tableStorage' }),
      job({ id: 8, status: 'failed', errorCode: 'failed', changeLog: null }),
    ]);
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await userEvent.click(screen.getByText('Import history'));
    const table = await screen.findByRole('table', { name: 'Import history' });
    const buttons = within(table).getAllByRole('button', { name: 'View changes' });
    expect(buttons).toHaveLength(1);

    await userEvent.click(buttons[0]);

    await waitFor(() => expect(fetchImportChanges).toHaveBeenCalledWith(7, expect.anything(), expect.anything()));
    expect(await screen.findByText(/still being written/i)).toBeInTheDocument();
  });

  it('loads import history on first expansion and renders counts and reasons', async () => {
    fetchImportHistory.mockResolvedValue([
      job(),
      job({ id: 8, status: 'failed', errorCode: 'typeChanged', rowsApplied: 0, rowsCleared: 0, rowsUnknownUpn: 0, rowsInvalid: 4 }),
    ]);
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await userEvent.click(screen.getByText('Import history'));

    await waitFor(() => expect(fetchImportHistory).toHaveBeenCalledWith(1, 10, expect.anything()));
    const table = await screen.findByRole('table', { name: 'Import history' });
    expect(within(table).getAllByText('Merge')).toHaveLength(2);
    expect(within(table).getByText(/2 changed, 1 cleared, 1 unknown, 0 unusable/i)).toBeInTheDocument();
    expect(within(table).getByText(/type was changed after the file was previewed/i)).toBeInTheDocument();
  });
});
