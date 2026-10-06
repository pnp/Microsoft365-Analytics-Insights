// AITracker.js copies set these on the window as they start (1.6.0 and later) or once they have tracked a page (all versions)
interface WindowWithTracker {
  spoInsightsAITrackerVersion?: string;
  modernPageNav?: unknown;
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
