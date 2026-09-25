import { describe, expect, it } from 'vitest';
import { loadCatalog, translateStatic } from '../../i18n';
import type { UserDataDetailRow } from '../../types/userData';
import { detailText } from './CategoryRow';

describe('user lookup detail text', () => {
  it('formats server-supplied usage detail dates in the active language', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    const row: UserDataDetailRow = {
      timestamp: '2026-09-25T00:00:00Z',
      title: 'Activity report day',
      detail: 'Last activity 9/25/2026',
      detailKey: 'usage.lastActivity',
      detailDateUtc: '2026-09-25T00:00:00Z',
    };

    const translated = detailText(es, 'usage-office', row);
    expect(translated).toContain('Última actividad');
    expect(translated).not.toContain('Last activity');
    expect(translated).not.toContain('9/25/2026');
  });
});
