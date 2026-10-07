# AITracker.js

The SharePoint page tracker: page views, time on page, link clicks, searches and page metadata, sent to Application Insights for the importer to load into SQL.

It runs on its own on classic SharePoint pages, loaded by a `ScriptLink` custom action. On modern pages the SPFx extension in [`../../ModernPagesAITrackerExtension`](../../ModernPagesAITrackerExtension) bootstraps it. How the two fit together, and the rules the tracker has to keep, are in [`src/SPO/.github/copilot-instructions.md`](../../.github/copilot-instructions.md).

```
npm ci
npx jest              # unit tests
npm run build:prod    # writes ../aitracker.js
```

Bump `AI_TRACKER_VER` in `src/AiTrackerConstants.ts` with every change. It is logged in the browser console on every tracked page, and sent with every page view.
