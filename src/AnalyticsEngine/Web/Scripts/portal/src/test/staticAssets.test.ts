import { describe, expect, it } from 'vitest';
import config from '../../vite.config';

describe('ASP.NET Core static asset hosting', () => {
  it('emits site-root URLs for assets copied into wwwroot', () => {
    expect(config.base).toBe('/');
  });

  it('retains the output directory consumed by the MSBuild copy target', () => {
    expect(config.build?.outDir).toBe('build');
  });
});
