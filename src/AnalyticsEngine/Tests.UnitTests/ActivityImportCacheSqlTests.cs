using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;

namespace Tests.UnitTests
{
    /// <summary>
    /// The audit-log de-duplication cache's two reads against a real database: the run cache's per-batch
    /// primary-key lookup, and the <c>AUDIT_PERBATCH_DEDUP_CACHE</c> safety-valve's streamed window load. Both are
    /// hand-written SQL, so only a database proves the parameters, chunking and predicates.
    /// </summary>
    [TestClass]
    public class ActivityImportCacheSqlTests
    {
        [TestMethod]
        public async Task SqlLookup_FindsExactlyTheImportedAndIgnoredIds_AcrossQueryChunks()
        {
            var now = DateTime.UtcNow;
            using (var seeded = new SeededEvents(importedAt: new[] { now, now.AddDays(-200) }, ignoredAt: new[] { now, now.AddDays(-200) }))
            {
                // 2,345 ids: queries of 1,000, 1,000 and 345 (padded to 500). The known ids sit on the chunk edges.
                var ids = Enumerable.Range(0, 2345).Select(_ => Guid.NewGuid()).ToList();
                ids[0] = seeded.Imported[0];
                ids[999] = seeded.Ignored[0];
                ids[1000] = seeded.Imported[1];
                ids[2344] = seeded.Ignored[1];

                var found = await SqlActivityImportCacheLoader.Instance.FindAlreadyProcessedAsync(ids);

                // Any age: the lookup is by primary key, so an event older than the download window (e.g. one the
                // Copilot backfill re-reads) is recognised too.
                CollectionAssert.AreEquivalent(seeded.Imported.Concat(seeded.Ignored).ToList(), found.ToList());
            }
        }

        [TestMethod]
        public void WindowLoad_HoldsOnlyTheImportedAndIgnoredIdsInsideTheWindow()
        {
            var now = DateTime.UtcNow;
            using (var seeded = new SeededEvents(importedAt: new[] { now.AddHours(-1), now.AddDays(-30) }, ignoredAt: new[] { now.AddHours(-1), now.AddDays(-30) }))
            {
                var cache = ActivityImportCache.GetAndBuildNewCache(now.AddHours(-2), now);

                Assert.IsTrue(cache.HaveSeenInProcessedOrIgnoredEvents(seeded.Imported[0]));
                Assert.IsTrue(cache.HaveSeenInProcessedOrIgnoredEvents(seeded.Ignored[0]));
                Assert.IsFalse(cache.HaveSeenInProcessedOrIgnoredEvents(seeded.Imported[1]), "Outside the window.");
                Assert.IsFalse(cache.HaveSeenInProcessedOrIgnoredEvents(seeded.Ignored[1]), "Outside the window.");
            }
        }

        /// <summary>Synthetic <c>audit_events</c> / <c>ignored_audit_events</c> rows, deleted again on dispose.</summary>
        private sealed class SeededEvents : IDisposable
        {
            private readonly User _user = new User { UserPrincipalName = $"dedup-{Guid.NewGuid():N}@contoso.onmicrosoft.com" };
            private readonly EventOperation _operation = new EventOperation { Name = "dedup lookup test " + Guid.NewGuid().ToString("N") };

            public SeededEvents(DateTime[] importedAt, DateTime[] ignoredAt)
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    foreach (var timeStamp in importedAt)
                    {
                        var auditEvent = new CommonAuditEvent { Id = Guid.NewGuid(), TimeStamp = timeStamp, User = _user, Operation = _operation };
                        db.AuditEventsCommon.Add(auditEvent);
                        Imported.Add(auditEvent.Id);
                    }
                    foreach (var timeStamp in ignoredAt)
                    {
                        var ignored = new IgnoredEvent { event_id = Guid.NewGuid(), processed_timestamp = timeStamp };
                        db.ignored_audit_events.Add(ignored);
                        Ignored.Add(ignored.event_id);
                    }
                    db.SaveChanges();
                }
            }

            public List<Guid> Imported { get; } = new List<Guid>();
            public List<Guid> Ignored { get; } = new List<Guid>();

            public void Dispose()
            {
                var imported = Imported.ToList();
                var ignored = Ignored.ToList();
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.AuditEventsCommon.RemoveRange(db.AuditEventsCommon.Where(e => imported.Contains(e.Id)));
                    db.ignored_audit_events.RemoveRange(db.ignored_audit_events.Where(e => ignored.Contains(e.event_id)));
                    db.SaveChanges();

                    db.users.Attach(_user);
                    db.users.Remove(_user);
                    db.event_operations.Attach(_operation);
                    db.event_operations.Remove(_operation);
                    db.SaveChanges();
                }
            }
        }
    }
}
