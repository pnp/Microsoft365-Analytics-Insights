import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import { apiFetch } from './http';
import type {
  TeamsAdoption,
  TeamsAvailability,
  TeamsCollaboration,
  TeamsConversations,
  TeamsExportSection,
  TeamsGrouping,
  TeamsMeetings,
  TeamsOverview,
  TeamsPeople,
} from '../types/teamsExplorer';

const baseUrl = (): string => `${window.location.origin}/api/TeamsExplorer`;

const TEAMS_EXPORT_FAILURE_KEYS: Record<TeamsExportSection, TranslationKey> = {
  people: 'errors.teamsExplorer.exportPeopleFailed',
  dormant: 'errors.teamsExplorer.exportDormantFailed',
  teams: 'errors.teamsExplorer.exportTeamsFailed',
  channels: 'errors.teamsExplorer.exportChannelsFailed',
  adoption: 'errors.teamsExplorer.exportAdoptionFailed',
};

async function getJson<T>(path: string, failureKey: TranslationKey, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(`${baseUrl()}/${path}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });

  if (!response.ok) {
    throw new Error(translateActive(failureKey, { status: response.status }));
  }

  return response.json() as Promise<T>;
}

/** Which Teams data sources are switched on, and what to tell the admin about the ones that are not. */
export function fetchTeamsAvailability(signal?: AbortSignal): Promise<TeamsAvailability> {
  return getJson<TeamsAvailability>('availability', 'errors.teamsExplorer.dataSourcesFailed', signal);
}

export function fetchTeamsOverview(days: number, signal?: AbortSignal): Promise<TeamsOverview> {
  return getJson<TeamsOverview>(`overview?days=${days}`, 'errors.teamsExplorer.overviewFailed', signal);
}

export function fetchTeamsAdoption(
  days: number,
  groupBy: TeamsGrouping,
  signal?: AbortSignal,
): Promise<TeamsAdoption> {
  const qs = new URLSearchParams({ days: String(days), groupBy });
  return getJson<TeamsAdoption>(`adoption?${qs}`, 'errors.teamsExplorer.adoptionFailed', signal);
}

export function fetchTeamsMeetings(days: number, signal?: AbortSignal): Promise<TeamsMeetings> {
  return getJson<TeamsMeetings>(`meetings?days=${days}`, 'errors.teamsExplorer.meetingsFailed', signal);
}

export function fetchTeamsCollaboration(days: number, signal?: AbortSignal): Promise<TeamsCollaboration> {
  return getJson<TeamsCollaboration>(`collaboration?days=${days}`, 'errors.teamsExplorer.collaborationFailed', signal);
}

export function fetchTeamsConversations(days: number, signal?: AbortSignal): Promise<TeamsConversations> {
  return getJson<TeamsConversations>(`conversations?days=${days}`, 'errors.teamsExplorer.conversationsFailed', signal);
}

export function fetchTeamsPeople(days: number, signal?: AbortSignal): Promise<TeamsPeople> {
  return getJson<TeamsPeople>(`people?days=${days}`, 'errors.teamsExplorer.peopleFailed', signal);
}

/**
 * Downloads one section as CSV.
 *
 * Fetched and saved as a blob rather than pointed at with a plain `<a href>`, for the same reason
 * the Licence activity export is: a plain link cannot send the `X-Requested-With` header `apiFetch`
 * uses, so an expired session would answer with a redirect to the sign-in page and the browser
 * would helpfully save that HTML as a `.csv`.
 */
export async function downloadTeamsExport(
  section: TeamsExportSection,
  days: number,
  groupBy: TeamsGrouping,
  top = 500,
  signal?: AbortSignal,
): Promise<void> {
  const qs = new URLSearchParams({ days: String(days), groupBy, top: String(top) });

  const response = await apiFetch(`${baseUrl()}/export/${section}?${qs}`, {
    method: 'GET',
    headers: { Accept: 'text/csv' },
    signal,
  });

  if (!response.ok) {
    throw new Error(translateActive(TEAMS_EXPORT_FAILURE_KEYS[section], { status: response.status }));
  }

  const blob = await response.blob();
  saveBlob(blob, filenameFromResponse(response) ?? `teams-${section}.csv`);
}

function filenameFromResponse(response: Response): string | null {
  const header = response.headers.get('Content-Disposition');
  if (!header) return null;
  const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (utf8?.[1]) return decodeURIComponent(utf8[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain?.[1] ?? null;
}

/** Saves a blob to disk via a transient object URL (download without navigating). */
function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
