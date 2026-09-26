import { afterEach, describe, expect, it } from 'vitest';
import { loadCatalog, setActiveLanguage, translateStatic } from '../../i18n';
import type { UserDataDetailRow } from '../../types/userData';
import { detailText } from './CategoryRow';

describe('user lookup detail text', () => {
  afterEach(() => setActiveLanguage('en'));

  it('formats server-supplied usage detail dates in the active language', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    // The server marks this date UTC (SqlUserDataLookupQuery.AsUtc), so it arrives with a 'Z'. The
    // portal formats it in UTC, so the calendar day is the one stored, whatever the reader's time zone.
    // UserDataLookupSqlIntegrationTests.UsageDrillDown_LastActivityDate_IsSentAsUtcWithItsKey pins the
    // server half: unmarked, the browser would read it as local midnight and show the day before.
    const row: UserDataDetailRow = {
      timestamp: '2026-09-25T00:00:00Z',
      title: 'Activity report day',
      detail: 'Last activity 9/25/2026',
      detailKey: 'usage.lastActivity',
      detailDateUtc: '2026-09-25T00:00:00Z',
    };

    expect(detailText(es, 'usage-office', row)).toBe('Última actividad 25/9/26');
  });

  it('falls back to the server text when the date fact is missing', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    const row: UserDataDetailRow = {
      timestamp: '2026-09-25T00:00:00Z',
      title: 'Activity report day',
      detail: 'Last activity 9/25/2026',
      detailKey: 'usage.lastActivity',
      detailDateUtc: null,
    };

    expect(detailText(es, 'usage-office', row)).toBe('Last activity 9/25/2026');
  });
});
