using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for ActivityReportSet.OldestContent / NewestContent.
    /// The previous implementation did an O(n log n) full sort
    /// (OrderByDescending(...).Last() / .First()) on every property access.
    /// The fix replaces it with O(n) Min/Max scans. These tests pin down
    /// the contract so a future refactor cannot accidentally regress to
    /// FirstOrDefault/LastOrDefault on an unsorted collection.
    /// </summary>
    [TestClass]
    public class ActivityReportSetMinMaxTests
    {
        private class TestActivityReportSet : ActivityReportSet { }

        private static AbstractAuditLogContent MakeLog(DateTime created)
        {
            return new SharePointAuditLogContent
            {
                Id = Guid.NewGuid(),
                CreationTime = created,
                UserId = "user@contoso.com",
                Workload = "SharePoint",
                Operation = "FileAccessed"
            };
        }

        [TestMethod]
        public void OldestContent_ReturnsMinimumCreationTime()
        {
            var set = new TestActivityReportSet
            {
                MakeLog(new DateTime(2026, 5, 30, 10, 0, 0, DateTimeKind.Utc)),
                MakeLog(new DateTime(2026, 5, 30,  8, 0, 0, DateTimeKind.Utc)),
                MakeLog(new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc)),
            };

            Assert.AreEqual(new DateTime(2026, 5, 30, 8, 0, 0, DateTimeKind.Utc), set.OldestContent);
        }

        [TestMethod]
        public void NewestContent_ReturnsMaximumCreationTime()
        {
            var set = new TestActivityReportSet
            {
                MakeLog(new DateTime(2026, 5, 30, 10, 0, 0, DateTimeKind.Utc)),
                MakeLog(new DateTime(2026, 5, 30,  8, 0, 0, DateTimeKind.Utc)),
                MakeLog(new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc)),
            };

            Assert.AreEqual(new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc), set.NewestContent);
        }

        [TestMethod]
        public void OldestAndNewestContent_SingleEntry_ReturnsThatEntryForBoth()
        {
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var set = new TestActivityReportSet { MakeLog(t) };

            Assert.AreEqual(t, set.OldestContent);
            Assert.AreEqual(t, set.NewestContent);
        }

        [TestMethod]
        public void OldestAndNewestContent_DoNotMutateUnderlyingCollectionOrder()
        {
            // Pre-fix used OrderByDescending which allocates a new enumerator but does not
            // mutate the list. Min/Max also do not mutate. Pin that down so a future
            // implementation that uses Sort() in-place would be caught here.
            var first  = MakeLog(new DateTime(2026, 5, 30, 10, 0, 0, DateTimeKind.Utc));
            var middle = MakeLog(new DateTime(2026, 5, 30,  8, 0, 0, DateTimeKind.Utc));
            var last   = MakeLog(new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc));

            var set = new TestActivityReportSet { first, middle, last };

            // Access both properties
            _ = set.OldestContent;
            _ = set.NewestContent;

            // Underlying list still in insertion order
            Assert.AreSame(first, set[0]);
            Assert.AreSame(middle, set[1]);
            Assert.AreSame(last, set[2]);
        }
    }
}
