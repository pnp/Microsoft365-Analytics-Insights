import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import ImportChangesDialog from './ImportChangesDialog';
import type { UserOrgChangeLogPage, UserOrgImportJob } from '../../types/userOrgs';

const mocks = vi.hoisted(() => ({ fetchImportChanges: vi.fn() }));
const { fetchImportChanges } = mocks;

vi.mock('../../api/userOrgsApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/userOrgsApi')>();
  return { ...actual, fetchImportChanges: (...args: unknown[]) => mocks.fetchImportChanges(...args) };
});

const GREEK = '\u039a\u03b1\u03bb\u03b7\u03bc\u03ad\u03c1\u03b1 \u03ba\u03cc\u03c3\u03bc\u03b5';

function job(over: Partial<UserOrgImportJob> = {}): UserOrgImportJob {
  return {
    id: 42,
    orgTypeId: 1,
    mode: 'replace',
    status: 'succeeded',
    fileName: 'cost-centres.csv',
    startedBy: 'admin@contoso.com',
    queuedUtc: '2026-09-01T08:00:00.000Z',
    startedUtc: '2026-09-01T08:00:01.000Z',
    finishedUtc: '2026-09-01T08:00:09.000Z',
    attempts: 1,
    rowsTotal: 3,
    rowsApplied: 2,
    rowsCleared: 1,
    rowsUnknownUpn: 0,
    rowsInvalid: 0,
    errorCode: null,
    errorMessage: null,
    changeLog: 'tableStorage',
    ...over,
  };
}

function page(over: Partial<UserOrgChangeLogPage> = {}): UserOrgChangeLogPage {
  return {
    jobId: 42,
    status: 'available',
    storage: 'tableStorage',
    summary: {
      orgTypeName: 'Cost Centre',
      mode: 'replace',
      startedBy: 'admin@contoso.com',
      fileName: 'cost-centres.csv',
      queuedUtc: '2026-09-01T08:00:00.000Z',
      finishedUtc: '2026-09-01T08:00:09.000Z',
      added: 1,
      changed: 1,
      cleared: 1,
      changeCount: 3,
      storedChanges: 3,
      rowsUnknownUpn: 0,
      rowsInvalid: 0,
    },
    items: [
      { upn: 'adele@contoso.com', before: null, after: GREEK, kind: 'added' },
      { upn: 'alex@contoso.com', before: 'CC-100', after: 'CC-200', kind: 'changed' },
      { upn: 'megan@contoso.com', before: 'CC-300', after: null, kind: 'cleared' },
    ],
    continuation: null,
    ...over,
  };
}

describe('ImportChangesDialog', () => {
  beforeEach(() => {
    fetchImportChanges.mockReset();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows who imported what, and every change with its before and after', async () => {
    fetchImportChanges.mockResolvedValue(page());
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />);

    const table = await screen.findByRole('table', { name: 'Changes made by this import' });
    expect(screen.getByText(/by admin@contoso.com \(Replace\) from cost-centres.csv/)).toBeInTheDocument();
    expect(screen.getByText('1 added, 1 changed, 1 cleared')).toBeInTheDocument();
    expect(screen.getByText('This change list is kept in Azure Table Storage.')).toBeInTheDocument();

    const rows = within(table).getAllByRole('row');
    expect(within(rows[1]).getByText('adele@contoso.com')).toBeInTheDocument();
    expect(within(rows[1]).getByText(GREEK)).toBeInTheDocument();
    expect(within(rows[1]).getByText('(no value)')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Added')).toBeInTheDocument();
    expect(within(rows[2]).getByText('CC-100')).toBeInTheDocument();
    expect(within(rows[2]).getByText('Changed')).toBeInTheDocument();
    expect(within(rows[3]).getByText('Cleared')).toBeInTheDocument();
    expect(screen.getByText('Showing 3 of 3')).toBeInTheDocument();
    expect(fetchImportChanges).toHaveBeenCalledWith(42, { search: '', pageSize: 50 }, expect.anything());
  });

  it('says plainly when the list lives only in one web server\u2019s memory, and when it was cut short', async () => {
    fetchImportChanges.mockResolvedValue(
      page({ storage: 'memory', summary: { ...page().summary!, changeCount: 5000, storedChanges: 3 } }),
    );
    renderWithProvider(<ImportChangesDialog job={job({ changeLog: 'memory' })} onDismiss={vi.fn()} />);

    expect(await screen.findByText(/kept in this web server's memory only/i)).toBeInTheDocument();
    expect(screen.getByText(/Only the first 3 of 5,000 changes were kept/)).toBeInTheDocument();
  });

  it.each([
    ['pending', 'tableStorage', /still being written/i],
    ['none', null, /because it was not applied/i],
    ['missing', 'memory', /kept in memory and is no longer available/i],
    ['missing', 'tableStorage', /could not be found in the storage account/i],
    ['unavailable', 'tableStorage', /can't be reached right now/i],
  ])('explains a %s list (%s)', async (status, storage, message) => {
    fetchImportChanges.mockResolvedValue(
      page({ status, storage: storage as UserOrgChangeLogPage['storage'], summary: null, items: [] }),
    );
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />);

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText(/changed nobody/i)).not.toBeInTheDocument();
  });

  it('asks again when the list was still being written', async () => {
    fetchImportChanges
      .mockResolvedValueOnce(page({ status: 'pending', summary: null, items: [] }))
      .mockResolvedValueOnce(page());
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('table', { name: 'Changes made by this import' })).toBeInTheDocument();
    expect(fetchImportChanges).toHaveBeenCalledTimes(2);
  });

  it('searches by the start of the user principal name', async () => {
    fetchImportChanges
      .mockResolvedValueOnce(page())
      .mockResolvedValueOnce(page({ items: [page().items[0]] }))
      .mockResolvedValueOnce(page({ items: [] }));
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />);
    await screen.findByRole('table', { name: 'Changes made by this import' });

    await userEvent.type(screen.getByLabelText('Search by user principal name'), 'adele{Enter}');

    await waitFor(() => expect(fetchImportChanges).toHaveBeenLastCalledWith(42, { search: 'adele', pageSize: 50 }, expect.anything()));
    expect(await screen.findByText('adele@contoso.com')).toBeInTheDocument();
    expect(screen.queryByText('alex@contoso.com')).not.toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText('Search by user principal name'));
    await userEvent.type(screen.getByLabelText('Search by user principal name'), 'zed{Enter}');

    expect(await screen.findByText(/No changes for users whose name starts with/)).toBeInTheDocument();
  });

  it('pages on with the continuation the server gave', async () => {
    fetchImportChanges
      .mockResolvedValueOnce(page({ items: [page().items[0]], continuation: 'next-1' }))
      .mockResolvedValueOnce(page({ items: [page().items[1]], continuation: null }));
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Show more' }));

    expect(await screen.findByText('alex@contoso.com')).toBeInTheDocument();
    expect(screen.getByText('adele@contoso.com')).toBeInTheDocument();
    expect(fetchImportChanges).toHaveBeenLastCalledWith(42, { search: '', continuation: 'next-1', pageSize: 50 });
    expect(screen.queryByRole('button', { name: 'Show more' })).not.toBeInTheDocument();
  });

  it('does not add a page that was still loading when a new search replaced the list', async () => {
    let resolveMore!: (value: UserOrgChangeLogPage) => void;
    fetchImportChanges
      .mockResolvedValueOnce(page({ items: [page().items[0]], continuation: 'next-1' }))
      .mockReturnValueOnce(new Promise<UserOrgChangeLogPage>((resolve) => { resolveMore = resolve; }))
      .mockResolvedValueOnce(page({ items: [page().items[2]], continuation: null }));
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Show more' }));
    await userEvent.type(screen.getByLabelText('Search by user principal name'), 'megan{Enter}');
    expect(await screen.findByText('megan@contoso.com')).toBeInTheDocument();

    await act(async () => {
      resolveMore(page({ items: [page().items[1]], continuation: 'next-2' }));
    });

    expect(screen.getByText('megan@contoso.com')).toBeInTheDocument();
    expect(screen.queryByText('alex@contoso.com')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show more' })).not.toBeInTheDocument();
  });

  it('says so when the list goes away between pages, rather than passing off what it has as all of it', async () => {
    fetchImportChanges
      .mockResolvedValueOnce(page({ storage: 'memory', items: [page().items[0]], continuation: 'next-1' }))
      .mockResolvedValueOnce(page({ status: 'missing', storage: 'memory', summary: null, items: [], continuation: null }));
    renderWithProvider(<ImportChangesDialog job={job({ changeLog: 'memory' })} onDismiss={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Show more' }));

    expect(await screen.findByText(/kept in memory and is no longer available/i)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('downloads every change as a CSV worded in the reader\u2019s language', async () => {
    let captured: Blob | undefined;
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: vi.fn() });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() });
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      captured = blob as Blob;
      return 'blob:csv';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    fetchImportChanges
      .mockResolvedValueOnce(page())
      .mockResolvedValueOnce(page({ items: [{ upn: '=cmd@contoso.com', before: null, after: 'X', kind: 'added' }], continuation: 'p2' }))
      .mockResolvedValueOnce(page({ items: [page().items[2]], continuation: null }));
    renderWithProvider(<ImportChangesDialog job={job()} onDismiss={vi.fn()} />, { language: 'es' });

    await userEvent.click(await screen.findByRole('button', { name: 'Descargar todos los cambios (CSV)' }));

    await waitFor(() => expect(captured).toBeDefined());
    const csv = await captured!.text();
    expect(csv).toContain('Usuario,Antes,Despu\u00e9s,Cambio\r\n');
    expect(csv).toContain("'=cmd@contoso.com,,X,A\u00f1adido");
    expect(csv).toContain('megan@contoso.com,CC-300,,Borrado');
    expect(fetchImportChanges).toHaveBeenNthCalledWith(2, 42, { continuation: null, pageSize: 1000 });
    expect(fetchImportChanges).toHaveBeenNthCalledWith(3, 42, { continuation: 'p2', pageSize: 1000 });
  });
});
