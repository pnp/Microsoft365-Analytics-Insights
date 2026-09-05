import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { useUsersQuery } from './useUsersQuery';
import { fetchUsers } from '../../api/licenceActivityApi';
import type { LicenceActivityQueryEcho, LicenceActivityUsers, UsersParams } from '../../types/licenceActivity';

vi.mock('../../api/licenceActivityApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/licenceActivityApi')>();
  return { ...actual, fetchUsers: vi.fn() };
});

const mockedFetch = vi.mocked(fetchUsers);

const echo: LicenceActivityQueryEcho = {
  from: '2026-05-01',
  to: '2026-05-19',
  departmentId: null,
  countryId: null,
  licenceTypeId: 1,
  workload: 'teams',
  search: '',
  sort: 'activity',
  direction: 'desc',
  top: 10,
  page: 1,
  pageSize: 50,
};

interface Deferred {
  resolve: (page: LicenceActivityUsers) => void;
  reject: (error: unknown) => void;
  signal: AbortSignal;
}

let deferreds: Deferred[] = [];

function usersSnapshot(id: string): LicenceActivityUsers {
  return {
    snapshotId: id,
    generatedUtc: '2026-05-20T00:00:00Z',
    expiresUtc: '2026-05-20T00:02:00Z',
    overviewId: 'ov',
    query: echo,
    totalUsers: 0,
    rankedUsers: 0,
    mostActive: [],
    leastActive: [],
    users: [],
    messages: [],
  };
}

function Harness({ params }: { params: UsersParams | null }) {
  const { data } = useUsersQuery(params);
  return <span data-testid="snap">{data?.snapshotId ?? 'none'}</span>;
}

const A: UsersParams = { overviewId: 'ov', licenceTypeId: 1, workload: 'teams', top: 10, sort: 'activity', direction: 'desc', page: 1, pageSize: 50 };
const B: UsersParams = { ...A, licenceTypeId: 2 };

beforeEach(() => {
  deferreds = [];
  mockedFetch.mockReset();
  mockedFetch.mockImplementation(
    (_params, signal) =>
      new Promise<LicenceActivityUsers>((resolve, reject) => {
        deferreds.push({ resolve, reject, signal: signal as AbortSignal });
      }),
  );
});

describe('useUsersQuery', () => {
  it('aborts the in-flight request when the parameters change', () => {
    const { rerender } = renderWithProvider(<Harness params={A} />);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
    rerender(<Harness params={B} />);
    expect(mockedFetch).toHaveBeenCalledTimes(2);
    expect(deferreds[0].signal.aborted).toBe(true);
  });

  it('rejects a stale result: a superseded request resolving late does not overwrite the newer one', async () => {
    const { rerender } = renderWithProvider(<Harness params={A} />);
    rerender(<Harness params={B} />);

    await act(async () => deferreds[1].resolve(usersSnapshot('B')));
    expect(screen.getByTestId('snap')).toHaveTextContent('B');

    await act(async () => deferreds[0].resolve(usersSnapshot('A')));
    expect(screen.getByTestId('snap')).toHaveTextContent('B');
  });

  it('issues no request when params are null', () => {
    renderWithProvider(<Harness params={null} />);
    expect(mockedFetch).not.toHaveBeenCalled();
    expect(screen.getByTestId('snap')).toHaveTextContent('none');
  });

  it('does not show or export the previous licence while its replacement loads or fails', async () => {
    const { rerender } = renderWithProvider(<Harness params={A} />);
    await act(async () => deferreds[0].resolve(usersSnapshot('A')));
    expect(screen.getByTestId('snap')).toHaveTextContent('A');

    rerender(<Harness params={B} />);
    expect(screen.getByTestId('snap')).toHaveTextContent('none');
    await act(async () => deferreds[1].reject(new Error('synthetic request failure')));
    expect(screen.getByTestId('snap')).toHaveTextContent('none');
  });
});
