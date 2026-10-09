import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { fetchEffectiveGlobalFilter } from '../../api/globalFilterApi';
import type { GlobalFilterEffective } from '../../types/globalFilter';
import { GLOBAL_FILTER_BYPASS_COOKIE, GlobalFilterProvider, useGlobalFilter, type GlobalFilterContextValue } from './GlobalFilterProvider';

vi.mock('../../api/globalFilterApi', () => ({
  fetchEffectiveGlobalFilter: vi.fn(),
}));

function effective(overrides: Partial<GlobalFilterEffective> = {}): GlobalFilterEffective {
  return { active: true, applied: true, bypassed: false, canBypass: true, revision: 1, filter: null, invalid: false, ...overrides };
}

let context: GlobalFilterContextValue;

function Probe() {
  context = useGlobalFilter();
  return (
    <span data-testid="state">
      {context.status}:{context.viewKey}:{context.effective?.revision ?? '-'}:{String(context.effective?.bypassed ?? '-')}
    </span>
  );
}

function renderProvider() {
  return render(
    <MemoryRouter>
      <GlobalFilterProvider>
        <Probe />
      </GlobalFilterProvider>
    </MemoryRouter>,
  );
}

function clearCookie() {
  document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=; path=/; max-age=0`;
}

function pendingRead() {
  let resolve!: (value: GlobalFilterEffective) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<GlobalFilterEffective>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  vi.resetAllMocks();
  clearCookie();
});

afterEach(clearCookie);

describe('GlobalFilterProvider', () => {
  it('reads the filter once, without remounting the page for the first answer', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValue(effective());
    renderProvider();

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(1);
  });

  it('cannot set a bypass before capability reporting has completed', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockImplementationOnce(() => new Promise(() => {}));
    renderProvider();
    await act(() => context.setBypassed(true));
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(context.viewKey).toBe(0);
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(1);
  });

  it('clears an old sign-in’s bypass and refuses to set it without both permissions', async () => {
    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);

    await act(() => context.setBypassed(true));
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(1);
    expect(context.viewKey).toBe(0);

    // A fresh denial, not this tab's cached capability, clears another tab's switch.
    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    const read = pendingRead();
    vi.mocked(fetchEffectiveGlobalFilter).mockReturnValueOnce(read.promise);
    act(() => window.dispatchEvent(new Event('focus')));
    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    await act(async () => read.resolve(effective({ canBypass: false })));
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(2);
    expect(context.effective?.applied).toBe(true);
    expect(context.viewKey).toBe(1);
  });

  it.each(['focus', 'visibilitychange'])('refreshes cached denial on %s without clearing a newly authorized tab’s bypass', async (event) => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    renderProvider();
    await waitFor(() => expect(context.status).toBe('ready'));

    // Another tab signs in with both roles, then explicitly switches off the filter.
    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    const read = pendingRead();
    vi.mocked(fetchEffectiveGlobalFilter).mockReturnValueOnce(read.promise);
    act(() => {
      (event === 'focus' ? window : document).dispatchEvent(new Event(event));
      window.dispatchEvent(new Event('focus'));
    });
    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(2);
    await act(async () => read.resolve(effective({ bypassed: true, applied: false })));

    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:true');
    expect(context.effective?.canBypass).toBe(true);
    expect(context.effective?.applied).toBe(false);
  });

  it('refreshes cached denial even when the old sign-in had no active filter', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false, active: false, applied: false }));
    renderProvider();
    await waitFor(() => expect(context.status).toBe('ready'));

    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ bypassed: true, applied: false }));
    await act(async () => window.dispatchEvent(new Event('focus')));
    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:true');
  });

  it('preserves another tab’s cookie on a failed capability refresh without granting cached-denied bypass', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    renderProvider();
    await waitFor(() => expect(context.status).toBe('ready'));

    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockRejectedValueOnce(new Error('offline'));
    await act(async () => window.dispatchEvent(new Event('focus')));
    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('error:1:1:false');
    expect(context.effective?.canBypass).toBe(false);
    expect(context.effective?.applied).toBe(true);

    clearCookie();
    await act(() => context.setBypassed(true));
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(2);
  });

  it('does not act on a denied response superseded by a newer capability read', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    renderProvider();
    await waitFor(() => expect(context.status).toBe('ready'));

    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    const stale = pendingRead();
    vi.mocked(fetchEffectiveGlobalFilter).mockReturnValueOnce(stale.promise);
    act(() => window.dispatchEvent(new Event('focus')));
    const staleSignal = vi.mocked(fetchEffectiveGlobalFilter).mock.calls[1][0];
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ bypassed: true, applied: false }));
    await act(() => context.refresh());
    expect(staleSignal?.aborted).toBe(true);
    await act(async () => stale.resolve(effective({ canBypass: false })));

    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:true');
  });

  it.each(['denial', 'error'])('ignores an outstanding focus %s after unmount', async (outcome) => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    const rendered = renderProvider();
    await waitFor(() => expect(context.status).toBe('ready'));

    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    const read = pendingRead();
    vi.mocked(fetchEffectiveGlobalFilter).mockReturnValueOnce(read.promise);
    act(() => window.dispatchEvent(new Event('focus')));
    const signal = vi.mocked(fetchEffectiveGlobalFilter).mock.calls[1][0];
    rendered.unmount();
    expect(signal?.aborted).toBe(true);
    await act(async () => {
      if (outcome === 'denial') read.resolve(effective({ canBypass: false }));
      else read.reject(new Error('offline'));
    });
    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
  });

  it('clears bypass when a permission is withdrawn, remounts filtered reports and does not restore it with the role', async () => {
    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ bypassed: true, applied: false }));
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:true'));

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    await act(() => context.refresh());
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:false');
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(context.effective?.applied).toBe(true);

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    await act(() => context.refresh());
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:false');
  });

  it('does not optimistically bypass an unauthorized reader after a failed refresh', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));
    vi.mocked(fetchEffectiveGlobalFilter).mockRejectedValueOnce(new Error('offline'));
    await act(() => context.refresh());
    await act(() => context.setBypassed(true));
    expect(screen.getByTestId('state')).toHaveTextContent('error:0:1:false');
    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(context.effective?.applied).toBe(true);
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(2);
  });

  it('sets the cookie, reads again and remounts the reports once when an administrator switches it off', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ bypassed: true, applied: false }));
    await act(() => context.setBypassed(true));

    expect(document.cookie).toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:true');

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    await act(() => context.setBypassed(false));

    expect(document.cookie).not.toContain(`${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
    expect(screen.getByTestId('state')).toHaveTextContent('ready:2:1:false');
  });

  it('remounts the reports even when the answer after a switch fails, and describes what the cookie now asks for', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    vi.mocked(fetchEffectiveGlobalFilter).mockRejectedValueOnce(new Error('offline'));
    await act(() => context.setBypassed(true));

    // The reports follow the cookie, which requires Administration and See PII, so the bar must not go on
    // describing the filtered view; the status still says the read failed.
    expect(screen.getByTestId('state')).toHaveTextContent('error:1:1:true');
    expect(context.effective?.applied).toBe(false);

    vi.mocked(fetchEffectiveGlobalFilter).mockRejectedValueOnce(new Error('offline'));
    await act(() => context.setBypassed(false));
    expect(screen.getByTestId('state')).toHaveTextContent('error:2:1:false');
    expect(context.effective?.applied).toBe(true);
  });

  it('remounts the reports when an administrator replaced the filter, and not when nothing changed', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    await act(() => context.refresh());
    expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false');

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ revision: 2 }));
    await act(() => context.refresh());
    expect(screen.getByTestId('state')).toHaveTextContent('ready:1:2:false');
  });

  it('reads again and remounts when another tab switched it off, once this tab is back in view', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    // Nothing has changed: coming back to the tab costs nothing.
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(1);

    // Another tab sets the browser-wide cookie.
    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ bypassed: true, applied: false }));
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:1:1:true'));
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(2);
  });

  it('follows the cookie another tab set even when the read that should confirm it fails', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockRejectedValueOnce(new Error('offline'));
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });

    // The remounted reports are unfiltered, so the bar must not keep the filtered pills.
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('error:1:1:true'));
    expect(context.effective?.applied).toBe(false);
  });

  it('does not read again on focus for a reader, or while no filter is defined', async () => {
    document.cookie = `${GLOBAL_FILTER_BYPASS_COOKIE}=1; path=/`;
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ active: false, applied: false }));
    const first = renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    // With no filter the server says "not switched off" whatever the cookie holds; that is not a disagreement.
    act(() => {
      window.dispatchEvent(new Event('focus'));
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(1);
    first.unmount();

    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective({ canBypass: false }));
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });
    expect(fetchEffectiveGlobalFilter).toHaveBeenCalledTimes(2);
  });
});
