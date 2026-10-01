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

  it('remounts the reports even when the answer after a switch fails, because the cookie has changed', async () => {
    vi.mocked(fetchEffectiveGlobalFilter).mockResolvedValueOnce(effective());
    renderProvider();
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('ready:0:1:false'));

    vi.mocked(fetchEffectiveGlobalFilter).mockRejectedValueOnce(new Error('offline'));
    await act(() => context.setBypassed(true));

    // What was known stays known; only the status says the refresh failed.
    expect(screen.getByTestId('state')).toHaveTextContent('error:1:1:false');
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
});
