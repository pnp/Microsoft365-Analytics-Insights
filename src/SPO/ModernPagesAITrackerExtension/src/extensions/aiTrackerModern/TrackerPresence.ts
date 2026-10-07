// AITracker.js copies set the first two on the window as they start (1.6.0 and later) or once they have tracked a page (all
// versions). The third is this extension's own: the load of a copy in progress, shared by its instances in the page.
interface WindowWithTracker {
  spoInsightsAITrackerVersion?: string;
  modernPageNav?: unknown;
  spoInsightsAITrackerLoading?: Promise<void>;
}

/**
 * The version of the AITracker.js copy already tracking this page, if there is one. 1.6.0 and later record their version
 * when they start; earlier versions only show themselves once they have tracked a page, by publishing window.modernPageNav.
 */
export function trackerAlreadyRunning(w: Window): string | undefined {
  const tracker = w as unknown as WindowWithTracker;
  if (tracker.spoInsightsAITrackerVersion) {
    return tracker.spoInsightsAITrackerVersion;
  }
  if (typeof tracker.modernPageNav === "function") {
    return "earlier than 1.6.0";
  }
  return undefined;
}

/**
 * Loads this site's copy of AITracker.js with `load`, unless a copy is already tracking the page. Returns the version of the
 * copy found running, or undefined if this site's copy was loaded. Rejects if `load` does.
 *
 * One copy loads at a time. SharePoint can navigate to another site collection while the first site's copy is still on its
 * way, and the next site's instance of this extension waits for it rather than loading its own copy alongside: a copy
 * before 1.6.0 doesn't check for another, so it would start a second tracker. Such a copy only shows itself once it has
 * tracked the page, just after the page's load event. So when a copy shows nothing while the page is still loading, the
 * next instance waits until then; if it still shows nothing, it couldn't start, and the next instance loads its own.
 *
 * The wait has no time limit of its own. SharePoint's loader gives up on a script after 90 seconds (RequireJS waitSeconds),
 * which ends it; a shorter limit here would load the next copy while the first could still arrive.
 */
export async function loadTrackerUnlessRunning(w: Window, load: () => Promise<unknown>): Promise<string | undefined> {
  const page = w as unknown as WindowWithTracker;
  while (page.spoInsightsAITrackerLoading) {
    await page.spoInsightsAITrackerLoading;
  }

  const running = trackerAlreadyRunning(w);
  if (running) {
    return running;
  }

  const loaded = load();
  const settled: Promise<void> = loaded
    .then(() => untilTrackerWouldShow(w), () => undefined)
    .then(() => {
      if (page.spoInsightsAITrackerLoading === settled) {
        page.spoInsightsAITrackerLoading = undefined;
      }
    });
  page.spoInsightsAITrackerLoading = settled;

  await loaded;
  return undefined;
}

// Resolves once a copy that has just loaded would show itself if it's running: at once if it has, or if the page has loaded;
// otherwise just after the page's load event. A copy before 1.6.0 tracks the page in a timeout of its own from a load listener
// added before this one, so a timeout set from this listener runs after it.
function untilTrackerWouldShow(w: Window): Promise<void> {
  if (trackerAlreadyRunning(w) || w.document.readyState === "complete") {
    return Promise.resolve();
  }
  return new Promise<void>(resolve => w.addEventListener("load", () => w.setTimeout(resolve, 0), { once: true }));
}
