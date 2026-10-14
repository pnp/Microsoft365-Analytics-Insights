extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.LeadershipCohort;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    public partial class LeadershipCohortTests
    {
        [TestMethod]
        public void Compare_SuppressesBelowTenAndCarriesNoCounts()
        {
            var nine = LeadershipAdoptionCalculator.Compare(Analysis(40), Enumerable.Range(1, 9).ToList(), Now);
            Assert.AreEqual(LeadershipComparisonStatuses.Suppressed, nine.Status);
            Assert.AreEqual(10, nine.MinimumCohort);
            Assert.IsNull(nine.LicensedLeaders);
            Assert.IsNull(nine.ActiveLeaders);
            Assert.IsNull(nine.LeaderAdoptionRatePct);
            Assert.IsNull(nine.LeaderAverageScore);
            Assert.IsNull(nine.TenantAdoptionRatePct, "A suppressed result must not even let a reader subtract tenant figures.");

            var ten = LeadershipAdoptionCalculator.Compare(Analysis(40), Enumerable.Range(1, 10).ToList(), Now);
            Assert.AreEqual(LeadershipComparisonStatuses.Ok, ten.Status);
            Assert.AreEqual(10, ten.LicensedLeaders);
        }

        /// <summary>
        /// The tenant's figures sit beside the leaders' over the same denominator, so tenant minus leaders describes
        /// everyone outside the cohort. A complement of 1..9 is suppressed like a cohort of 1..9; zero exposes nobody.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, LeadershipComparisonStatuses.Ok, null)]
        [DataRow(1, LeadershipComparisonStatuses.Suppressed, LeadershipComparisonReasons.ComplementTooSmall)]
        [DataRow(9, LeadershipComparisonStatuses.Suppressed, LeadershipComparisonReasons.ComplementTooSmall)]
        [DataRow(10, LeadershipComparisonStatuses.Ok, null)]
        public void Compare_ComplementOutsideTheCohortMustAlsoClearTheMinimum(int complement, string status, string reason)
        {
            const int leaders = 30;
            var analysis = Analysis(leaders + complement);
            // Members beyond the licensed population (unlicensed leaders) must not hide a small complement.
            var members = Enumerable.Range(1, leaders).Concat(Enumerable.Range(500, 25)).ToList();

            var comparison = LeadershipAdoptionCalculator.Compare(analysis, members, Now);

            Assert.AreEqual(status, comparison.Status, "complement " + complement);
            Assert.AreEqual(reason, comparison.Reason, "complement " + complement);
            Assert.AreEqual(10, comparison.MinimumCohort);
            if (status == LeadershipComparisonStatuses.Ok)
            {
                Assert.AreEqual(leaders, comparison.LicensedLeaders);
                if (complement == 0)
                {
                    Assert.AreEqual(comparison.TenantAdoptionRatePct, comparison.LeaderAdoptionRatePct, "Every licensed user is a leader.");
                    Assert.AreEqual(0d, comparison.AdoptionGapPts);
                }
                return;
            }

            AssertWithholdsEveryFigure(comparison);
        }

        [TestMethod]
        public void Compare_ComplementFollowsAStricterSegmentMinimum()
        {
            var analysis = Analysis(40);
            analysis.Summary.Options = new CopilotAdoptionOptions { MinSeatsPerSegment = 15 };
            // 26 leaders, 14 outside: the cohort clears 15 but the complement does not.
            var comparison = LeadershipAdoptionCalculator.Compare(analysis, Enumerable.Range(1, 26).ToList(), Now);
            Assert.AreEqual(LeadershipComparisonStatuses.Suppressed, comparison.Status);
            Assert.AreEqual(LeadershipComparisonReasons.ComplementTooSmall, comparison.Reason);
            Assert.AreEqual(15, comparison.MinimumCohort);
            Assert.AreEqual(LeadershipComparisonStatuses.Ok, LeadershipAdoptionCalculator.Compare(analysis, Enumerable.Range(1, 25).ToList(), Now).Status);
        }

        [TestMethod]
        public void Compare_WithheldComplementSerialisesNoCountOrMembership()
        {
            var comparison = LeadershipAdoptionCalculator.Compare(Analysis(35), Enumerable.Range(1, 30).ToList(), Now);
            var json = Newtonsoft.Json.Linq.JObject.FromObject(comparison);
            foreach (var property in json.Properties())
            {
                if (property.Name == "status" || property.Name == "reason" || property.Name == "minimumCohort"
                    || property.Name == "membershipRefreshedUtc" || property.Name == "figuresIncomplete") continue;
                Assert.AreEqual(Newtonsoft.Json.Linq.JTokenType.Null, property.Value.Type, property.Name + " must be withheld.");
            }
            var text = json.ToString(Formatting.None);
            Assert.IsFalse(text.Contains("30") || text.Contains("35") || text.Contains("contoso"), text);
            Assert.IsFalse(comparison.FiguresIncomplete);
        }

        private static void AssertWithholdsEveryFigure(LeadershipAdoptionComparison comparison)
        {
            Assert.IsNull(comparison.LicensedLeaders);
            Assert.IsNull(comparison.ActiveLeaders);
            Assert.IsNull(comparison.HabitualLeaders);
            Assert.IsNull(comparison.LeaderAdoptionRatePct);
            Assert.IsNull(comparison.LeaderHabitRatePct);
            Assert.IsNull(comparison.LeaderAverageScore);
            Assert.IsNull(comparison.TenantAdoptionRatePct);
            Assert.IsNull(comparison.TenantHabitRatePct);
            Assert.IsNull(comparison.TenantAverageScore);
            Assert.IsNull(comparison.AdoptionGapPts);
            Assert.IsNull(comparison.HabitGapPts);
            Assert.IsNull(comparison.ScoreGap);
        }

        [TestMethod]
        public void Compare_LeadersWithoutALicenceDoNotCountTowardsTheMinimum()
        {
            // 30 members, only 9 of them licensed: suppressed - the cohort is the licensed leaders.
            var leaders = Enumerable.Range(41, 30).Concat(Enumerable.Range(1, 9)).ToList();
            Assert.AreEqual(LeadershipComparisonStatuses.Suppressed, LeadershipAdoptionCalculator.Compare(Analysis(40), leaders, Now).Status);
        }

        [TestMethod]
        public void Compare_MinimumFollowsAStricterSegmentMinimumButNeverGoesBelowTen()
        {
            var analysis = Analysis(40);
            analysis.Summary.Options = new CopilotAdoptionOptions { MinSeatsPerSegment = 3 };
            Assert.AreEqual(10, LeadershipAdoptionCalculator.EffectiveMinimum(analysis.Summary.Options));
            Assert.AreEqual(LeadershipComparisonStatuses.Suppressed, LeadershipAdoptionCalculator.Compare(analysis, Enumerable.Range(1, 9).ToList(), Now).Status);

            analysis.Summary.Options = new CopilotAdoptionOptions { MinSeatsPerSegment = 15 };
            var fourteen = LeadershipAdoptionCalculator.Compare(analysis, Enumerable.Range(1, 14).ToList(), Now);
            Assert.AreEqual(LeadershipComparisonStatuses.Suppressed, fourteen.Status);
            Assert.AreEqual(15, fourteen.MinimumCohort);
        }

        [TestMethod]
        public void Compare_RatesAndGapsUseTheSameDenominatorAsTheTenant()
        {
            var analysis = Analysis(40);
            // Leaders 1..20: ids 1-5 never used, 6-10 trialling, 11-20 established (score 80).
            var comparison = LeadershipAdoptionCalculator.Compare(analysis, Enumerable.Range(1, 20).ToList(), Now);

            Assert.AreEqual(20, comparison.LicensedLeaders);
            Assert.AreEqual(15, comparison.ActiveLeaders);
            Assert.AreEqual(10, comparison.HabitualLeaders);
            Assert.AreEqual(75d, comparison.LeaderAdoptionRatePct);
            Assert.AreEqual(50d, comparison.LeaderHabitRatePct);
            Assert.AreEqual(Math.Round((5 * 20 + 10 * 80) / 20d, 1), comparison.LeaderAverageScore);
            Assert.AreEqual(60d, comparison.TenantAdoptionRatePct);
            Assert.AreEqual(15d, comparison.AdoptionGapPts);
            Assert.AreEqual(50d - analysis.Summary.HabitRatePct, comparison.HabitGapPts);
            Assert.AreEqual(Now, comparison.MembershipRefreshedUtc);
            Assert.IsFalse(comparison.FiguresIncomplete);

            analysis.LicensedUsersCapped = true;
            Assert.IsTrue(LeadershipAdoptionCalculator.Compare(analysis, Enumerable.Range(1, 20).ToList(), Now).FiguresIncomplete);
        }

        [TestMethod]
        public async Task Provider_IsOffByDefault()
        {
            var comparison = await new LeadershipComparisonProvider(() => NewStore().Store, () => Now).GetAsync(Analysis(40), scopedView: false);
            Assert.AreEqual(LeadershipComparisonStatuses.NotConfigured, comparison.Status);
            Assert.IsNull(comparison.LicensedLeaders);
        }

        [TestMethod]
        public async Task Provider_ChangedGroupIsPendingUntilItsOwnRefresh()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(40), mapAll: true).RefreshAsync();
            await store.Store.SaveSettingsAsync(new LeadershipCohortSettings { GroupId = Guid.NewGuid().ToString(), Revision = "r2", UpdatedUtc = Now });

            var comparison = await Provider(store).GetAsync(Analysis(40), scopedView: false);

            Assert.AreEqual(LeadershipComparisonStatuses.PendingRefresh, comparison.Status, "The old group's members must not be shown under the new group.");
            Assert.IsNull(comparison.LicensedLeaders);
        }

        [TestMethod]
        public async Task Provider_StaleAfterSeventyTwoHours()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(40), mapAll: true).RefreshAsync();

            Assert.AreEqual(LeadershipComparisonStatuses.Ok, (await Provider(store, Now.AddHours(72)).GetAsync(Analysis(40), false)).Status);
            var stale = await Provider(store, Now.AddHours(72).AddMinutes(1)).GetAsync(Analysis(40), false);
            Assert.AreEqual(LeadershipComparisonStatuses.Stale, stale.Status);
            Assert.AreEqual(Now, stale.MembershipRefreshedUtc);
            Assert.IsNull(stale.LicensedLeaders, "Stale membership is reported, not used.");
        }

        [TestMethod]
        public async Task Provider_ScopedViewNeverComparesASubset()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(40), mapAll: true).RefreshAsync();

            var comparison = await Provider(store).GetAsync(Analysis(40), scopedView: true);

            Assert.AreEqual(LeadershipComparisonStatuses.ScopedView, comparison.Status);
            Assert.IsNull(comparison.LicensedLeaders);
        }

        [TestMethod]
        public async Task Provider_APageFromAnotherRefreshIsMembershipChangingNotAPartialSet()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(40), mapAll: true).RefreshAsync();
            var snapshot = await store.Store.GetSnapshotAsync();
            await store.RawValues.SetStringAsync(LeadershipCohortStore.PageKey(snapshot.Slot, 0), "{\"v\":\"another\",\"ids\":[1,2,3]}");

            var comparison = await Provider(store).GetAsync(Analysis(40), false);

            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, comparison.Status);
            Assert.AreEqual(LeadershipComparisonReasons.MembershipChanging, comparison.Reason);
        }

        [TestMethod]
        public async Task Provider_UnreachableStateIsUnavailableNeverNotConfigured()
        {
            var store = new LeadershipCohortStore(new ThrowingStore(), isDurable: true);

            var comparison = await new LeadershipComparisonProvider(() => store, () => Now).GetAsync(Analysis(40), false);

            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, comparison.Status);
            Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, comparison.Reason);
        }

        [TestMethod]
        public async Task Provider_ComputesOncePerAnalysisAndMembershipAndCachesReads()
        {
            var counting = new CountingStore();
            var store = new TestStore(counting);
            await Configure(store);
            await Refresher(store, new FakeDirectory(40), mapAll: true).RefreshAsync();
            var now = Now;
            var provider = new LeadershipComparisonProvider(() => store.Store, () => now);
            var analysis = Analysis(40);

            counting.Reads = 0;
            var first = await provider.GetAsync(analysis, false);
            var readsForFirst = counting.Reads;
            var second = await provider.GetAsync(analysis, false);

            Assert.AreSame(first, second, "The O(N) pass runs once per cached analysis, not per reader.");
            Assert.AreEqual(readsForFirst, counting.Reads, "Within a minute no further state reads.");
            Assert.AreEqual(4, readsForFirst, "Settings, header, refresh request and one member page.");

            now += LeadershipComparisonProvider.HeaderCacheDuration;
            Assert.AreSame(first, await provider.GetAsync(analysis, false));
            Assert.AreEqual(readsForFirst + 3, counting.Reads, "After expiry only the three header rows are reread; unchanged members are reused.");

            var otherAnalysis = await provider.GetAsync(Analysis(40), false);
            Assert.AreNotSame(first, otherAnalysis);
        }

        [TestMethod]
        public async Task Provider_RefreshedMembershipIsPickedUpAfterInvalidate()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(20), mapAll: true).RefreshAsync();
            var provider = Provider(store);
            var analysis = Analysis(40);
            Assert.AreEqual(20, (await provider.GetAsync(analysis, false)).LicensedLeaders);

            await Refresher(store, new FakeDirectory(30), mapAll: true).RefreshAsync();
            Assert.AreEqual(20, (await provider.GetAsync(analysis, false)).LicensedLeaders, "Held for the header cache window.");
            provider.Invalidate();
            Assert.AreEqual(30, (await provider.GetAsync(analysis, false)).LicensedLeaders);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Provider_ManualRefreshIsPendingAfterFreshStateRead(bool invalidate)
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(20), mapAll: true).RefreshAsync();
            var now = Now;
            var provider = new LeadershipComparisonProvider(() => store.Store, () => now);
            var analysis = Analysis(40);
            var ready = await provider.GetAsync(analysis, false);
            Assert.AreEqual(LeadershipComparisonStatuses.Ok, ready.Status);

            var service = new LeadershipCohortService(store.Store, invalidate ? (Action)provider.Invalidate : null, () => now);
            Assert.IsNull((await service.RefreshAsync()).Refresh, "The admin acknowledges a pending durable request.");
            if (!invalidate)
            {
                Assert.AreSame(ready, await provider.GetAsync(analysis, false), "The header cache window remains intentional.");
                now += LeadershipComparisonProvider.HeaderCacheDuration;
            }

            var pending = await provider.GetAsync(analysis, false);
            Assert.AreEqual(LeadershipComparisonStatuses.PendingRefresh, pending.Status,
                "A fresh state read must not reuse the previous comparison while its membership refresh is pending.");
            AssertWithholdsEveryFigure(pending);

            await Refresher(store, new FakeDirectory(30), mapAll: true).RefreshIfDueAsync();
            provider.Invalidate();
            var refreshed = await provider.GetAsync(analysis, false);
            Assert.AreEqual(LeadershipComparisonStatuses.Ok, refreshed.Status);
            Assert.AreEqual(30, refreshed.LicensedLeaders);
        }

        [DataTestMethod]
        [DataRow(LeadershipCohortRefreshStatuses.Ready, LeadershipComparisonStatuses.Ok, null)]
        [DataRow(LeadershipCohortRefreshStatuses.GroupNotFound, LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.GroupNotFound)]
        [DataRow(LeadershipCohortRefreshStatuses.PermissionMissing, LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.PermissionMissing)]
        [DataRow(LeadershipCohortRefreshStatuses.TooLarge, LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.TooLarge)]
        [DataRow(LeadershipCohortRefreshStatuses.Failed, LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.RefreshFailed)]
        public async Task Provider_MatchingRequestAcknowledgementExposesTheSavedOutcome(string refreshStatus, string expectedStatus, string expectedReason)
        {
            var values = new CountingStore();
            var store = new TestStore(values);
            await Configure(store);
            await Refresher(store, new FakeDirectory(20), mapAll: true).RefreshAsync();
            var snapshot = await store.GetSnapshotAsync();
            await new LeadershipCohortService(store.Store, null, () => Now).RefreshAsync();
            snapshot.RefreshRequestId = (await store.Store.GetRefreshRequestAsync()).Id;
            snapshot.Status = refreshStatus;
            if (snapshot.IsReady) await store.Store.SaveMembersAsync(snapshot, Enumerable.Range(1, 20).ToList());
            else await store.Store.SaveFailureAsync(snapshot);

            values.Reads = 0;
            var comparison = await Provider(store).GetAsync(Analysis(40), false);
            Assert.AreEqual(expectedStatus, comparison.Status);
            Assert.AreEqual(expectedReason, comparison.Reason);
            Assert.AreEqual(snapshot.IsReady ? 4 : 3, values.Reads, "Failed outcomes never load member pages.");
            if (snapshot.IsReady) Assert.AreEqual(20, comparison.LicensedLeaders);
            else AssertWithholdsEveryFigure(comparison);
        }

        [TestMethod]
        public async Task Provider_RequestDuringSnapshotWriteRemainsPendingWithoutLoadingMembers()
        {
            var values = new CountingStore();
            var store = new TestStore(values);
            await Configure(store);
            await Refresher(store, new FakeDirectory(20), mapAll: true).RefreshAsync();
            var service = new LeadershipCohortService(store.Store, null, () => Now);
            await service.RefreshAsync();
            var firstRequest = await store.Store.GetRefreshRequestAsync();
            values.BeforeWrite = key => key == LeadershipCohortStore.SnapshotKey ? (Task)service.RefreshAsync() : Task.CompletedTask;
            var snapshot = await Refresher(store, new FakeDirectory(30), mapAll: true).RefreshIfDueAsync();
            values.BeforeWrite = null;
            Assert.AreEqual(firstRequest.Id, snapshot.RefreshRequestId);
            Assert.AreNotEqual(firstRequest.Id, (await store.Store.GetRefreshRequestAsync()).Id);

            values.Reads = 0;
            var provider = Provider(store);
            var analysis = Analysis(40);
            var pending = await provider.GetAsync(analysis, false);
            Assert.AreEqual(LeadershipComparisonStatuses.PendingRefresh, pending.Status);
            AssertWithholdsEveryFigure(pending);
            Assert.AreEqual(3, values.Reads, "Pending membership is not loaded.");
            var scoped = await provider.GetAsync(analysis, true);
            Assert.AreEqual(LeadershipComparisonStatuses.ScopedView, scoped.Status);
            AssertWithholdsEveryFigure(scoped);
            Assert.AreEqual(3, values.Reads, "Scope checks preserve the header cache.");

            await Refresher(store, new FakeDirectory(30), mapAll: true).RefreshIfDueAsync();
            provider.Invalidate();
            Assert.AreEqual(30, (await provider.GetAsync(analysis, false)).LicensedLeaders);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Provider_UnknownRefreshRequestFailsClosedAndRecovers(bool malformed)
        {
            var values = new CountingStore();
            var store = new TestStore(values);
            await Configure(store);
            await Refresher(store, new FakeDirectory(20), mapAll: true).RefreshAsync();
            var now = Now;
            var provider = new LeadershipComparisonProvider(() => store.Store, () => now);
            var analysis = Analysis(40);
            Assert.AreEqual(LeadershipComparisonStatuses.Ok, (await provider.GetAsync(analysis, false)).Status);
            if (malformed) await values.SetStringAsync(LeadershipCohortStore.RefreshRequestKey, "{");
            else values.FailReadKey = LeadershipCohortStore.RefreshRequestKey;
            provider.Invalidate();
            values.Reads = 0;

            var unavailable = await provider.GetAsync(analysis, false);
            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, unavailable.Status);
            Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, unavailable.Reason);
            AssertWithholdsEveryFigure(unavailable);
            Assert.AreEqual(3, values.Reads, "Request-read failures never load member pages or reuse old figures.");
            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, (await provider.GetAsync(analysis, true)).Status);
            Assert.AreEqual(3, values.Reads, "Failures retain their shorter cache interval.");

            values.FailReadKey = null;
            await values.DeleteAsync(LeadershipCohortStore.RefreshRequestKey);
            now += LeadershipComparisonProvider.FailureCacheDuration;
            Assert.AreEqual(LeadershipComparisonStatuses.Ok, (await provider.GetAsync(analysis, false)).Status);
        }

        [TestMethod]
        public void Summary_WithLeadershipComparisonIsACopy()
        {
            var summary = Analysis(40).Summary;
            var comparison = LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Ok);

            var copy = summary.WithLeadershipComparison(comparison);

            Assert.AreNotSame(summary, copy);
            Assert.IsNull(summary.LeadershipComparison, "The cached summary is shared between readers and must not change.");
            Assert.AreSame(comparison, copy.LeadershipComparison);
            Assert.AreEqual(summary.LicensedUsers, copy.LicensedUsers);
        }

        [TestMethod]
        public void Json_EveryPropertyHasAnExplicitName()
        {
            var types = new[]
            {
                typeof(LeadershipAdoptionComparison), typeof(LeadershipCohortSettings), typeof(LeadershipCohortSnapshot),
                typeof(LeadershipCohortStatusModel), typeof(LeadershipCohortRefreshModel), typeof(LeadershipCohortSaveRequest),
                typeof(LeadershipCohortError),
            };
            foreach (var type in types)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
                {
                    Assert.IsNotNull(property.GetCustomAttribute<JsonPropertyAttribute>(), type.Name + "." + property.Name + " has no [JsonProperty].");
                }
            }
            var page = typeof(LeadershipCohortStore).Assembly.GetType("Common.Entities.LeadershipCohort.LeadershipCohortMemberPage");
            CollectionAssert.AreEquivalent(new[] { "v", "ids" },
                page.GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyAttribute>().PropertyName).ToArray());

            var json = JsonConvert.SerializeObject(LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Suppressed));
            StringAssert.Contains(json, "\"status\":\"suppressed\"");
            Assert.IsFalse(json.IndexOf("userPrincipalName", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [TestMethod]
        public void ServerKeys_RefreshStatusesAllMapToAComparisonReason()
        {
            foreach (var status in new[] { LeadershipCohortRefreshStatuses.GroupNotFound, LeadershipCohortRefreshStatuses.PermissionMissing,
                LeadershipCohortRefreshStatuses.TooLarge, LeadershipCohortRefreshStatuses.Failed })
            {
                var reason = LeadershipComparisonReasons.ForRefreshStatus(status);
                Assert.IsTrue(typeof(LeadershipComparisonReasons).GetFields().Any(f => (string)f.GetRawConstantValue() == reason), status + " -> " + reason);
            }
        }
    }
}
