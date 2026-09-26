/**
 * Who this report came from.
 *
 * Printed in the footer of every printed page: a report that leaves the screen is read by people
 * who were not there when it was produced, gets forwarded, and turns up months later in a licence
 * discussion. Without the product name, the repository and the build it was produced by, there is
 * no way to tell what generated the numbers or whether a figure that now looks wrong came from a
 * version that has since been fixed.
 */

import { translateActive, type TFunction } from './i18n';

export const PRODUCT_NAME = 'Microsoft 365 Advanced Analytics';

export const REPOSITORY_URL = 'https://github.com/pnp/Microsoft365-Analytics-Insights';

/**
 * Values that mean "this is not a released build", and so must not be printed as a version.
 *
 * `DEV_BUILD` is the compiled-in default of `Common.Entities.BuildConstants.BuildLabel`, which the
 * release pipeline rewrites; the placeholder survives when index.html is served by `npm run dev`
 * rather than by HomeController, which never substitutes it. Printing either one as though it were
 * a version is worse than printing nothing: it reads as a real label and cannot be looked up.
 */
const NOT_A_RELEASE = ['DEV_BUILD', '__BuildLabel__'];

/**
 * The running build's label (for example "Build 1841"), or null when this is not a released build.
 *
 * Read from the global that HomeController stamps into index.html, rather than from an API: the
 * footer has to exist before `window.print()` is called, and the endpoint that carries this
 * elsewhere (api/SystemStatus) COUNT(*)s whole tables.
 */
export function buildLabel(): string | null {
  if (typeof window === 'undefined') return null;
  const label = (window.o365AnalyticsBuildLabel ?? '').trim();
  if (!label || NOT_A_RELEASE.includes(label)) return null;
  return label;
}

/** What the footer says in place of a build number when the pipeline never stamped one. */
export const DEVELOPMENT_BUILD_TEXT = 'development build';

/**
 * How the printed footer names the build, for the parenthesis after the product name -
 * "Microsoft 365 Advanced Analytics (build 1836)".
 *
 * Always returns something. The footer used to drop the whole segment for an unstamped build, so a
 * report printed from a developer's machine or an unreleased test deployment named no build at all
 * and read, on paper, exactly like one printed from a release - which is the case where knowing the
 * build matters most. Saying "development build" is the same admission, made out loud.
 *
 * The pipeline stamps "Build 1836" (`BuildLabel` in ci.yml), which is lower-cased here so it reads
 * as part of the line rather than as a second title. A label in any other shape still gets the word
 * "build" in front of it, so the parenthesis never leaves a bare number to be guessed at.
 */
export function printedBuildText(t: TFunction = translateActive): string {
  const label = buildLabel();
  if (!label) return t('app.print.developmentBuild');

  const withoutPrefix = label.replace(/^build\b\s*/i, '').trim();
  return withoutPrefix ? t('app.print.buildLabel', { build: withoutPrefix }) : t('app.print.developmentBuild');
}

/**
 * A build label for the screen, in the portal language.
 *
 * The pipeline stamps "Build 1836" (`BuildLabel` in ci.yml) and titles stable GitHub releases
 * "Stable build 1835"; both reached the Spanish Health, Overview and Service configuration pages in
 * English, beside a printed footer that already said "compilación". Those two shapes are re-worded
 * around their number. Anything else - an unstamped build, a release titled some other way - is shown
 * exactly as it arrived, because guessing at a label is worse than printing it.
 */
export function buildLabelText(t: TFunction, label: string): string;
export function buildLabelText(t: TFunction, label: string | null | undefined): string | null | undefined;
export function buildLabelText(t: TFunction, label: string | null | undefined): string | null | undefined {
  if (!label) return label;
  const stable = /^stable build\s+(\S+)$/i.exec(label.trim());
  if (stable) return t('app.stableBuildLabel', { build: stable[1] });
  const build = /^build\s+(\S+)$/i.exec(label.trim());
  if (build) return t('app.buildLabel', { build: build[1] });
  return label;
}
