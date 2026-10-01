import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { invalidateUserFilterDimensions, resetUserFilterDimensionsCache, useUserFilterDimensions } from './useUserFilterDimensions';
import type { UserFilterDimensionList } from '../../types/userFilter';

const mocks = vi.hoisted(() => ({ fetchUserFilterDimensions: vi.fn() }));

vi.mock('../../api/userFilterApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/userFilterApi')>();
  return { ...actual, fetchUserFilterDimensions: (...args: unknown[]) => mocks.fetchUserFilterDimensions(...args) };
});

function list(name: string): UserFilterDimensionList {
  return {
    people: 10,
    loadedUtc: '2026-09-28T08:00:00.000Z',
    dimensions: [
      { key: 'org:1', kind: 'custom', name, orgTypeId: 1, distinctValues: 3, peopleWithValue: 10, supportsTextMatch: true, fixedValues: false },
    ],
  };
}

const nameOf = (result: { current: { list: UserFilterDimensionList | null } }) => result.current.list?.dimensions[0].name;

describe('useUserFilterDimensions', () => {
  beforeEach(() => {
    resetUserFilterDimensionsCache();
    mocks.fetchUserFilterDimensions.mockReset();
  });

  it('does not request the PII-bearing catalogue while disabled', async () => {
    const result = renderHook(() => useUserFilterDimensions(false));

    await waitFor(() => expect(result.result.current.loading).toBe(false));
    expect(result.result.current.list).toBeNull();
    expect(mocks.fetchUserFilterDimensions).not.toHaveBeenCalled();
  });

  it('shares one list between filter bars, until the organisation types change', async () => {
    mocks.fetchUserFilterDimensions.mockResolvedValueOnce(list('Cost centre')).mockResolvedValueOnce(list('Cost centre (UK)'));

    const first = renderHook(() => useUserFilterDimensions());
    await waitFor(() => expect(nameOf(first.result)).toBe('Cost centre'));
    first.unmount();

    const second = renderHook(() => useUserFilterDimensions());
    expect(nameOf(second.result), 'Mounted again, the shared list is used.').toBe('Cost centre');
    second.unmount();
    expect(mocks.fetchUserFilterDimensions).toHaveBeenCalledTimes(1);

    // The admin renames the type on the User organisations page, then goes back to a report.
    invalidateUserFilterDimensions();
    const third = renderHook(() => useUserFilterDimensions());
    await waitFor(() => expect(nameOf(third.result)).toBe('Cost centre (UK)'));
    expect(mocks.fetchUserFilterDimensions).toHaveBeenCalledTimes(2);
  });

  it('does not keep a list that was already on its way when the types changed', async () => {
    let resolveStale!: (value: UserFilterDimensionList) => void;
    mocks.fetchUserFilterDimensions
      .mockReturnValueOnce(new Promise<UserFilterDimensionList>((resolve) => { resolveStale = resolve; }))
      .mockResolvedValueOnce(list('Cost centre (UK)'));

    const first = renderHook(() => useUserFilterDimensions());
    invalidateUserFilterDimensions();
    await act(async () => {
      resolveStale(list('Cost centre'));
    });
    first.unmount();

    const second = renderHook(() => useUserFilterDimensions());
    await waitFor(() => expect(nameOf(second.result)).toBe('Cost centre (UK)'));
    expect(mocks.fetchUserFilterDimensions).toHaveBeenCalledTimes(2);
  });
});
