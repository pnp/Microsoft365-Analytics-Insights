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
using System.Net.Http;
using System.Net.Http.Headers;
using DataUtils.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    public partial class LeadershipCohortTests
    {
        [TestMethod]
        public async Task Admin_SaveValidatesQueuesDurablyAndClears()
        {
            var store = NewStore();
            var invalidations = 0;
            var directory = new FakeDirectory(25);
            var service = new LeadershipCohortService(store.Store, () => invalidations++, () => Now);

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
            Assert.IsNull(saved.Refresh, "Acknowledges settings, not a membership read that may take longer than HTTP permits.");
            Assert.AreEqual(0, directory.MemberPageCalls);
            Assert.IsTrue(LeadershipCohortRefresher.IsDue(await store.Store.GetSettingsAsync(), null, Now));
            await Refresher(store, directory, mapAll: true).RefreshIfDueAsync();
            var refreshed = await service.GetStatusAsync();
            Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready, refreshed.Refresh.Status);
            Assert.AreEqual(25, refreshed.Refresh.MatchedUsers);
            Assert.AreEqual("Contoso Leadership", refreshed.Refresh.GroupDisplayName);
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
            var service = new LeadershipCohortService(volatileStore, null, () => Now);

            var notDurable = await AssertThrows<LeadershipCohortRequestException>(() => service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = GroupId.ToString() }));
            Assert.AreEqual(HttpStatusCode.Conflict, notDurable.Status);
            Assert.AreEqual(LeadershipCohortErrorCodes.StateNotDurable, notDurable.Code);
            Assert.IsFalse((await service.GetStatusAsync()).StateDurable);

            var notConfigured = await AssertThrows<LeadershipCohortRequestException>(() => service.RefreshAsync());
            Assert.AreEqual(LeadershipCohortErrorCodes.NotConfigured, notConfigured.Code);
        }

        [TestMethod]
        public async Task Admin_RefreshQueuesWhileWorkerBusyAndReportsWorkerFailures()
        {
            var store = NewStore();
            await Configure(store);
            var gate = new SemaphoreSlim(1, 1);
            var directory = new FakeDirectory(20) { FailWith = HttpStatusCode.Forbidden };
            var service = new LeadershipCohortService(store.Store, null, () => Now);

            var status = await service.RefreshAsync();
            Assert.IsNull(status.Refresh);

            await gate.WaitAsync();
            Assert.IsNull((await service.RefreshAsync()).Refresh, "Requests persist without waiting for the worker gate.");
            Assert.IsNull(await Refresher(store, directory, gate: gate).RefreshIfDueAsync());
            gate.Release();
            await Refresher(store, directory, gate: gate).RefreshIfDueAsync();
            status = await service.GetStatusAsync();
            Assert.AreEqual(LeadershipCohortRefreshStatuses.PermissionMissing, status.Refresh.Status);
            Assert.AreEqual(403, status.Refresh.HttpStatus);
            Assert.AreEqual(1, gate.CurrentCount);
            Assert.IsNull(await Refresher(store, directory, gate: gate).RefreshIfDueAsync(), "Consumed failures follow the retry interval.");
        }

        [TestMethod]
        public async Task Admin_StatusHidesTheRefreshOfAPreviousGroup()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(20)).RefreshAsync();
            await store.Store.SaveSettingsAsync(new LeadershipCohortSettings { GroupId = Guid.NewGuid().ToString(), Revision = "r2", UpdatedUtc = Now });

            var status = await new LeadershipCohortService(store.Store, null, () => Now).GetStatusAsync();

            Assert.IsTrue(status.Configured);
            Assert.IsNull(status.Refresh, "The previous group's name and counts must not be shown as the new group's.");
        }

        [DataTestMethod]
        [DataRow("save")]
        [DataRow("clear")]
        [DataRow("refresh")]
        [DataRow("status")]
        public async Task Admin_StorageDeadlineCancelsAndAwaitsTheOperation(string operation)
        {
            var values = new BlockingStore();
            var service = new LeadershipCohortService(new LeadershipCohortStore(values, true), null,
                () => Now, TimeSpan.FromMilliseconds(50));
            Func<Task> action;
            if (operation == "refresh") action = () => service.RefreshAsync();
            else if (operation == "status") action = () => service.GetStatusAsync();
            else action = () => service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = operation == "clear" ? "" : GroupId.ToString() });
            var error = await AssertThrows<LeadershipCohortRequestException>(action);
            Assert.AreEqual(LeadershipCohortErrorCodes.StateUnavailable, error.Code);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, error.Status);
            Assert.AreEqual(0, values.Active, "Cancellation is awaited, not raced against an abandoned write.");
            Assert.IsNull(await values.Inner.GetStringAsync(LeadershipCohortStore.SettingsKey));

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await AssertThrows<OperationCanceledException>(() => service.GetStatusAsync(cancelled.Token));
            }
            Assert.AreEqual(0, values.Active);
        }

        [TestMethod]
        public async Task Admin_SaveReturnsAcknowledgedSettingsWithoutPostSaveRead()
        {
            var values = new CountingStore();
            var service = new LeadershipCohortService(new LeadershipCohortStore(values, true), null, () => Now);
            Assert.IsTrue((await service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = GroupId.ToString() })).Configured);
            Assert.AreEqual(0, values.Reads, "A later read outage must not misreport an acknowledged save as a failure.");
        }

        [TestMethod]
        public async Task Admin_StorageCertificateAuthenticationHonoursCancellationBeforeNetwork()
        {
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await AssertThrows<OperationCanceledException>(() => DataUtils.AuthHelper.RetrieveKeyVaultCertificate(
                    "synthetic-certificate", "https://contoso.vault.azure.net", NullLogger.Instance, cancelled.Token));
                await AssertThrows<OperationCanceledException>(() => StorageTableClientFactory.CreateRuntimeCredentialAsync(
                    ObjectId(1), ObjectId(2), null, "https://contoso.vault.azure.net", true, NullLogger.Instance, cancelled.Token));
            }
        }

        [TestMethod]
        public async Task Admin_RequestsSurviveReopeningAndNeverOverwriteNewSettings()
        {
            var store = NewStore();
            await Configure(store);
            await Refresher(store, new FakeDirectory(20)).RefreshAsync();
            var service = new LeadershipCohortService(store.Store, null, () => Now);
            await service.RefreshAsync();
            var firstRequest = await store.Store.GetRefreshRequestAsync();
            var oldSnapshot = await store.GetSnapshotAsync();
            Assert.IsTrue(LeadershipCohortRefresher.IsPending(await store.Store.GetSettingsAsync(), oldSnapshot, firstRequest));
            await service.RefreshAsync();
            var latestRequest = await store.Store.GetRefreshRequestAsync();
            oldSnapshot.RefreshRequestId = firstRequest.Id;
            Assert.IsTrue(LeadershipCohortRefresher.IsPending(await store.Store.GetSettingsAsync(), oldSnapshot, latestRequest),
                "An older in-flight attempt cannot consume a newer request.");

            var reopened = new TestStore(store.RawValues);
            await Refresher(reopened, new FakeDirectory(30)).RefreshIfDueAsync();
            Assert.AreEqual(latestRequest.Id, (await reopened.GetSnapshotAsync()).RefreshRequestId);
            Assert.IsNotNull((await new LeadershipCohortService(reopened.Store, null, () => Now).GetStatusAsync()).Refresh);
            Assert.IsNull(await Refresher(reopened, new FakeDirectory(30)).RefreshIfDueAsync());

            await service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = ObjectId(655) });
            var newSettings = await store.Store.GetSettingsAsync();
            Assert.AreEqual(ObjectId(655), newSettings.GroupId);
            await Refresher(store, new FakeDirectory(20)).RefreshIfDueAsync();
            Assert.AreEqual(ObjectId(655), (await store.GetSnapshotAsync()).GroupId,
                "A request targets the currently configured group, never restores the group a caller previously read.");
        }

        [TestMethod]
        public async Task Worker_PartialSaveLeavesRequestPendingAndGateReleasedUntilRetryPublishes()
        {
            var values = new InterruptedPageStore();
            var store = new TestStore(values);
            await Configure(store);
            var gate = new SemaphoreSlim(1, 1);
            await Refresher(store, new FakeDirectory(20), gate: gate).RefreshAsync();
            var old = await store.GetSnapshotAsync();
            await new LeadershipCohortService(store.Store, null, () => Now).RefreshAsync();
            values.FailKey = LeadershipCohortStore.PageKey(old.Slot == "a" ? "b" : "a", 1);

            await AssertThrows<LeadershipCohortStateUnavailableException>(() =>
                Refresher(store, new FakeDirectory(4500), gate: gate, mapAll: true).RefreshIfDueAsync());
            Assert.AreEqual(1, gate.CurrentCount);
            Assert.AreEqual(old.Version, (await store.GetSnapshotAsync()).Version);
            Assert.AreEqual(10, (await store.ReadMembersAsync(old)).Count, "Partial pages never publish a new header.");
            Assert.IsNull((await new LeadershipCohortService(store.Store, null, () => Now).GetStatusAsync()).Refresh);

            values.FailKey = null;
            var retried = await Refresher(store, new FakeDirectory(4500), gate: gate, mapAll: true).RefreshIfDueAsync();
            Assert.AreEqual(4500, (await store.ReadMembersAsync(retried)).Count);
            Assert.AreEqual((await store.Store.GetRefreshRequestAsync()).Id, retried.RefreshRequestId);
            Assert.AreEqual(1, gate.CurrentCount);
        }

        [TestMethod]
        public async Task Worker_HealthyRefreshMapsAndSavesAgainstItsOwnSyntheticLocalDatabase()
        {
            var name = "UT_LeadershipPending_" + Guid.NewGuid().ToString("N");
            const string master = @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True";
            var connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
            using (var connection = new Microsoft.Data.SqlClient.SqlConnection(master))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"CREATE DATABASE [{name}]";
                    await command.ExecuteNonQueryAsync();
                }
                try
                {
                    using (var sql = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
                    {
                        await sql.OpenAsync();
                        using (var command = sql.CreateCommand())
                        {
                            command.CommandText = @"CREATE TABLE dbo.users (id int NOT NULL PRIMARY KEY, azure_ad_id nvarchar(max) NULL);
                                INSERT dbo.users (id, azure_ad_id) VALUES (1, '00000000-0000-0000-0000-000000000001'),
                                (2, '00000000-0000-0000-0000-000000000002');";
                            await command.ExecuteNonQueryAsync();
                        }
                    }
                    var store = NewStore();
                    var service = new LeadershipCohortService(store.Store, null, () => Now);
                    Assert.IsNull((await service.SaveAsync(new LeadershipCohortSaveRequest { GroupId = GroupId.ToString() })).Refresh);
                    var mapper = new SqlLeadershipCohortUserIdMapper(() => new IsolatedLeadershipContext(connectionString));
                    var worker = new LeadershipCohortRefresher(store.Store, () => new FakeDirectory(3), mapper,
                        utcNow: () => Now, gate: new SemaphoreSlim(1, 1));
                    var result = await worker.RefreshIfDueAsync();
                    Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready, result.Status);
                    CollectionAssert.AreEquivalent(new[] { 1, 2 }, (await store.ReadMembersAsync(result)).ToArray());
                    Assert.AreEqual(2, (await service.GetStatusAsync()).Refresh.MatchedUsers);
                }
                finally
                {
                    Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        private sealed class IsolatedLeadershipContext : Common.Entities.AnalyticsEntitiesContext
        {
            static IsolatedLeadershipContext() { System.Data.Entity.Database.SetInitializer<IsolatedLeadershipContext>(null); }
            public IsolatedLeadershipContext(string connectionString) : base(new Microsoft.Data.SqlClient.SqlConnection(connectionString)) { }
        }

        private sealed class InterruptedPageStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            public string FailKey;
            public string Description => "synthetic interrupted page storage";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => _inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                if (key == FailKey) throw new InvalidOperationException("synthetic write interruption");
                return _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
        }

        [TestMethod]
        public async Task Admin_ReturnsPendingWhileGraphWaitsForSynthetic429AndWorkerReleasesGate()
        {
            var store = NewStore();
            await Configure(store);
            var gate = new SemaphoreSlim(1, 1);
            using (var stopWorker = new CancellationTokenSource())
            {
                var clock = new BlockingRetryClock(stopWorker.Token);
                using (var http = new AutoThrottleHttpClient(new ThrottledDirectoryHandler(), NullLogger.Instance, clock))
                {
                    var worker = new LeadershipCohortRefresher(store.Store,
                        () => new GraphGroupDirectoryReader(http, NullLogger.Instance), new FakeMapper(),
                        utcNow: () => Now, gate: gate);
                    var running = worker.RefreshIfDueAsync();
                    await clock.Waiting.Task;
                    try
                    {
                        var service = new LeadershipCohortService(store.Store, null, () => Now);
                        Assert.IsNull((await service.RefreshAsync()).Refresh);
                        Assert.AreEqual(0, gate.CurrentCount, "Only the importer owns the gate, not the returned HTTP request.");
                        Assert.IsNotNull(await store.Store.GetRefreshRequestAsync());
                    }
                    finally
                    {
                        stopWorker.Cancel();
                        await running;
                    }
                    Assert.AreEqual(1, gate.CurrentCount);
                    Assert.IsNull((await new LeadershipCohortService(store.Store, null, () => Now).GetStatusAsync()).Refresh,
                        "The request made during the old attempt is still pending.");
                    Assert.AreEqual(LeadershipCohortRefreshStatuses.Ready,
                        (await Refresher(store, new FakeDirectory(20), gate: gate).RefreshIfDueAsync()).Status);
                }
            }
        }

        private sealed class BlockingRetryClock : IAutoThrottleHttpClientClock
        {
            private readonly CancellationToken _stop;
            public BlockingRetryClock(CancellationToken stop) { _stop = stop; }
            public DateTimeOffset UtcNow => new DateTimeOffset(Now);
            public TaskCompletionSource<bool> Waiting { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                Assert.AreEqual(TimeSpan.FromSeconds(300), delay);
                Waiting.TrySetResult(true);
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop, cancellationToken))
                    await Task.Delay(Timeout.Infinite, linked.Token);
            }
        }

        private sealed class ThrottledDirectoryHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
                return Task.FromResult(response);
            }
        }

        private sealed class BlockingStore : IKeyValueStore
        {
            public InMemoryKeyValueStore Inner { get; } = new InMemoryKeyValueStore();
            public int Active;
            public string Description => "synthetic cancellable storage";
            private async Task Wait(CancellationToken token)
            {
                Interlocked.Increment(ref Active);
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Interlocked.Decrement(ref Active); }
            }
            public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                await Wait(cancellationToken);
                return await Inner.GetStringAsync(key, cancellationToken);
            }
            public async Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                await Wait(cancellationToken);
                await Inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Inner.ExistsAsync(key, cancellationToken);
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
            public string FailReadKey;
            public Func<string, Task> BeforeWrite;

            public string Description => _inner.Description;
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Reads);
                if (key == FailReadKey) throw new TimeoutException("synthetic request read failure");
                return _inner.GetStringAsync(key, cancellationToken);
            }
            public async Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                if (BeforeWrite != null) await BeforeWrite(key);
                await _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
        }
    }
}
