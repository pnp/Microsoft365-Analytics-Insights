using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// The per-event audit-log staging decision, lifted out of ActivityReportSqlPersistenceManager by
    /// issue #373. Runs with zero SQL Server and zero Graph: ActivityImportCache.GetEmptyCache() is a plain
    /// in-memory object, and the org-URL filter and user-groups lookup are injected as delegates.
    /// </summary>
    [TestClass]
    public class ActivityStagingRulesTests
    {
        private static AbstractAuditLogContent Event(Guid id, string upn = "someone@contoso.onmicrosoft.com")
            => new AzureADAuditLogContent { Id = id, UserId = upn, CreationTime = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc) };

        /// <summary>Records every staged event plus how often each injected collaborator was consulted.</summary>
        private class Harness
        {
            public readonly HashSet<Guid> DecidedInThisSet = new HashSet<Guid>();
            public readonly List<AbstractAuditLogContent> Staged = new List<AbstractAuditLogContent>();
            public int UrlFilterCalls;
            public int UserFilterCalls;

            public bool UrlInScopeResult = true;
            public bool UserInGroupsFilterResult = true;

            public ActivityImportCache Cache { get; } = ActivityImportCache.GetEmptyCache();

            public Task<ActivityStagingDecision> DecideAsync(AbstractAuditLogContent log, HashSet<Guid> decidedInThisSet = null, Action<AbstractAuditLogContent> stageRow = null)
            {
                return ActivityStagingRules.DecideAndRememberAsync(
                    log,
                    decidedInThisSet ?? DecidedInThisSet,
                    Cache,
                    l => { UrlFilterCalls++; return UrlInScopeResult; },
                    upn => { UserFilterCalls++; return Task.FromResult(UserInGroupsFilterResult); },
                    stageRow ?? (l => Staged.Add(l)));
            }
        }

        [TestMethod]
        public async Task Dedup_NewEvent_IsImported()
        {
            var h = new Harness();
            var log = Event(Guid.NewGuid());

            var decision = await h.DecideAsync(log);

            Assert.IsFalse(decision.IsDuplicate);
            Assert.IsTrue(decision.Staged);
            Assert.AreEqual(SaveResultEnum.Imported, decision.Result);
            CollectionAssert.AreEqual(new List<AbstractAuditLogContent> { log }, h.Staged);
            Assert.IsTrue(h.Cache.HaveSeenInProcessedOrIgnoredEvents(log), "An imported event must be remembered so a later batch does not stage it again.");
        }

        [TestMethod]
        public async Task Dedup_EventAlreadyImported_IsSkipped()
        {
            var h = new Harness();
            var log = Event(Guid.NewGuid());
            h.Cache.RememberProcessedEvent(log);

            var decision = await h.DecideAsync(log);

            Assert.IsTrue(decision.IsDuplicate);
            Assert.IsFalse(decision.Staged);
            Assert.AreEqual(0, h.Staged.Count);

            // The dedup check must short-circuit BEFORE the filters. The user-groups lookup is Graph-backed,
            // so consulting it for an already-imported event would be an outbound call per duplicate - and a
            // batch spans nearly the whole download window, so almost every event in it is a duplicate.
            Assert.AreEqual(0, h.UrlFilterCalls);
            Assert.AreEqual(0, h.UserFilterCalls);
        }

        [TestMethod]
        public async Task Dedup_EventPreviouslyIgnored_IsSkipped()
        {
            var h = new Harness();
            var log = Event(Guid.NewGuid());
            h.Cache.RememberNewlyIgnoredEvent(log);

            var decision = await h.DecideAsync(log);

            Assert.IsTrue(decision.IsDuplicate);
            Assert.AreEqual(0, h.UrlFilterCalls);
            Assert.AreEqual(0, h.UserFilterCalls);
        }

        [TestMethod]
        public async Task Dedup_SameIdTwiceInOneSet_IsOnlyStagedOnce()
        {
            // Deliberately a user-out-of-scope event: that is the ONLY outcome the cache does not
            // remember, so the second decision can only be caught by the in-set HashSet. With an imported
            // event the cache would catch it and deleting decidedInThisSet.Add would go unnoticed.
            var h = new Harness { UserInGroupsFilterResult = false };
            var id = Guid.NewGuid();

            var first = await h.DecideAsync(Event(id));
            var second = await h.DecideAsync(Event(id));

            Assert.AreEqual(SaveResultEnum.UserOutOfScope, first.Result);
            Assert.IsFalse(h.Cache.HaveSeenInProcessedOrIgnoredEvents(Event(id)), "Precondition: this outcome is not cached.");
            Assert.IsTrue(second.IsDuplicate);
            Assert.AreEqual(1, h.UserFilterCalls, "The repeated id must not be re-evaluated against the user filter.");
            Assert.AreEqual(0, h.Staged.Count);
        }

        [TestMethod]
        public async Task Dedup_CacheKeptCurrentAcrossBatches_SecondBatchSeesFirstBatchIds()
        {
            // The run-scoped cache is shared by every save batch of a cycle; the in-set HashSet is not.
            var h = new Harness();
            var log = Event(Guid.NewGuid());

            var firstBatch = new HashSet<Guid>();
            var secondBatch = new HashSet<Guid>();

            var first = await h.DecideAsync(log, firstBatch);
            var second = await h.DecideAsync(Event(log.Id), secondBatch);

            Assert.AreEqual(SaveResultEnum.Imported, first.Result);
            Assert.IsTrue(second.IsDuplicate, "The shared cache must carry the first batch's ids into the second batch.");
            Assert.AreEqual(1, h.Staged.Count);
        }

        [TestMethod]
        public async Task Dedup_UrlOutsideOrgUrlsWhitelist_IsCountedAsOutOfScopeAndRemembered()
        {
            var h = new Harness { UrlInScopeResult = false };
            var log = Event(Guid.NewGuid());

            var decision = await h.DecideAsync(log);

            Assert.IsFalse(decision.IsDuplicate);
            Assert.IsFalse(decision.Staged);
            Assert.AreEqual(SaveResultEnum.UrlOutOfScope, decision.Result);

            // Specifically the NEWLY-IGNORED bucket, not just "processed": asserting only
            // HaveSeenInProcessedOrIgnoredEvents would still pass if the rule were changed to
            // RememberProcessedEvent, which is a different fact about the event.
            var newlyIgnored = h.Cache.GetIds(ActivityImportCache.CacheType.NewlyIgnored);
            Assert.IsTrue(newlyIgnored.Any(chunk => chunk.ContainsKey(log.Id)));
            Assert.IsTrue(h.Cache.HaveSeenInProcessedOrIgnoredEvents(log));

            // ...and the user-groups lookup is never reached, so an out-of-scope site costs no Graph traffic.
            Assert.AreEqual(0, h.UserFilterCalls);
        }

        [TestMethod]
        public async Task Dedup_UserOutsideGroupsFilter_IsNotRemembered_SoItIsReconsideredLater()
        {
            var h = new Harness { UserInGroupsFilterResult = false };
            var log = Event(Guid.NewGuid());

            var decision = await h.DecideAsync(log);

            Assert.AreEqual(SaveResultEnum.UserOutOfScope, decision.Result);
            Assert.IsFalse(decision.Staged);

            // Deliberately asymmetric with the URL case: a user-filter miss is NOT written to the cache, so
            // the same event is reconsidered on a later batch/cycle (e.g. after the group membership or the
            // configured filter changes). Remembering it here would permanently suppress the event.
            Assert.IsFalse(h.Cache.HaveSeenInProcessedOrIgnoredEvents(log));

            // It is still recorded in the set, so it is not re-evaluated within this same set.
            Assert.IsTrue(h.DecidedInThisSet.Contains(log.Id));
        }

        [TestMethod]
        public async Task Dedup_StagingRowThatFailsToBuild_IsNotRememberedAsProcessed()
        {
            // The staging row is built before the event is remembered. If the row cannot be built the event
            // must stay unknown to the cache, otherwise a retry of the batch would silently skip it.
            var h = new Harness();
            var log = Event(Guid.NewGuid());

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                h.DecideAsync(log, stageRow: l => throw new InvalidOperationException("staging row could not be built")));

            Assert.IsFalse(h.Cache.HaveSeenInProcessedOrIgnoredEvents(log));
            Assert.IsFalse(h.DecidedInThisSet.Contains(log.Id));
        }

        [TestMethod]
        public async Task Dedup_UnicodeUpn_IsPassedToTheUserFilterUnchanged()
        {
            // The Management Activity API's UserId is NOT schema-guaranteed to be an Entra UPN - the
            // common schema also carries app@sharepoint, SIDs and GUIDs - so nothing upstream validates
            // it, and this rule must not assume. (Entra UPNs themselves are ASCII: see #402/#414.)
            //
            // Note the scope: this covers the IN-MEMORY staging decision only. Further downstream the
            // value is inserted into dbo.users.user_name, which is varchar(250)
            // (insert_activity_from_staging_table.sql), so a genuinely non-ASCII value would be
            // corrupted at rest. That is a property of the storage, not of these rules; what is pinned
            // here is that the rules pass whatever arrives through to the filter verbatim, with no
            // normalisation or ASCII folding of their own.
            const string nonAsciiUserId = "καλημέρα@contoso.onmicrosoft.com";
            string seenUpn = null;

            var cache = ActivityImportCache.GetEmptyCache();
            var log = Event(Guid.NewGuid(), nonAsciiUserId);

            var decision = await ActivityStagingRules.DecideAndRememberAsync(
                log, new HashSet<Guid>(), cache,
                l => true,
                upn => { seenUpn = upn; return Task.FromResult(true); },
                l => { });

            Assert.AreEqual(nonAsciiUserId, seenUpn);
            Assert.AreEqual(SaveResultEnum.Imported, decision.Result);
        }
    }
}
