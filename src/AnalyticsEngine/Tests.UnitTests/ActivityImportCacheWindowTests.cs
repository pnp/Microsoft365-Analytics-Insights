using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// The audit-log de-duplication cache window rules, including the AUDIT_PERBATCH_DEDUP_CACHE operator
    /// safety-valve. Extracted by issue #373 so both are assertable with no SQL Server and no wall clock.
    /// </summary>
    [TestClass]
    public class ActivityImportCacheWindowTests
    {
        private static readonly DateTime NowUtc = new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc);
        private static readonly DateTime OldestInBatch = new DateTime(2026, 3, 8, 1, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime NewestInBatch = new DateTime(2026, 3, 10, 13, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void ImportCache_ByDefault_IsPerRunAndCoversTheWholeDownloadWindow()
        {
            var window = ActivityImportCacheWindow.Resolve(false, OldestInBatch, NewestInBatch, daysBeforeNowToDownload: 3, nowUtc: NowUtc);

            Assert.AreEqual(ActivityDedupCacheScope.PerRun, window.Scope);

            // One day of extra lower margin: the download window is computed at cycle start, slightly before
            // the cache is built, so an event just outside the exact boundary must still be covered.
            Assert.AreEqual(4, window.DaysBack);
            Assert.AreEqual(NowUtc.AddDays(-4), window.FromUtc);
            Assert.AreEqual(NowUtc.AddMinutes(2), window.ToUtc);

            // The batch's own span is irrelevant in per-run mode - that is the whole point of the change the
            // safety-valve reverts.
            Assert.AreNotEqual(OldestInBatch, window.FromUtc);
        }

        [TestMethod]
        public void ImportCache_PerBatchSafetyValveEnabled_UsesTheBatchSpanInsteadOfTheDownloadWindow()
        {
            var window = ActivityImportCacheWindow.Resolve(true, OldestInBatch, NewestInBatch, daysBeforeNowToDownload: 3, nowUtc: NowUtc);

            Assert.AreEqual(ActivityDedupCacheScope.PerBatch, window.Scope);
            Assert.AreEqual(OldestInBatch, window.FromUtc);
            Assert.AreEqual(NewestInBatch, window.ToUtc);
        }

        [TestMethod]
        public void ImportCache_PerBatchWindow_DoesNotDependOnTheClock()
        {
            var a = ActivityImportCacheWindow.Resolve(true, OldestInBatch, NewestInBatch, 3, NowUtc);
            var b = ActivityImportCacheWindow.Resolve(true, OldestInBatch, NewestInBatch, 3, NowUtc.AddYears(5));

            Assert.AreEqual(a.FromUtc, b.FromUtc);
            Assert.AreEqual(a.ToUtc, b.ToUtc);
        }

        [TestMethod]
        public void ImportCache_RunWindow_IsDrivenByTheSuppliedInstantNotTheWallClock()
        {
            // Would fail if the rule ever read DateTime.UtcNow itself.
            var pastInstant = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var window = ActivityImportCacheWindow.Resolve(false, OldestInBatch, NewestInBatch, 7, pastInstant);

            Assert.AreEqual(pastInstant.AddDays(-8), window.FromUtc);
            Assert.AreEqual(pastInstant.AddMinutes(2), window.ToUtc);
        }

        [TestMethod]
        public void ImportCache_RunWindow_ZeroOrNegativeDownloadWindow_StillCoversAtLeastTwoDays()
        {
            // A misconfigured DaysBeforeNowToDownload must not collapse the cache to "now", which would make
            // every already-imported event look new and re-stage the whole window.
            foreach (var configured in new[] { 0, -1 })
            {
                var window = ActivityImportCacheWindow.Resolve(false, OldestInBatch, NewestInBatch, configured, NowUtc);
                Assert.AreEqual(2, window.DaysBack, $"DaysBeforeNowToDownload={configured}");
                Assert.AreEqual(NowUtc.AddDays(-2), window.FromUtc, $"DaysBeforeNowToDownload={configured}");
            }
        }

        [TestMethod]
        public void ImportCache_WindowIsPaddedOneMinuteEitherSide()
        {
            // EF6 maps DateTime to datetime2, whose precision differs from the datetime columns in the
            // database, so an exact boundary comparison can miss edge values.
            Assert.AreEqual(NowUtc.AddMinutes(-1), ActivityImportCacheWindow.PadFrom(NowUtc));
            Assert.AreEqual(NowUtc.AddMinutes(1), ActivityImportCacheWindow.PadTo(NowUtc));
        }
    }
}
