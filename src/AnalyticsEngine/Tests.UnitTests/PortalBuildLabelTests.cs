extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// The build label the portal stamps into its printed report footer.
    ///
    /// The SPA cannot fetch this: the footer has to be in the page before <c>window.print()</c> runs,
    /// and the endpoint that carries the label elsewhere (api/SystemStatus) COUNT(*)s whole tables, so
    /// it is far too expensive to call on every page just to name a version. index.html is already
    /// served through HomeController, so the label is substituted in there instead - which makes this
    /// one string replacement the only thing standing between a printed report and being unable to say
    /// which build produced its figures.
    /// </summary>
    [TestClass]
    public class PortalBuildLabelTests
    {
        private const string IndexHtml =
            "<script>window.o365AnalyticsBuildLabel = \"__BuildLabel__\";</script>";

        [TestMethod]
        public void StampsTheRunningBuildIntoTheServedPage()
        {
            var html = HomeController.InjectBuildLabel(IndexHtml, "Build 1841");

            StringAssert.Contains(html, "window.o365AnalyticsBuildLabel = \"Build 1841\";");
            Assert.IsFalse(html.Contains(HomeController.BuildLabelPlaceholder),
                "The placeholder must not survive into the served page - the SPA reads it as 'unknown build'.");
        }

        [TestMethod]
        public void LeavesADeveloperBuildSayingSo()
        {
            // The compiled-in default, which the release pipeline rewrites. The SPA maps it to "no
            // version" rather than printing it, so it must reach the page unchanged rather than being
            // blanked here - blanking it would make a dev build indistinguishable from a failed
            // substitution.
            var html = HomeController.InjectBuildLabel(IndexHtml, BuildConstants.BuildLabel);

            StringAssert.Contains(html, "window.o365AnalyticsBuildLabel = \"DEV_BUILD\";");
        }

        [TestMethod]
        public void EncodesTheLabelForTheScriptItLandsIn()
        {
            // It is a build constant rather than user input today, but this substitutes into a quoted
            // string literal inside an inline <script>: a bare quote would end the string and break
            // every page in the portal, not merely the footer.
            var html = HomeController.InjectBuildLabel(IndexHtml, "Build \"1841\"\\x");

            Assert.IsFalse(html.Contains("\"Build \"1841\"\\x\""),
                "An unencoded quote would terminate the JavaScript string literal.");
            StringAssert.Contains(html, "\\\"1841\\\"");
            StringAssert.Contains(html, "\\\\x");
        }

        [TestMethod]
        public void SurvivesAMissingLabelAndAMissingPage()
        {
            StringAssert.Contains(
                HomeController.InjectBuildLabel(IndexHtml, null),
                "window.o365AnalyticsBuildLabel = \"\";");

            Assert.IsNull(HomeController.InjectBuildLabel(null, "Build 1841"));
        }

        [TestMethod]
        public void UsesThePlaceholderThePortalActuallyWrites()
        {
            // The portal side of this is a literal in index.html and a literal in product.ts, with no
            // type or build step joining them to the constant here. A rename on either side leaves the
            // page loading, the report printing, and the footer silently unversioned.
            Assert.AreEqual("__BuildLabel__", HomeController.BuildLabelPlaceholder);
        }
    }
}
