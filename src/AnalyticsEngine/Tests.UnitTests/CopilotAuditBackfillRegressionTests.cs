using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill;

namespace Tests.UnitTests
{
    public partial class CopilotAuditBackfillTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public async Task Importer_LedgerWriteFailureRecoversSuccessfulSubmissionBeforeRestartTopUp()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new ThrowingLedgerStore();
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);
            await state.CreateAsync(now.AddDays(-10), now, "admin@contoso.com");
            await state.SaveSubmissionLedgerAsync(new CopilotAuditBackfillSubmissionLedger
            {
                SubmissionTimestampsUtc = Enumerable.Repeat(now.AddHours(-1), 99).ToList()
            });
            store.ThrowLedgerWrites = true;
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "running" } };
            await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            store.ThrowLedgerWrites = false;

            var restarted = new CopilotAuditBackfillStateStore(store, true, () => now);
            await NewImporter(restarted, source, new CountingPersistence()).AdvanceLatestAsync();

            Assert.AreEqual(1, source.SubmitCount, "The successful POST before the failed ledger write consumed the last slot.");
            Assert.AreEqual(100, (await restarted.GetSubmissionLedgerAsync()).SubmissionTimestampsUtc.Count);
        }

        [TestMethod]
        public async Task Importer_CancelAndNewJobRecoverSubmissionMissingFromSharedLedger()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new ThrowingLedgerStore();
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);
            var first = await state.CreateAsync(now.AddDays(-10), now, "admin@contoso.com");
            await state.SaveSubmissionLedgerAsync(new CopilotAuditBackfillSubmissionLedger
            {
                SubmissionTimestampsUtc = Enumerable.Repeat(now.AddHours(-1), 99).ToList()
            });
            store.ThrowLedgerWrites = true;
            var source = new FakeAuditSearchSource();
            await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            store.ThrowLedgerWrites = false;
            await state.RequestCancelAsync(first.Id);
            await state.CreateAsync(now.AddDays(-2), now, "admin@contoso.com");

            var job = await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();

            Assert.AreEqual(1, source.SubmitCount);
            Assert.IsTrue(job.SubmissionsPausedUntilUtc > now);
            Assert.AreEqual(100, (await state.GetSubmissionLedgerAsync()).SubmissionTimestampsUtc.Count);
        }

        [TestMethod]
        public async Task Importer_ThrottlePauseSurvivesLedgerFailureAndReplacementJob()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new ThrowingLedgerStore();
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);
            var first = await state.CreateAsync(now.AddDays(-1), now, "admin@contoso.com");
            store.ThrowLedgerWrites = true;
            var source = new FakeAuditSearchSource { SubmitFailure = new CopilotAuditSearchThrottledException(1800, null) };
            await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            store.ThrowLedgerWrites = false;
            await state.RequestCancelAsync(first.Id);
            await state.CreateAsync(now.AddDays(-2), now, "admin@contoso.com");

            var job = await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();

            Assert.AreEqual(1, source.SubmitCount);
            Assert.AreEqual(now.AddMinutes(30), job.SubmissionsPausedUntilUtc);
            Assert.AreEqual(now.AddMinutes(30), (await state.GetSubmissionLedgerAsync()).SubmissionsPausedUntilUtc);
        }

        [TestMethod]
        public async Task Importer_HealthyLedgerDoesNotDoubleCountIdenticalSubmissionTimestamps()
        {
            var state = await NewStateWithJobAsync(days: 10);
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "running" } };
            await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            Assert.AreEqual(4, source.SubmitCount);
            Assert.AreEqual(4, (await state.GetSubmissionLedgerAsync()).SubmissionTimestampsUtc.Count);
        }

        [TestMethod]
        public async Task Importer_ExpiredSubmissionBudgetYieldsBeforeToppingUpMoreQueries()
        {
            var state = await NewStateWithJobAsync(days: 10);
            var expired = false;
            var source = new CallbackSource(_ => throw new AssertFailedException("Expired cycle must not fetch records."))
            {
                OnSubmit = () => { expired = true; return Task.CompletedTask; }
            };
            var job = await RegressionImporter(state, source, new CountingPersistence(),
                () => expired ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(1, source.Submits);
            Assert.AreEqual(1, job.InFlightSlices.Count);
            Assert.AreEqual(9, job.PendingSlices.Count);
        }

        [TestMethod]
        public async Task Importer_CancelBeforeAdvanceDoesNotRequirePermission()
        {
            var state = await NewStateWithJobAsync();
            await state.RequestCancelAsync((await state.GetLatestAsync()).Id);
            var source = new FakeAuditSearchSource { Access = AppTokenPermissionAccess.NotGranted };
            var job = await NewImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Cancelled, job.State);
            Assert.AreEqual(0, source.SubmitCount);
        }

        [TestMethod]
        public async Task Importer_PollWithinBudgetStillImportsRecords()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource();
            source.Pages.Enqueue(new CopilotAuditSearchRecordPage
            {
                Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(1)) }
            });
            var persistence = new CountingPersistence();
            var job = await RegressionImporter(state, source, persistence,
                () => source.PollCount > 0 ? TimeSpan.FromMinutes(7) : TimeSpan.Zero).AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(1, source.PollCount);
            Assert.AreEqual(1, source.RecordPagesRead);
            Assert.AreEqual(1, persistence.CommitCount);
            Assert.AreEqual(1, job.SlicesCompleted);
        }

        [DataTestMethod]
        [DataRow(8)]
        [DataRow(9)]
        public async Task Importer_PollExhaustingBudgetDefersRecordsUntilNextCycle(int elapsedMinutes)
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource();
            source.Pages.Enqueue(new CopilotAuditSearchRecordPage
            {
                Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(1)) }
            });
            var persistence = new CountingPersistence();
            var job = await RegressionImporter(state, source, persistence,
                () => source.PollCount > 0 ? TimeSpan.FromMinutes(elapsedMinutes) : TimeSpan.Zero).AdvanceLatestAsync();

            Assert.AreEqual(1, source.PollCount);
            Assert.AreEqual(0, source.RecordPagesRead, "A successful status poll must not start records after exhausting the cycle budget.");
            Assert.AreEqual(0, persistence.CommitCount);
            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(0, job.SlicesCompleted);
            Assert.AreEqual(1, job.InFlightSlices.Count);
            Assert.IsTrue(job.InFlightSlices[0].ImportingRecords);
            Assert.AreEqual("query-1", job.InFlightSlices[0].QueryId);

            job = await RegressionImporter(state, source, persistence, () => TimeSpan.Zero).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(1, source.SubmitCount, "Resume must not resubmit the successful query.");
            Assert.AreEqual(1, source.PollCount, "Resume must retain the successful poll's record-import state.");
            Assert.AreEqual(1, source.RecordPagesRead);
            Assert.AreEqual(1, persistence.CommitCount);
            Assert.AreEqual(1, job.SlicesCompleted);
            Assert.AreEqual(1L, job.RecordsImported);
        }

        [TestMethod]
        public async Task Importer_ExpiredBudgetDoesNotDrainOtherReadyQueries()
        {
            var state = await NewStateWithJobAsync(days: 10);
            var expired = false;
            var reads = 0;
            var source = new CallbackSource(_ =>
            {
                reads++;
                expired = true;
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(1)) }
                });
            });
            var job = await RegressionImporter(state, source, new CountingPersistence(),
                () => expired ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(4, source.Submits);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(3, job.InFlightSlices.Count);
            Assert.AreEqual(1L, job.RecordsImported);
        }

        [TestMethod]
        public async Task Importer_ServiceRecordLimitSplitsDayThroughSixHoursToHoursWithoutImportingParents()
        {
            var state = await NewStateWithJobAsync();
            var source = new SplittingSource();
            var persistence = new CountingPersistence();
            CopilotAuditBackfillJob job;
            var cycles = 0;
            do
            {
                job = await RegressionImporter(state, source, persistence).AdvanceLatestAsync();
                Assert.IsTrue(++cycles <= 20, "Split slices did not converge.");
            } while (job.IsActive);
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.IsNull(job.LastErrorCode);
            Assert.AreEqual(29, source.Slices.Count);
            Assert.AreEqual(5, job.SlicesSplit);
            Assert.AreEqual(24, source.RecordWindows.Count);
            Assert.AreEqual(24L, job.RecordsImported);
            var windows = source.RecordWindows.OrderBy(s => s.StartUtc).ToList();
            Assert.AreEqual(new DateTime(2026, 10, 1), windows.First().StartUtc);
            Assert.AreEqual(new DateTime(2026, 10, 2), windows.Last().EndUtc);
            for (var i = 0; i < windows.Count; i++)
            {
                Assert.AreEqual(TimeSpan.FromHours(1), windows[i].EndUtc - windows[i].StartUtc);
                if (i > 0) Assert.AreEqual(windows[i - 1].EndUtc, windows[i].StartUtc);
            }
        }

        [TestMethod]
        public async Task Importer_CancelDuringSubmissionDoesNotTopUpOtherQueries()
        {
            var state = await NewStateWithJobAsync(days: 10);
            var first = await state.GetLatestAsync();
            var source = new CallbackSource(_ => Task.FromResult(new CopilotAuditSearchRecordPage()))
            {
                OnSubmit = () => state.RequestCancelAsync(first.Id)
            };
            var job = await RegressionImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Cancelled, job.State);
            Assert.AreEqual(1, source.Submits);
        }

        [TestMethod]
        public async Task Importer_CancelDuringPagingStopsBeforeNextPageAndSurvivesRestart()
        {
            var state = await NewStateWithJobAsync();
            var job = await state.GetLatestAsync();
            var reads = 0;
            var source = new CallbackSource(async next =>
            {
                reads++;
                await state.RequestCancelAsync(job.Id);
                return new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(1)) },
                    NextLink = "page-2"
                };
            });
            var persistence = new CountingPersistence();
            job = await RegressionImporter(state, source, persistence).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Cancelled, job.State);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(1, persistence.CommitCount);
            job = await RegressionImporter(state, source, persistence).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Cancelled, job.State);
            Assert.AreEqual(1, reads);
        }

        [TestMethod]
        public async Task Importer_RestartResumesCommittedPageWithoutResubmittingQuery()
        {
            var store = new InMemoryKeyValueStore();
            var state = new CopilotAuditBackfillStateStore(store, true);
            await state.CreateAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), "admin@contoso.com");
            var links = new List<string>();
            var expire = false;
            var source = new CallbackSource(next =>
            {
                links.Add(next);
                expire = true;
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(links.Count)) },
                    NextLink = links.Count == 1 ? "page-2" : null
                });
            });
            var persistence = new CountingPersistence();
            var first = RegressionImporter(state, source, persistence,
                () => expire ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero);
            var job = await first.AdvanceLatestAsync();
            Assert.AreEqual("page-2", job.InFlightSlices.Single().RecordsNextLink);
            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(1, source.Submits);
            Assert.AreEqual(2L, job.RecordsImported);
            CollectionAssert.AreEqual(new string[] { null, "page-2" }, links);
        }

        [TestMethod]
        public async Task Importer_RunningRemoteQueryYieldsWithoutPollingSleep()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "running" } };
            var sleeps = 0;
            var importer = new CopilotAuditBackfillImporter(state, source, new CountingPersistence(),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                delay: _ => { sleeps++; return Task.CompletedTask; }, existingEvents: new FakeExistingEventFilter());
            var job = await importer.AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(source.SubmitCount, source.PollCount);
            Assert.AreEqual(0, sleeps);
        }

        [TestMethod]
        public async Task Importer_MixedMalformedRecordsAreVisibleGapsNotCleanCompletion()
        {
            var state = await NewStateWithJobAsync();
            var source = new CallbackSource(_ => Task.FromResult(CopilotAuditSearchRecordPage.FromJson(
                new JObject { ["value"] = new JArray(JObject.FromObject(SyntheticRecord(SyntheticId(1))), null, "invalid") }.ToString())));
            var job = await RegressionImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.CompletedWithGaps, job.State);
            Assert.AreEqual(3L, job.RecordsSeen);
            Assert.AreEqual(1L, job.RecordsImported);
            Assert.AreEqual(2L, job.MappingFailureCounts.Values.Sum());
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, job.Gaps.Single().ErrorCode);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, job.LastErrorCode);
        }

        [TestMethod]
        public void GraphRecords_MissingValueArrayDoesNotMasqueradeAsEmptySuccess()
        {
            Assert.ThrowsException<JsonSerializationException>(() => CopilotAuditSearchRecordPage.FromJson("{}"));
            Assert.ThrowsException<JsonSerializationException>(() => CopilotAuditSearchRecordPage.FromJson("{\"value\":{}}"));
            Assert.AreEqual(0, CopilotAuditSearchRecordPage.FromJson("{\"value\":[]}").Records.Count);
        }

        [TestMethod]
        public async Task Importer_ContinuationPermissionErrorIsNotRetriedAsFirstPage()
        {
            var state = await NewStateWithJobAsync();
            var links = new List<string>();
            var source = new CallbackSource(next =>
            {
                links.Add(next);
                if (next != null) throw new GraphHttpException(HttpStatusCode.Forbidden, "https://graph.microsoft.com", "{\"error\":{\"code\":\"Authorization_RequestDenied\"}}", null);
                return Task.FromResult(new CopilotAuditSearchRecordPage { NextLink = "page-2" });
            });
            var job = await RegressionImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.GraphAccessDenied, job.LastErrorCode);
            CollectionAssert.AreEqual(new string[] { null, "page-2" }, links);
        }

        [TestMethod]
        public async Task GraphSource_RealHttpAdapterPreservesSubmissionFilterAndOpaquePagingLink()
        {
            var requests = new List<string>();
            var continuation = GraphCopilotAuditSearchSource.BaseUrl + "/q/records?$skiptoken=opaque%2Btoken%3D";
            var handler = new StaticResponseHandler(request =>
            {
                requests.Add(request.RequestUri.AbsoluteUri);
                if (request.Method == HttpMethod.Post)
                {
                    var body = JObject.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    Assert.AreEqual("CopilotInteraction", (string)body["operationFilters"][0]);
                    Assert.AreEqual(1, ((JArray)body["operationFilters"]).Count);
                    Assert.AreEqual(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), body["filterStartDateTime"].Value<DateTime>());
                    Assert.AreEqual(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), body["filterEndDateTime"].Value<DateTime>());
                    return JsonResponse("{\"id\":\"q\",\"status\":\"running\"}");
                }
                return JsonResponse(requests.Count == 2
                    ? new JObject { ["value"] = new JArray(), ["@odata.nextLink"] = continuation }.ToString()
                    : new JObject { ["value"] = new JArray(JObject.FromObject(SyntheticRecord(SyntheticId(1)))) }.ToString());
            });
            var source = new GraphCopilotAuditSearchSource(new ManualGraphCallClient(handler, NullLogger.Instance), null, NullLogger.Instance);
            await source.SubmitQueryAsync(new CopilotAuditBackfillSlice
            {
                StartUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)
            });
            var first = await source.GetRecordsAsync("q");
            var last = await source.GetRecordsAsync("q", first.NextLink);
            Assert.AreEqual(continuation, requests.Last());
            Assert.AreEqual(1, last.Records.Count);
            Assert.IsNull(last.NextLink);
        }

        [TestMethod]
        public async Task Importer_RollingBudgetExpiresAtExactBoundaryAfterMidnight()
        {
            var now = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true, () => now);
            await state.CreateAsync(now.AddDays(-1), now, "admin@contoso.com");
            await state.SaveSubmissionLedgerAsync(new CopilotAuditBackfillSubmissionLedger
            {
                SubmissionTimestampsUtc = Enumerable.Repeat(now.AddHours(-24), 1)
                    .Concat(Enumerable.Repeat(now.AddHours(-23), 99)).ToList()
            });
            var source = new FakeAuditSearchSource();
            var job = await new CopilotAuditBackfillImporter(state, source, new CountingPersistence(),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => now, existingEvents: new FakeExistingEventFilter()).AdvanceLatestAsync();
            Assert.AreEqual(1, source.SubmitCount);
            Assert.AreEqual(100, (await state.GetSubmissionLedgerAsync()).SubmissionTimestampsUtc.Count);
        }

        private static CopilotAuditBackfillImporter RegressionImporter(CopilotAuditBackfillStateStore state,
            ICopilotAuditSearchSource source, IActivityReportPersistenceManager persistence, Func<TimeSpan> elapsed = null,
            ICopilotAuditBackfillExistingEventFilter existing = null)
            => new CopilotAuditBackfillImporter(state, source, persistence,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                existingEvents: existing ?? new FakeExistingEventFilter(), elapsedOverride: elapsed);

        private static HttpResponseMessage JsonResponse(string json)
            => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static string SyntheticId(int value) => $"00000000-0000-0000-0000-{value:000000000000}";

        private sealed class CallbackSource : ICopilotAuditSearchSource
        {
            private readonly Func<string, Task<CopilotAuditSearchRecordPage>> _page;
            public int Submits;
            public Func<Task> OnSubmit;
            public CallbackSource(Func<string, Task<CopilotAuditSearchRecordPage>> page) { _page = page; }
            public Task<AppTokenPermissionAccess> GetPermissionAccessAsync() => Task.FromResult(AppTokenPermissionAccess.Granted);
            public async Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
            {
                Submits++;
                if (OnSubmit != null) await OnSubmit();
                return new CopilotAuditSearchQuery { Id = "q-" + Submits, Status = "running" };
            }
            public Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
                => Task.FromResult(new CopilotAuditSearchQuery { Id = queryId, Status = "succeeded" });
            public Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null) => _page(nextLink);
        }

        private sealed class SplittingSource : ICopilotAuditSearchSource
        {
            public List<CopilotAuditBackfillSlice> Slices = new List<CopilotAuditBackfillSlice>();
            public List<CopilotAuditBackfillSlice> RecordWindows = new List<CopilotAuditBackfillSlice>();
            public Task<AppTokenPermissionAccess> GetPermissionAccessAsync() => Task.FromResult(AppTokenPermissionAccess.Granted);
            public Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
            {
                Slices.Add(slice);
                return Task.FromResult(new CopilotAuditSearchQuery { Id = Slices.Count.ToString() });
            }
            public Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
                => Task.FromResult(CopilotAuditSearchQuery.FromJson(new JObject
                {
                    ["id"] = queryId, ["status"] = "succeeded",
                    ["isRecordCountLimitExceeded"] = (Slices[int.Parse(queryId) - 1].EndUtc - Slices[int.Parse(queryId) - 1].StartUtc).TotalHours > 1
                }.ToString()));
            public Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null)
            {
                var slice = Slices[int.Parse(queryId) - 1];
                RecordWindows.Add(slice);
                Assert.AreEqual(TimeSpan.FromHours(1), slice.EndUtc - slice.StartUtc);
                var record = SyntheticRecord(SyntheticId(int.Parse(queryId)));
                record.CreatedDateTime = slice.StartUtc.AddMinutes(30);
                record.AuditData["CreationTime"] = record.CreatedDateTime.Value.ToString("s") + "Z";
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord> { record }
                });
            }
        }
    }
}
