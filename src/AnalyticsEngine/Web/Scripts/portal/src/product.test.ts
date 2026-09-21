import { describe, it, expect, afterEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { PRODUCT_NAME, REPOSITORY_URL, buildLabel } from './product';

const PORTAL_DIR = process.cwd();

/**
 * The build label the printed footer names.
 *
 * It travels a long way for one short string - `BuildConstants.BuildLabel` in C#, substituted by
 * HomeController into a global in index.html, read here - and every hop is somewhere it can go
 * missing without anything failing. These tests pin both ends of that chain.
 */
describe('build label', () => {
  afterEach(() => {
    delete (window as Partial<Window>).o365AnalyticsBuildLabel;
  });

  it('reports a released build', () => {
    window.o365AnalyticsBuildLabel = 'Build 1841';
    expect(buildLabel()).toBe('Build 1841');
  });

  it('reports nothing for a build that was never stamped', () => {
    // DEV_BUILD is the compiled-in default the release pipeline rewrites; the placeholder survives
    // when `npm run dev` serves index.html instead of HomeController. Printing either as a version
    // is worse than printing none: it reads as a real label and cannot be looked up.
    for (const notARelease of ['DEV_BUILD', '__BuildLabel__', '', '   ']) {
      window.o365AnalyticsBuildLabel = notARelease;
      expect(buildLabel(), `"${notARelease}" is not a version`).toBeNull();
    }

    delete (window as Partial<Window>).o365AnalyticsBuildLabel;
    expect(buildLabel()).toBeNull();
  });

  it('is asked for by the name index.html actually sets', () => {
    // The two sides are a plain global with no type or build step joining them up, so a rename on
    // either side would leave every printed report unversioned and nothing would fail.
    const html = readFileSync(join(PORTAL_DIR, 'index.html'), 'utf8');
    expect(html).toContain('window.o365AnalyticsBuildLabel');
    expect(html).toContain('"__BuildLabel__"');
  });

  it('uses the placeholder the server substitutes', () => {
    // HomeController.InjectBuildLabel replaces this exact token when it serves index.html. If the
    // two ever disagree the page still loads, still prints, and silently says nothing about which
    // build produced it.
    const controller = readFileSync(
      join(PORTAL_DIR, '..', '..', 'Controllers', 'HomeController.cs'),
      'utf8',
    );
    expect(controller).toContain('BuildLabelPlaceholder = "__BuildLabel__"');
    expect(controller).toContain('InjectBuildLabel(');
  });
});

describe('product identity', () => {
  it('names the product and its repository', () => {
    expect(PRODUCT_NAME).toBe('Microsoft 365 Advanced Analytics');
    expect(REPOSITORY_URL).toBe('https://github.com/pnp/Microsoft365-Analytics-Insights');
  });
});
