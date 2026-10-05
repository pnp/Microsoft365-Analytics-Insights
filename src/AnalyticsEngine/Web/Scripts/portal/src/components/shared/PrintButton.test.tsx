import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import PrintButton from './PrintButton';
import { PRINT_ROW_LIMIT, registerPrintParticipant, resetPrintPreparation } from './printPreparation';
import { notify } from '../toast';

vi.mock('../toast', () => ({ notify: vi.fn() }));

describe('PrintButton', () => {
  beforeEach(() => {
    resetPrintPreparation();
    vi.mocked(notify).mockReset();
    vi.spyOn(window, 'print').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetPrintPreparation();
  });

  it('prints the page', () => {
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    expect(window.print).toHaveBeenCalledTimes(1);
  });

  it('routes Ctrl+P through the same path, so the keyboard prints what the button would', () => {
    // The browser's own Ctrl+P prints the page as it stands: the first page of every list.
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    const event = new KeyboardEvent('keydown', { key: 'p', ctrlKey: true, bubbles: true, cancelable: true });
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(window.print).toHaveBeenCalledTimes(1);
  });

  it('routes Cmd+P the same way on a Mac', () => {
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    const event = new KeyboardEvent('keydown', { key: 'P', metaKey: true, bubbles: true, cancelable: true });
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(window.print).toHaveBeenCalledTimes(1);
  });

  it('leaves other shortcuts alone', () => {
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    for (const init of [
      { key: 'p' },
      { key: 'p', ctrlKey: true, shiftKey: true },
      { key: 'p', ctrlKey: true, altKey: true },
      { key: 'o', ctrlKey: true },
    ]) {
      const event = new KeyboardEvent('keydown', { ...init, bubbles: true, cancelable: true });
      window.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }
    expect(window.print).not.toHaveBeenCalled();
  });

  it('stops listening for Ctrl+P once it leaves the page', () => {
    const { unmount } = renderWithProvider(<PrintButton tooltip="Prints this view" />);
    unmount();

    const event = new KeyboardEvent('keydown', { key: 'p', ctrlKey: true, bubbles: true, cancelable: true });
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });

  it('prints a long list\u2019s first rows, and warns on screen that it was cut short', async () => {
    registerPrintParticipant({
      rowCount: () => 12345,
      needsLoading: () => true,
      prepare: vi.fn(async () => undefined),
      restore: vi.fn(),
    });
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    await waitFor(() => expect(notify).toHaveBeenCalledTimes(1));
    expect(window.print).toHaveBeenCalledTimes(1);
    const [message, intent] = vi.mocked(notify).mock.calls[0];
    expect(intent).toBe('warning');
    expect(message).toContain(`Printed the first ${PRINT_ROW_LIMIT.toLocaleString('en')} of 12,345 rows`);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('raises no warning when every row was printed', async () => {
    registerPrintParticipant({
      rowCount: () => 80,
      needsLoading: () => true,
      prepare: vi.fn(async () => undefined),
      restore: vi.fn(),
    });
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    await waitFor(() => expect(window.print).toHaveBeenCalledTimes(1));
    expect(notify).not.toHaveBeenCalled();
  });

  it('says so when the full list could not be loaded', async () => {
    registerPrintParticipant({
      rowCount: () => 80,
      needsLoading: () => true,
      prepare: () => Promise.reject(new Error('HTTP 500')),
      restore: vi.fn(),
    });
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    expect((await screen.findByRole('dialog')).textContent).toContain('Could not prepare the printout');
    expect(window.print).not.toHaveBeenCalled();
  });

  it('shows that it is working while a list loads', async () => {
    let release!: () => void;
    registerPrintParticipant({
      rowCount: () => 80,
      needsLoading: () => true,
      prepare: () =>
        new Promise<void>((resolve) => {
          release = resolve;
        }),
      restore: vi.fn(),
    });
    renderWithProvider(<PrintButton tooltip="Prints this view" />);

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    expect(await screen.findByRole('button', { name: 'Preparing\u2026' })).toBeTruthy();
    expect(window.print).not.toHaveBeenCalled();

    await act(async () => {
      release();
    });
    expect(window.print).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Print' })).toBeTruthy();
  });
});
