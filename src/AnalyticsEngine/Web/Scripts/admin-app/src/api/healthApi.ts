import type { HealthDashboard } from '../types/health';

const baseUrl = (): string => window.o365AnalyticsHealthAPI ?? `${window.location.origin}/api/Health`;

/** Fetch the system-health overview ("is it working?") shown on the Health page. */
export async function fetchHealth(): Promise<HealthDashboard> {
  const response = await fetch(baseUrl(), {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load system health (${response.status}).`);
  }

  return response.json() as Promise<HealthDashboard>;
}
