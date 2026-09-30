import { describe, expect, it } from 'vitest';
import { loadCatalog, translateStatic } from '../../i18n';
import { teamAuthErrorText } from './TeamList';

describe('teamAuthErrorText', () => {
  it('translates the known storage prerequisite error and falls back for unknown server text', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    expect(teamAuthErrorText(es, "Teams deep analytics can't be enabled because Azure Storage is not configured for this deployment. Add a Storage connection string so Teams authorisation tokens can be stored."))
      .toContain('Azure Storage no está configurado');
    expect(teamAuthErrorText(es, 'A future Teams authorisation error.')).toBe('A future Teams authorisation error.');
  });
});
