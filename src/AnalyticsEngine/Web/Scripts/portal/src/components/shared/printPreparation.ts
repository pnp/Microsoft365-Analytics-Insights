import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react';
import { flushSync } from 'react-dom';

/**
 * Getting a page ready for paper before it is printed.
 *
 * Most of printing is CSS: the `@media print` block in index.css hides everything marked
 * `data-print="hide"` and flattens the layout. But a paged list cannot be fixed in CSS alone. It
 * holds fifty rows because the server sends fifty, so a printout of "page 1 of 12" is all a
 * stylesheet could ever produce - and a printed page has no Next button to get the rest.
 *
 * So a list that is paged registers itself here while it is on screen (`usePrintAllRows`), and a
 * print that goes through `requestPrint` - the Print button, and Ctrl+P while that button is on the
 * page - first asks each of them to load and render every row, then prints, then puts them back to
 * the page they were showing.
 *
 * A print started from the browser's own menu cannot be delayed like this: `beforeprint` is
 * synchronous and the rows are on the server. That printout keeps the page on screen, and each list
 * says so on paper rather than passing fifty rows off as the whole list.
 */

/**
 * The most rows one list prints in full.
 *
 * A thousand rows is about fifty sheets collapsed, and several times that with every row expanded,
 * which is already more than anyone reads. It is also a ceiling on what the browser is asked to lay
 * out: these lists grow with the tenant, a 200,000-user tenant has tens of thousands of seat
 * holders, and loading and rendering every one of them to print it would stall the tab long before
 * a page came out. A longer list is therefore not printed partially and silently - printing stops
 * and asks for a narrower filter, and the list's export is the way to get every row.
 */
export const PRINT_ROW_LIMIT = 1000;

/**
 * Rows fetched per request while loading a list for printing.
 *
 * The Copilot Adoption API caps a page at 500 rows (`MaxTake`) and quietly clamps anything larger,
 * so asking for more in one request would return 500 and look like the whole list. The loader below
 * advances by the rows it actually received, so a server that clamps lower still converges.
 */
export const PRINT_FETCH_PAGE_SIZE = 500;

/** One list on the page that needs loading before the page can be printed. */
export interface PrintParticipant {
  /** Every row the list would print: the filtered total, not the rows on screen. */
  rowCount(): number;
  /** True while some of those rows have not been loaded into the page. */
  needsLoading(): boolean;
  /** Loads every row and commits it to the document. Resolves once the rows are on the page. */
  prepare(signal: AbortSignal): Promise<void>;
  /** Puts the list back to the page it was showing before. */
  restore(): void;
}

export type PrintOutcome =
  | { kind: 'printed' }
  /** A print is already being prepared; this request did nothing. */
  | { kind: 'busy' }
  /** A list on the page is longer than `limit`, so nothing was printed. */
  | { kind: 'tooManyRows'; rows: number; limit: number }
  /** A list could not be loaded in full, so nothing was printed. */
  | { kind: 'failed'; error: unknown };

export type PrintPhase = 'idle' | 'preparing';

const participants = new Set<PrintParticipant>();
const phaseListeners = new Set<() => void>();
let phase: PrintPhase = 'idle';

function setPhase(next: PrintPhase): void {
  if (phase === next) return;
  phase = next;
  for (const listener of phaseListeners) listener();
}

function subscribeToPhase(listener: () => void): () => void {
  phaseListeners.add(listener);
  return () => {
    phaseListeners.delete(listener);
  };
}

const currentPhase = (): PrintPhase => phase;

/** Whether a print is being prepared - for a Print button to show it is working rather than dead. */
export function usePrintPhase(): PrintPhase {
  return useSyncExternalStore(subscribeToPhase, currentPhase, currentPhase);
}

/** Adds a list to the next print. Returns the function that removes it again. */
export function registerPrintParticipant(participant: PrintParticipant): () => void {
  participants.add(participant);
  return () => {
    participants.delete(participant);
  };
}

/**
 * Prints the page, with every registered list loaded in full.
 *
 * When no list needs loading this prints inside the caller's own event, synchronously, exactly as a
 * bare `window.print()` would - there is no reason to give up the click's user activation, or to
 * make the common case wait a tick.
 */
export function requestPrint(): Promise<PrintOutcome> {
  if (phase !== 'idle') return Promise.resolve({ kind: 'busy' });

  const active = [...participants];
  const rows = active.reduce((largest, participant) => Math.max(largest, participant.rowCount()), 0);
  if (rows > PRINT_ROW_LIMIT) {
    return Promise.resolve({ kind: 'tooManyRows', rows, limit: PRINT_ROW_LIMIT });
  }

  const pending = active.filter((participant) => participant.needsLoading());
  if (pending.length === 0) {
    window.print();
    return Promise.resolve({ kind: 'printed' });
  }

  return prepareThenPrint(pending);
}

async function prepareThenPrint(pending: PrintParticipant[]): Promise<PrintOutcome> {
  const controller = new AbortController();
  setPhase('preparing');

  try {
    await Promise.all(pending.map((participant) => participant.prepare(controller.signal)));
  } catch (error) {
    // One list failing means the printout would be missing rows it claims to hold, so nothing is
    // printed - and the other lists stop loading rather than finishing work nobody will see.
    controller.abort();
    for (const participant of pending) participant.restore();
    setPhase('idle');
    return { kind: 'failed', error };
  }

  try {
    // Blocking in every desktop browser: it returns once the print dialog has closed, so the full
    // lists are still in the document for as long as the printout is being produced.
    window.print();
  } finally {
    for (const participant of pending) participant.restore();
    setPhase('idle');
  }
  return { kind: 'printed' };
}

/** Test-only: forget every participant and any print left half-prepared by a previous test. */
export function resetPrintPreparation(): void {
  participants.clear();
  setPhase('idle');
}

/**
 * Lets a server-paged list print every row rather than the page on screen.
 *
 * Registers the list with the next print while `enabled` - pass false while the list is loading,
 * or while it sits in a section or tab that is not showing, since a hidden list is not printed and
 * must not hold the printout up. Returns the full set of rows while a print is being produced, and
 * null the rest of the time; render `printRows ?? pageRows`.
 *
 * The full list is only ever held for the length of a print. Keeping it would put the whole list
 * on screen, which is exactly what the paging is there to prevent.
 */
export function usePrintAllRows<Row>({
  enabled,
  total,
  loadedRows,
  loadPage,
}: {
  enabled: boolean;
  /** Every row matching the list's current filters, as the server reports it. */
  total: number;
  /** How many of those rows are on screen now. */
  loadedRows: number;
  /** Fetches one page of the list, with the same filters and sort as the rows on screen. */
  loadPage: (skip: number, take: number, signal: AbortSignal) => Promise<{ rows: Row[] }>;
}): Row[] | null {
  const [printRows, setPrintRows] = useState<Row[] | null>(null);

  // The participant is registered once and reads the list's state when a print actually starts, so
  // a filter changed since registering is the filter that gets printed.
  const latest = useRef({ total, loadedRows, loadPage });
  useLayoutEffect(() => {
    latest.current = { total, loadedRows, loadPage };
  });

  useEffect(() => {
    if (!enabled) return undefined;

    return registerPrintParticipant({
      rowCount: () => latest.current.total,
      needsLoading: () => latest.current.loadedRows < latest.current.total,
      prepare: async (signal) => {
        const { total: expected, loadPage: load } = latest.current;
        const rows: Row[] = [];
        while (rows.length < expected) {
          const page = await load(rows.length, Math.min(PRINT_FETCH_PAGE_SIZE, expected - rows.length), signal);
          // An empty page means the list shrank underneath us. Print what exists rather than loop.
          if (page.rows.length === 0) break;
          rows.push(...page.rows);
        }
        // Synchronously, so the rows are in the document before the caller goes on to print it.
        flushSync(() => setPrintRows(rows));
      },
      restore: () => setPrintRows(null),
    });
  }, [enabled]);

  return printRows;
}
