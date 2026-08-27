import { apiFetch } from './http';
import type {
  HealthSummary,
  DataOverviewSection,
  LivenessSection,
  ExceptionsSection,
  ComponentsSection,
  ConfigSection,
} from '../types/health';

const baseUrl = (): string => window.o365AnalyticsHealthAPI ?? `${window.location.origin}/api/Health`;

/** GETs a Health sub-section and parses JSON, throwing a friendly error on a non-200. */
async function getSection<T>(path: string, label: string): Promise<T> {
  const response = await apiFetch(`${baseUrl()}/${path}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load ${label} (${response.status}).`);
  }

  return response.json() as Promise<T>;
}

/** Overview: overall traffic-light + per-section grid (cheap - skips the heavy SQL scans). */
export const fetchHealthSummary = (): Promise<HealthSummary> => getSection('summary', 'system health');

/** Data overview (SQL counts + freshness). The only heavy section - fetched on demand. */
export const fetchHealthData = (): Promise<DataOverviewSection> => getSection('data', 'data overview');

/** Import liveness (App Insights). */
export const fetchHealthLiveness = (): Promise<LivenessSection> => getSection('liveness', 'import liveness');

/** Exceptions overview (App Insights). */
export const fetchHealthExceptions = (): Promise<ExceptionsSection> => getSection('exceptions', 'exceptions');

/** Component health (runtime credential + Service Bus + App Insights). */
export const fetchHealthComponents = (): Promise<ComponentsSection> => getSection('components', 'component health');

/** Configuration + schema + Teams webhook state. */
export const fetchHealthConfig = (): Promise<ConfigSection> => getSection('config', 'configuration');
