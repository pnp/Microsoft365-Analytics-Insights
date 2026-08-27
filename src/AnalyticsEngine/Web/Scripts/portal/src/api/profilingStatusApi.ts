import { apiFetch } from './http';
import type { ProfilingStatus, TraceLogPage } from '../types/profilingStatus';

const baseUrl = (): string =>
  window.o365AnalyticsProfilingStatusAPI ?? `${window.location.origin}/api/ProfilingStatus`;

/** Fetch profiling data-freshness (earliest/latest dates for the profiling & source tables). */
export async function fetchProfilingStatus(): Promise<ProfilingStatus> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load profiling status (${response.status}).`);
  }

  return response.json() as Promise<ProfilingStatus>;
}

/** Fetch a page of profiling.TraceLogs (newest first). Page is zero-based. */
export async function fetchTraceLogs(page: number, pageSize: number): Promise<TraceLogPage> {
  const url = `${baseUrl()}/tracelogs?page=${encodeURIComponent(page)}&pageSize=${encodeURIComponent(pageSize)}`;
  const response = await apiFetch(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load profiling trace logs (${response.status}).`);
  }

  return response.json() as Promise<TraceLogPage>;
}
