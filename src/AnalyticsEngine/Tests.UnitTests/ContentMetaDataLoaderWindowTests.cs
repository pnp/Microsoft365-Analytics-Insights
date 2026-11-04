using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.UnitTests
{
    /// <summary>
    /// The window of audit content the importer asks the Office 365 Management Activity API to list. The API refuses a listing
    /// that starts more than 7 days in the past (AF20055):
    /// https://learn.microsoft.com/office/office-365-management-api/office-365-management-activity-api-reference#errors
    /// </summary>
    [TestClass]
    public class ContentMetaDataLoaderWindowTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// R1-9: DaysBeforeNowToDownload of 7 or more made the oldest chunk start beyond the API's limit, so its listing
        /// failed on every cycle for every content type and was reported as a download error.
        /// </summary>
        [DataTestMethod]
        [DataRow(7)]
        [DataRow(30)]
        public void Window_LongerThanTheApiLists_StartsInsideItsSevenDays_AndIsReportedOnce(int days)
        {
            var log = new RecordingLogger();
            var loader = new FakeContentMetaDataLoader(log, new AppConfig { DaysBeforeNowToDownload = days, TimeChunkOverlapMinutes = 5 }, 0);

            var chunks = loader.GetScanningTimeChunksFrom(Now);
            loader.GetScanningTimeChunksFrom(Now);

            Assert.AreEqual(Now - ContentMetaDataLoader<TestActivitySummary>.MaxListingLookback + ContentMetaDataLoader<TestActivitySummary>.ListingLookbackMargin,
                chunks.First().Start);
            Assert.IsTrue(Now - chunks.First().Start < TimeSpan.FromDays(7));
            Assert.IsTrue(chunks.Last().End <= Now);
            Assert.AreEqual(1, log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("DaysBeforeNowToDownload")), "Said once, not per cycle of chunks.");
        }

        [DataTestMethod]
        [DataRow(6, 6)]
        [DataRow(1, 1)]
        [DataRow(0, 1)]
        public void Window_InsideTheLimit_IsUnchanged(int days, int expectedDays)
        {
            var log = new RecordingLogger();
            var loader = new FakeContentMetaDataLoader(log, new AppConfig { DaysBeforeNowToDownload = days, TimeChunkOverlapMinutes = 5 }, 0);

            var chunks = loader.GetScanningTimeChunksFrom(Now);

            Assert.AreEqual(Now.AddDays(-expectedDays), chunks.First().Start);
            Assert.IsTrue(chunks.Last().End <= Now);
            Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Warning));
        }
    }
}
