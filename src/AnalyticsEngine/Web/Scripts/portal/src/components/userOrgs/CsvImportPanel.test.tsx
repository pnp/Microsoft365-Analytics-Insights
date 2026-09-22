import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import CsvImportPanel from './CsvImportPanel';
import type { UserOrgCsvPreview, UserOrgImportJob, UserOrgType } from '../../types/userOrgs';

const previewCsv = vi.fn();
const importCsv = vi.fn();
const fetchImportJob = vi.fn();

vi.mock('../../api/userOrgsApi', () => ({
  previewCsv: (...args: unknown[]) => previewCsv(...args),
  importCsv: (...args: unknown[]) => importCsv(...args),
  fetchImportJob: (...args: unknown[]) => fetchImportJob(...args),
}));

function orgType(over: Partial<UserOrgType> = {}): UserOrgType {
  return {
    id: 1,
    name: 'Cost Centre',
    source: 'csv',
    entraAttributeName: null,
    isEnabled: true,
    assignedUserCount: 1200,
    distinctValueCount: 40,
    createdUtc: '2026-01-01T00:00:00.000Z',
    modifiedUtc: null,
    lastImport: null,
    ...over,
  };
}

function preview(over: Partial<UserOrgCsvPreview> = {}): UserOrgCsvPreview {
  return {
    fileName: 'orgs.csv',
    delimiter: 'comma',
    headerDetected: true,
    upnColumnName: 'UPN',
    orgColumnName: 'OrgName',
    rows: [
      { lineNumber: 2, upn: 'a@contoso.com', orgValue: 'Retail', userExists: true, clearsValue: false },
      { lineNumber: 3, upn: 'ghost@contoso.com', orgValue: 'Ops', userExists: false, clearsValue: false },
      { lineNumber: 4, upn: 'c@contoso.com', orgValue: null, userExists: true, clearsValue: true },
    ],
    problems: [],
    moreRowsExist: false,
    totalRows: 3,
    unknownUpnCount: 1,
    wouldClearCount: 0,
    currentlyAssignedCount: 1200,
    matchedUserCount: 1,
    truncatedValueCount: 0,
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
    finishedUtc: '2026-01-01T00:00:05.000Z',
    rowsTotal: 3,
    rowsApplied: 2,
    rowsCleared: 1,
    rowsUnknownUpn: 1,
    rowsInvalid: 0,
    errorMessage: null,
    ...over,
  };
}

function csvFile(name = 'orgs.csv'): File {
  return new File(['UPN,OrgName\r\na@contoso.com,Retail\r\n'], name, { type: 'text/csv' });
}

async function chooseFile(file = csvFile()) {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement;
  await userEvent.upload(input, file);
}

describe('CsvImportPanel', () => {
  beforeEach(() => {
    previewCsv.mockReset();
    importCsv.mockReset();
    fetchImportJob.mockReset();
  });

  it('previews the chosen file and flags rows that match no user', async () => {
    previewCsv.mockResolvedValue(preview());
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() => expect(screen.getByText('a@contoso.com')).toBeInTheDocument());
    expect(screen.getByText('ghost@contoso.com')).toBeInTheDocument();
    // The unmatched row is the thing an admin most needs to notice before a Replace.
    expect(screen.getByText(/match no user in this database/i)).toBeInTheDocument();
  });

  it('reports matches over the whole file, not just the rows on screen', async () => {
    // A truncated export whose first ten user principal names happen to exist looks perfectly clean
    // in the table. Judging the preview by the displayed rows is how a Merge of half a file gets
    // waved through, so the counts have to come from the whole file.
    previewCsv.mockResolvedValue(
      preview({
        rows: [
          {
            lineNumber: 2,
            upn: 'a@contoso.com',
            orgValue: 'Retail',
            userExists: true,
            clearsValue: false,
          },
        ],
        moreRowsExist: true,
        totalRows: 40000,
        unknownUpnCount: 39000,
      }),
    );
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() =>
      expect(screen.getByText(/1,000 of the 40,000 rows in the file/i)).toBeInTheDocument(),
    );
    expect(screen.getByText(/39,000 rows in the file match no user/i)).toBeInTheDocument();
  });

  it('says when organisation names will be stored shortened', async () => {
    // Two names identical for their first 200 characters silently become ONE organisation, and
    // nothing else in the preview or the import summary would reveal it.
    previewCsv.mockResolvedValue(preview({ truncatedValueCount: 3 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() => expect(screen.getByText(/stored shortened/i)).toBeInTheDocument());
  });

  it('shows an import left running by a recycle, even in a session that did not start it', async () => {
    // The panel used to start empty, so after a refresh a live or interrupted import looked like
    // nothing was happening - and the next upload was rejected with "already in progress" next to
    // an idle-looking panel.
    renderWithProvider(
      <CsvImportPanel
        orgType={orgType({ lastImport: job({ status: 'interrupted' }) })}
        onImportFinished={vi.fn()}
      />,
    );

    await waitFor(() =>
      expect(screen.getByText(/stopped reporting progress/i)).toBeInTheDocument(),
    );
  });

  it('shows a row with no organisation as clearing the value rather than as blank', async () => {
    previewCsv.mockResolvedValue(preview());
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() => expect(screen.getByText('clears the value')).toBeInTheDocument());
  });

  it('says when no header was recognised, so the column assumption is visible', async () => {
    previewCsv.mockResolvedValue(preview({ headerDetected: false, upnColumnName: null, orgColumnName: null }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() =>
      expect(screen.getByText(/No header row recognised/i)).toBeInTheDocument(),
    );
  });

  it('defaults to merge and only warns about clearing when replace is chosen', async () => {
    previewCsv.mockResolvedValue(preview({ wouldClearCount: 1199 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    await waitFor(() => expect(screen.getByLabelText(/Merge/)).toBeChecked());
    expect(screen.queryByText(/This will clear/i)).not.toBeInTheDocument();

    await userEvent.click(screen.getByLabelText(/Replace/));

    // Quantified from the whole file, not from the ten-row sample: the number that matters is how
    // many people lose their value, and a sample cannot reveal it.
    expect(screen.getByText(/This will clear/i)).toBeInTheDocument();
    expect(screen.getByText(/1,199 users'/)).toBeInTheDocument();
  });

  it('blocks a destructive replace until it is explicitly confirmed', async () => {
    previewCsv.mockResolvedValue(preview({ wouldClearCount: 1199 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled());
    await userEvent.click(screen.getByLabelText(/Replace/));

    expect(screen.getByRole('button', { name: /^Import/ })).toBeDisabled();

    await userEvent.click(screen.getByLabelText(/I understand/));

    expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled();
  });

  it('re-arms the confirmation when the mode changes back and forth', async () => {
    // A confirmation must never carry over to a different blast radius.
    previewCsv.mockResolvedValue(preview({ wouldClearCount: 1199 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    await waitFor(() => expect(screen.getByLabelText(/Replace/)).toBeInTheDocument());
    await userEvent.click(screen.getByLabelText(/Replace/));
    await userEvent.click(screen.getByLabelText(/I understand/));
    expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled();

    await userEvent.click(screen.getByLabelText(/Merge/));
    await userEvent.click(screen.getByLabelText(/Replace/));

    expect(screen.getByRole('button', { name: /^Import/ })).toBeDisabled();
  });

  it('does not demand confirmation when a replace would clear nobody', async () => {
    previewCsv.mockResolvedValue(preview({ wouldClearCount: 0 }));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();
    await waitFor(() => expect(screen.getByLabelText(/Replace/)).toBeInTheDocument());
    await userEvent.click(screen.getByLabelText(/Replace/));

    expect(screen.queryByText(/This will clear/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled();
  });

  it('sends the confirmation to the server, because the server re-checks it', async () => {
    // The disabled button only stops this browser. The server refuses a clearing replace unless
    // confirmClear arrives with it, so the flag has to be on the wire, not just in component state.
    previewCsv.mockResolvedValue(preview({ wouldClearCount: 1199 }));
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    fetchImportJob.mockResolvedValue(job());

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await waitFor(() => expect(screen.getByLabelText(/Replace/)).toBeInTheDocument());
    await userEvent.click(screen.getByLabelText(/Replace/));
    await userEvent.click(screen.getByLabelText(/I understand/));
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    await waitFor(() => expect(importCsv).toHaveBeenCalled());
    expect(importCsv).toHaveBeenCalledWith(1, 'replace', expect.any(File), true);
  });

  it('does not claim confirmation for a merge', async () => {
    previewCsv.mockResolvedValue(preview());
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    fetchImportJob.mockResolvedValue(job());

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    await waitFor(() => expect(importCsv).toHaveBeenCalled());
    expect(importCsv).toHaveBeenCalledWith(1, 'merge', expect.any(File), false);
  });

  it('reports unusable rows without blocking the import', async () => {
    previewCsv.mockResolvedValue(
      preview({ problems: [{ lineNumber: 9, reason: 'the user column is empty or too long' }] }),
    );
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() => expect(screen.getByText(/line 9/)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled();
  });

  it('polls until the import finishes and then shows the row counts', async () => {
    previewCsv.mockResolvedValue(preview());
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    fetchImportJob.mockResolvedValue(job());
    const onImportFinished = vi.fn();

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={onImportFinished} />);
    await chooseFile();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    await waitFor(() => expect(screen.getByText('Import finished.')).toBeInTheDocument(), {
      timeout: 5000,
    });
    expect(screen.getByText(/2 changed/)).toBeInTheDocument();
    expect(screen.getByText(/1 cleared/)).toBeInTheDocument();
    expect(screen.getByText(/1 unknown user/)).toBeInTheDocument();
    expect(onImportFinished).toHaveBeenCalled();
  });

  it('explains an interrupted import instead of spinning forever', async () => {
    // A recycled App Service leaves the job saying "running"; on screen that is indistinguishable
    // from a slow import, so it has to be called out explicitly.
    previewCsv.mockResolvedValue(preview());
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    fetchImportJob.mockResolvedValue(job({ status: 'interrupted' }));

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    await waitFor(
      () => expect(screen.getByText(/stopped reporting progress/i)).toBeInTheDocument(),
      { timeout: 5000 },
    );
  });

  it('surfaces a failed import with its message', async () => {
    previewCsv.mockResolvedValue(preview());
    importCsv.mockResolvedValue({ jobId: 7, rowsQueued: 3, rowsInvalid: 0 });
    fetchImportJob.mockResolvedValue(job({ status: 'failed', errorMessage: 'merge exploded' }));

    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);
    await chooseFile();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Import/ })).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: /^Import/ }));

    await waitFor(() => expect(screen.getByText(/merge exploded/)).toBeInTheDocument(), {
      timeout: 5000,
    });
  });

  it('shows the server message when a preview is rejected', async () => {
    previewCsv.mockRejectedValue(new Error('That file is larger than the 32 MB limit.'));
    renderWithProvider(<CsvImportPanel orgType={orgType()} onImportFinished={vi.fn()} />);

    await chooseFile();

    await waitFor(() =>
      expect(screen.getByText(/larger than the 32 MB limit/)).toBeInTheDocument(),
    );
  });
});
