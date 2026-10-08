extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.LeadershipCohort;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.State;
using Common.Entities.UserScope;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    public partial class LeadershipCohortTests
    {
        [TestMethod]
        public async Task Admin_SaveValidatesRefreshesAndClears()
        {
            var store = NewStore();
            var invalidations = 0;
            var directory = new FakeDirectory(25);
            var service = new LeadershipCohortService(store.Store, () => Refresher(store, directory, mapAll: true), () => invalidations++, () => Now);

            foreach (var bad in new[] { "not-a-guid", "00000000-0000-0000-0000-000000000000" })
            {
                var ex = await AssertThrows<LeadershipCohortRequestException>(() => service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = bad }));
                Assert.AreEqual(HttpStatusCode.BadRequest, ex.Status);
                Assert.AreEqual(LeadershipCohortErrorCodes.InvalidGroupId, ex.Code);
            }
            Assert.IsNull(await store.Store.GetSettingsAsync());

            var saved = await service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = " " + GroupId.ToString("B").ToUpperInvariant() + " " });
            Assert.IsTrue(saved.Configured);
            Assert.AreEqual(GroupId.ToString("D"), saved.GroupId, "Stored in canonical form.");
            Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready, saved.Refresh.Status, "Saving reads the group straight away.");
            Assert.AreEqual(25, saved.Refresh.MatchedUsers);
            Assert.AreEqual("Contoso Leadership", saved.Refresh.GroupDisplayName);
            Assert.AreEqual(10, saved.MinimumCohort);
            Assert.IsTrue(invalidations >= 1);

            var cleared = await service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = "" });
            Assert.IsFalse(cleared.Configured);
            Assert.IsNull(cleared.Refresh);
            Assert.IsNull(await store.Store.GetSettingsAsync());
        }

        [TestMethod]
        public async Task Admin_RefusesToSaveWithoutDurableStorageAndToRefreshWhenNotConfigured()
        {
            var volatileStore = new LeadershipCohortStore(new InMemoryKeyValueStore(), isDurable: false);
            var service = new LeadershipCohortService(volatileStore, () => throw new AssertFailedException("must not refresh"), null, () => Now);

            var notDurable = await AssertThrows<LeadershipCohortRequestException>(() => service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = GroupId.ToString() }));
            Assert.AreEqual(HttpStatusCode.Conflict, notDurable.Status);
            Assert.AreEqual(LeadershipCohortErrorCodes.StateNotDurable, notDurable.Code);
            Assert.IsFalse((await service.GetStatusAsync()).StateDurable);

            var notConfigured = await AssertThrows<LeadershipCohortRequestException>(() => service.RefreshAsync());
            Assert.AreEqual(LeadershipCohortErrorCodes.NotConfigured, notConfigured.Code);
        }

        [TestMethod]
        public async Task Admin_RefreshReportsBusyAndFailures()
        {
            var store = NewStore();
            await Configure(store);
            var gate = new SemaphoreSlim(1, 1);
            var directory = new FakeDirectory(20) { FailWith = HttpStatusCode.Forbidden };
            var service = new LeadershipCohortService(store.Store, () => Refresher(store, directory, gate: gate), null, () => Now);

            var status = await service.RefreshAsync();
            Assert.AreEqual(LeadershipCohortRefreshStatuses.PermissionMissing, status.Refresh.Status);
            Assert.AreEqual(403, status.Refresh.HttpStatus);

            await gate.WaitAsync();
            var busy = await AssertThrows<LeadershipCohortRequestException>(() => service.RefreshAsync());
            Assert.AreEqual(LeadershipCohortErrorCodes.RefreshInProgress, busy.Code);
        }

        [TestMethod]
        public async Task Admin_StatusHidesTheRefreshOfAPreviousGroup()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(20)).RefreshAsync();
            await store.Store.SaveSettingsAsync(new LeadershipCohortSettings { GroupId = Guid.NewGuid().ToString(), Revision = "r2", UpdatedUtc = Now });

            var status = await new LeadershipCohortService(store.Store, () => null, null, () => Now).GetStatusAsync();

            Assert.IsTrue(status.Configured);
            Assert.IsNull(status.Refresh, "The previous group's name and counts must not be shown as the new group's.");
        }

        // ---------------------------------------------------------------- helpers

        private static TestStore NewStore() => new TestStore(new InMemoryKeyValueStore());

        private static Task Configure(TestStore store) =>
            store.Store.SaveSettingsAsync(new LeadershipCohortSettings { GroupId = GroupId.ToString("D"), Revision = "r1", UpdatedUtc = Now });

        private static LeadershipCohortRefresher Refresher(TestStore store, FakeDirectory directory, int maxMembers = LeadershipCohortStore.MaxMembers,
            SemaphoreSlim gate = null, Func<DateTime> utcNow = null, bool mapAll = false)
        {
            return new LeadershipCohortRefresher(store.Store, () => directory, new FakeMapper { All = mapAll },
                utcNow: utcNow ?? (() => Now), maxMembers: maxMembers, gate: gate ?? new SemaphoreSlim(1, 1));
        }

        private static LeadershipComparisonProvider Provider(TestStore store, DateTime? now = null) =>
            new LeadershipComparisonProvider(() => store.Store, () => now ?? Now);

        /// <summary>
        /// A tenant of <paramref name="licensed"/> users with ids 1..N. Ids 1-5 never used, 6-10 trialling, 11-20
        /// established, 21-29 developing, the rest never used: 24 of 40 active (60%), 10 habitual (25%).
        /// </summary>
        private static CopilotAdoptionAnalysis Analysis(int licensed)
        {
            var analysis = new CopilotAdoptionAnalysis();
            for (var id = 1; id <= licensed; id++)
            {
                var band = id <= 5 ? AdoptionBand.NeverUsed
                    : id <= 10 ? AdoptionBand.Trialling
                    : id <= 20 ? AdoptionBand.Established
                    : id <= 29 ? AdoptionBand.Developing
                    : AdoptionBand.NeverUsed;
                var score = band == AdoptionBand.NeverUsed ? 0 : band == AdoptionBand.Trialling ? 20 : band == AdoptionBand.Developing ? 40 : 80;
                analysis.LicensedUsers.Add(new LicensedUserAdoptionRow
                {
                    UserId = id,
                    UserPrincipalName = "user" + id.ToString(CultureInfo.InvariantCulture) + "@contoso.example",
                    Band = band,
                    AdoptionScore = score,
                });
            }
            var active = analysis.LicensedUsers.Count(u => u.Band > AdoptionBand.Dormant);
            var habitual = analysis.LicensedUsers.Count(u => CopilotAdoptionScoring.IsHabitual(u.Band));
            analysis.Summary.LicensedUsers = licensed;
            analysis.Summary.AdoptionRatePct = CopilotAdoptionScoring.Percentage(active, licensed);
            analysis.Summary.HabitRatePct = CopilotAdoptionScoring.Percentage(habitual, licensed);
            analysis.Summary.AverageAdoptionScore = Math.Round(analysis.LicensedUsers.Average(u => u.AdoptionScore), 1);
            return analysis;
        }

        private static async Task<T> AssertThrows<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action();
            }
            catch (T ex)
            {
                return ex;
            }
            Assert.Fail("Expected " + typeof(T).Name);
            return null;
        }

        /// <summary>A member's synthetic object id: zeroes, with the member's number in the last twelve hex digits.</summary>
        private static string ObjectId(int member) => "00000000-0000-0000-0000-" + member.ToString("x12", CultureInfo.InvariantCulture);

        private static int MemberNumber(string objectId) => int.Parse(objectId.Substring(24), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        private sealed class TestStore
        {
            public TestStore(IKeyValueStore values)
            {
                RawValues = values;
                Store = new LeadershipCohortStore(values, isDurable: true);
            }

            public IKeyValueStore RawValues { get; }
            public LeadershipCohortStore Store { get; }

            public Task<LeadershipCohortSnapshot> GetSnapshotAsync() => Store.GetSnapshotAsync();
            public Task<IReadOnlyCollection<int>> ReadMembersAsync(LeadershipCohortSnapshot snapshot) => Store.ReadMembersAsync(snapshot);
        }

        private sealed class FakeDirectory : IGroupDirectoryReader
        {
            private readonly int _memberCount;

            public FakeDirectory(int memberCount) { _memberCount = memberCount; }

            public bool GroupExists { get; set; } = true;
            public bool EndlessEmptyPages { get; set; }
            public HttpStatusCode? FailWith { get; set; }
            public int MemberPageCalls { get; private set; }

            public Task<DirectoryGroup> GetGroupByIdAsync(Guid groupId)
            {
                if (FailWith.HasValue) throw new DirectoryReadException(FailWith.Value, "https://graph.example/groups", "{}");
                return Task.FromResult(GroupExists ? new DirectoryGroup { Id = groupId.ToString(), DisplayName = "Contoso Leadership" } : null);
            }

            public Task<IReadOnlyList<DirectoryGroup>> FindGroupsByDisplayNameAsync(string displayName) => throw new NotSupportedException();

            public Task<DirectoryPage<DirectoryGroup>> ListGroupsAsync(string nextLink) => throw new NotSupportedException();

            public Task<DirectoryPage<DirectoryUser>> ListUserMembersAsync(string groupId, string nextLink)
            {
                MemberPageCalls++;
                if (EndlessEmptyPages) return Task.FromResult(new DirectoryPage<DirectoryUser> { NextLink = "more" });
                var start = string.IsNullOrEmpty(nextLink) ? 0 : int.Parse(nextLink, CultureInfo.InvariantCulture);
                var count = Math.Min(GraphGroupDirectoryReader.PageSize, _memberCount - start);
                var page = new DirectoryPage<DirectoryUser>
                {
                    Items = Enumerable.Range(start + 1, Math.Max(0, count))
                        .Select(n => new DirectoryUser { Id = ObjectId(n), UserPrincipalName = "member" + n + "@contoso.example" })
                        .ToList(),
                    NextLink = start + count < _memberCount ? (start + count).ToString(CultureInfo.InvariantCulture) : null,
                };
                return Task.FromResult(page);
            }
        }

        private sealed class FakeMapper : ILeadershipCohortUserIdMapper
        {
            public bool All { get; set; }
            public bool Throw { get; set; }

            public Task<IReadOnlyList<int>> MapAsync(IReadOnlyList<string> objectIds)
            {
                if (Throw) throw new InvalidOperationException("SQL unavailable");
                IReadOnlyList<int> ids = objectIds.Select(MemberNumber).Where(n => All || n % 2 == 0).ToList();
                return Task.FromResult(ids);
            }
        }

        private sealed class ThrowingStore : IKeyValueStore
        {
            public string Description => "unreachable test store";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => throw new TimeoutException("unreachable");
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw new TimeoutException("unreachable");
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new TimeoutException("unreachable");
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new TimeoutException("unreachable");
        }

        private sealed class CountingStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            public int Reads;

            public string Description => _inner.Description;
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Reads);
                return _inner.GetStringAsync(key, cancellationToken);
            }
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
        }
    }
}
