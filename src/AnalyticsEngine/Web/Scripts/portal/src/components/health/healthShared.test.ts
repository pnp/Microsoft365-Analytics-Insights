import { describe, expect, it } from 'vitest';

import { BLOB_CHECKPOINT_REASON_KEYS, translateHealthComponentDetail, translateHealthComponentDetailText } from './healthShared';
import { loadCatalog, translateActive, translateStatic } from '../../i18n';

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
