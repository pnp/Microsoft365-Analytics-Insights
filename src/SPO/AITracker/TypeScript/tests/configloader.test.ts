/**
 * @jest-environment jsdom
 */

import { TestConfigLoader } from './MockLoaders';
import { AITrackerConfig } from '../src/Models';
import { ConfigHandler } from '../src/Config/ConfigHandler';

describe('Config load tests', () => {
  test('ConfigHandler with TestConfigLoader', async () => {
    const m = new ConfigHandler(new TestConfigLoader());

    m.clearConfigCache();
    expect(m.haveValidCachedConfig()).toBeFalsy();

    // Cache a default config
    m.setConfigCache(AITrackerConfig.GetDefault());
    expect(m.haveValidCachedConfig()).toBeTruthy();

    // Clear again
    m.clearConfigCache();
    expect(m.haveValidCachedConfig()).toBeFalsy();

    const config = await m.getConfigFromCacheOrAppService();
    expect(config).toBeDefined();

    // Load from cache and check it's valid
    m.setConfigCache(AITrackerConfig.GetDefault());
    expect(m.haveValidCachedConfig()).toBeTruthy();
  });
});
