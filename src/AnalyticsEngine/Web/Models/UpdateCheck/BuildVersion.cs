using System;
using System.Text.RegularExpressions;

namespace Web.AnalyticsWeb.Models.UpdateCheck
{
    /// <summary>
    /// Parses and compares the two different shapes this product expresses its version in.
    /// </summary>
    /// <remarks>
    /// There are exactly two, and they do not match each other:
    /// <list type="bullet">
    /// <item>
    /// The running build is <see cref="Common.Entities.BuildConstants.BuildLabel"/>, which the release
    /// pipeline rewrites from <c>"DEV_BUILD"</c> to <c>"Build 1796"</c> (see the sed step in
    /// <c>.github/workflows/ci.yml</c>). A locally-compiled build keeps <c>"DEV_BUILD"</c>.
    /// </item>
    /// <item>
    /// A published release's <c>tag_name</c> on GitHub is the bare build number, e.g. <c>"1756"</c>
    /// (its <c>name</c> is "Stable build 1756").
    /// </item>
    /// </list>
    /// So the comparison is an integer compare of the build number pulled out of each. This is pure
    /// string/number logic with no IO, deliberately separated so it can be tested directly.
    /// </remarks>
    public static class BuildVersion
    {
        /// <summary>The label a build that was not produced by the release pipeline carries.</summary>
        public const string DevBuildLabel = "DEV_BUILD";

        // The first run of digits in the string. Both shapes above are covered by this, and it stays
        // tolerant of a tag someone writes by hand as "v1756" or "Build 1756".
        private static readonly Regex _firstNumber = new Regex(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// True when <paramref name="buildLabel"/> is a local/developer build rather than a released one.
        /// Such a build has no build number, so it can never be compared against a release.
        /// </summary>
        public static bool IsDevBuild(string buildLabel)
        {
            return string.IsNullOrWhiteSpace(buildLabel)
                || buildLabel.Trim().Equals(DevBuildLabel, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the build number from a build label or a release tag, or null when there isn't one.
        /// </summary>
        public static int? TryParseBuildNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsDevBuild(value))
            {
                return null;
            }

            var match = _firstNumber.Match(value);
            if (!match.Success)
            {
                return null;
            }

            // A build number that doesn't fit in an int is not a build number we produced.
            return int.TryParse(match.Value, out var number) ? (int?)number : null;
        }

        /// <summary>
        /// True only when both versions are known AND the released one is genuinely higher. Anything
        /// unknown returns false: we would rather say nothing than tell an admin to upgrade on a guess.
        /// </summary>
        public static bool IsUpdateAvailable(int? currentBuild, int? latestBuild)
        {
            return currentBuild.HasValue && latestBuild.HasValue && latestBuild.Value > currentBuild.Value;
        }
    }
}
