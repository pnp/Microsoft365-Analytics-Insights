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

beforeEach(() => {
  vi.clearAllMocks();
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

    // The reports follow the cookie, which the server honours for an administrator, so the bar must not go on
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
