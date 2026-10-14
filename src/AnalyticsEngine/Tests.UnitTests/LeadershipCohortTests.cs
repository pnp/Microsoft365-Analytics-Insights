extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.LeadershipCohort;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.State;
using Common.Entities.UserScope;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot Adoption leadership comparison (#654): the bounded Graph refresh, the stored membership, the
    /// suppressed aggregate, its caching and every way it reports that it cannot be trusted. All identifiers are
    /// synthetic (zero-based GUIDs, Contoso).
    /// </summary>
    [TestClass]
    public partial class LeadershipCohortTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);
        private static readonly Guid GroupId = new Guid("00000000-0000-0000-0000-000000000654");

        [TestMethod]
        public async Task Refresh_PagesThroughDirectMembersAndStoresMatchedIdsOnly()
        {
            var store = NewStore();
            await Configure(store);
            var directory = new FakeDirectory(memberCount: 2500);

            var snapshot = await Refresher(store, directory).RefreshAsync();

            Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready, snapshot.Status);
            Assert.AreEqual(3, directory.MemberPageCalls, "999 + 999 + 502 direct members is three Graph pages.");
            Assert.AreEqual(2500, snapshot.DirectMembers);
            Assert.AreEqual(1250, snapshot.MatchedUsers, "The fake mapper only knows even-numbered members.");
            Assert.AreEqual(Now, snapshot.RefreshedUtc);
            Assert.AreEqual("Contoso Leadership", snapshot.GroupDisplayName);

            var stored = await store.GetSnapshotAsync();
            var members = await store.ReadMembersAsync(stored);
            Assert.AreEqual(1250, members.Count);
            Assert.IsTrue(members.Contains(2) && !members.Contains(1));

            // Stored as SQL ids only: no object id, UPN or name is persisted with the membership.
            var raw = await store.RawValues.GetStringAsync(LeadershipCohortStore.PageKey(stored.Slot, 0));
            Assert.IsFalse(raw.IndexOf("contoso", StringComparison.OrdinalIgnoreCase) >= 0 || raw.Contains("0000-0000"), raw.Substring(0, 80));
        }

        [TestMethod]
        public async Task Refresh_RefusesAGroupLargerThanTheCapWithoutReadingAllOfIt()
        {
            var store = NewStore();
            await Configure(store);
            var directory = new FakeDirectory(memberCount: 50000);

            var snapshot = await Refresher(store, directory, maxMembers: 1500).RefreshAsync();

            Assert.AreEqual(LeadershipCohortRefreshStatuses.TooLarge, snapshot.Status);
            Assert.AreEqual(2, directory.MemberPageCalls, "Stops on the page that crosses the cap.");
            Assert.IsNull(await store.ReadMembersAsync(await store.GetSnapshotAsync()));
        }

        [TestMethod]
        public async Task Refresh_StopsWhenThePageBudgetRunsOutEvenIfPagesAreEmpty()
        {
            var store = NewStore();
            await Configure(store);
            var directory = new FakeDirectory(memberCount: 0) { EndlessEmptyPages = true };

            var snapshot = await Refresher(store, directory, maxMembers: 1000).RefreshAsync();

            Assert.AreEqual(LeadershipCohortRefreshStatuses.TooLarge, snapshot.Status);
            Assert.AreEqual(3, directory.MemberPageCalls, "ceil(1000 / 999) + 1 pages is the budget.");
        }

        [TestMethod]
        public async Task Refresh_AcceptsAGroupExactlyAtTheCap()
        {
            var store = NewStore();
            await Configure(store);

            var snapshot = await Refresher(store, new FakeDirectory(memberCount: 1998), maxMembers: 1998).RefreshAsync();

            Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready, snapshot.Status);
            Assert.AreEqual(1998, snapshot.DirectMembers);
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.Forbidden, LeadershipCohortRefreshStatuses.PermissionMissing, null)]
        [DataRow(HttpStatusCode.Unauthorized, LeadershipCohortRefreshStatuses.PermissionMissing, null)]
        [DataRow(HttpStatusCode.NotFound, LeadershipCohortRefreshStatuses.GroupNotFound, null)]
        [DataRow(HttpStatusCode.ServiceUnavailable, LeadershipCohortRefreshStatuses.Failed, LeadershipCohortFailureKinds.GraphError)]
        public async Task Refresh_GraphErrorsAreRecordedAndDiscardThePreviousMembers(HttpStatusCode status, string expected, string kind)
        {
            var store = NewStore();
            await Configure(store);
            var directory = new FakeDirectory(memberCount: 20);
            Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready, (await Refresher(store, directory).RefreshAsync()).Status);

            directory.FailWith = status;
            var snapshot = await Refresher(store, directory, utcNow: () => Now.AddHours(7)).RefreshAsync();

            Assert.AreEqual(expected, snapshot.Status);
            Assert.AreEqual(kind, snapshot.FailureKind);
            Assert.AreEqual((int)status, snapshot.HttpStatus);
            Assert.IsNull(snapshot.RefreshedUtc);

            var stored = await store.GetSnapshotAsync();
            Assert.AreEqual(expected, stored.Status, "A failure replaces the header: there is no stale-success fallback.");
            Assert.IsNull(await store.ReadMembersAsync(stored));

            var comparison = await Provider(store, Now.AddHours(7)).GetAsync(Analysis(20), scopedView: false);
            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, comparison.Status);
            Assert.AreEqual(LeadershipComparisonReasons.ForRefreshStatus(expected), comparison.Reason);
            Assert.IsNull(comparison.LicensedLeaders);
        }

        [TestMethod]
        public async Task Refresh_GroupMissingFromGraphIsGroupNotFound()
        {
            var store = NewStore();
            await Configure(store);

            var snapshot = await Refresher(store, new FakeDirectory(20) { GroupExists = false }).RefreshAsync();

            Assert.AreEqual(LeadershipCohortRefreshStatuses.GroupNotFound, snapshot.Status);
        }

        [TestMethod]
        public async Task Refresh_ClientAndSqlFailuresAreClassified()
        {
            var store = NewStore();
            await Configure(store);

            var noClient = await new LeadershipCohortRefresher(store.Store, () => throw new InvalidOperationException("no credentials"),
                new FakeMapper(), utcNow: () => Now, gate: new SemaphoreSlim(1, 1)).RefreshAsync();
            Assert.AreEqual(LeadershipCohortRefreshStatuses.Failed, noClient.Status);
            Assert.AreEqual(LeadershipCohortFailureKinds.GraphClient, noClient.FailureKind);

            var noSql = await new LeadershipCohortRefresher(store.Store, () => new FakeDirectory(20),
                new FakeMapper { Throw = true }, utcNow: () => Now, gate: new SemaphoreSlim(1, 1)).RefreshAsync();
            Assert.AreEqual(LeadershipCohortRefreshStatuses.Failed, noSql.Status);
            Assert.AreEqual(LeadershipCohortFailureKinds.SqlError, noSql.FailureKind);
        }

        [TestMethod]
        public async Task Refresh_IsSingleFlightAndDoesNothingWhenNotConfigured()
        {
            var store = NewStore();
            Assert.IsNull(await Refresher(store, new FakeDirectory(20)).RefreshAsync(), "Not configured: nothing to refresh.");

            await Configure(store);
            var gate = new SemaphoreSlim(1, 1);
            await gate.WaitAsync();
            var directory = new FakeDirectory(20);
            Assert.IsNull(await Refresher(store, directory, gate: gate).RefreshAsync(), "A refresh already running wins.");
            Assert.AreEqual(0, directory.MemberPageCalls);
        }

        [TestMethod]
        public void IsDue_FollowsRevisionSuccessAndRetryIntervals()
        {
            var settings = new LeadershipCohortSettings { GroupId = GroupId.ToString(), Revision = "r1" };
            var ok = new LeadershipCohortSnapshot { SettingsRevision = "r1", Status = LeadershipCohortRefreshStatuses.Ready, AttemptedUtc = Now };
            var failed = new LeadershipCohortSnapshot { SettingsRevision = "r1", Status = LeadershipCohortRefreshStatuses.PermissionMissing, AttemptedUtc = Now };

            Assert.IsFalse(LeadershipCohortRefresher.IsDue(null, null, Now), "Off by default.");
            Assert.IsTrue(LeadershipCohortRefresher.IsDue(settings, null, Now));
            Assert.IsFalse(LeadershipCohortRefresher.IsDue(settings, ok, Now.AddHours(5)));
            Assert.IsTrue(LeadershipCohortRefresher.IsDue(settings, ok, Now.AddHours(6)));
            Assert.IsFalse(LeadershipCohortRefresher.IsDue(settings, failed, Now.AddMinutes(59)));
            Assert.IsTrue(LeadershipCohortRefresher.IsDue(settings, failed, Now.AddHours(1)));
            ok.SettingsRevision = "r0";
            Assert.IsTrue(LeadershipCohortRefresher.IsDue(settings, ok, Now), "A changed group is refreshed at once.");
        }

        [TestMethod]
        public async Task Store_SlotsAlternateAndShrinkingRemovesSurplusPages()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(9000), mapAll: true).RefreshAsync();
            var first = await store.GetSnapshotAsync();
            Assert.AreEqual(5, first.PageCount);

            await Refresher(store, new FakeDirectory(30), mapAll: true).RefreshAsync();
            var second = await store.GetSnapshotAsync();
            Assert.AreNotEqual(first.Slot, second.Slot);
            Assert.AreEqual(30, (await store.ReadMembersAsync(second)).Count);

            await Refresher(store, new FakeDirectory(40), mapAll: true).RefreshAsync();
            var third = await store.GetSnapshotAsync();
            Assert.AreEqual(first.Slot, third.Slot);
            Assert.AreEqual(1, third.PageCount);
            for (var page = 1; page < LeadershipCohortStore.MaxPages; page++)
            {
                Assert.IsFalse(await store.RawValues.ExistsAsync(LeadershipCohortStore.PageKey(first.Slot, page)), "Surplus page " + page + " must be removed.");
            }
        }
    }
}
