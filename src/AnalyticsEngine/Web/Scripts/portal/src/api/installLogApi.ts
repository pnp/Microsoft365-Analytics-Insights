import { translateActive } from '../i18n/runtime';
import { apiFetch } from './http';
import type { InstallLogEntry } from '../types/installLog';

const baseUrl = (): string =>
  window.o365AnalyticsInstallLogAPI ?? `${window.location.origin}/api/InstallLog`;

/** Fetch the install log (config history from sys_configs), newest first. */
export async function fetchInstallLog(): Promise<InstallLogEntry[]> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive('errors.installLog.loadFailed', { status: response.status }));
  }

  return response.json() as Promise<InstallLogEntry[]>;
}
