using Newtonsoft.Json;
using System;

namespace Web.AnalyticsWeb.Models.UpdateCheck
{
    /// <summary>
    /// Result of an "is there a newer release than the build we're running?" check, returned by
    /// <c>api/UpdateCheck</c>.
    /// </summary>
    /// <remarks>
    /// Every failure mode is reported in <see cref="CheckError"/> rather than as an HTTP error, so the
    /// portal can always render the build it is running even when GitHub is unreachable - which is the
    /// normal case on a locked-down deployment with no outbound internet.
    /// </remarks>
    public class UpdateCheckApiModel
    {
        /// <summary>The label of the running build, e.g. "Build 1796" (or "DEV_BUILD" locally).</summary>
        [JsonProperty("currentBuildLabel")]
        public string CurrentBuildLabel { get; set; }

        /// <summary>The running build number, or null for a local build with no number.</summary>
        [JsonProperty("currentBuild")]
        public int? CurrentBuild { get; set; }

        /// <summary>True when this is a locally-compiled build, so no comparison is possible.</summary>
        [JsonProperty("isDevBuild")]
        public bool IsDevBuild { get; set; }

        /// <summary>Build number of the latest published stable release, or null if it couldn't be read.</summary>
        [JsonProperty("latestBuild")]
        public int? LatestBuild { get; set; }

        /// <summary>Release title, e.g. "Stable build 1756".</summary>
        [JsonProperty("latestReleaseName")]
        public string LatestReleaseName { get; set; }

        /// <summary>Link to the release page, for the admin to read the notes and download from.</summary>
        [JsonProperty("latestReleaseUrl")]
        public string LatestReleaseUrl { get; set; }

        /// <summary>When the latest release was published.</summary>
        [JsonProperty("latestPublishedUtc")]
        public DateTime? LatestPublishedUtc { get; set; }

        /// <summary>True only when both build numbers are known and the released one is higher.</summary>
        [JsonProperty("updateAvailable")]
        public bool UpdateAvailable { get; set; }

        /// <summary>Why the check couldn't be completed, phrased for an admin. Null on success.</summary>
        [JsonProperty("checkError")]
        public string CheckError { get; set; }

        /// <summary>
        /// When the GitHub result being reported was actually fetched. The result is cached briefly, so
        /// this can be older than the moment the admin pressed the button - showing it avoids implying a
        /// live call happened when it didn't.
        /// </summary>
        [JsonProperty("checkedAtUtc")]
        public DateTime CheckedAtUtc { get; set; }
    }
}
