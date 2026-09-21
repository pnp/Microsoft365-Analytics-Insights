/**
 * Who this report came from.
 *
 * Printed in the footer of every printed page: a report that leaves the screen is read by people
 * who were not there when it was produced, gets forwarded, and turns up months later in a licence
 * discussion. Without the product name, the repository and the build it was produced by, there is
 * no way to tell what generated the numbers or whether a figure that now looks wrong came from a
 * version that has since been fixed.
 */

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
