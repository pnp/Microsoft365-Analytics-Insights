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

    // Built exactly as UpdateChecker.cs builds them, so these prove the SPA recognises the real sentences.
    const rateLimited = (resetText: string) =>
      'GitHub rejected the request because its API rate limit has been reached, not because of a '
      + `permissions problem. The limit resets at ${resetText}. Anonymous requests are limited to 60 `
      + 'per hour per public IP address, which is shared by everything behind your outbound address. '
      + 'Try again after the reset.';
    const serverSentences = [
      "Timed out after 10s contacting github.com. If this web app has no outbound internet access (for example a private-endpoint deployment with restricted egress), update checks can't work from here - check the release page manually instead.",
      "Couldn't reach github.com to check for updates: DNS failure. This is expected if the web app has no outbound internet access; check the release page manually.",
      'Update check failed: Unexpected character.',
      'This is a locally-compiled build (DEV_BUILD), so it has no build number to compare. The latest published release is shown for reference.',
      "Couldn't read a build number out of this build's label ('1.2-custom'), so it can't be compared. The latest published release is shown for reference.",
      "Couldn't read a build number from the latest GitHub release, so the two can't be compared. Open the release page to check manually.",
      rateLimited('2026-09-26 09:00:00Z'),
      rateLimited('shortly'),
      "GitHub returned 404 for the releases endpoint. If this deployment sits behind a proxy that intercepts HTTPS, it may be returning its own response rather than GitHub's.",
      'GitHub returned 502 (BadGateway) when asked for the latest release.',
    ];

    it('translates every fixed UpdateChecker sentence, keeping its facts', async () => {
      await loadCatalog('es');
      const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
        translateStatic('es', key, values);

      for (const sentence of serverSentences) {
        const translated = updateCheckErrorText(es, sentence);
        expect(translated, sentence).not.toBe(sentence);
        expect(translated, sentence).not.toMatch(/\{\w+\}/);
      }
      expect(updateCheckErrorText(es, rateLimited('2026-09-26 09:00:00Z'))).toContain('se restablece el 2026-09-26 09:00:00Z');
      expect(updateCheckErrorText(es, rateLimited('shortly'))).toContain('se restablecerá en breve');
      expect(updateCheckErrorText(es, rateLimited('shortly'))).not.toContain('shortly');
      expect(updateCheckErrorText(es, serverSentences[4])).toContain("('1.2-custom')");
      expect(updateCheckErrorText(es, serverSentences[9])).toBe('GitHub devolvió 502 (BadGateway) al solicitar la última versión.');
    });

    it('shows English readers exactly the sentence the server wrote', () => {
      const en = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
        translateStatic('en', key, values);
      for (const sentence of serverSentences) {
        expect(updateCheckErrorText(en, sentence)).toBe(sentence);
      }
    });
  });
});
