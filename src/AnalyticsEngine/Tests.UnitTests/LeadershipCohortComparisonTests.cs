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
            var provider = Provider(store);
            var analysis = Analysis(40);

            counting.Reads = 0;
            var first = await provider.GetAsync(analysis, false);
            var readsForFirst = counting.Reads;
            var second = await provider.GetAsync(analysis, false);

            Assert.AreSame(first, second, "The O(N) pass runs once per cached analysis, not per reader.");
            Assert.AreEqual(readsForFirst, counting.Reads, "Within a minute no further state reads.");
            Assert.AreEqual(3, readsForFirst, "Settings, header and one member page.");

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
