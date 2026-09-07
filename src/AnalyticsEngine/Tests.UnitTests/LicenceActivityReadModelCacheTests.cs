extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.LicenceActivity;
using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class LicenceActivityReadModelCacheTests
    {
        private static readonly DateTime Now = new DateTime(2000, 7, 4, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public async Task IdenticalRangesCoalesceAndCallerCancellationDoesNotCancelTheModel()
        {
            var cache = new LicenceActivityReadModelCache(utcNow: () => Now);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<LicenceActivityReadModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            CancellationToken sharedToken = default(CancellationToken);
            Task<LicenceActivityReadModel> Load(CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                sharedToken = token;
                started.TrySetResult(true);
                return completion.Task;
            }
            using (var caller = new CancellationTokenSource())
            {
                var first = cache.GetAsync("scope", "range", Load, caller.Token);
                await started.Task;
                var others = Enumerable.Range(0, 8)
                    .Select(_ => cache.GetAsync("scope", "range", Load, CancellationToken.None)).ToArray();
                caller.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => first);
                Assert.IsFalse(sharedToken.IsCancellationRequested);
                var model = EmptyModel();
                completion.SetResult(model);
                var leases = await Task.WhenAll(others);
                Assert.AreEqual(1, calls);
                Assert.IsTrue(leases.All(lease => ReferenceEquals(model, lease.Model)));
                Assert.AreSame(model, cache.Find("scope", model.Id).Model);
            }
        }

        [TestMethod]
        public async Task DistinctColdLoadsAreBoundedUntilTheActualLoaderFinishes()
        {
            var cache = new LicenceActivityReadModelCache(utcNow: () => Now);
            var completion = new TaskCompletionSource<LicenceActivityReadModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = cache.GetAsync("scope", "first", _ => completion.Task, CancellationToken.None);
            Assert.ThrowsException<LicenceActivityReadModelBusyException>(() =>
                cache.GetAsync("scope", "second", _ => Task.FromResult(EmptyModel()), CancellationToken.None));
            completion.SetResult(EmptyModel());
            await first;
            var next = await cache.GetAsync("scope", "second", _ => Task.FromResult(EmptyModel()), CancellationToken.None);
            Assert.IsNotNull(next.Model);
        }

        [TestMethod]
        public async Task FailureIsNotCachedAndCanBeRetried()
        {
            var cache = new LicenceActivityReadModelCache(utcNow: () => Now);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => cache.GetAsync(
                "scope", "range", _ => Task.FromException<LicenceActivityReadModel>(new InvalidOperationException("synthetic")),
                CancellationToken.None));
            var lease = await cache.GetAsync("scope", "range", _ => Task.FromResult(EmptyModel()), CancellationToken.None);
            Assert.IsNotNull(lease.Model);
        }

        [TestMethod]
        public async Task DeadlineCancelsTheLoaderAndNeverPublishesLateData()
        {
            var cache = new LicenceActivityReadModelCache(
                utcNow: () => Now, loadTimeout: TimeSpan.FromMilliseconds(40));
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<LicenceActivityReadModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = cache.GetAsync("scope", "range", async token =>
            {
                using (token.Register(() => cancelled.TrySetResult(true)))
                    return await finish.Task;
            }, CancellationToken.None);
            Assert.AreSame(cancelled.Task, await Task.WhenAny(cancelled.Task, pending),
                "The synthetic loader must start before its response deadline.");
            Assert.ThrowsException<LicenceActivityReadModelBusyException>(() =>
                cache.GetAsync("scope", "other", _ => Task.FromResult(EmptyModel()), CancellationToken.None));
            var late = EmptyModel();
            finish.SetResult(late);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
            Assert.ThrowsException<LicenceActivityReadModelExpiredException>(() => cache.Find("scope", late.Id));
        }

        [TestMethod]
        public async Task ExpiryEvictionAndScopeIsolationDoNotRetainOldModels()
        {
            var now = Now;
            var cache = new LicenceActivityReadModelCache(utcNow: () => now);
            var first = await cache.GetAsync("scope-a", "range", _ => Task.FromResult(EmptyModel()), CancellationToken.None);
            now = now.AddSeconds(1);
            var second = await cache.GetAsync("scope-b", "range", _ => Task.FromResult(EmptyModel()), CancellationToken.None);
            Assert.AreNotEqual(first.Model.Id, second.Model.Id);
            Assert.ThrowsException<LicenceActivityReadModelExpiredException>(() => cache.Find("scope-b", first.Model.Id));
            now = now.AddSeconds(1);
            var third = await cache.GetAsync("scope-a", "other", _ => Task.FromResult(EmptyModel()), CancellationToken.None);
            Assert.ThrowsException<LicenceActivityReadModelExpiredException>(() => cache.Find("scope-a", first.Model.Id));
            Assert.AreSame(second.Model, cache.Find("scope-b", second.Model.Id).Model);
            now = third.ExpiresUtc;
            Assert.ThrowsException<LicenceActivityReadModelExpiredException>(() => cache.Find("scope-a", third.Model.Id));
            var refreshed = await cache.GetAsync("scope-a", "other", _ => Task.FromResult(EmptyModel()), CancellationToken.None);
            Assert.AreNotEqual(third.Model.Id, refreshed.Model.Id);
        }

        [TestMethod]
        public async Task SharedLoaderDoesNotCaptureTheRequestSynchronizationContext()
        {
            var previous = SynchronizationContext.Current;
            var cache = new LicenceActivityReadModelCache(utcNow: () => Now);
            Task<LicenceActivityReadModelLease> task;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new RejectingContext());
                task = cache.GetAsync("scope", "range", async token =>
                {
                    Assert.IsNull(SynchronizationContext.Current);
                    await Task.Delay(1, token);
                    Assert.IsNull(SynchronizationContext.Current);
                    return EmptyModel();
                }, CancellationToken.None);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.IsNotNull((await task).Model);
        }

        [TestMethod]
        public async Task HttpSnapshotCannotOutliveItsSourceAndInternalKeysAreNotSerialized()
        {
            var cache = new LicenceActivitySnapshotCache<LicenceActivityOverview>(
                2, TimeSpan.FromMinutes(5), utcNow: () => Now,
                diagnostics: _ => null, reportFailure: (_, __) => { });
            var sourceExpiry = Now.AddSeconds(15);
            var snapshot = await cache.GetAsync("scope", "range", (_, __) =>
                Task.FromResult(new LicenceActivityOverview
                {
                    ReadModelId = "internal-synthetic-key",
                    SourceExpiresUtc = sourceExpiry
                }));
            Assert.AreEqual(sourceExpiry, snapshot.ExpiresUtc);
            var json = JsonConvert.SerializeObject(snapshot);
            Assert.IsFalse(json.Contains("internal-synthetic-key"));
            Assert.IsFalse(json.Contains("sourceExpiresUtc"));
            Assert.IsFalse(json.Contains("readModelId"));
        }

        [TestMethod]
        public async Task EvictedSourceInvalidatesOverviewSoRefreshDoesNotRepeatAnExpiredId()
        {
            var cache = new LicenceActivitySnapshotCache<LicenceActivityOverview>(
                2, TimeSpan.FromMinutes(5), utcNow: () => Now,
                diagnostics: _ => null, reportFailure: (_, __) => { });
            var current = true;
            var loads = 0;
            Task<LicenceActivityOverview> Load(ILicenceActivityDiagnostics _, CancellationToken __)
            {
                loads++;
                return Task.FromResult(new LicenceActivityOverview());
            }
            var first = await cache.GetAsync("scope", "range", Load, isCurrent: _ => current);
            var warm = await cache.GetAsync("scope", "range", Load, isCurrent: _ => current);
            Assert.AreEqual(first.SnapshotId, warm.SnapshotId);
            current = false;
            var refreshed = await cache.GetAsync("scope", "range", Load, isCurrent: _ => current);
            Assert.AreNotEqual(first.SnapshotId, refreshed.SnapshotId);
            Assert.AreEqual(2, loads);
            Assert.ThrowsException<LicenceActivityExpiredException>(() => cache.Find("scope", first.SnapshotId));
        }

        [TestMethod]
        public async Task DemographicQueriesReuseTheRangeButSourceChangesDoNot()
        {
            var sources = new LicenceActivitySources { UserMetadata = true, NowUtc = Now };
            var loader = new EmptyLoader();
            var store = new CachedLicenceActivityStore(loader, new LicenceActivityReadModelCache(utcNow: () => Now), "scope");
            var query = LicenceActivityQuery.Create("2000-06-19", "2000-06-25", Now);
            var first = await store.LoadOverviewAsync(query, sources, null, CancellationToken.None);
            var filtered = await store.LoadOverviewAsync(
                LicenceActivityQuery.Create(query.From, query.To, Now, departmentId: 0), sources, null, CancellationToken.None);
            Assert.AreEqual(first.ReadModelId, filtered.ReadModelId);
            Assert.AreEqual(1, loader.Calls);
            sources.UsageReports = true;
            var changed = await store.LoadOverviewAsync(query, sources, null, CancellationToken.None);
            Assert.AreNotEqual(first.ReadModelId, changed.ReadModelId);
            Assert.AreEqual(2, loader.Calls);
        }

        [TestMethod]
        public async Task HttpRefreshReplacesAnEvictedReadModelAndDrilldownRecovers()
        {
            var sources = new LicenceActivitySources { UserMetadata = true, NowUtc = Now };
            var store = new CachedLicenceActivityStore(
                new OneUserLoader(), new LicenceActivityReadModelCache(capacity: 1, utcNow: () => Now), "scope");
            using (var host = new LicenceActivityHttpHost(store, sources, () => Now))
            {
                const string path = "api/LicenceActivity/overview?from=2000-06-19&to=2000-06-25";
                var original = JObject.Parse(await host.Client.GetStringAsync(path));
                await host.Client.GetStringAsync("api/LicenceActivity/overview?from=2000-06-12&to=2000-06-18");
                using (var expired = await host.Client.GetAsync(
                    "api/LicenceActivity/users?overviewId=" + original["snapshotId"] + "&licenceTypeId=1"))
                    Assert.AreEqual(HttpStatusCode.Gone, expired.StatusCode);
                var refreshed = JObject.Parse(await host.Client.GetStringAsync(path));
                Assert.AreNotEqual((string)original["snapshotId"], (string)refreshed["snapshotId"]);
                var users = JObject.Parse(await host.Client.GetStringAsync(
                    "api/LicenceActivity/users?overviewId=" + refreshed["snapshotId"] + "&licenceTypeId=1"));
                Assert.AreEqual(1, (int)users["totalUsers"]);
                Assert.AreEqual("synthetic@contoso.example", (string)users["users"][0]["userPrincipalName"]);
            }
        }

        private static LicenceActivityReadModel EmptyModel() => new LicenceActivityReadModel(
            LicenceActivityQuery.Create("2000-06-19", "2000-06-25", Now),
            Array.Empty<LicenceActivitySku>(), Array.Empty<LicenceActivityDirectoryUser>(),
            Array.Empty<LicenceActivityMembership>(),
            LicenceActivityQuery.Workloads.Select(workload => new LicenceActivityCoverage
            {
                Workload = workload, Status = "disabled", Source = "microsoftGraphUsageReport",
                Measure = "synthetic", ExpectedSamples = 1
            }).ToArray(),
            new Dictionary<string, IReadOnlyDictionary<int, LicenceActivityScore>>());

        private sealed class EmptyLoader : ILicenceActivityReadModelLoader
        {
            internal int Calls;
            public Task<LicenceActivityReadModel> LoadReadModelAsync(
                LicenceActivityQuery range, LicenceActivitySources sources,
                ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(EmptyModel());
            }
        }

        private sealed class OneUserLoader : ILicenceActivityReadModelLoader
        {
            public Task<LicenceActivityReadModel> LoadReadModelAsync(
                LicenceActivityQuery range, LicenceActivitySources sources,
                ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken) =>
                Task.FromResult(new LicenceActivityReadModel(
                    range,
                    new[] { new LicenceActivitySku { LicenceTypeId = 1, Name = "Contoso Suite" } },
                    new[] { new LicenceActivityDirectoryUser { UserId = 1, UserPrincipalName = "synthetic@contoso.example" } },
                    new[] { new LicenceActivityMembership(1, 1) },
                    LicenceActivityQuery.Workloads.Select(workload => new LicenceActivityCoverage
                    {
                        Workload = workload, Status = "disabled", Source = "microsoftGraphUsageReport",
                        Measure = "synthetic", ExpectedSamples = 1
                    }).ToArray(),
                    new Dictionary<string, IReadOnlyDictionary<int, LicenceActivityScore>>()));
        }

        private sealed class RejectingContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state) =>
                throw new InvalidOperationException("A shared load captured its initiating request.");
        }
    }
}
