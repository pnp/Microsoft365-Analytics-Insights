import { translateActive } from '../i18n/runtime';
import { apiFetch } from './http';
import type { DlpAvailability, DlpGovernanceSummary, DlpSummary } from '../types/dlp';

const baseUrl = (): string => `${window.location.origin}/api/Dlp`;

/** Whether either DLP source is switched on, and what to tell the admin if not. */
export async function fetchDlpAvailability(): Promise<DlpAvailability> {
  const response = await apiFetch(`${baseUrl()}/availability`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive('errors.dlp.availabilityFailed', { status: response.status }));
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
    throw new Error(translateActive('errors.dlp.summaryFailed', { status: response.status }));
  }

  return response.json() as Promise<DlpSummary>;
}

/**
 * The page's governance section for one reporting window: prompt-safety rates, labelled content, and the
 * model and plugin mix. Its own call, so the DLP figures never wait for it and its failure stays in its section.
 */
export async function fetchDlpGovernance(days: number): Promise<DlpGovernanceSummary> {
  const response = await apiFetch(`${baseUrl()}/governance?days=${encodeURIComponent(String(days))}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive('errors.dlp.governanceFailed', { status: response.status }));
  }

  return response.json() as Promise<DlpGovernanceSummary>;
}
