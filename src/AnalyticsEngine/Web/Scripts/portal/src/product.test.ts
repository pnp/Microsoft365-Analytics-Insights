import { describe, it, expect, afterEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { PRODUCT_NAME, REPOSITORY_URL, buildLabel, printedBuildText } from './product';

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

/**
 * What the footer's parenthesis says - "Microsoft 365 Advanced Analytics (build 1836)".
 *
 * Never empty, which is the point. The footer used to drop the segment for an unstamped build, so
 * a report printed from a developer's machine named no build at all and read exactly like one from
 * a release - the case where knowing the build matters most.
 */
describe('printed build text', () => {
  afterEach(() => {
    delete (window as Partial<Window>).o365AnalyticsBuildLabel;
  });

  it('lower-cases the label the pipeline stamps so it reads inside the parenthesis', () => {
    // ci.yml stamps `BuildLabel: Build <number>`, so this is the shape that actually ships.
    window.o365AnalyticsBuildLabel = 'Build 1841';
    expect(printedBuildText()).toBe('build 1841');
  });

  it('keeps the word "build" in front of a label that arrives as a bare number', () => {
    // A bare "1841" in a parenthesis is a number with nothing saying what it counts.
    window.o365AnalyticsBuildLabel = '1841';
    expect(printedBuildText()).toBe('build 1841');
  });

  it('says so, out loud, when the pipeline never stamped a build', () => {
    for (const notARelease of ['DEV_BUILD', '__BuildLabel__', '', '   ', 'Build']) {
      window.o365AnalyticsBuildLabel = notARelease;
      expect(printedBuildText(), `"${notARelease}" has no build number`).toBe('development build');
    }

    delete (window as Partial<Window>).o365AnalyticsBuildLabel;
    expect(printedBuildText()).toBe('development build');
  });

  it('never prints a label that is not a release as though it were a version', () => {
    for (const notARelease of ['DEV_BUILD', '__BuildLabel__']) {
      window.o365AnalyticsBuildLabel = notARelease;
      expect(printedBuildText()).not.toContain(notARelease);
    }
  });
});
