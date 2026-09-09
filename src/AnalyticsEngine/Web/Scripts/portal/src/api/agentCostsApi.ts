import { apiFetch } from './http';
import type {
  AgentCostAvailability,
  AgentCostBreakdownRow,
  AgentCostDailyPoint,
  AgentCostDetailPage,
  AgentCostDetailRow,
  AgentCostFilterOptions,
  AgentCostFilters,
  AgentCostSummary,
  AgentCostUserRow,
  AzureCostBreakdownRow,
  AzureDimension,
  CreditDimension,
} from '../types/agentCosts';

const baseUrl = (): string =>
  window.o365AnalyticsAgentCostsAPI ?? `${window.location.origin}/api/AgentCosts`;

/** A failure from the Agent costs API, carrying the server's own message when it sent one. */
export class AgentCostsApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'AgentCostsApiError';
    this.status = status;
  }
}

/** Reads the server's `{ message }` error body without consuming the original response. */
async function readServerMessage(response: Response): Promise<string | null> {
  try {
    const body = (await response.clone().json()) as { message?: unknown } | null;
    return body && typeof body.message === 'string' ? body.message : null;
  } catch {
    return null;
  }
}

async function getJson<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(`${baseUrl()}${path}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });

  if (!response.ok) {
    const message = (await readServerMessage(response)) ?? `Couldn't load ${what} (${response.status}).`;
    throw new AgentCostsApiError(response.status, message);
  }

  return response.json() as Promise<T>;
}

/**
 * Turns the page's filter state into a query string, omitting anything unset.
 *
 * Blank values are dropped rather than sent as empty parameters: the server treats a present-but-empty
 * filter the same way, but omitting it keeps the URL readable in a browser network trace, which is
 * where an admin diagnosing "why is this report empty?" will look first.
 */
function filterQuery(filters: AgentCostFilters): URLSearchParams {
  const qs = new URLSearchParams({ from: filters.from, to: filters.to });
  if (filters.agentId) qs.set('agentId', filters.agentId);
  if (filters.environmentId) qs.set('environmentId', filters.environmentId);
  if (filters.harness) qs.set('harness', filters.harness);
  if (filters.feature) qs.set('feature', filters.feature);
  if (filters.model) qs.set('model', filters.model);
  if (filters.tool) qs.set('tool', filters.tool);
  if (filters.knowledge) qs.set('knowledge', filters.knowledge);
  if (filters.channel) qs.set('channel', filters.channel);
  if (filters.search?.trim()) qs.set('search', filters.search.trim());
  return qs;
}

export function fetchAvailability(signal?: AbortSignal): Promise<AgentCostAvailability> {
  return getJson<AgentCostAvailability>('/availability', 'the agent cost availability', signal);
}

export function fetchSummary(filters: AgentCostFilters, signal?: AbortSignal): Promise<AgentCostSummary> {
  return getJson<AgentCostSummary>(`/summary?${filterQuery(filters)}`, 'the agent cost summary', signal);
}

export function fetchTrend(filters: AgentCostFilters, signal?: AbortSignal): Promise<AgentCostDailyPoint[]> {
  return getJson<AgentCostDailyPoint[]>(`/trend?${filterQuery(filters)}`, 'the daily credit trend', signal);
}

export function fetchBreakdown(
  filters: AgentCostFilters,
  dimension: CreditDimension,
  top = 20,
  signal?: AbortSignal,
): Promise<AgentCostBreakdownRow[]> {
  const qs = filterQuery(filters);
  qs.set('dimension', dimension);
  qs.set('top', String(top));
  return getJson<AgentCostBreakdownRow[]>(`/breakdown?${qs}`, 'the credit breakdown', signal);
}

export interface DetailParams extends AgentCostFilters {
  page: number;
  pageSize: number;
  sort: string;
  direction: 'asc' | 'desc';
}

export function fetchDetail(params: DetailParams, signal?: AbortSignal): Promise<AgentCostDetailPage> {
  const qs = filterQuery(params);
  qs.set('page', String(params.page));
  qs.set('pageSize', String(params.pageSize));
  qs.set('sort', params.sort);
  qs.set('direction', params.direction);
  return getJson<AgentCostDetailPage>(`/detail?${qs}`, 'the detailed credit rows', signal);
}

export function fetchAzureBreakdown(
  filters: AgentCostFilters,
  dimension: AzureDimension,
  top = 20,
  signal?: AbortSignal,
): Promise<AzureCostBreakdownRow[]> {
  const qs = new URLSearchParams({ from: filters.from, to: filters.to, dimension, top: String(top) });
  return getJson<AzureCostBreakdownRow[]>(`/azure?${qs}`, 'the Azure cost breakdown', signal);
}

/**
 * The biggest per-user credit consumers. Copilot Studio only - Azure spend is resource-scoped and has no
 * per-user view on any Cost Management surface.
 */
export function fetchTopUsers(
  filters: AgentCostFilters,
  top = 20,
  signal?: AbortSignal,
): Promise<AgentCostUserRow[]> {
  const qs = new URLSearchParams({ from: filters.from, to: filters.to, top: String(top) });
  if (filters.environmentId) qs.set('environmentId', filters.environmentId);
  return getJson<AgentCostUserRow[]>(`/users?${qs}`, 'the per-user credit consumption', signal);
}

export function fetchFilterOptions(filters: AgentCostFilters, signal?: AbortSignal): Promise<AgentCostFilterOptions> {
  const qs = new URLSearchParams({ from: filters.from, to: filters.to });
  return getJson<AgentCostFilterOptions>(`/filters?${qs}`, 'the available filters', signal);
}

/**
 * Fetches every row matching the current filters, by walking the pages server-side.
 *
 * Exists because exporting only the visible page is close to useless for a chargeback or showback
 * conversation - the whole point of the granular grid is the filtered set, not 50 arbitrary rows of it.
 * Capped, because this runs in the browser and the point is an export an admin can open, not a bulk
 * extract; the caller is told when the cap truncated the result so it can say so rather than quietly
 * hand over a partial file.
 */
export async function fetchAllDetailRows(
  filters: AgentCostFilters,
  sort: string,
  direction: 'asc' | 'desc',
  maxRows = 10000,
  signal?: AbortSignal,
): Promise<{ rows: AgentCostDetailRow[]; truncated: boolean; totalRows: number }> {
  const pageSize = 500; // Matches SqlAgentCostReportStore.MaxPageSize.
  const rows: AgentCostDetailRow[] = [];
  let page = 1;
  let totalRows = 0;

  for (;;) {
    const result = await fetchDetail({ ...filters, page, pageSize, sort, direction }, signal);
    totalRows = result.totalRows;
    rows.push(...result.rows);

    if (result.rows.length < pageSize) break;
    if (rows.length >= maxRows) break;
    if (rows.length >= totalRows) break;
    page += 1;
  }

  return { rows: rows.slice(0, maxRows), truncated: totalRows > maxRows, totalRows };
}
