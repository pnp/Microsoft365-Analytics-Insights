import { translateActive } from '../i18n/runtime';
import { apiFetch } from './http';
import type { Agent365CatalogResponse } from '../types/agent365Catalog';

const BASE = `${window.location.origin}/api/Agent365Catalog`;

export async function fetchAgent365Catalog(
  offset: number,
  pageSize: number,
  neverUsedOnly: boolean,
): Promise<Agent365CatalogResponse> {
  const query = new URLSearchParams({
    offset: String(offset),
    pageSize: String(pageSize),
    neverUsedOnly: String(neverUsedOnly),
  });
  const response = await apiFetch(`${BASE}?${query}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw new Error(translateActive('admin.agent365Catalog.error.load'));
  }
  return response.json() as Promise<Agent365CatalogResponse>;
}
