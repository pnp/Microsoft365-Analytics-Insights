import type { ReportAreaData, ReportAreaKey, ReportAreas } from '../types/reports';

const baseUrl = (): string =>
  window.o365AnalyticsReportsAPI ?? `${window.location.origin}/api/Reports`;

/** Which report areas are enabled for this deployment (drives the visible sub-tabs). */
export async function fetchReportAreas(): Promise<ReportAreas> {
  const response = await fetch(`${baseUrl()}/areas`, {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load report areas (${response.status}).`);
  }

  return response.json() as Promise<ReportAreas>;
}

/** Fetch the weekly charts for one report area over the given window (months). */
export async function fetchReportArea(area: ReportAreaKey, months: number): Promise<ReportAreaData> {
  const url = `${baseUrl()}/${area}?months=${encodeURIComponent(months)}`;
  const response = await fetch(url, {
    method: 'GET',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't load the ${area} report (${response.status}).`);
  }

  return response.json() as Promise<ReportAreaData>;
}
