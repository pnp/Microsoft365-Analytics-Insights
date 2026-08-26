// Mirrors Web/Models/UpdateCheck/UpdateCheckApiModel.cs (returned by api/UpdateCheck).

export interface UpdateCheck {
  /** Label of the running build, e.g. "Build 1796" (or "DEV_BUILD" locally). */
  currentBuildLabel: string | null;
  /** Running build number, or null for a local build with no number. */
  currentBuild: number | null;
  /** True when this is a locally-compiled build, so no comparison is possible. */
  isDevBuild: boolean;
  /** Build number of the latest published stable release, or null if it couldn't be read. */
  latestBuild: number | null;
  latestReleaseName: string | null;
  latestReleaseUrl: string | null;
  latestPublishedUtc: string | null;
  /** True only when both build numbers are known and the released one is higher. */
  updateAvailable: boolean;
  /** Why the check couldn't be completed, phrased for an admin. Null on success. */
  checkError: string | null;
  /** When the GitHub result being reported was actually fetched (it is cached briefly). */
  checkedAtUtc: string;
}
