extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using BuildVersion = AnalyticsWeb::Web.AnalyticsWeb.Models.UpdateCheck.BuildVersion;

namespace Tests.UnitTests
{
    /// <summary>
    /// The portal's "check for updates" comparison. This is the only logic in the feature that can be
    /// silently wrong - a bad compare either nags every admin to upgrade to the build they already run,
    /// or quietly never tells them a release exists - so it is covered directly.
    /// </summary>
    [TestClass]
    public class UpdateCheckBuildVersionTests
    {
        [TestMethod]
        public void DevBuildIsRecognised()
        {
            Assert.IsTrue(BuildVersion.IsDevBuild("DEV_BUILD"));
            Assert.IsTrue(BuildVersion.IsDevBuild("dev_build"), "Comparison should be case-insensitive.");
            Assert.IsTrue(BuildVersion.IsDevBuild("  DEV_BUILD  "), "Surrounding whitespace shouldn't matter.");
            Assert.IsTrue(BuildVersion.IsDevBuild(null));
            Assert.IsTrue(BuildVersion.IsDevBuild(""));

            Assert.IsFalse(BuildVersion.IsDevBuild("Build 1796"));
        }

        [TestMethod]
        public void ParsesTheBuildNumberFromBothRealFormats()
        {
            // What the release pipeline writes into BuildConstants.BuildLabel...
            Assert.AreEqual(1796, BuildVersion.TryParseBuildNumber("Build 1796"));

            // ...and what GitHub carries as a release tag_name.
            Assert.AreEqual(1756, BuildVersion.TryParseBuildNumber("1756"));

            // Tolerated hand-written variations.
            Assert.AreEqual(1756, BuildVersion.TryParseBuildNumber("v1756"));
            Assert.AreEqual(1756, BuildVersion.TryParseBuildNumber("Stable build 1756"));
        }

        [TestMethod]
        public void ReturnsNullWhenThereIsNoBuildNumber()
        {
            Assert.IsNull(BuildVersion.TryParseBuildNumber("DEV_BUILD"));
            Assert.IsNull(BuildVersion.TryParseBuildNumber(null));
            Assert.IsNull(BuildVersion.TryParseBuildNumber(""));
            Assert.IsNull(BuildVersion.TryParseBuildNumber("   "));
            Assert.IsNull(BuildVersion.TryParseBuildNumber("no digits here"));

            // Larger than int.MaxValue - not a build number we produced, so don't guess.
            Assert.IsNull(BuildVersion.TryParseBuildNumber("99999999999999999999"));
        }

        [TestMethod]
        public void UpdateIsOnlyOfferedWhenTheReleaseIsGenuinelyNewer()
        {
            Assert.IsTrue(BuildVersion.IsUpdateAvailable(1756, 1796), "A higher release build is an update.");

            Assert.IsFalse(BuildVersion.IsUpdateAvailable(1796, 1796), "Same build is not an update.");
            Assert.IsFalse(BuildVersion.IsUpdateAvailable(1796, 1756),
                "Running ahead of the published release (e.g. a pre-release build) must not offer a downgrade.");
        }

        [TestMethod]
        public void UnknownVersionsNeverOfferAnUpdate()
        {
            // Never nag on a guess: if either side is unknown, say nothing.
            Assert.IsFalse(BuildVersion.IsUpdateAvailable(null, 1796));
            Assert.IsFalse(BuildVersion.IsUpdateAvailable(1756, null));
            Assert.IsFalse(BuildVersion.IsUpdateAvailable(null, null));
        }

        [TestMethod]
        public void ADevBuildNeverOffersAnUpdate()
        {
            // End to end over the two helpers: a locally-compiled build has no number, so however new the
            // published release is, the portal must not claim an update is available.
            var current = BuildVersion.TryParseBuildNumber(BuildVersion.DevBuildLabel);
            var latest = BuildVersion.TryParseBuildNumber("1796");

            Assert.IsNull(current);
            Assert.AreEqual(1796, latest);
            Assert.IsFalse(BuildVersion.IsUpdateAvailable(current, latest));
        }
    }
}
