import { describe, expect, it } from 'vitest';

import {
  BLOB_CHECKPOINT_REASON_KEYS,
  formatHealthDuration,
  translateHealthComponentDetail,
  translateHealthComponentDetailText,
  translateHealthComponentName,
  translateHealthReasonText,
} from './healthShared';
import { loadCatalog, translateActive, translateStatic } from '../../i18n';

describe('translateHealthReasonText', () => {
  const en = (key: Parameters<typeof translateActive>[0], values?: Parameters<typeof translateActive>[1]) => translateStatic('en', key, values);
  const es = (key: Parameters<typeof translateActive>[0], values?: Parameters<typeof translateActive>[1]) => translateStatic('es', key, values);

  const rollupReasons = [
    'Database schema is behind this build (2 migration(s) pending) - run the upgrader.',
    'No completed import cycle seen for Office365ActivityImporter.',
    "Office365ActivityImporter hasn't completed a cycle in 50h (SLA 24h).",
    'Office365ActivityImporter last completed a cycle 30h ago (SLA 24h).',
    '3 SQL capacity / read-only exception(s) in the last 24h - check database storage.',
    "Teams calls webhook subscription is 'Missing'.",
    "Teams calls webhook subscription is 'Error'.",
    "ServiceBus is degraded: Teams calls queue 'callrecords': 12 active, 3 dead-lettered.",
  ];

  it('renders the server\'s own sentence, unchanged, in English', () => {
    for (const reason of rollupReasons) {
      expect(translateHealthReasonText(reason, en)).toBe(reason);
    }
  });

  it('translates the roll-up sentences into Spanish, keeping the job name and the figures', async () => {
    await loadCatalog('es');

    expect(translateHealthReasonText(rollupReasons[0], es)).toBe(
      'El esquema de la base de datos va por detrás de esta compilación (2 migración(es) pendiente(s)): ejecute el actualizador.',
    );
    expect(translateHealthReasonText(rollupReasons[2], es)).toBe('Office365ActivityImporter no ha completado ningún ciclo en 50 h (SLA 24 h).');
    expect(translateHealthReasonText(rollupReasons[7], es)).toBe(
      "El componente Service Bus está degradado: Cola de llamadas de Teams 'callrecords': mensajes activos: 12; mensajes fallidos: 3.",
    );
    // Count-invariant on purpose: "{active} activos" read "1 activos" for a single message.
    expect(translateHealthReasonText("ServiceBus is degraded: Teams calls queue 'callrecords': 1 active, 1 dead-lettered.", es)).toBe(
      "El componente Service Bus está degradado: Cola de llamadas de Teams 'callrecords': mensajes activos: 1; mensajes fallidos: 1.",
    );
  });

  it('keeps a sentence it does not recognise exactly as the server wrote it', async () => {
    await loadCatalog('es');
    const unknown = 'A sentence a newer server wrote that this portal build does not know.';

    expect(translateHealthReasonText(unknown, es)).toBe(unknown);
    expect(translateHealthComponentDetailText(unknown, es)).toBe(unknown);
    expect(translateHealthComponentDetailText('Azure Table checkpoint unavailable: a new failure. Using non-durable in-memory checkpoint (lost on restart; durable cross-cycle metadata recovery unavailable). See importer error log.', es))
      .toContain('a new failure');
  });

  describe('health server facts rendered by the portal', () => {
    it('translates component display names by key and leaves unknown components alone', async () => {
      await loadCatalog('es');

      expect(translateHealthComponentName('Credential', (key, values) => translateStatic('es', key, values))).toBe('Credencial');
      expect(translateHealthComponentName('BlobCheckpoint', (key, values) => translateStatic('es', key, values))).toBe('Punto de control de blobs');
      expect(translateHealthComponentName('ContosoConnector', (key, values) => translateStatic('es', key, values))).toBe('ContosoConnector');
    });

    it('formats numeric liveness durations in the active language instead of showing server English', async () => {
      await loadCatalog('es');

      const es = (key: Parameters<typeof translateActive>[0], values?: Parameters<typeof translateActive>[1]) => translateStatic('es', key, values);
      expect(formatHealthDuration(90_061, 'Audit events import: 1 days, 1 hours, 1 mins, and 1 seconds.', es))
        .toBe('1 día, 1 hora, 1 minuto, 1 segundo.');
      expect(formatHealthDuration(3_662, 'Audit events import: 1 hours, 1 mins, and 2 seconds.', es))
        .toBe('1 hora, 1 minuto, 2 segundos.');
      expect(formatHealthDuration(null, 'Audit events import: 1 hours, 1 mins, and 2 seconds.', es))
        .toBe('Audit events import: 1 hours, 1 mins, and 2 seconds.');
    });
  });
});

describe('translateHealthComponentDetailText', () => {
  it('translates the blob checkpoint storage-firewall detail', () => {
    const detail = "Azure Table checkpoint unavailable: Storage firewall/network rules rejected the Table checkpoint request (HTTP 403 AuthorizationFailure). On a public install, set the storage account to 'Enabled from all networks'; IP allow-list rules do not apply to requests from an App Service in the same Azure region as the storage account. Anything stricter needs App Service VNet integration plus a Microsoft.Storage service endpoint, or the private-endpoint deployment. Using non-durable in-memory checkpoint (lost on restart; durable cross-cycle metadata recovery unavailable). See importer error log.";

    const translated = translateHealthComponentDetailText(detail, translateActive);

    expect(translated).toContain('HTTP 403 AuthorizationFailure');
    expect(translated).toContain('same Azure region');
    expect(translated).toContain('non-durable in-memory checkpoint');
  });

  it('translates the blob checkpoint storage-firewall reason key', () => {
    const translated = translateHealthComponentDetail({
      component: 'BlobCheckpoint',
      status: 'Degraded',
      detail: 'fallback English',
      reasonKey: 'blobCheckpoint.storageFirewall',
      errorCode: 'AuthorizationFailure',
      httpStatus: 403,
      daysToExpiry: null,
      lastSeenUtc: null,
    }, translateActive);

    expect(translated).toContain('HTTP 403 AuthorizationFailure');
    expect(translated).toContain('same Azure region');
    expect(translated).not.toContain('fallback English');
  });

  it('translates every blob checkpoint reason key in Spanish instead of falling back to server English', async () => {
    await loadCatalog('es');

    for (const reasonKey of Object.keys(BLOB_CHECKPOINT_REASON_KEYS)) {
      const translated = translateHealthComponentDetail({
        component: 'BlobCheckpoint',
        status: reasonKey === 'blobCheckpoint.healthy' ? 'Healthy' : 'Degraded',
        detail: 'server English fallback',
        reasonKey,
        errorCode: 'AuthenticationFailed',
        httpStatus: 403,
        daysToExpiry: null,
        lastSeenUtc: null,
      }, (key, values) => translateStatic('es', key, values));

      expect(translated, reasonKey).not.toContain('server English fallback');
      expect(translated, reasonKey).not.toContain('Table checkpoint request');
      expect(translated, reasonKey).not.toContain('Storage connection string');
    }
  });
});
