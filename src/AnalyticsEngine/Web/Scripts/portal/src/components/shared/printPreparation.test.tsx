import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import {
  PRINT_FETCH_PAGE_SIZE,
  PRINT_ROW_LIMIT,
  registerPrintParticipant,
  requestPrint,
  resetPrintPreparation,
  usePrintAllRows,
  usePrintPhase,
} from './printPreparation';

/**
 * Printing a paged list.
 *
 * A stylesheet can hide a pager but it cannot print rows that are not on the page, so these lists
 * load before a print - in full, or their first rows past the limit - and go back to their page
 * afterwards. What matters is the state of the document at the moment the browser captures it -
 * which is what the `window.print` stub below records - and that nothing is ever printed partially
 * and silently.
 */

const range = (from: number, count: number) => Array.from({ length: count }, (_, i) => `row ${from + i}`);

function List({
  total,
  pageRows,
  loadPage,
  enabled = true,
}: {
  total: number;
  pageRows: string[];
  loadPage: (skip: number, take: number, signal: AbortSignal) => Promise<{ rows: string[] }>;
  enabled?: boolean;
}) {
  const printRows = usePrintAllRows({ enabled, total, loadedRows: pageRows.length, loadPage });
  const phase = usePrintPhase();
  return (
    <div>
      <span data-testid="phase">{phase}</span>
      <ul>
        {(printRows ?? pageRows).map((row) => (
          <li key={row}>{row}</li>
        ))}
      </ul>
    </div>
  );
}

/** A server that pages exactly as the Copilot Adoption API does, clamping at `clamp` rows. */
function server(total: number, clamp = PRINT_FETCH_PAGE_SIZE) {
  return vi.fn(async (skip: number, take: number) => ({
    rows: range(skip, Math.max(0, Math.min(take, clamp, total - skip))),
  }));
}

let rowsWhenPrinted: number | null;

beforeEach(() => {
  resetPrintPreparation();
  rowsWhenPrinted = null;
  vi.spyOn(window, 'print').mockImplementation(() => {
    rowsWhenPrinted = screen.queryAllByRole('listitem').length;
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  resetPrintPreparation();
});

describe('requestPrint', () => {
  it('prints straight away, inside the caller\u2019s own event, when nothing needs loading', () => {
    // Synchronously, so the click keeps its user activation and a page with no long list prints
    // exactly as a bare window.print() would.
    void requestPrint();
    expect(window.print).toHaveBeenCalledTimes(1);
  });

  it('does not wait for a list whose rows are all on screen already', () => {
    const loadPage = server(30);
    render(<List total={30} pageRows={range(0, 30)} loadPage={loadPage} />);

    void requestPrint();

    expect(window.print).toHaveBeenCalledTimes(1);
    expect(loadPage).not.toHaveBeenCalled();
  });

  it('loads every row of a paged list before printing, and prints them all', async () => {
    const loadPage = server(750);
    render(<List total={750} pageRows={range(0, 50)} loadPage={loadPage} />);

    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    });

    expect(rowsWhenPrinted).toBe(750);
    // In the server's own page size, so a request is never silently clamped to part of the list.
    expect(loadPage.mock.calls.map(([skip, take]) => [skip, take])).toEqual([
      [0, 500],
      [500, 250],
    ]);
  });

  it('still gets every row from a server that clamps to less than it was asked for', async () => {
    const loadPage = server(260, 100);
    render(<List total={260} pageRows={range(0, 50)} loadPage={loadPage} />);

    await act(async () => {
      await requestPrint();
    });

    expect(rowsWhenPrinted).toBe(260);
    expect(loadPage.mock.calls.map(([skip]) => skip)).toEqual([0, 100, 200]);
  });

  it('puts the list back to its page once the printout is done', async () => {
    render(<List total={120} pageRows={range(0, 50)} loadPage={server(120)} />);

    await act(async () => {
      await requestPrint();
    });

    expect(rowsWhenPrinted).toBe(120);
    expect(screen.getAllByRole('listitem')).toHaveLength(50);
    expect(screen.getByTestId('phase').textContent).toBe('idle');
  });

  it('prints the first rows of a list longer than the limit, and reports that it was cut short', async () => {
    // Tenant-sized lists: tens of thousands of seat holders cannot be laid out to print, but refusing
    // left nothing to print at all. So the first rows - in the list's own order - are loaded and
    // printed, and the outcome says the list was cut short, for the list and the button to say so.
    const loadPage = server(PRINT_ROW_LIMIT + 1);
    render(<List total={PRINT_ROW_LIMIT + 1} pageRows={range(0, 50)} loadPage={loadPage} />);

    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({
        kind: 'printed',
        truncated: { rows: PRINT_ROW_LIMIT + 1, limit: PRINT_ROW_LIMIT },
      });
    });

    expect(rowsWhenPrinted).toBe(PRINT_ROW_LIMIT);
    // Never a row past the limit is requested, however long the list.
    expect(loadPage.mock.calls.map(([skip, take]) => [skip, take])).toEqual([
      [0, PRINT_FETCH_PAGE_SIZE],
      [PRINT_FETCH_PAGE_SIZE, PRINT_ROW_LIMIT - PRINT_FETCH_PAGE_SIZE],
    ]);
    expect(screen.getAllByRole('listitem')).toHaveLength(50);
  });

  it('prints only the limit even from a server that returns more than it was asked for', async () => {
    const loadPage = vi.fn(async (skip: number) => ({ rows: range(skip, PRINT_FETCH_PAGE_SIZE + 7) }));
    render(<List total={PRINT_ROW_LIMIT * 4} pageRows={range(0, 50)} loadPage={loadPage} />);

    await act(async () => {
      await requestPrint();
    });

    expect(rowsWhenPrinted).toBe(PRINT_ROW_LIMIT);
  });

  it('prints a list of exactly the limit', async () => {
    render(<List total={PRINT_ROW_LIMIT} pageRows={range(0, 50)} loadPage={server(PRINT_ROW_LIMIT)} />);

    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    });

    expect(rowsWhenPrinted).toBe(PRINT_ROW_LIMIT);
  });

  it('prints nothing when the full list cannot be loaded', async () => {
    const failure = new Error('HTTP 500');
    const loadPage = vi.fn().mockResolvedValueOnce({ rows: range(0, 500) }).mockRejectedValueOnce(failure);
    render(<List total={900} pageRows={range(0, 50)} loadPage={loadPage} />);

    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'failed', error: failure });
    });

    expect(window.print).not.toHaveBeenCalled();
    expect(screen.getAllByRole('listitem')).toHaveLength(50);
    expect(screen.getByTestId('phase').textContent).toBe('idle');
  });

  it('ignores a second request while the first is still loading', async () => {
    let release!: () => void;
    const loadPage = vi.fn(
      (skip: number, take: number) =>
        new Promise<{ rows: string[] }>((resolve) => {
          release = () => resolve({ rows: range(skip, take) });
        }),
    );
    render(<List total={80} pageRows={range(0, 50)} loadPage={loadPage} />);

    let first!: Promise<unknown>;
    act(() => {
      first = requestPrint();
    });
    expect(screen.getByTestId('phase').textContent).toBe('preparing');
    await expect(requestPrint()).resolves.toEqual({ kind: 'busy' });

    await act(async () => {
      release();
      await first;
    });
    expect(window.print).toHaveBeenCalledTimes(1);
  });

  it('leaves out a list that is not showing', async () => {
    // A list in a hidden section or tab is not on the printout, so it must neither hold the print
    // up while it loads nor be reported as cut short.
    const loadPage = server(5000);
    render(<List enabled={false} total={5000} pageRows={range(0, 50)} loadPage={loadPage} />);

    await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    expect(loadPage).not.toHaveBeenCalled();
  });

  it('forgets a list once it unmounts', async () => {
    const { unmount } = render(<List total={5000} pageRows={range(0, 50)} loadPage={server(5000)} />);
    unmount();

    await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
  });

  it('reads the list as it is when the print starts, not as it was when it registered', async () => {
    const first = server(120);
    const second = server(70);
    const { rerender } = render(<List total={120} pageRows={range(0, 50)} loadPage={first} />);
    rerender(<List total={70} pageRows={range(0, 50)} loadPage={second} />);

    await act(async () => {
      await requestPrint();
    });

    expect(first).not.toHaveBeenCalled();
    expect(second.mock.calls.map(([skip, take]) => [skip, take])).toEqual([[0, 70]]);
    expect(rowsWhenPrinted).toBe(70);
  });

  it('reports the longest registered list when a print was cut short', async () => {
    const unregister = registerPrintParticipant({
      rowCount: () => PRINT_ROW_LIMIT + 500,
      needsLoading: () => true,
      prepare: vi.fn(async () => undefined),
      restore: vi.fn(),
    });
    render(<List total={40} pageRows={range(0, 40)} loadPage={server(40)} />);

    await act(async () => {
      await expect(requestPrint()).resolves.toMatchObject({
        kind: 'printed',
        truncated: { rows: PRINT_ROW_LIMIT + 500, limit: PRINT_ROW_LIMIT },
      });
    });
    expect(window.print).toHaveBeenCalledTimes(1);
    unregister();
  });
});
