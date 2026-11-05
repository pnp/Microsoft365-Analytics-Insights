# SharePoint tracker: modern-page extension (SPFx)

This SharePoint Framework (SPFx 1.22.2) application customizer loads the SharePoint tracker, `AITracker.js`, on **modern** pages. It doesn't track anything itself. All the tracking logic lives in [`../AITracker/TypeScript`](../AITracker/TypeScript), which also runs on its own on classic pages, where there's no SPFx. The extension:

- sets the tracker's settings from its properties (`appInsightsConnectionStringHash`, `insightsWebRootUrlHash`);
- loads the site collection's own copy, `<site collection>/SPOInsights/AITracker.js?ver=<cacheToken>`, unless a tracker is already running in the page;
- passes SharePoint's in-page navigations to the running tracker.

Read the [SharePoint instructions](../.github/copilot-instructions.md) before changing either part. They explain how the two parts work together, how the package is deployed, the rules the tracker must keep, and how to validate a change on a development tenant.

## How it's deployed

The installer deploys the package to the tenant app catalog, tenant-wide, which only makes the extension available. It then enables tracking site collection by site collection, by adding site custom actions that carry the extension's properties, and uploads each site's copy of `AITracker.js`. See [How it is deployed](../.github/copilot-instructions.md#how-it-is-deployed--the-intended-design).

## Build

SPFx doesn't support the repository's Node.js major (26), so this folder has its own `.nvmrc`: **Node 22**. Its `.npmrc` sets `engine-strict`, so `npm ci` refuses to run on any other major.

```powershell
cd src\SPO\ModernPagesAITrackerExtension
nvm use          # Node 22, from this folder's .nvmrc
npm ci
npm run build    # runs the jest tests, then packages the solution
```

`npm run build` writes the package to `sharepoint/solution/spoinsights-modern-ui-aitracker.sppkg`. Heft is a development dependency, so nothing needs installing globally. On a machine that other work shares, don't switch the global Node: put a portable Node 22 on this one process's `PATH` instead.

## Ship a change

**CI doesn't build this project.** The release zips the package committed at `src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg` into `AITrackerInstaller.zip`, and the installer deploys it as it is. So every change to the extension follows these steps, in this order:

1. **Bump the version, before you build.** Set `version` in `config/package-solution.json` and `AITRACKER_MODERN_VERSION` in `src/extensions/aiTrackerModern/AiTrackerModernApplicationCustomizer.ts` to the same new value. The extension logs it in the browser console, which is how anyone tells which version a page is running.
2. **Build** with `npm ci` and `npm run build`, as above.
3. **Copy** `sharepoint/solution/spoinsights-modern-ui-aitracker.sppkg` over `src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg`.
4. **Check** from the repository root:

   ```powershell
   ./.github/scripts/Test-SpfxPackage.ps1
   ```

   It fails unless `package-solution.json`, `AITRACKER_MODERN_VERSION`, the committed package's `AppManifest.xml` and the extension bundle inside that package all carry the same version.
5. **Commit** the source change, both version stamps and the rebuilt package together.

The required `test_aitracker` check runs the same script whenever the extension, the package or the script changes. It can only tell that the package was rebuilt when the version changed: a change committed without a version bump passes CI, and the release ships the old package.

A change to `AITracker.js` is separate: it bumps `AI_TRACKER_VER` in `../AITracker/TypeScript/src/AiTrackerConstants.ts`, and CI builds it.

## Test it

`npm run build` runs the extension's jest tests (`src/**/*.test.ts`). `npm start` serves the extension from your machine for SharePoint's debug mode, but the debug configurations in `config/serve.json` carry no properties, so the extension doesn't track anything until you add them locally. Don't commit them.

Unit tests can't see how SharePoint navigates, so validate every change on a development tenant, as [Validate on a dev tenant with Playwright](../.github/copilot-instructions.md#validate-on-a-dev-tenant-with-playwright) describes.
