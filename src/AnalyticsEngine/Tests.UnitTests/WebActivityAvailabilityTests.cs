using Common.Entities.SpoWebActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// What the web activity page tells an admin when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// This is the part of the page that has to work when the rest of it cannot. An empty report is
    /// ambiguous - not deployed, not configured, broken, or simply a quiet intranet - and an admin
    /// who cannot tell those apart will go and debug the wrong one.
    /// </remarks>
    [TestClass]
    [TestCategory("WebActivity")]
    public class WebActivityAvailabilityTests
    {
        private static WebActivitySources AllOn() => new WebActivitySources
        {
            Readable = true,
            WebTraffic = true,
            UserMetadata = true,
            AppInsightsConfigured = true,
        };

        private static DateTime Now => new DateTime(2026, 3, 18, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A successful read of the page-hit table.</summary>
        private static WebActivityCollectionStatus Seen(DateTime? lastHit) =>
            new WebActivityCollectionStatus { Readable = true, LastHitUtc = lastHit };

        /// <summary>A read that FAILED - which must never be reported as "nothing collected".</summary>
        private static WebActivityCollectionStatus Unreadable() =>
            new WebActivityCollectionStatus { Readable = false };

        [TestMethod]
        public void HealthyDeployment_ReportsNothingMissing()
        {
            var model = WebActivityAvailability.Build(AllOn(), Seen(Now.AddHours(-2)), true, true, Now);

            Assert.IsTrue(model.Available);
            Assert.AreEqual(0, model.Reasons.Count, string.Join(" | ", model.Reasons));
        }

        [TestMethod]
        public void ImportOff_IsReportedBeforeAnythingDownstreamOfIt()
        {
            var sources = AllOn();
            sources.WebTraffic = false;

            var model = WebActivityAvailability.Build(sources, Seen(null), false, false, Now);

            Assert.IsFalse(model.Available);
            StringAssert.Contains(model.Reasons[0], "web traffic import is switched off");

            // The tracker-not-deployed message must NOT also appear: telling an admin to redeploy a
            // tracker when the import that reads it is switched off sends them to the wrong place.
            Assert.IsFalse(model.Reasons.Any(r => r.Contains("AI Tracker")));
        }

        [TestMethod]
        public void ConfiguredButNoHitsEver_PointsAtTheTrackerNotTheImporter()
        {
            var model = WebActivityAvailability.Build(AllOn(), Seen(null), null, null, Now);

            Assert.IsFalse(model.Available);
            Assert.IsTrue(model.Reasons.Any(r => r.Contains("AI Tracker")));
        }

        [TestMethod]
        public void MissingConnectionString_IsReportedInsteadOfTheTrackerAdvice()
        {
            var sources = AllOn();
            sources.AppInsightsConfigured = false;

            var model = WebActivityAvailability.Build(sources, Seen(null), null, null, Now);

            Assert.IsTrue(model.Reasons.Any(r => r.Contains("Application Insights connection string")));
            Assert.IsFalse(model.Reasons.Any(r => r.Contains("AI Tracker")));
        }

        [TestMethod]
        public void StaleCollection_IsFlaggedButDoesNotHideTheData()
        {
            var lastHit = Now.AddDays(-9);
            var model = WebActivityAvailability.Build(AllOn(), Seen(lastHit), true, true, Now);

            Assert.IsTrue(model.Available, "Nine-day-old data is still data; it just needs explaining.");
            Assert.IsTrue(model.Reasons.Any(r => r.Contains("days old")));
        }

        [TestMethod]
        public void RecentQuietWeekend_IsNotReportedAsAFailure()
        {
            // Two days is inside the threshold. A one-day threshold would cry wolf every weekend on
            // any intranet quiet enough to have no Saturday traffic.
            var model = WebActivityAvailability.Build(AllOn(), Seen(Now.AddDays(-2)), true, true, Now);

            Assert.IsFalse(model.Reasons.Any(r => r.Contains("days old")), string.Join(" | ", model.Reasons));
        }

        [TestMethod]
        public void UnknownOptionalFeatures_AreAssumedPresentRatherThanReportedMissing()
        {
            // Null means "the query that would have told us failed". Reporting that as "click capture
            // is off" would send an admin to enable something that may already be on.
            var model = WebActivityAvailability.Build(AllOn(), Seen(Now.AddHours(-1)), null, null, Now);

            Assert.IsTrue(model.SearchAvailable);
            Assert.IsTrue(model.ClickTrackingAvailable);
            Assert.IsFalse(model.Reasons.Any(r => r.Contains("element clicks")));
        }

        [TestMethod]
        public void NoSearchesOrClicks_ExplainsExactlyWhichPanelIsAffected()
        {
            var model = WebActivityAvailability.Build(AllOn(), Seen(Now.AddHours(-1)), false, false, Now);

            Assert.IsTrue(model.Available, "Search and click capture are optional extras, not the page.");
            Assert.IsTrue(model.Reasons.Any(r => r.Contains("Web searches tab")));
            Assert.IsTrue(model.Reasons.Any(r => r.Contains("Journeys tab")));
        }

        [TestMethod]
        public void NoDirectory_SaysVisitorCountsAreStillAccurate()
        {
            var sources = AllOn();
            sources.UserMetadata = false;

            var model = WebActivityAvailability.Build(sources, Seen(Now.AddHours(-1)), true, true, Now);

            Assert.IsTrue(model.Available);
            var reason = model.Reasons.Single(r => r.Contains("user metadata"));
            StringAssert.Contains(reason, "Visitor counts are still accurate");
        }

        [TestMethod]
        public void AFailedReadIsReportedAsUnknownNotAsNothingCollected()
        {
            // The advice differs completely: "no page view has ever arrived" sends an admin to
            // redeploy the SharePoint tracker across every site collection. Giving that advice
            // because a query timed out costs a day and fixes nothing.
            var model = WebActivityAvailability.Build(AllOn(), Unreadable(), null, null, Now);

            Assert.IsFalse(model.CollectionStatusKnown);
            Assert.IsFalse(model.Reasons.Any(r => r.Contains("AI Tracker")));
            Assert.IsTrue(model.Reasons.Any(r => r.Contains("could not be determined")));
        }

        [TestMethod]
        public void UnreadableConfiguration_IsNotReportedAsEveryImportBeingOff()
        {
            // With configuration unreadable every toggle reads false, which would otherwise render as
            // a deliberate "the web traffic import is switched off" and send an admin to the
            // installer rather than to the broken configuration.
            // A null sources object IS the unreadable case: nobody managed to read configuration.
            var model = WebActivityAvailability.Build(null, Unreadable(), null, null, Now);

            Assert.AreEqual(1, model.Reasons.Count, string.Join(" | ", model.Reasons));
            StringAssert.Contains(model.Reasons[0], "configuration could not be read");

            // Specifically must not make the claim an admin would act on - "the web traffic import
            // is switched off" sends them to the installer. Saying the toggles CANNOT BE TRUSTED is
            // the opposite message and is allowed to mention the phrase.
            Assert.IsFalse(
                model.Reasons.Any(r => r.Contains("The web traffic import is switched off")),
                string.Join(" | ", model.Reasons));
        }

        [TestMethod]
        public void NullSources_DoNotThrow()
        {
            var model = WebActivityAvailability.Build(null);

            Assert.IsFalse(model.Available);
            Assert.IsTrue(model.Reasons.Count > 0);
        }

        [TestMethod]
        public void UnreadableConfiguration_IsCarriedOnTheSourcesObjectItself()
        {
            // Readability travels WITH the toggles rather than as a separate argument a caller can
            // forget: a source object whose flags are all false because nothing could be read must
            // never be reported as a tenant that switched every import off, whichever endpoint
            // happens to be asking.
            var unreadable = WebActivitySources.FromConfig(null);
            Assert.IsFalse(unreadable.Readable);

            var model = WebActivityAvailability.Build(unreadable, Unreadable(), null, null, Now);
            StringAssert.Contains(model.Reasons[0], "configuration could not be read");
        }

        [TestMethod]
        public void ImportOff_WithHistoricalHits_StillReportsAndSaysTheDataHasStopped()
        {
            // None of the reporting queries is conditioned on the import toggle, so a tenant that
            // collected hits and later switched the import off still gets populated charts. Marking
            // the page unavailable put an "every tab will be empty" banner directly above real data.
            var sources = AllOn();
            sources.WebTraffic = false;

            var model = WebActivityAvailability.Build(sources, Seen(Now.AddDays(-30)), true, true, Now);

            Assert.IsTrue(model.Available, "Stored hits are still reportable once collection stops.");
            StringAssert.Contains(model.Reasons[0], "no NEW SharePoint page views");
            Assert.IsFalse(
                model.Reasons.Any(r => r.Contains("every tab on this page will be empty")),
                "That claim is false when there is data, and it sits right above the charts proving it.");
        }

        [TestMethod]
        public void ImportOff_WithNoHitsAtAll_StillSaysEveryTabWillBeEmpty()
        {
            var sources = AllOn();
            sources.WebTraffic = false;

            var model = WebActivityAvailability.Build(sources, Seen(null), false, false, Now);

            Assert.IsFalse(model.Available);
            StringAssert.Contains(model.Reasons[0], "every tab on this page will be empty");
        }
    }
}
