using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// The audit-log save batch's staging pass - de-duplicate, scope-check, build the staging rows, then
    /// load + merge - as split out of ActivityReportSqlPersistenceManager by issue #373 part 2.
    ///
    /// Zero SQL Server: the staging table is reached only through <see cref="IActivityStagingWriter"/>, and
    /// the org-URL whitelist and user-groups filter were already injectable. ActivityStagingRulesTests
    /// covers the per-event decision; this covers what the batch does WITH those decisions - the
    /// operator-facing <c>ImportStat</c> counters and log line, the phase timings, and which staging table
    /// the merge is pointed at in serial versus sharded mode.
    /// </summary>
    [TestClass]
    public class ActivitySavePipelineTests
    {
        private const string InScopeUpn = "in@contoso.onmicrosoft.com";
        private const string OutOfScopeUpn = "out@contoso.onmicrosoft.com";
        private static readonly DateTime Created = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);

        private static SharePointAuditLogContent SpEvent(string upn, string objectId = "https://contoso.sharepoint.com/sites/x/a.docx", Guid? id = null)
            => new SharePointAuditLogContent
            {
                Id = id ?? Guid.NewGuid(),
                UserId = upn,
                CreationTime = Created,
                Workload = ActivityImportConstants.WORKLOAD_SP,
                Operation = "FileAccessed",
                ObjectId = objectId,
                SiteUrl = "https://contoso.sharepoint.com/sites/x",
                SourceFileName = "a.docx",
                SourceFileExtension = "docx",
                ItemType = "File",
                EventData = "<event/>"
            };

        private static ActivityReportSet SetOf(params AbstractAuditLogContent[] events)
        {
            var set = new WebActivityReportSet();
            set.AddRange(events);
            return set;
        }

        /// <summary>
        /// A groups cache + filter pair where <see cref="InScopeUpn"/> matches and <see cref="OutOfScopeUpn"/>
        /// does not. Note UserGroupsCache treats "user has no groups at all" as a match, so the excluded user
        /// must be given a non-matching group rather than none.
        /// </summary>
        private static MockUserGroupsCache GroupsCache()
            => new MockUserGroupsCache(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { InScopeUpn, new List<string> { "Finance" } },
                { OutOfScopeUpn, new List<string> { "Legal" } }
            }, NullLoggerShim.Instance);

        private static ActivityStagingPass PassFor(AuditFilterConfig filterConfig, ILogger logger)
            => new ActivityStagingPass(filterConfig, GroupsCache(), new Common.Entities.Config.UserGroupsFilterModel("Finance"), logger);

        [TestMethod]
        public async Task SavePipeline_EachOutcomeIsStagedAndCountedCorrectly()
        {
            var imported = SpEvent(InScopeUpn);
            var userOutOfScope = SpEvent(OutOfScopeUpn);
            var urlOutOfScope = SpEvent(InScopeUpn, "https://other-tenant.sharepoint.com/sites/y/b.docx");
            var alreadyProcessed = SpEvent(InScopeUpn);
            var repeatOfImported = SpEvent(InScopeUpn, id: imported.Id);

            var cache = ActivityImportCache.GetEmptyCache();
            cache.RememberProcessedEvent(alreadyProcessed);

            var logger = new RecordingLogger();
            var filter = new PredicateAuditFilterConfig(e => !ReferenceEquals(e, urlOutOfScope));
            var writer = new InMemoryActivityStagingWriter();
            var batch = writer.CreateBatch(null);

            var result = await PassFor(filter, logger).RunAsync(
                SetOf(imported, userOutOfScope, urlOutOfScope, alreadyProcessed, repeatOfImported),
                cache, batch, stagingTableName: null, mergeLock: null);

            Assert.AreEqual(5, result.Stats.Total, "Total counts everything the batch was handed.");
            Assert.AreEqual(1, result.Stats.Imported);
            Assert.AreEqual(1, result.Stats.UsersOutOfScope);
            Assert.AreEqual(1, result.Stats.URLsOutOfScope);

            // Deliberately zero: an event skipped by the de-duplication cache (or repeated inside the set)
            // increments NO counter at all. That is long-standing behaviour, not an oversight of the split -
            // ActivityStagingRules returns SaveResultEnum.NotSaved for it and the caller's counter block is
            // guarded by !IsDuplicate. Changing it would change an operator-facing number.
            Assert.AreEqual(0, result.Stats.ProcessedAlready);

            Assert.AreEqual(1, writer.LastBatch.Rows.Count, "Only the in-scope, in-filter, not-yet-seen event may be staged.");
            Assert.AreEqual(imported.Id, writer.LastBatch.Rows[0].Id);

            CollectionAssert.AreEquivalent(new List<AbstractAuditLogContent> { imported }, result.SavedToSql.ToList(),
                "Only staged events go to the metadata pass - anything else has no audit_events row to attach metadata to.");
        }

        [TestMethod]
        public async Task SavePipeline_UserOutsideGroupsFilter_LogsTheOperatorLineNamingTheUser()
        {
            var logger = new RecordingLogger();
            var writer = new InMemoryActivityStagingWriter();
            var batch = writer.CreateBatch(null);

            await PassFor(new AllowAllFilterConfig(), logger).RunAsync(
                SetOf(SpEvent(OutOfScopeUpn), SpEvent(InScopeUpn)),
                ActivityImportCache.GetEmptyCache(), batch, stagingTableName: null, mergeLock: null);

            var skipLines = logger.Entries.Where(e => e.Message.StartsWith("Skipping activity report for user")).ToList();
            Assert.AreEqual(1, skipLines.Count, "Exactly the one filtered user is reported.");
            Assert.AreEqual(LogLevel.Information, skipLines[0].Level);
            Assert.AreEqual($"Skipping activity report for user '{OutOfScopeUpn}' - not in user groups filter", skipLines[0].Message);
        }

        [TestMethod]
        public async Task SavePipeline_SerialMode_MergesTheSharedStagingTableWithNoMergeLock()
        {
            var writer = new InMemoryActivityStagingWriter();
            var batch = writer.CreateBatch(null);

            await PassFor(new AllowAllFilterConfig(), new RecordingLogger()).RunAsync(
                SetOf(SpEvent(InScopeUpn)), ActivityImportCache.GetEmptyCache(), batch,
                stagingTableName: null, mergeLock: null);

            var merged = writer.LastBatch;
            Assert.AreEqual(1, merged.MergeCount);
            Assert.IsNull(merged.LastStagingTableName,
                "No override means InsertBatch uses the entity's [TempTableName] - the single shared staging table.");
            Assert.IsNull(merged.LastMergeLock,
                "On the serial path the whole save is already serialised by the static save semaphore, so no merge lock is passed.");
            Assert.AreEqual(10000, merged.LastInsertsPerThread,
                "The production staging fan-out size. Asserted as a literal, not against the constant, so changing the constant is a visible change.");

            StringAssert.Contains(merged.LastMergeSql, ActivityImportConstants.STAGING_TABLE_ACTIVITY);
            Assert.IsFalse(merged.LastMergeSql.Contains("${STAGING_TABLE_ACTIVITY}"),
                "Every placeholder in the merge script must be substituted, or the merge is invalid SQL.");
            Assert.AreEqual(1, merged.RowCountAtMerge, "The staged rows must be in the batch before the merge runs, not after.");
        }

        [TestMethod]
        public async Task SavePipeline_ShardedMode_PointsTheMergeAtTheShardAndHandsDownTheSharedWriteLock()
        {
            // Deliberately NOT ActivitySaveConcurrencyPolicy.NewShardedStagingTableName(): in Release builds
            // the shared table name is a prefix of the sharded one, so a generated name could not tell the
            // two apart. The generator itself is covered by ActivitySaveConcurrencyPolicyTests.
            const string shard = "##unit_test_activity_shard_a1b2c3";
            var sharedWriteLock = new SemaphoreSlim(1, 1);
            var writer = new InMemoryActivityStagingWriter();
            var batch = writer.CreateBatch(null);

            await PassFor(new AllowAllFilterConfig(), new RecordingLogger()).RunAsync(
                SetOf(SpEvent(InScopeUpn)), ActivityImportCache.GetEmptyCache(), batch,
                stagingTableName: shard, mergeLock: sharedWriteLock);

            var merged = writer.LastBatch;
            Assert.AreEqual(shard, merged.LastStagingTableName, "Each concurrent save must load into its OWN staging table.");
            Assert.AreSame(sharedWriteLock, merged.LastMergeLock,
                "The merge writes shared lookup/fact tables, so the save's shared-write lock must reach it.");

            StringAssert.Contains(merged.LastMergeSql, shard, "The merge must read the shard this save just loaded...");
            Assert.IsFalse(merged.LastMergeSql.Contains(ActivityImportConstants.STAGING_TABLE_ACTIVITY),
                "...and must not touch the shared staging table another save may be loading.");
        }

        [TestMethod]
        public async Task SavePipeline_StagingRowCarriesTheEventsOwnUserAndUrlUnchanged()
        {
            // A staging row that took the wrong UPN, or mangled a non-Latin URL, would corrupt users/urls on
            // the merge - and both are customer text, so neither may be normalised here.
            const string greekUrl = "https://contoso.sharepoint.com/sites/x/Καλημέρα κόσμε.docx";
            var log = SpEvent(InScopeUpn, greekUrl);
            var writer = new InMemoryActivityStagingWriter();
            var batch = writer.CreateBatch(null);

            await PassFor(new AllowAllFilterConfig(), new RecordingLogger()).RunAsync(
                SetOf(log), ActivityImportCache.GetEmptyCache(), batch, stagingTableName: null, mergeLock: null);

            var row = writer.LastBatch.Rows.Single();
            Assert.AreEqual(log.Id, row.Id);
            Assert.AreEqual(InScopeUpn, row.UserName, "The staging row's user_name is the event's own UserId.");
            Assert.AreEqual(greekUrl, row.ObjectId, "A Unicode URL inside the column width must reach SQL byte-for-byte.");
            Assert.AreEqual(log.CreationTime, row.TimeStamp);
            Assert.AreEqual(ActivityImportConstants.WORKLOAD_SP, row.Workload);
            Assert.AreEqual("a.docx", row.FileName);
        }

        [TestMethod]
        public async Task SavePipeline_SlowScopeCheck_IsChargedToTheDedupPhaseNotTheMerge()
        {
            // The two halves of the crossed pair below exist because a single "is a timing recorded?" test
            // would pass even if the two phases were swapped.
            const int slowMs = 400;
            var writer = new InMemoryActivityStagingWriter();
            var batch = writer.CreateBatch(null);
            var slowFilter = new PredicateAuditFilterConfig(e => { Thread.Sleep(slowMs); return true; });

            var sw = Stopwatch.StartNew();
            var result = await PassFor(slowFilter, new RecordingLogger()).RunAsync(
                SetOf(SpEvent(InScopeUpn)), ActivityImportCache.GetEmptyCache(), batch,
                stagingTableName: null, mergeLock: null);
            sw.Stop();

            Assert.IsTrue(sw.ElapsedMilliseconds >= slowMs * 0.75, "Precondition: the pass really did take the injected delay.");
            Assert.IsTrue(result.Stats.SaveDedupMs >= slowMs * 0.75,
                $"The scope check happens in the dedup phase; SaveDedupMs was {result.Stats.SaveDedupMs}.");
            Assert.IsTrue(result.Stats.SaveMergeMs < slowMs * 0.75,
                $"...and must not be charged to the merge; SaveMergeMs was {result.Stats.SaveMergeMs}.");
        }

        [TestMethod]
        public async Task SavePipeline_SlowMerge_IsChargedToTheMergePhaseNotTheDedup()
        {
            const int slowMs = 400;
            var writer = new InMemoryActivityStagingWriter { OnMerge = () => Task.Delay(slowMs) };
            var batch = writer.CreateBatch(null);

            var sw = Stopwatch.StartNew();
            var result = await PassFor(new AllowAllFilterConfig(), new RecordingLogger()).RunAsync(
                SetOf(SpEvent(InScopeUpn)), ActivityImportCache.GetEmptyCache(), batch,
                stagingTableName: null, mergeLock: null);
            sw.Stop();

            Assert.IsTrue(sw.ElapsedMilliseconds >= slowMs * 0.75, "Precondition: the pass really did take the injected delay.");
            Assert.IsTrue(result.Stats.SaveMergeMs >= slowMs * 0.75,
                $"The staging load + merge is the merge phase; SaveMergeMs was {result.Stats.SaveMergeMs}.");
            Assert.IsTrue(result.Stats.SaveDedupMs < slowMs * 0.75,
                $"...and must not be charged to the dedup phase; SaveDedupMs was {result.Stats.SaveDedupMs}.");
        }

        /// <summary>MockUserGroupsCache takes an ILogger; these tests do not assert on its output.</summary>
        private class NullLoggerShim : ILogger
        {
            public static readonly NullLoggerShim Instance = new NullLoggerShim();
            public IDisposable BeginScope<TState>(TState state) => new Scope();
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
            private class Scope : IDisposable { public void Dispose() { } }
        }
    }
}
