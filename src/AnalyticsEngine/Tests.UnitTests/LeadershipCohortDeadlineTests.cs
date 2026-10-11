extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;

namespace Tests.UnitTests
{
    public partial class LeadershipCohortTests
    {
        [DataTestMethod]
        [DataRow(LeadershipCohortStore.SettingsKey)]
        [DataRow(LeadershipCohortStore.SnapshotKey)]
        [DataRow(LeadershipCohortStore.RefreshRequestKey)]
        [DataRow("Members:a:000")]
        [DataRow("Members:a:001")]
        [DataRow("Members:a:004")]
        public async Task Provider_OptionalReadDeadlineWithholdsOnlyLeadership(string heldKey)
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values, LeadershipCohortStore.MaxMembers);
            var provider = DeadlineProvider(store);
            var analysis = Analysis(40);
            values.HoldKey = heldKey;
            var request = provider.GetAsync(analysis, false);
            try
            {
                await CompletesPromptly(values.Started.Task);
                await CompletesPromptly(request);
                var comparison = await request;
                Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, comparison.Status);
                Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, comparison.Reason);
                AssertWithholdsEveryFigure(comparison);
                Assert.IsNull(analysis.Summary.LeadershipComparison, "The shared completed analysis is never changed.");
                Assert.AreEqual(40, analysis.Summary.LicensedUsers);
                Assert.IsTrue(values.LastHeldToken.IsCancellationRequested, "The deadline reaches the real store read.");
                Assert.IsTrue(values.ReadTokens.All(t => t.CanBeCanceled && t.IsCancellationRequested),
                    "Every header and member page receives the same bounded cancellation token.");
                await CompletesPromptly(values.Stopped.Task);
                Assert.AreEqual(0, values.Active);
            }
            finally
            {
                values.Release();
                await request;
            }
        }

        [DataTestMethod]
        [DataRow(LeadershipCohortStore.SettingsKey)]
        [DataRow(LeadershipCohortStore.SnapshotKey)]
        [DataRow(LeadershipCohortStore.RefreshRequestKey)]
        [DataRow("Members:a:001")]
        [DataRow("Members:a:004")]
        public async Task Report_CachedAnalysisRemainsAvailableWhenOptionalStorageStalls(string heldKey)
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values, LeadershipCohortStore.MaxMembers);
            var provider = DeadlineProvider(store);
            var analysis = Analysis(40);
            var runner = new DeadlineRunner(analysis);
            var cache = new DeadlineAnalysisCache();
            var coordinator = DeadlineCoordinator(runner, cache);
            Assert.AreSame(analysis, await coordinator.GetAsync(28, new List<int>()));
            values.HoldKey = heldKey;

            using (var host = DeadlineHost(coordinator, provider))
            {
                var request = host.Client.GetAsync("api/CopilotAdoption/summary");
                try
                {
                    await CompletesPromptly(values.Started.Task);
                    await CompletesPromptly(request);
                    var response = await request;
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual(40, (int)body["licensedUsers"]);
                    Assert.AreEqual(60d, (double)body["adoptionRatePct"]);
                    var comparison = body["leadershipComparison"];
                    Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, (string)comparison["status"]);
                    Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, (string)comparison["reason"]);
                    Assert.AreEqual(JTokenType.Null, comparison["licensedLeaders"].Type);
                    Assert.AreEqual(1, runner.Calls, "The cached analysis is used, not restarted or replaced.");
                    Assert.AreEqual(1, cache.Writes);
                    Assert.IsNull(analysis.Summary.LeadershipComparison);
                    Assert.IsTrue(values.LastHeldToken.IsCancellationRequested);
                    await CompletesPromptly(values.Stopped.Task);
                    Assert.AreEqual(0, values.Active);
                }
                finally
                {
                    values.Release();
                    await request;
                }
            }
        }

        [TestMethod]
        public async Task Provider_OneDeadlineCoversTheWholeReadChain_NotEachRead()
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values);
            values.Reads = 0;
            values.ReadDelay = TimeSpan.FromMilliseconds(70);

            var comparison = await DeadlineProvider(store).GetAsync(Analysis(40), false);

            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, comparison.Status);
            Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, comparison.Reason);
            Assert.IsTrue(values.Reads < 4, "Four individually fast reads must not restart the total storage deadline.");
            AssertWithholdsEveryFigure(comparison);
        }

        [DataTestMethod]
        [DataRow(LeadershipCohortStore.SettingsKey, true)]
        [DataRow(LeadershipCohortStore.SnapshotKey, true)]
        [DataRow(LeadershipCohortStore.RefreshRequestKey, true)]
        [DataRow("Members:a:000", true)]
        [DataRow("Members:a:001", true)]
        [DataRow("Members:a:001", false)]
        public async Task Provider_CallerCancellationReachesEveryRead_AndNeverCachesFailure(string heldKey, bool cooperative)
        {
            var values = new HeldLeadershipReads { Cooperative = cooperative };
            var store = await ReadyDeadlineStore(values, 2500);
            var provider = new LeadershipComparisonProvider(() => store, () => Now, TimeSpan.FromSeconds(5));
            var analysis = Analysis(40);
            values.HoldKey = heldKey;
            using (var caller = new CancellationTokenSource())
            {
                var request = provider.GetAsync(analysis, false, caller.Token);
                try
                {
                    await CompletesPromptly(values.Started.Task);
                    caller.Cancel();
                    await CompletesPromptly(request);
                    var error = await AssertThrows<OperationCanceledException>(() => request);
                    Assert.AreEqual(caller.Token, error.CancellationToken);
                    Assert.IsTrue(values.LastHeldToken.IsCancellationRequested);
                    if (cooperative)
                    {
                        await CompletesPromptly(values.Stopped.Task);
                        Assert.AreEqual(0, values.Active);
                    }

                    var healthy = await provider.GetAsync(analysis, false);
                    Assert.AreEqual(LeadershipComparisonStatuses.Ok, healthy.Status,
                        "Cancellation is not a 15-second cached storage failure.");
                    var reads = values.Reads;
                    await AssertThrows<OperationCanceledException>(() => provider.GetAsync(analysis, false, caller.Token));
                    Assert.AreEqual(reads, values.Reads, "A cancelled cache hit must still propagate cancellation.");
                    Assert.IsNull(analysis.Summary.LeadershipComparison);
                }
                finally
                {
                    values.Release();
                    try { await request; } catch (OperationCanceledException) { }
                    await CompletesPromptly(values.Stopped.Task);
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Provider_DeadlineFailureCacheRecoversAfterItsTtlOrInvalidation(bool invalidate)
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values);
            var now = Now;
            var provider = DeadlineProvider(store, () => now);
            var analysis = Analysis(40);
            var original = await provider.GetAsync(analysis, false);
            now += LeadershipComparisonProvider.HeaderCacheDuration;
            values.HoldKey = LeadershipCohortStore.RefreshRequestKey;

            var failure = await provider.GetAsync(analysis, false);
            Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, failure.Reason);
            await CompletesPromptly(values.Stopped.Task);
            values.HoldKey = null;
            var reads = values.Reads;
            Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, (await provider.GetAsync(analysis, false)).Status);
            Assert.AreEqual(reads, values.Reads);
            if (invalidate) provider.Invalidate();
            else
            {
                now += LeadershipComparisonProvider.FailureCacheDuration - TimeSpan.FromTicks(1);
                Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, (await provider.GetAsync(analysis, false)).Status);
                Assert.AreEqual(reads, values.Reads, "A deadline retains the existing 15-second failure-cache policy.");
                now += TimeSpan.FromTicks(1);
            }

            Assert.AreSame(original, await provider.GetAsync(analysis, false),
                "Recovery keeps the per-analysis, per-membership-version comparison cache.");
            Assert.AreEqual(reads + 4, values.Reads, "A failed state never lends old members to its retry.");
        }

        [DataTestMethod]
        [DataRow(LeadershipCohortStore.SnapshotKey, false)]
        [DataRow(LeadershipCohortStore.SnapshotKey, true)]
        [DataRow("Members:a:000", false)]
        [DataRow("Members:a:000", true)]
        public async Task Provider_LateNonCooperativeReadCannotPublishOverANewRevision(string heldKey, bool lateFault)
        {
            var values = new HeldLeadershipReads
            {
                Cooperative = false,
                LateFailure = lateFault ? new InvalidOperationException("synthetic late read failure") : null,
            };
            var store = await ReadyDeadlineStore(values);
            var provider = DeadlineProvider(store);
            var analysis = Analysis(40);
            values.HoldKey = heldKey;
            try
            {
                var expired = await provider.GetAsync(analysis, false);
                Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, expired.Reason);
                AssertWithholdsEveryFigure(expired);
                Assert.IsTrue(values.LastHeldToken.IsCancellationRequested);
                Assert.AreEqual(1, values.Active, "The deliberately non-cooperative store is bounded by the backstop.");

                await PublishDeadlineRevision(store, "r2", "v2", 30);
                provider.Invalidate();
                var current = await provider.GetAsync(analysis, false);
                Assert.AreEqual(30, current.LicensedLeaders);
                values.Release();
                await CompletesPromptly(values.Stopped.Task);
                await Task.Delay(50);

                Assert.AreSame(current, await provider.GetAsync(analysis, false),
                    "Neither a late success nor a late observed fault may overwrite the new revision.");
                Assert.IsNull(analysis.Summary.LeadershipComparison);
            }
            finally
            {
                values.Release();
                await CompletesPromptly(values.Stopped.Task);
            }
        }

        [TestMethod]
        public async Task Provider_InvalidationDuringAReadWithholdsTheObsoleteResult()
        {
            var values = new HeldLeadershipReads { Cooperative = false };
            var store = await ReadyDeadlineStore(values);
            var provider = new LeadershipComparisonProvider(() => store, () => Now, TimeSpan.FromSeconds(5));
            var analysis = Analysis(40);
            values.HoldKey = LeadershipCohortStore.SnapshotKey;
            var obsolete = provider.GetAsync(analysis, false);
            try
            {
                await CompletesPromptly(values.Started.Task);
                await PublishDeadlineRevision(store, "r2", "v2", 30);
                provider.Invalidate();
                var current = await provider.GetAsync(analysis, false);
                Assert.AreEqual(30, current.LicensedLeaders);
                values.Release();
                var withheld = await obsolete;
                Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, withheld.Reason);
                AssertWithholdsEveryFigure(withheld);
                Assert.AreSame(current, await provider.GetAsync(analysis, false));
            }
            finally
            {
                values.Release();
                await obsolete;
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Provider_AnOlderFailedReadCannotOverwriteAnotherReadersHealthyCache(bool deadline)
        {
            var values = new HeldLeadershipReads
            {
                Cooperative = false,
                LateFailure = new InvalidOperationException("synthetic older read failure"),
            };
            var store = await ReadyDeadlineStore(values);
            var provider = new LeadershipComparisonProvider(() => store, () => Now,
                deadline ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
            var analysis = Analysis(40);
            values.HoldKey = LeadershipCohortStore.SettingsKey;
            var older = provider.GetAsync(analysis, false);
            try
            {
                await CompletesPromptly(values.Started.Task);
                var current = await provider.GetAsync(analysis, false);
                if (!deadline) values.Release();
                var failed = await older;
                Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, failed.Reason,
                    "An expired request withholds its own figures even when another reader recovered.");
                AssertWithholdsEveryFigure(failed);
                values.Release();
                await CompletesPromptly(values.Stopped.Task);
                Assert.AreSame(current, await provider.GetAsync(analysis, false));
            }
            finally
            {
                values.Release();
                await older;
            }
        }

        [TestMethod]
        public async Task Provider_AtTwoHundredThousandUsers_ReadsPagesNotPeopleAndReusesTheComparison()
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values, LeadershipCohortStore.MaxMembers);
            var now = Now;
            var provider = DeadlineProvider(store, () => now);
            var analysis = Analysis(200000);

            var comparison = await provider.GetAsync(analysis, false);

            Assert.AreEqual(LeadershipComparisonStatuses.Ok, comparison.Status);
            Assert.AreEqual(LeadershipCohortStore.MaxMembers, comparison.LicensedLeaders);
            Assert.AreEqual(3 + LeadershipCohortStore.MaxPages, values.Reads);
            Assert.AreSame(comparison, await provider.GetAsync(analysis, false));
            Assert.AreEqual(3 + LeadershipCohortStore.MaxPages, values.Reads, "No per-reader or per-person storage work.");
            now += LeadershipComparisonProvider.HeaderCacheDuration;
            Assert.AreSame(comparison, await provider.GetAsync(analysis, false));
            Assert.AreEqual(6 + LeadershipCohortStore.MaxPages, values.Reads, "An unchanged membership rereads only three headers.");
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(true, true)]
        [DataRow(false, true)]
        public async Task Provider_DeadlineAndCallerCancellationReachTheRealTableSdkAndLazyOpening(bool stallOpening, bool callerCancellation)
        {
            using (var service = new CancellableLeadershipTableService(stallOpening))
            using (var client = new HttpClient(service))
            using (var caller = new CancellationTokenSource())
            {
                var options = new TableClientOptions { Transport = new HttpClientTransport(client) };
                options.Retry.MaxRetries = 3;
                options.Retry.NetworkTimeout = TimeSpan.FromSeconds(100);
                var connectionString = "DefaultEndpointsProtocol=https;AccountName=contoso;AccountKey="
                    + Convert.ToBase64String(new byte[64]) + ";EndpointSuffix=core.windows.net";
                var opens = 0;
                var table = new StateStore.LazyTableClient(token =>
                {
                    Interlocked.Increment(ref opens);
                    return StorageTableClientFactory.CreateAndEnsureTableAsync(connectionString, StateStore.TableName,
                        null, null, null, null, false, null, "synthetic leadership table", token, false, options, null);
                });
                var store = new LeadershipCohortStore(StateStore.Open(table, StatePartitions.LeadershipCohort), true);
                var provider = new LeadershipComparisonProvider(() => store, () => Now,
                    callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500));
                var analysis = Analysis(40);
                var request = provider.GetAsync(analysis, false, caller.Token);
                await CompletesPromptly(service.Started.Task);
                if (callerCancellation) caller.Cancel();
                await CompletesPromptly(request);
                if (callerCancellation)
                {
                    var error = await AssertThrows<OperationCanceledException>(() => request);
                    Assert.AreEqual(caller.Token, error.CancellationToken);
                }
                else
                {
                    Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, (await request).Reason);
                    AssertWithholdsEveryFigure(await request);
                }
                await CompletesPromptly(service.Stopped.Task);
                Assert.IsTrue(service.LastToken.IsCancellationRequested);
                Assert.AreEqual(0, service.ActiveRequests, "The SDK request stops; it is not merely abandoned.");

                service.Stall = false;
                if (!callerCancellation) provider.Invalidate();
                Assert.AreEqual(LeadershipComparisonStatuses.NotConfigured, (await provider.GetAsync(analysis, false)).Status);
                Assert.AreEqual(stallOpening ? 2 : 1, opens, "A cancelled lazy opening releases its gate and is not cached.");
            }
        }

        [DataTestMethod]
        [DataRow("api/CopilotAdoption/summary", LeadershipCohortStore.SettingsKey)]
        [DataRow("api/CopilotAdoption/summary", "Members:a:001")]
        [DataRow("api/CopilotAdoption/export/workbook", LeadershipCohortStore.SettingsKey)]
        [DataRow("api/CopilotAdoption/export/workbook", "Members:a:001")]
        public async Task Report_CallerCancellationReachesOptionalReadsWithoutChangingTheCachedAnalysis(string endpoint, string heldKey)
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values, 2500);
            var provider = new LeadershipComparisonProvider(() => store, () => Now, TimeSpan.FromSeconds(5));
            var analysis = Analysis(40);
            var runner = new DeadlineRunner(analysis);
            var cache = new DeadlineAnalysisCache();
            var coordinator = DeadlineCoordinator(runner, cache);
            await coordinator.GetAsync(28, new List<int>());
            values.HoldKey = heldKey;
            using (var host = DeadlineHost(coordinator, provider))
            using (var caller = new CancellationTokenSource())
            {
                var request = host.Client.GetAsync(endpoint, caller.Token);
                try
                {
                    await CompletesPromptly(values.Started.Task);
                    caller.Cancel();
                    await CompletesPromptly(request);
                    await AssertThrows<OperationCanceledException>(() => request);
                    await CompletesPromptly(values.Cancelled.Task);
                    await CompletesPromptly(values.Stopped.Task);
                    Assert.AreEqual(0, values.Active);
                    Assert.IsTrue(values.LastHeldToken.IsCancellationRequested);

                    var recovered = await host.Client.GetAsync(endpoint);
                    Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
                    Assert.AreEqual(1, runner.Calls);
                    Assert.AreEqual(1, cache.Writes);
                    Assert.IsNull(analysis.Summary.LeadershipComparison);
                }
                finally
                {
                    values.Release();
                    try { await request; } catch (OperationCanceledException) { }
                }
            }
        }

        [TestMethod]
        public async Task Report_CancellingAnAnalysisWaiterLeavesTheSharedRunAvailableToTheNextReader()
        {
            var store = await ReadyDeadlineStore(new InMemoryKeyValueStore());
            var analysis = Analysis(40);
            var runner = new DeadlineRunner(analysis)
            {
                Complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var cache = new DeadlineAnalysisCache();
            var coordinator = DeadlineCoordinator(runner, cache);
            using (var host = DeadlineHost(coordinator, DeadlineProvider(store)))
            using (var caller = new CancellationTokenSource())
            {
                var request = host.Client.GetAsync("api/CopilotAdoption/summary", caller.Token);
                Task<CopilotAdoptionAnalysis> shared = null;
                try
                {
                    await CompletesPromptly(runner.Started.Task);
                    shared = coordinator.GetAsync(28, new List<int>());
                    caller.Cancel();
                    await CompletesPromptly(request);
                    await AssertThrows<OperationCanceledException>(() => request);
                    Assert.IsFalse(shared.IsCompleted, "The request cancels only its wait, not the shared analysis.");
                    runner.Complete.TrySetResult(true);
                    Assert.AreSame(analysis, await shared);
                    var response = await host.Client.GetAsync("api/CopilotAdoption/summary");
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual(LeadershipComparisonStatuses.Ok,
                        (string)JObject.Parse(await response.Content.ReadAsStringAsync())["leadershipComparison"]["status"]);
                    Assert.AreEqual(1, runner.Calls);
                    Assert.AreEqual(1, cache.Writes);
                    Assert.IsNull(analysis.Summary.LeadershipComparison);
                }
                finally
                {
                    runner.Complete.TrySetResult(true);
                    try { await request; } catch (OperationCanceledException) { }
                    if (shared != null) await shared;
                }
            }
        }

        [DataTestMethod]
        [DataRow(false, false, 9, null)]
        [DataRow(true, false, 9, null)]
        [DataRow(false, true, 9, null)]
        [DataRow(true, true, 9, null)]
        [DataRow(false, false, 30, LeadershipComparisonReasons.ComplementTooSmall)]
        [DataRow(true, false, 30, LeadershipComparisonReasons.ComplementTooSmall)]
        [DataRow(false, true, 30, LeadershipComparisonReasons.ComplementTooSmall)]
        [DataRow(true, true, 30, LeadershipComparisonReasons.ComplementTooSmall)]
        public async Task Report_DeadlineRecoveryKeepsCohortAndComplementPrivacyForAllRoles(
            bool seePii, bool administration, int members, string reason)
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values, members);
            var provider = DeadlineProvider(store);
            var runner = new DeadlineRunner(Analysis(35));
            var coordinator = DeadlineCoordinator(runner, new DeadlineAnalysisCache());
            var roles = new List<string>();
            if (seePii) roles.Add(PortalRoles.SeePii);
            if (administration) roles.Add(PortalRoles.Administration);
            values.HoldKey = LeadershipCohortStore.SettingsKey;
            using (var host = DeadlineHost(coordinator, provider, roles.ToArray()))
            {
                var response = await host.Client.GetAsync("api/CopilotAdoption/summary");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var comparison = JObject.Parse(await response.Content.ReadAsStringAsync())["leadershipComparison"];
                Assert.AreEqual(LeadershipComparisonStatuses.Unavailable, (string)comparison["status"]);
                Assert.AreEqual(LeadershipComparisonReasons.StateUnavailable, (string)comparison["reason"]);
                AssertWithholdsEveryFigure(comparison.ToObject<LeadershipAdoptionComparison>());
                var failedExport = await host.Client.GetAsync("api/CopilotAdoption/export/workbook");
                Assert.AreEqual(HttpStatusCode.OK, failedExport.StatusCode);
                AssertWorkbookWithholds(await failedExport.Content.ReadAsByteArrayAsync(), LeadershipComparisonStatuses.Unavailable);

                await CompletesPromptly(values.Stopped.Task);
                provider.Invalidate();
                var recovered = JObject.Parse(await host.Client.GetStringAsync("api/CopilotAdoption/summary"))["leadershipComparison"];
                Assert.AreEqual(LeadershipComparisonStatuses.Suppressed, (string)recovered["status"]);
                Assert.AreEqual(reason, (string)recovered["reason"]);
                AssertWithholdsEveryFigure(recovered.ToObject<LeadershipAdoptionComparison>());
                var export = await host.Client.GetAsync("api/CopilotAdoption/export/workbook");
                Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
                AssertWorkbookWithholds(await export.Content.ReadAsByteArrayAsync(), LeadershipComparisonStatuses.Suppressed);
                Assert.AreEqual(1, runner.Calls);
            }
        }

        [TestMethod]
        public async Task Report_WorkbookDeadlineReturnsAValidCompletedDownload()
        {
            var values = new HeldLeadershipReads();
            var store = await ReadyDeadlineStore(values);
            values.HoldKey = LeadershipCohortStore.SnapshotKey;
            var runner = new DeadlineRunner(Analysis(40));
            var cache = new DeadlineAnalysisCache();
            var coordinator = DeadlineCoordinator(runner, cache);
            await coordinator.GetAsync(28, new List<int>());
            using (var host = DeadlineHost(coordinator, DeadlineProvider(store)))
            {
                var request = host.Client.GetAsync("api/CopilotAdoption/export/workbook");
                await CompletesPromptly(request);
                var response = await request;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType.MediaType);
                AssertWorkbookWithholds(await response.Content.ReadAsByteArrayAsync(), LeadershipComparisonStatuses.Unavailable);
                Assert.AreEqual(1, runner.Calls);
                Assert.AreEqual(1, cache.Writes);
                await CompletesPromptly(values.Stopped.Task);
                Assert.AreEqual(0, values.Active);
            }
        }

        private static LeadershipComparisonProvider DeadlineProvider(LeadershipCohortStore store, Func<DateTime> utcNow = null) =>
            new LeadershipComparisonProvider(() => store, utcNow ?? (() => Now), TimeSpan.FromMilliseconds(100));

        private static async Task<LeadershipCohortStore> ReadyDeadlineStore(IKeyValueStore values, int members = 20)
        {
            var store = new LeadershipCohortStore(values, true);
            await PublishDeadlineRevision(store, "r1", "v1", members);
            if (values is HeldLeadershipReads held)
            {
                held.Reads = 0;
                held.ReadTokens.Clear();
            }
            return store;
        }

        private static async Task PublishDeadlineRevision(LeadershipCohortStore store, string revision, string version, int members)
        {
            await store.SaveSettingsAsync(new LeadershipCohortSettings
            {
                GroupId = ObjectId(654),
                Revision = revision,
                UpdatedUtc = Now,
            });
            await store.SaveMembersAsync(new LeadershipCohortSnapshot
            {
                Version = version,
                GroupId = ObjectId(654),
                SettingsRevision = revision,
                Status = LeadershipCohortRefreshStatuses.Ready,
                AttemptedUtc = Now,
                RefreshedUtc = Now,
            }, Enumerable.Range(1, members).ToArray());
        }

        private static async Task CompletesPromptly(Task task)
        {
            Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(1500)),
                "An optional leadership storage read must not hang a completed adoption report.");
        }

        private static AdoptionCoordinator DeadlineCoordinator(DeadlineRunner runner, DeadlineAnalysisCache cache) =>
            new AdoptionCoordinator(runner, cache, (_, __) => NullAnalysisTelemetry.Instance, TimeSpan.FromMinutes(10));

        private static PortalTestHost DeadlineHost(AdoptionCoordinator coordinator, LeadershipComparisonProvider provider, params string[] roles) =>
            new PortalTestHost(new[] { typeof(CopilotAdoptionAPIController) }, PortalTestHost.SignedIn(roles),
                PortalAccessPolicy.Enforcing, _ => new CopilotAdoptionAPIController(coordinator) { LeadershipComparisons = provider });

        private static void AssertWorkbookWithholds(byte[] bytes, string status)
        {
            var facts = new Dictionary<string, string>();
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    using (var part = entry.Open())
                    {
                        foreach (var row in XDocument.Load(part).Descendants().Where(e => e.Name.LocalName == "row"))
                        {
                            var cells = row.Elements().ToList();
                            if (cells.Count < 2 || !cells[0].Value.StartsWith("leadership.", StringComparison.Ordinal)) continue;
                            facts.Add(cells[0].Value, cells[1].Value);
                        }
                    }
                }
            }
            Assert.AreEqual(status, facts["leadership.status"]);
            foreach (var key in new[] { "licensedLeaders", "activeLeaders", "habitualLeaders", "leaderAdoptionRatePct",
                "leaderHabitRatePct", "leaderAverageScore", "tenantAdoptionRatePct", "tenantHabitRatePct", "tenantAverageScore",
                "adoptionGapPts", "habitGapPts", "scoreGap" })
                Assert.AreEqual(string.Empty, facts["leadership." + key], "The export must be as private as the screen: " + key);
        }

        private sealed class CancellableLeadershipTableService : HttpMessageHandler
        {
            private readonly bool _stallOpening;
            internal CancellableLeadershipTableService(bool stallOpening) { _stallOpening = stallOpening; }
            internal bool Stall = true;
            internal CancellationToken LastToken;
            internal int ActiveRequests;
            internal TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Stopped { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (Stall && request.Method == (_stallOpening ? HttpMethod.Post : HttpMethod.Get))
                {
                    LastToken = cancellationToken;
                    Interlocked.Increment(ref ActiveRequests);
                    Started.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    finally
                    {
                        Interlocked.Decrement(ref ActiveRequests);
                        Stopped.TrySetResult(true);
                    }
                }
                if (request.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.NoContent);
                var response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "{\"odata.error\":{\"code\":\"ResourceNotFound\",\"message\":{\"lang\":\"en-US\",\"value\":\"Synthetic missing row.\"}}}",
                        System.Text.Encoding.UTF8, "application/json"),
                };
                response.Headers.Add("x-ms-error-code", "ResourceNotFound");
                return response;
            }
        }

        private sealed class DeadlineRunner : AdoptionRunner
        {
            private readonly CopilotAdoptionAnalysis _analysis;
            internal DeadlineRunner(CopilotAdoptionAnalysis analysis)
            {
                _analysis = analysis;
                var summary = analysis.Summary;
                summary.Options = CopilotAdoptionOptions.Default;
                summary.GeneratedUtc = Now;
                summary.WindowDays = summary.Options.WindowDays;
                summary.FromUtc = Now.AddDays(-summary.WindowDays);
                summary.ToUtc = Now;
                summary.DataSources.UserMetadataAvailable = true;
                summary.DataSources.AuditAvailable = true;
                new CopilotAdoptionService(summary.Options).FinaliseSummary(analysis);
            }
            internal int Calls;
            internal TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Complete { get; set; }

            public async Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc,
                bool usesExplicitDates, List<int> seatLicenceTypeIds, CopilotAdoptionEffectiveScoreSettings scoreSettings, ICopilotAdoptionRunTelemetry telemetry)
            {
                Interlocked.Increment(ref Calls);
                Started.TrySetResult(true);
                if (Complete != null) await Complete.Task;
                return _analysis;
            }
        }

        private sealed class DeadlineAnalysisCache : AdoptionCache
        {
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _entries = new Dictionary<string, CopilotAdoptionAnalysis>();
            internal int Writes;
            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                lock (_entries) return _entries.TryGetValue(key, out analysis);
            }
            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
                lock (_entries)
                {
                    _entries[key] = analysis;
                    Writes++;
                }
            }
        }

        private sealed class HeldLeadershipReads : IKeyValueStore
        {
            internal InMemoryKeyValueStore Inner { get; } = new InMemoryKeyValueStore();
            internal string HoldKey;
            internal bool Cooperative = true;
            internal Exception LateFailure;
            internal int Active;
            internal int Reads;
            internal TimeSpan ReadDelay;
            internal CancellationToken LastHeldToken;
            internal List<CancellationToken> ReadTokens { get; } = new List<CancellationToken>();
            internal TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Stopped { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Cancelled { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _held;

            public string Description => "synthetic held leadership storage";

            public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Reads);
                lock (ReadTokens) ReadTokens.Add(cancellationToken);
                if (ReadDelay > TimeSpan.Zero) await Task.Delay(ReadDelay, cancellationToken);
                var value = await Inner.GetStringAsync(key, cancellationToken);
                if (key != HoldKey || Interlocked.CompareExchange(ref _held, 1, 0) != 0) return value;
                LastHeldToken = cancellationToken;
                Interlocked.Increment(ref Active);
                Started.TrySetResult(true);
                try
                {
                    using (cancellationToken.Register(() =>
                    {
                        Cancelled.TrySetResult(true);
                        if (Cooperative) _release.TrySetCanceled(cancellationToken);
                    }))
                    {
                        await _release.Task;
                    }
                    if (LateFailure != null) throw LateFailure;
                    return value;
                }
                finally
                {
                    Interlocked.Decrement(ref Active);
                    Stopped.TrySetResult(true);
                }
            }

            internal void Release() => _release.TrySetResult(true);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) =>
                Inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Inner.ExistsAsync(key, cancellationToken);
        }
    }
}
