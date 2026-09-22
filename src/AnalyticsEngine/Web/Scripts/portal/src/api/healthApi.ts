import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
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
async function getSection<T>(path: string, failureKey: TranslationKey): Promise<T> {
  const response = await apiFetch(`${baseUrl()}/${path}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive(failureKey, { status: response.status }));
  }

  return response.json() as Promise<T>;
}

/** Overview: overall traffic-light + per-section grid (cheap - skips the heavy SQL scans). */
export const fetchHealthSummary = (): Promise<HealthSummary> => getSection('summary', 'errors.health.summaryFailed');

/** Data overview (SQL counts + freshness). The only heavy section - fetched on demand. */
export const fetchHealthData = (): Promise<DataOverviewSection> => getSection('data', 'errors.health.dataFailed');

/** Import liveness (App Insights). */
export const fetchHealthLiveness = (): Promise<LivenessSection> => getSection('liveness', 'errors.health.livenessFailed');

/** Exceptions overview (App Insights). */
export const fetchHealthExceptions = (): Promise<ExceptionsSection> => getSection('exceptions', 'errors.health.exceptionsFailed');

/** Component health (runtime credential + Service Bus + App Insights). */
export const fetchHealthComponents = (): Promise<ComponentsSection> => getSection('components', 'errors.health.componentsFailed');

/** Configuration + schema + Teams webhook state. */
export const fetchHealthConfig = (): Promise<ConfigSection> => getSection('config', 'errors.health.configFailed');
