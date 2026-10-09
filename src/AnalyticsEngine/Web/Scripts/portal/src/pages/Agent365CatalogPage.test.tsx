import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { fetchAgent365Catalog } from '../api/agent365CatalogApi';
import type { Agent365CatalogResponse } from '../types/agent365Catalog';
import { formatDateParts, loadCatalog, translateStatic } from '../i18n';
import Agent365CatalogPage from './Agent365CatalogPage';

vi.mock('../api/agent365CatalogApi', () => ({ fetchAgent365Catalog: vi.fn() }));

const UTC_DATE = '2026-10-01T00:15:00Z';
const response: Agent365CatalogResponse = {
  importEnabled: true,
  lastAttemptUtc: UTC_DATE,
  lastAttemptCompletedUtc: UTC_DATE,
  lastAttemptSucceeded: false,
  lastAttemptError: 'Synthetic import failure',
  lastSuccessfulImportUtc: UTC_DATE,
  totalCount: 3, offset: 0, pageSize: 50, neverUsedOnly: false, packageCount: 3, neverUsedCount: 1,
  packages: [
    {
      packageId: 'contoso-used', displayName: 'Contoso used agent', packageType: null, platform: null,
      publisher: null, manifestId: null, version: null, isBlocked: null,
      lastModifiedUtc: UTC_DATE, lastUsedUtc: UTC_DATE, lastUsedDateTimeProvided: true, knownNeverUsed: false,
      activeUsers: null, totalSessions: null, totalRunTimeHours: null, exceptionRate: null,
      supportedHosts: [], elements: [],
    },
    {
      packageId: 'contoso-never', displayName: 'Contoso never-used agent', packageType: null, platform: null,
      publisher: null, manifestId: null, version: null, isBlocked: null,
      lastModifiedUtc: null, lastUsedUtc: null, lastUsedDateTimeProvided: true, knownNeverUsed: true,
      activeUsers: 0, totalSessions: 0, totalRunTimeHours: 0, exceptionRate: 0, supportedHosts: [], elements: [],
    },
    {
      packageId: 'contoso-unknown', displayName: 'Contoso unknown usage', packageType: null, platform: null,
      publisher: null, manifestId: null, version: null, isBlocked: null,
      lastModifiedUtc: null, lastUsedUtc: null, lastUsedDateTimeProvided: false, knownNeverUsed: false,
      activeUsers: null, totalSessions: null, totalRunTimeHours: null, exceptionRate: null,
      supportedHosts: [], elements: [],
    },
  ],
};

beforeEach(() => {
  vi.stubEnv('TZ', 'Europe/Madrid');
  vi.clearAllMocks();
  vi.mocked(fetchAgent365Catalog).mockResolvedValue(response);
});
afterEach(() => vi.unstubAllEnvs());

describe('Agent 365 UTC catalog dates', () => {
  it.each(['en', 'es'] as const)('keeps UTC calendar dates and null usage distinctions in %s', async (language) => {
    // An offsetless SQL timestamp in Madrid would render the previous UTC day.
    expect(new Date(UTC_DATE).getTimezoneOffset()).toBe(-120);
    expect(new Date(UTC_DATE).toISOString()).toBe(UTC_DATE.replace('Z', '.000Z'));
    expect(new Date('2026-10-01T00:15:00').toISOString()).toBe('2026-09-30T22:15:00.000Z');
    await loadCatalog(language);
    renderWithProvider(<Agent365CatalogPage />, { language });

    expect(await screen.findByText('Contoso used agent')).toBeVisible();
    const expected = formatDateParts(new Date(UTC_DATE), { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' });
    expect(screen.getAllByText(expected)).toHaveLength(3);
    const previousDay = formatDateParts(new Date('2026-09-30T22:15:00Z'), { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' });
    expect(screen.queryByText(previousDay)).not.toBeInTheDocument();
    expect(screen.getByText(translateStatic(language, 'admin.agent365Catalog.usage.neverUsed'))).toBeVisible();
    expect(screen.getAllByText(translateStatic(language, 'admin.agent365Catalog.usage.unknown')).length).toBeGreaterThan(0);
  });
});
