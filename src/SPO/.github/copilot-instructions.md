# Copilot Instructions — SharePoint web components (`src/SPO`)

Read the repository-wide [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md) first: sensitive-data handling, git workflow and pull requests apply here too.

## Two parts, one tracker: classic and modern pages

SharePoint Online has two kinds of page and the tracker must work on both. So all the tracking logic lives in one script, and SPFx only bootstraps it on modern pages. **Keep this dual mode**: don't move tracking logic into the SPFx extension, and don't make `AITracker.js` depend on SPFx.

| Part | What it does |
|---|---|
| `AITracker/TypeScript` → `AITracker.js` | All the tracking: page views, time on page (`PAGE_EXIT`), link clicks, searches, page metadata. Must run **standalone on classic pages**, which have no SPFx. There a `ScriptLink` user custom action loads it (`ClassicPageCustomAction` in the installer; the site needs custom scripts allowed, `DenyAddAndCustomizePages 0`). Its `ScriptBlock` sets `window.appInsightsConnectionStringHash` / `window.insightsWebRootUrlHash` and appends the script. Classic pages only ever do full page loads, and define `_spPageContextInfo` themselves. |
| `ModernPagesAITrackerExtension` | An SPFx application customizer that only **bootstraps** `AITracker.js` on modern pages. It sets the same window globals from its properties and loads `<site collection>/SPOInsights/AITracker.js?ver=<cacheToken>`. It keeps `window._spPageContextInfo` current from `legacyPageContext`, and forwards SharePoint's page navigations (no page reload) to the script's `window.modernPageNav(...)`. |

## How it is deployed — the intended design

- The `.sppkg` is deployed **tenant-wide** (`skipFeatureDeployment: true`) only to make the extension *available*. The installer *enables* it per site collection by stamping site custom actions that carry its properties (`appInsightsConnectionStringHash`, `insightsWebRootUrlHash`, `cacheToken`). See `SiteAITrackerInstaller` / `SpoSiteInstallAdaptor`. Each tracked site collection also gets its own copy of `AITracker.js` in its `SPOInsights` library.
- **Don't add a `ClientSideInstance.xml` to the package.** With `skipFeatureDeployment` it creates an enabled Tenant Wide Extensions entry with no properties, which runs the extension on every site in the tenant. Packages before 1.0.1.59 did, and deploying a newer package doesn't remove the entry. The installer removes it after deploying (`TenantAppCatalogManager.RemoveUnconfiguredTenantWideExtensionsAsync`, which deletes this component's entries only if they have no properties). Until it has, the extension must ignore an instance without properties, and must not register the site for it (`_o365AnalyticsInfo.siteUrls`), or the configured instance takes itself for a duplicate.
- `AITracker.js` is served with `Cache-Control: public, max-age=86400`. Only a new `?ver=<cacheToken>` makes browsers fetch an upload sooner than a day later. The installer sets a new token on every run. Uploading the file by hand means updating `cacheToken` in the site custom actions too.

## Rules for `AITracker.js`

- **One tracker per page.** SharePoint navigates between site collections without reloading the page, and the next site's extension then loads *that* site's copy into the same page. The first copy to start sets `window.spoInsightsAITrackerVersion`. A later copy must do nothing: no listeners, no second App Insights instance (it would replay the first one's session-storage send buffer as duplicates). Navigations reach the running copy through `window.modernPageNav`.
- **It may load after the page has loaded**: the SPFx extension started late, or SharePoint navigated here from an untracked site. Don't wait for a `load` event that has already fired. Publish `window.modernPageNav` only once the current page view is tracked.
- **Every event carries the `pageRequestId` of the page it describes.** The importer joins on it (`hits.page_request_id`), and `PAGE_EXIT` overwrites that hit's `seconds_on_page`. Time saved for the previous page must go out with the previous page's ID, not the new one's.
- **Per-page data uses the page's own web URL**, passed with each page view, never one captured when the script started. After a navigation the script is running in another site's page.
- **Don't assign `window.onbeforeunload`** or any other `on*` handler property. SharePoint assigns them too, and whichever is set last silently replaces the other. Use `addEventListener` (`pagehide` to save state as the browser leaves).
- **Keep the bundle lean.** It is downloaded and parsed on every tracked page load. Check the `npm run build:prod` output size when adding a dependency: `moment` with every locale was once two thirds of it.

## Bump the version with every change

Both parts log their version in the browser console, which is how you tell what a page is actually running:

- **`AI_TRACKER_VER`** in `AITracker/TypeScript/src/AiTrackerConstants.ts`. It is also sent with every page view and `PAGE_EXIT`.
- **The SPFx solution**: `version` in `ModernPagesAITrackerExtension/config/package-solution.json` **and** `AITRACKER_MODERN_VERSION` in `AiTrackerModernApplicationCustomizer.ts`. Keep the two identical.

## Building and testing

- **AITracker**: Node 24 (the repository `.nvmrc`). In `AITracker/TypeScript`, run `npm ci`, `npx jest`, and `npm run build:prod`, which writes `../aitracker.js`. CI builds it into `AITrackerInstaller.zip`. `tests/aitracker.test.ts` loads fresh copies of the bootstrap with `jest.isolateModules`, to cover the classic, late-load and second-copy paths.
- **SPFx extension**: Node 22 (its own `.nvmrc`; SPFx doesn't support Node 24). Run `npm ci`, then `npm run build`, which writes `solution/spoinsights-modern-ui-aitracker.sppkg`. **CI does not build it.** Copy the package to `AITracker/spoinsights-modern-ui-aitracker.sppkg` and commit it, or the release ships the old one. On a machine other work shares, don't switch the global Node: put a portable Node 22 on that one process's `PATH`.
