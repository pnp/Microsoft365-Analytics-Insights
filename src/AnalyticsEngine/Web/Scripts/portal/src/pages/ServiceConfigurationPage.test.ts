import { describe, expect, it } from 'vitest';
import { loadCatalog, translateStatic } from '../i18n';
import { updateCheckErrorText, webhookStatusDetail } from './ServiceConfigurationPage';

describe('webhookStatusDetail', () => {
  it('translates the known missing WebAppURL detail and falls back for unknown server text', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    expect(webhookStatusDetail(es, "WebAppURL is not configured, so the webhook subscription URL can't be determined."))
      .toBe('WebAppURL no está configurado, por lo que no se puede determinar la dirección URL de suscripción del webhook.');
    expect(webhookStatusDetail(es, 'A future webhook failure.')).toBe('A future webhook failure.');
  });

  describe('updateCheckErrorText', () => {
    it('translates known server-authored update-check failures and falls back for unknown text', async () => {
      await loadCatalog('es');
      const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
        translateStatic('es', key, values);

      expect(updateCheckErrorText(
        es,
        "Timed out after 10s contacting github.com. If this web app has no outbound internet access (for example a private-endpoint deployment with restricted egress), update checks can't work from here - check the release page manually instead.",
      )).toContain('Se agotó el tiempo de espera tras 10 s');
      expect(updateCheckErrorText(es, "Couldn't reach github.com to check for updates: DNS failure. This is expected if the web app has no outbound internet access; check the release page manually."))
        .toContain('No se pudo contactar con github.com');
      expect(updateCheckErrorText(es, 'A future update-check error.')).toBe('A future update-check error.');
    });
  });
});
