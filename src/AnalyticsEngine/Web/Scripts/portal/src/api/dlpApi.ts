import { apiFetch } from './http';
import type { DlpAvailability, DlpSummary } from '../types/dlp';

const baseUrl = (): string => `${window.location.origin}/api/Dlp`;

/** Whether either DLP source is switched on, and what to tell the admin if not. */
export async function fetchDlpAvailability(): Promise<DlpAvailability> {
  const response = await apiFetch(`${baseUrl()}/availability`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load DLP availability (${response.status}).`);
  }

  return response.json() as Promise<DlpAvailability>;
}

/** The whole DLP page for one reporting window. */
export async function fetchDlpSummary(days: number): Promise<DlpSummary> {
  const response = await apiFetch(`${baseUrl()}/summary?days=${encodeURIComponent(String(days))}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load DLP summary (${response.status}).`);
  }

  return response.json() as Promise<DlpSummary>;
}
