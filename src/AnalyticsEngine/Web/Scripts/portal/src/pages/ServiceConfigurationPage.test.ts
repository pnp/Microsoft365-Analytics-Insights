import { describe, expect, it } from 'vitest';
import { loadCatalog, translateStatic } from '../i18n';
import { webhookStatusDetail } from './ServiceConfigurationPage';

describe('webhookStatusDetail', () => {
  it('translates the known missing WebAppURL detail and falls back for unknown server text', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    expect(webhookStatusDetail(es, "WebAppURL is not configured, so the webhook subscription URL can't be determined."))
      .toBe('WebAppURL no está configurado, por lo que no se puede determinar la dirección URL de suscripción del webhook.');
    expect(webhookStatusDetail(es, 'A future webhook failure.')).toBe('A future webhook failure.');
  });
});
