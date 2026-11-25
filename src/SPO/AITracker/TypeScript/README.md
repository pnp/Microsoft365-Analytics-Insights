# AITracker.js

The SharePoint page tracker: page views, time on page, link clicks, searches and page metadata, sent to Application Insights for the importer to load into SQL.

It runs on its own on classic SharePoint pages, loaded by a `ScriptLink` custom action. On modern pages the SPFx extension in [`../../ModernPagesAITrackerExtension`](../../ModernPagesAITrackerExtension) bootstraps it. How the two fit together, and the rules the tracker has to keep, are in [`src/SPO/.github/copilot-instructions.md`](../../.github/copilot-instructions.md).

Registering the root collection does not enable tracking in `/sites/<collection>` or `/teams/<collection>`. Each needs its own registration. Only the original document visit owns its DOM title and load timing. SPFx 1.0.1.68 remembers if the browser left that visit while `AITracker.js` was downloading. Returning to the original URL is then a soft navigation, not a second hard load. A detour neither component observed cannot be inferred from URL equality.

Every modern soft view uses the destination pathname as its name and omits the unverified DOM title. The tracker takes web/list details from a request-bound SPFx snapshot, never from the callback's positional arguments. A fresh navigation event without a request path can establish the visit and collection, but not a list metadata lookup. A contradictory snapshot leaves the visit unreported and its clicks pending for a correct callback; it does not end the preceding request. An older extension can still report the URL and collection, but without the new binding evidence the tracker omits web/list fields. If no eligible report arrives before an unconfirmed visit ends, it is not recorded.

```
npm ci
npx jest              # unit tests
npm run build:prod    # writes ../aitracker.js
```

Bump `AI_TRACKER_VER` in `src/AiTrackerConstants.ts` with every change. It is logged in the browser console on every tracked page, and sent with every page view.

`TrackerTelemetry.ts` uses the Application Insights core, analytics, properties and sender packages. Update the four together. The full web SDK also bundles dependency collection and remote configuration, neither of which the tracker uses. The production build fails above 190 KiB of minified JavaScript; compare the actual bundle and compressed sizes before raising that limit. The tracker must still load as one site-hosted script on classic and modern pages.

Check the serialized envelope when updating the SDK. A tracked page view must not carry the browser's referrer, and a saved event must not acquire its operation name from an untracked site. Pass a measured hard load to both the importer's `pageLoad` field and the SDK's `duration` so a short visit's view enters the sender before its clicks. Leave the SDK duration unset when hard-load timing is unavailable. A navigation without a document load uses the time the tracker saw the page, with explicit zero duration and no document performance breakdown.

On `pagehide`, hand off the accepted visit's cumulative active time **before** flushing the SDK sender. A different tab's exit cookie may need to be sent before this tab replaces it; an already hidden tab can close without another visibility change. The activity clock starts when the document is visible and stops after 30 seconds without input. TimeMe checks periodically: it includes the grace period but does not charge the time between stopping and the user's return.

For a browser-level check on Windows with Edge and Python Playwright (`connect_over_cdp` with `no_defaults` support), run `python tests\tracker_lifecycle_edge.py` after `npm run build:prod`. The fixture launches a fresh headless profile, closes foreground and already-hidden tabs, checks a native back/forward-cache restore, and reads the real SDK uploads at a loopback receiver. The short-visit test budgets 50 ms of renderer time. Set `TRACKER_IDLE_WALL_CLOCK=1` to also observe a full 180 seconds of inactivity without advancing timers, or set `TRACKER_TEST_BUNDLE` to test a previous local build. No tenant, existing profile or cloud endpoint is used.
