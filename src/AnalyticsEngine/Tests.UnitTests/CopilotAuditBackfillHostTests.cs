extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.State;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ActivityHost = WebJob.Office365ActivityImporter.Program;
using BackfillService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAuditBackfill.CopilotAuditBackfillService;
using StartRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAuditBackfill.CopilotAuditBackfillStartRequest;

namespace Tests.UnitTests
{
    public partial class CopilotAuditBackfillTests
    {
        [DataTestMethod]
        [DataRow(true, DisplayName = "Offline cancellation with Copilot as the last Activity API workload")]
        [DataRow(false, DisplayName = "Copilot switched off with a running backfill")]
        public async Task ActivityHost_FinalizesPublishedJobWhenLastWorkloadIsDisabled(bool cancelled)
        {
            var now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
            var values = new InMemoryKeyValueStore(() => now);
            var state = new CopilotAuditBackfillStateStore(values, isDurable: true, () => now);
            var config = new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } };
            var portal = new BackfillService(state, config, () => now);
            var request = new StartRequest { StartDateUtc = now.AddDays(-1) };
            var queued = await portal.StartAsync(request, "admin@contoso.com");
            if (cancelled)
            {
                Assert.IsTrue((await portal.CancelAsync(queued.Id)).CancelRequested);
            }
            else
            {
                var checkpoint = await state.GetAsync(queued.Id);
                checkpoint.State = CopilotAuditBackfillStates.Running;
                checkpoint.StartedUtc = now;
                checkpoint.RecordsImported = 10;
                checkpoint.PermissionStatus = CopilotAuditBackfillPermissionStates.Granted;
                checkpoint.PendingSlices[0].QueryId = "query-in-progress";
                checkpoint.InFlightSlices.Add(checkpoint.PendingSlices[0]);
                checkpoint.PendingSlices.Clear();
                await state.SaveAsync(checkpoint);
            }
            var before = (await portal.GetStatusAsync()).LatestJob;
            config.ImportJobSettings.Copilot = false;
            var restarted = new CopilotAuditBackfillStateStore(values, isDurable: true, () => now);
            var downloads = 0;
            var source = new FakeAuditSearchSource();
            Task Download(CopilotAuditBackfillStateStore store)
            {
                downloads++;
                return NewImporter(store, source, new CountingPersistence()).AdvanceLatestAsync();
            }

            using (var captured = new CapturedAnalyticsLogger())
            {
                await ActivityHost.RunActivityPhaseAsync(config.ImportJobSettings, restarted, Download, captured.Logger, CancellationToken.None);
                var result = (await portal.GetStatusAsync()).LatestJob;
                Assert.AreEqual(cancelled ? CopilotAuditBackfillStates.Cancelled : CopilotAuditBackfillStates.Failed, result.State);
                Assert.AreEqual(cancelled ? null : CopilotAuditBackfillErrorCodes.CopilotImportOff, result.LastErrorCode);
                Assert.AreEqual(cancelled, result.CancelRequested);
                Assert.IsFalse(result.CopilotImportEnabled);
                Assert.IsNotNull(result.CompletedUtc);
                Assert.AreEqual(before.StartedUtc, result.StartedUtc);
                Assert.AreEqual(before.RecordsImported, result.RecordsImported);
                Assert.AreEqual(before.PendingSlices, result.PendingSlices);
                Assert.AreEqual(before.InFlightSlices, result.InFlightSlices);
                Assert.AreEqual(before.PermissionStatus, result.PermissionStatus, "No permission probe was made.");
                Assert.AreEqual(0, downloads);
                Assert.AreEqual(0, source.SubmitCount);
                var health = captured.Events.Single(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.HealthCheck));
                Assert.AreEqual(cancelled ? "Healthy" : "Degraded", health.Properties["Status"]);
                Assert.AreEqual(cancelled ? "copilotAuditBackfill.cancelled" : "copilotAuditBackfill.failed", health.Properties["ReasonKey"]);
                Assert.IsFalse(captured.Events.Any(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.ImportStage)));

                var jobKey = "Job:" + queued.Id;
                var finished = await values.GetVersionedAsync(jobKey);
                var events = captured.Events.Count;
                await ActivityHost.RunActivityPhaseAsync(config.ImportJobSettings, restarted, Download, captured.Logger, CancellationToken.None);
                Assert.AreEqual(finished.VersionToken, (await values.GetVersionedAsync(jobKey)).VersionToken);
                Assert.AreEqual(events, captured.Events.Count, "A disabled, idle cycle is not an import or a new terminal transition.");

                config.ImportJobSettings.Copilot = true;
                var replacement = await portal.StartAsync(request, "admin@contoso.com");
                Assert.AreNotEqual(queued.Id, replacement.Id);
                await ActivityHost.RunActivityPhaseAsync(config.ImportJobSettings, restarted, Download, captured.Logger, CancellationToken.None);
                Assert.AreEqual(1, downloads);
                Assert.AreEqual(1, source.SubmitCount, "Only the replacement may submit its one slice.");
                Assert.AreEqual(CopilotAuditBackfillStates.Completed, (await state.GetAsync(replacement.Id)).State);
                Assert.AreEqual(finished.VersionToken, (await values.GetVersionedAsync(jobKey)).VersionToken);
            }
        }

        [TestMethod]
        public async Task ActivityHost_DisabledDoesNotRewriteCompletedJobOrTouchUnpublishedSuccessor()
        {
            var now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
            var values = new InMemoryKeyValueStore(() => now);
            var state = new CopilotAuditBackfillStateStore(values, true, () => now);
            var job = await state.TryCreateWhenIdleAsync(now.AddDays(-1), now, "admin@contoso.com", true);
            Assert.IsTrue(await state.RequestCancelAsync(job.Id));
            // Work in flight can finish before the worker observes the cancellation.
            job.State = CopilotAuditBackfillStates.Completed;
            job.CompletedUtc = now;
            await state.SaveAsync(job);
            var reserved = new CopilotAuditBackfillJob { Id = job.Id + 1, State = CopilotAuditBackfillStates.Queued };
            await state.SaveAsync(reserved);
            var finishedVersion = (await values.GetVersionedAsync("Job:" + job.Id)).VersionToken;
            var reservedVersion = (await values.GetVersionedAsync("Job:" + reserved.Id)).VersionToken;

            using (var captured = new CapturedAnalyticsLogger())
            {
                await ActivityHost.RunActivityPhaseAsync(new ImportTaskSettings(), state, _ => Task.CompletedTask,
                    captured.Logger, CancellationToken.None);
                Assert.AreEqual(finishedVersion, (await values.GetVersionedAsync("Job:" + job.Id)).VersionToken);
                Assert.AreEqual(reservedVersion, (await values.GetVersionedAsync("Job:" + reserved.Id)).VersionToken);
                Assert.AreEqual(job.Id, (await state.GetLatestAsync()).Id);
                Assert.AreEqual(0, captured.Events.Count);
            }
        }

        [TestMethod]
        public async Task ActivityHost_AnotherEnabledActivityFeedStillRunsOnlyTheLiveStage()
        {
            var reads = 0;
            var state = new CopilotAuditBackfillStateStore(new BackfillReadProbe((_, __) => { reads++; return null; }), true);
            var downloads = 0;
            using (var captured = new CapturedAnalyticsLogger())
            {
                await ActivityHost.RunActivityPhaseAsync(new ImportTaskSettings { ImportDlp = true }, state,
                    _ => { downloads++; return Task.CompletedTask; }, captured.Logger, CancellationToken.None);
                Assert.AreEqual(1, downloads);
                Assert.AreEqual(0, reads, "The disabled recovery check must not also run after the live stage.");
                var stage = captured.Events.Single(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.ImportStage));
                Assert.AreEqual("ActivityImport", stage.Properties["Stage"]);
                Assert.AreEqual("Succeeded", stage.Properties["Outcome"]);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task ActivityHost_DisabledWithNoPublishedJobOnlyChecksDurablePointer(bool durable)
        {
            var reads = new List<string>();
            var values = new BackfillReadProbe((key, _) => { reads.Add(key); return null; });
            using (var captured = new CapturedAnalyticsLogger())
            {
                await ActivityHost.RunActivityPhaseAsync(new ImportTaskSettings(), new CopilotAuditBackfillStateStore(values, durable),
                    _ => throw new InvalidOperationException("Unexpected live download."), captured.Logger, CancellationToken.None);
                CollectionAssert.AreEqual(durable ? new[] { "LatestJob" } : Array.Empty<string>(), reads);
                Assert.AreEqual(0, captured.Events.Count);
                Assert.AreEqual(0, values.Writes);
            }
        }

        [TestMethod]
        public async Task ActivityHost_RequestedStopDuringDisabledCheckLeavesJobForNextCycle()
        {
            using (var stop = new CancellationTokenSource())
            using (var captured = new CapturedAnalyticsLogger())
            {
                var readToken = CancellationToken.None;
                var values = new BackfillReadProbe((key, token) =>
                {
                    if (key == "LatestJob") return "1";
                    readToken = token;
                    stop.Cancel();
                    token.ThrowIfCancellationRequested();
                    return null;
                });
                await Assert.ThrowsExceptionAsync<ShutdownRequestedException>(() => ActivityHost.RunActivityPhaseAsync(
                    new ImportTaskSettings(), new CopilotAuditBackfillStateStore(values, true), _ => Task.CompletedTask,
                    captured.Logger, stop.Token));
                Assert.AreEqual(stop.Token, readToken);
                Assert.AreEqual(0, values.Writes);
                Assert.AreEqual(0, captured.Events.Count);
            }
        }

        [TestMethod]
        public async Task ActivityHost_FailedCheckpointDoesNotAnnounceATerminalOutcome()
        {
            var values = new BackfillReadProbe((key, _) => key == "LatestJob" ? "1" : key == "Job:1" ? "{\"Id\":1,\"State\":\"queued\"}" : null)
            {
                FailWrite = true
            };
            using (var captured = new CapturedAnalyticsLogger())
            {
                await ActivityHost.RunActivityPhaseAsync(new ImportTaskSettings(), new CopilotAuditBackfillStateStore(values, true),
                    _ => Task.CompletedTask, captured.Logger, CancellationToken.None);
                Assert.AreEqual(1, values.Writes);
                Assert.AreEqual(0, captured.Events.Count, "The job and its health should not be reported as failed until the checkpoint is saved.");
                Assert.IsTrue(captured.Messages().Any(m => m.Contains("retry next cycle")));
            }
        }

        private sealed class BackfillReadProbe : IKeyValueStore
        {
            private readonly Func<string, CancellationToken, string> _read;
            public BackfillReadProbe(Func<string, CancellationToken, string> read) { _read = read; }
            public int Writes { get; private set; }
            public bool FailWrite { get; set; }
            public string Description => "Synthetic backfill state";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
                => Task.FromResult(_read(key, cancellationToken));
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                Writes++;
                if (FailWrite) throw new InvalidOperationException("Synthetic storage failure.");
                return Task.CompletedTask;
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
