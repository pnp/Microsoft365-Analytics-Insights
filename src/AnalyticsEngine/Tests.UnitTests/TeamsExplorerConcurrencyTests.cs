extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities;
using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// How much of the database Teams Explorer can ask for at once: a process-wide bound on its queries, and one build per
    /// section for every reader who misses the cache at the same time.
    /// </summary>
    [TestClass]
    public class TeamsExplorerConcurrencyTests
    {
        #region The store's query slots

        [TestMethod]
        public async Task Store_AQueryThatGetsNoSlotInTime_ReportsItsCode_AndOpensNoConnection()
        {
            var factory = new RecordingContextFactory();
            using (var slots = new SemaphoreSlim(0, SqlTeamsExplorerStore.MaxConcurrentQueries))
            {
                var store = new SqlTeamsExplorerStore(factory, 30, null, slots, TimeSpan.FromMilliseconds(20));

                var meetings = await store.GetMeetingsAsync(TeamsExplorerQuery.Create(28, DateTime.UtcNow));

                Assert.AreEqual(11, meetings.Queries.Count);
                foreach (var query in meetings.Queries)
                {
                    Assert.AreEqual(SqlTeamsExplorerStore.QueueTimeoutErrorCode, query.ErrorCode, query.Key);
                    Assert.AreEqual(SqlTeamsExplorerStore.QueueTimeoutMessage, query.Error, query.Key);
                }
                Assert.AreEqual(0, factory.Created, "No connection is opened without a slot.");
            }
        }

        /// <summary>
        /// Each query opens its connection only while it holds a slot, and gives the slot back however it ends - here every
        /// one fails - so a tab never holds more connections than there are slots, and a failure leaks none.
        /// </summary>
        [TestMethod]
        public async Task Store_OpensAConnectionOnlyWhileHoldingASlot_AndReleasesEverySlot()
        {
            const int Capacity = 2;
            using (var slots = new SemaphoreSlim(Capacity, Capacity))
            {
                var factory = new RecordingContextFactory(() => Capacity - slots.CurrentCount);
                var store = new SqlTeamsExplorerStore(factory, 30, null, slots, TimeSpan.FromSeconds(30));

                var tabs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.GetMeetingsAsync(TeamsExplorerQuery.Create(28, DateTime.UtcNow)))));

                Assert.AreEqual(4 * 11, factory.Created, "Every query ran once it had a slot.");
                Assert.IsTrue(factory.SlotsHeldAtOpen.All(held => held >= 1 && held <= Capacity),
                    "Slots held when a connection was opened: " + string.Join(", ", factory.SlotsHeldAtOpen));
                Assert.AreEqual(Capacity, slots.CurrentCount, "Every slot is back.");
                Assert.IsTrue(tabs.SelectMany(t => t.Queries).All(q => q.ErrorCode == null && !string.IsNullOrEmpty(q.Error)
                        && !string.IsNullOrEmpty(q.ErrorReason) && !string.IsNullOrEmpty(q.ErrorReference)),
                    "A query that failed reports its own failure - a reason and an error reference - not a queue.");
            }
        }

        [TestMethod]
        public void Store_EveryInstanceSharesTheProcessWideSlots()
        {
            var first = new SqlTeamsExplorerStore(new RecordingContextFactory());
            var second = new SqlTeamsExplorerStore(new RecordingContextFactory(), 25, null);

            Assert.AreSame(SqlTeamsExplorerStore.SharedQuerySlots, first.QuerySlots);
            Assert.AreSame(first.QuerySlots, second.QuerySlots, "The bound is for the process, not per request.");
        }

        #endregion

        #region One build per section

        [TestMethod]
        public async Task SharedBuild_ConcurrentMisses_ShareOneBuild_ThenTheCachedResult()
        {
            using (var cache = new MemoryCache("teamsExplorerSharedBuild"))
            {
                var builds = 0;
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Task<TeamsMeetings>> build = async () =>
                {
                    Interlocked.Increment(ref builds);
                    await release.Task.ConfigureAwait(false);
                    return new TeamsMeetings();
                };

                var waiting = Enumerable.Range(0, 5)
                    .Select(_ => TeamsExplorerAPIController.SharedBuild(cache, "meetings|28", build, TimeSpan.FromMinutes(1)))
                    .ToList();
                Assert.IsTrue(waiting.All(t => !t.IsCompleted), "Every request waits for the one build.");
                release.SetResult(true);
                var results = await Task.WhenAll(waiting);

                Assert.AreEqual(1, builds);
                Assert.IsTrue(results.All(r => ReferenceEquals(r, results[0])));
                Assert.AreSame(results[0], await TeamsExplorerAPIController.SharedBuild(cache, "meetings|28", build, TimeSpan.FromMinutes(1)));
                Assert.AreEqual(1, builds, "Served from the cache afterwards.");
            }
        }

        [TestMethod]
        public async Task SharedBuild_AFailedBuild_FailsEveryoneWaiting_AndIsNotKept()
        {
            using (var cache = new MemoryCache("teamsExplorerFailedBuild"))
            {
                var builds = 0;
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Task<TeamsMeetings>> failing = async () =>
                {
                    Interlocked.Increment(ref builds);
                    await release.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("synthetic build failure");
                };

                var first = TeamsExplorerAPIController.SharedBuild(cache, "k", failing, TimeSpan.FromMinutes(1));
                var second = TeamsExplorerAPIController.SharedBuild(cache, "k", failing, TimeSpan.FromMinutes(1));
                Assert.IsFalse(second.IsCompleted, "The second request waits on the first's build.");
                release.SetResult(true);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => first);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => second);
                Assert.AreEqual(1, builds);

                var model = await TeamsExplorerAPIController.SharedBuild(cache, "k", () => Task.FromResult(new TeamsMeetings()), TimeSpan.FromMinutes(1));
                Assert.IsNotNull(model, "The next request builds again.");
            }
        }

        [TestMethod]
        public async Task SharedBuild_ABuildOutlastingItsTtl_IsStillShared_AndItsResultThenExpiresOnTime()
        {
            // A build may queue for a slot and run every query of the tab, which can take longer than the 60-second ttl.
            // Its entry used to expire while it ran, and the next request started a second build of the same section.
            using (var cache = new MemoryCache("teamsExplorerLongBuild"))
            {
                var builds = 0;
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Task<TeamsMeetings>> build = async () =>
                {
                    Interlocked.Increment(ref builds);
                    await release.Task.ConfigureAwait(false);
                    return new TeamsMeetings();
                };
                var ttl = TimeSpan.FromMilliseconds(200);

                var first = TeamsExplorerAPIController.SharedBuild(cache, "k", build, ttl);
                await Task.Delay(ttl + ttl);
                var late = TeamsExplorerAPIController.SharedBuild(cache, "k", build, ttl);
                Assert.IsFalse(late.IsCompleted, "A request arriving after the ttl, while the build still runs, waits for it.");
                release.SetResult(true);

                Assert.AreSame(await first, await late);
                Assert.AreEqual(1, builds, "One build, however long it took.");

                // Once built, the result lasts its ttl from when it was ready, as before.
                await Task.Delay(ttl + ttl + ttl);
                await TeamsExplorerAPIController.SharedBuild(cache, "k", () => { Interlocked.Increment(ref builds); return Task.FromResult(new TeamsMeetings()); }, ttl);
                Assert.AreEqual(2, builds, "An expired result is built again.");
            }
        }

        /// <summary>
        /// Readers with and without See PII who open Meetings together share the one build, and each is still sent what they
        /// may see: the trim is applied to a copy after the shared build, never to what is cached.
        /// </summary>
        [TestMethod]
        public async Task Meetings_ReadersArrivingTogether_ShareOneBuild_AndEachGetsTheirOwnView()
        {
            var store = new GatedMeetingsStore();
            using (var cache = new MemoryCache("teamsExplorerMeetings"))
            using (var withPii = Host(store, cache, PortalRoles.SeePii))
            using (var withoutPii = Host(store, cache))
            {
                var requests = new[]
                {
                    withPii.Client.GetAsync("api/TeamsExplorer/meetings"),
                    withoutPii.Client.GetAsync("api/TeamsExplorer/meetings"),
                    withPii.Client.GetAsync("api/TeamsExplorer/meetings"),
                    withoutPii.Client.GetAsync("api/TeamsExplorer/meetings"),
                };
                var first = await Task.WhenAny(store.FirstBuildStarted.Task, Task.WhenAll(requests), Task.Delay(TimeSpan.FromSeconds(60)));
                Assert.AreSame(store.FirstBuildStarted.Task, first, "The requests reach the store and wait on its build.");
                await Task.Delay(100);
                store.Release.SetResult(true);
                var responses = await Task.WhenAll(requests);

                Assert.AreEqual(1, store.Builds, "One build for every reader who missed the cache together.");
                var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
                Assert.IsTrue(responses.All(r => r.StatusCode == HttpStatusCode.OK));
                Assert.AreEqual(1, ((JArray)JObject.Parse(bodies[0])["topOrganisers"]).Count, "A See PII reader gets the named leaderboard.");
                Assert.AreEqual(0, ((JArray)JObject.Parse(bodies[1])["topOrganisers"]).Count, "Others get the trimmed copy.");
                Assert.AreEqual(1, ((JArray)JObject.Parse(bodies[2])["topOrganisers"]).Count, "The cached section itself was never trimmed.");
            }
        }

        #endregion

        #region Helpers

        private static PortalTestHost Host(ITeamsExplorerStore store, ObjectCache cache, params string[] roles) =>
            new PortalTestHost(
                new[] { typeof(TeamsExplorerAPIController) },
                PortalTestHost.SignedIn(roles),
                PortalAccessPolicy.Enforcing,
                _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources(), null, null, cache));

        /// <summary>Counts connections opened, and how many slots were held at each; every open fails, as with no database.</summary>
        private sealed class RecordingContextFactory : IAnalyticsDbContextFactory
        {
            private readonly Func<int> _slotsHeld;
            private int _created;

            internal RecordingContextFactory(Func<int> slotsHeld = null)
            {
                _slotsHeld = slotsHeld;
            }

            internal int Created => Volatile.Read(ref _created);

            internal System.Collections.Concurrent.ConcurrentBag<int> SlotsHeldAtOpen { get; } = new System.Collections.Concurrent.ConcurrentBag<int>();

            public AnalyticsEntitiesContext Create()
            {
                Interlocked.Increment(ref _created);
                if (_slotsHeld != null)
                {
                    SlotsHeldAtOpen.Add(_slotsHeld());
                }
                throw new InvalidOperationException("synthetic: no database");
            }
        }

        private sealed class GatedMeetingsStore : ITeamsExplorerStore
        {
            private int _builds;

            internal int Builds => Volatile.Read(ref _builds);
            internal TaskCompletionSource<bool> FirstBuildStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref _builds);
                FirstBuildStarted.TrySetResult(true);
                await Release.Task.ConfigureAwait(false);
                return new TeamsMeetings { TopOrganisers = new List<TeamsNamedCountRow> { new TeamsNamedCountRow { Name = "Contoso Organiser", Count = 3 } } };
            }

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(0, 0));
            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources) => Task.FromResult(new TeamsOverview());
            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsAdoption());
            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsCollaboration());
            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable) => Task.FromResult(new TeamsConversations());
            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsPeople());
        }

        #endregion
    }
}
