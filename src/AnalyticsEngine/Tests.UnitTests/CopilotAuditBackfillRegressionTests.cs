extern alias AnalyticsWeb;

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
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using BackfillAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotAuditBackfillAPIController;
using BackfillRequestException = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAuditBackfill.CopilotAuditBackfillRequestException;
using BackfillStatusService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAuditBackfill.CopilotAuditBackfillService;

namespace Tests.UnitTests
{
    public partial class CopilotAuditBackfillTests
    {
        [TestMethod]
        public async Task Importer_PersistenceFailureIsLoggedWhileDurableErrorRemainsSanitised()
        {
            var state = await NewStateWithJobAsync();
            var logger = new FakeLoaderClasses.RecordingLogger();
            var failure = new InvalidOperationException("Synthetic private persistence failure.");
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[]
                {
                    new CopilotAuditSearchRecordPage
                    {
                        Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(1)) }
                    }
                })
            };
            var importer = new CopilotAuditBackfillImporter(state, source,
                new MeasuredScalePersistence(_ => throw failure),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, logger,
                () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), _ => Task.CompletedTask,
                new FakeExistingEventFilter());

            var job = await importer.AdvanceLatestAsync();

            Assert.IsTrue(logger.Entries.Any(e => ReferenceEquals(e.Exception, failure)),
                "Operators need the underlying SQL/backend exception in the private importer log.");
            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryFailed, job.LastErrorCode);
            Assert.AreEqual(nameof(InvalidOperationException), job.LastErrorDetail);
            Assert.AreEqual(1, job.PendingSlices.Count, "The failed slice must remain resumable.");
            Assert.AreEqual(0, job.RecordsImported);
        }

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
            CollectionAssert.AreEqual(new[] { "2026-10-01" }, job.CompletedDays,
                "A day completed through hourly children must appear in the operator's completed-days list.");
            var windows = source.RecordWindows.OrderBy(s => s.StartUtc).ToList();
            Assert.AreEqual(new DateTime(2026, 10, 1), windows.First().StartUtc);
            Assert.AreEqual(new DateTime(2026, 10, 2), windows.Last().EndUtc);
            for (var i = 0; i < windows.Count; i++)
            {
                Assert.AreEqual(TimeSpan.FromHours(1), windows[i].EndUtc - windows[i].StartUtc);
                if (i > 0) Assert.AreEqual(windows[i - 1].EndUtc, windows[i].StartUtc);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Importer_SplitDayWithMissingQueryCoverageIsNotCompleted(bool truncated)
        {
            var state = await NewStateWithJobAsync();
            var job = await state.GetLatestAsync();
            job.PendingSlices = CopilotAuditBackfillSlicer.SplitIntoHours(job.PendingSlices.Single());
            // A truncated slice only becomes a gap once nothing narrower is left to try.
            foreach (var slice in job.PendingSlices) slice.SplitLevel = truncated ? CopilotAuditBackfillSlicer.MaxSplitLevel : 2;
            if (!truncated) job.PendingSlices[0].AttemptCount = CopilotAuditBackfillImporter.MaxSliceAttempts - 1;
            await state.SaveAsync(job);
            var source = new FakeAuditSearchSource();
            source.Queries.Enqueue(new CopilotAuditSearchQuery
            {
                Status = truncated ? "succeeded" : "failed",
                IsTruncated = truncated
            });
            var cycles = 0;
            do
            {
                job = await RegressionImporter(state, source, new CountingPersistence()).AdvanceLatestAsync();
                Assert.IsTrue(++cycles <= 20);
            } while (job.IsActive);

            Assert.AreEqual(CopilotAuditBackfillStates.CompletedWithGaps, job.State);
            Assert.AreEqual(0, job.CompletedDays.Count);
            Assert.AreEqual(truncated ? CopilotAuditBackfillErrorCodes.QueryTruncated : CopilotAuditBackfillErrorCodes.QueryFailed,
                job.Gaps.Single().ErrorCode);
            var status = await new BackfillStatusService(state,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }).GetStatusAsync();
            Assert.AreEqual(0, status.LatestJob.CompletedDays.Length);
            CollectionAssert.AreEqual(new[] { "2026-10-01" },
                truncated ? status.LatestJob.IncompleteDays : status.LatestJob.FailedDays);
        }

        [TestMethod]
        public async Task Status_CompletedLegacySplitDayIsRecoveredWithoutWritingState()
        {
            var state = await NewStateWithJobAsync();
            var job = await state.GetLatestAsync();
            job.State = CopilotAuditBackfillStates.Completed;
            job.PendingSlices.Clear();
            job.SlicesCompleted = 24;
            await state.SaveAsync(job);

            var status = await new BackfillStatusService(state,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }).GetStatusAsync();

            CollectionAssert.AreEqual(new[] { "2026-10-01" }, status.LatestJob.CompletedDays);
            Assert.AreEqual(0, (await state.GetLatestAsync()).CompletedDays.Count,
                "The status projection heals old progress without rewriting an operator's saved job.");
        }

        [TestMethod]
        public void Progress_PartialBoundaryDaysRemainExcluded()
        {
            var job = new CopilotAuditBackfillJob
            {
                State = CopilotAuditBackfillStates.Completed,
                StartUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)
            };

            Assert.AreEqual(0, job.GetCompletedDays().Count);
        }

        [DataTestMethod]
        [DataRow("pending")]
        [DataRow("inFlight")]
        [DataRow("legacy")]
        public void Progress_CancelledDayWithOutstandingWorkIsNotCompleted(string queue)
        {
            var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var job = new CopilotAuditBackfillJob
            {
                State = CopilotAuditBackfillStates.Cancelled,
                StartUtc = start,
                EndUtc = start.AddDays(1)
            };
            var slice = new CopilotAuditBackfillSlice { StartUtc = start, EndUtc = start.AddHours(1) };
            if (queue == "pending") job.PendingSlices.Add(slice);
            else if (queue == "inFlight") job.InFlightSlices.Add(slice);
            else job.CurrentSlice = slice;

            Assert.AreEqual(0, job.GetCompletedDays().Count);
        }

        [TestMethod]
        public void Progress_RejectedSubmissionDoesNotInventCompletionFromAnEmptyQueue()
        {
            var job = new CopilotAuditBackfillJob
            {
                State = CopilotAuditBackfillStates.Failed,
                LastErrorCode = CopilotAuditBackfillErrorCodes.QueryRejected,
                StartUtc = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                CompletedDays = new List<string> { "2026-10-01" }
            };

            CollectionAssert.AreEqual(new[] { "2026-10-01" }, job.GetCompletedDays().ToArray());
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

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Importer_CancelDuringPagingStopsBeforeNextPageAndSurvivesRestart(bool hasNextPage)
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
                    Records = new List<CopilotAuditSearchRecord>
                    {
                        hasNextPage ? SyntheticRecord(SyntheticId(1)) : null
                    },
                    NextLink = hasNextPage ? "page-2" : null
                };
            });
            var persistence = new CountingPersistence();
            job = await RegressionImporter(state, source, persistence).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Cancelled, job.State);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(hasNextPage ? 1 : 0, persistence.CommitCount);
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
        public async Task Importer_TruncatedQuarterHourKeepsEveryPageAcrossRestartThenRecordsItsGapOnce()
        {
            var store = new InMemoryKeyValueStore();
            var state = new CopilotAuditBackfillStateStore(store, true);
            var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var job = await state.CreateAsync(start, start.AddMinutes(15), "admin@contoso.com");
            // A quarter-hour produced by splitting a truncated hour: nothing narrower is left to try.
            job.PendingSlices.Single().SplitLevel = CopilotAuditBackfillSlicer.MaxSplitLevel;
            await state.SaveAsync(job);
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "succeeded", IsTruncated = true } };
            source.Pages.Enqueue(new CopilotAuditSearchRecordPage
            {
                Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(1)) },
                NextLink = "page-2"
            });
            source.Pages.Enqueue(new CopilotAuditSearchRecordPage
            {
                Records = new List<CopilotAuditSearchRecord> { SyntheticRecord(SyntheticId(2)) }
            });
            var persistence = new CountingPersistence();

            job = await RegressionImporter(state, source, persistence,
                () => source.RecordPagesRead > 0 ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero).AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State,
                "A truncated query's records are read before its slice is closed, even across the cycle budget.");
            Assert.AreEqual(0, job.Gaps.Count, "The gap is recorded once the returned pages have been read, not before.");
            Assert.AreEqual(1L, job.RecordsImported);
            Assert.AreEqual("page-2", job.InFlightSlices.Single().RecordsNextLink);

            // A fresh store over the same state: the truncation must survive the reload, or the resumed slice
            // would be closed as a clean, complete one.
            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence).AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.CompletedWithGaps, job.State);
            Assert.AreEqual(1, source.SubmitCount, "Resuming must not resubmit the query.");
            Assert.AreEqual(1, source.PollCount, "Resuming continues paging; it does not re-poll the query.");
            Assert.AreEqual(2, source.RecordPagesRead);
            Assert.AreEqual(2, persistence.CommitCount);
            Assert.AreEqual(2L, job.RecordsImported, "Every record the truncated query returned is kept.");
            Assert.AreEqual(0, job.SlicesCompleted, "A truncated slice is incomplete, not completed.");
            var gap = job.Gaps.Single();
            Assert.IsTrue(gap.Incomplete);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryTruncated, gap.ErrorCode);
            Assert.AreEqual(start, gap.StartUtc);
            Assert.AreEqual(start.AddMinutes(15), gap.EndUtc);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryTruncated, job.LastErrorCode);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Importer_MixedMappingValiditySurvivesPagingRestart(bool validFirstPage)
        {
            var store = new InMemoryKeyValueStore();
            var state = new CopilotAuditBackfillStateStore(store, true);
            await state.CreateAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), "admin@contoso.com");
            var expired = false;
            var source = new CallbackSource(next =>
            {
                var firstPage = next == null;
                if (firstPage) expired = true;
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord>
                    {
                        firstPage == validFirstPage ? SyntheticRecord(SyntheticId(1)) : null
                    },
                    NextLink = firstPage ? "page-2" : null
                });
            });
            var persistence = new CountingPersistence();
            var job = await RegressionImporter(state, source, persistence,
                () => expired ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero).AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State,
                "An incomplete page sequence cannot establish that the entire query is unrecognised.");
            Assert.AreEqual("page-2", job.InFlightSlices.Single().RecordsNextLink);

            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence)
                .AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.CompletedWithGaps, job.State);
            Assert.AreEqual(2L, job.RecordsSeen);
            Assert.AreEqual(1L, job.RecordsImported);
            Assert.AreEqual(1L, job.MappingFailureCounts.Values.Sum());
            Assert.AreEqual(1, job.Gaps.Count);
            Assert.AreEqual(1, source.Submits);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Importer_AllUnrecognisedPagedQueryFailsOnlyAfterLastPage(bool emptyLastPage)
        {
            var store = new InMemoryKeyValueStore();
            var state = new CopilotAuditBackfillStateStore(store, true);
            await state.CreateAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), "admin@contoso.com");
            var expired = false;
            var source = new CallbackSource(next =>
            {
                var firstPage = next == null;
                if (firstPage) expired = true;
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = !firstPage && emptyLastPage
                        ? new List<CopilotAuditSearchRecord>()
                        : new List<CopilotAuditSearchRecord> { null },
                    NextLink = firstPage ? "page-2" : null
                });
            });
            var persistence = new CountingPersistence();
            var job = await RegressionImporter(state, source, persistence,
                () => expired ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero).AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual("page-2", job.InFlightSlices.Single().RecordsNextLink);

            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence)
                .AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, job.LastErrorCode);
            Assert.AreEqual(emptyLastPage ? 1L : 2L, job.MappingFailureCounts.Values.Sum());
            Assert.AreEqual(0, persistence.CommitCount);
        }

        [TestMethod]
        public async Task Importer_ReplacementQueryDoesNotInheritMappingSuccess()
        {
            var store = new InMemoryKeyValueStore();
            var state = new CopilotAuditBackfillStateStore(store, true);
            await state.CreateAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), "admin@contoso.com");
            var attempt = 0;
            var expired = false;
            var source = new CallbackSource(next =>
            {
                if (attempt == 1 && next != null)
                    throw new GraphHttpException(HttpStatusCode.InternalServerError, "https://graph.microsoft.com",
                        "{\"error\":{\"code\":\"InternalServerError\"}}", null);
                expired = attempt == 1;
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord>
                    {
                        attempt == 1 ? SyntheticRecord(SyntheticId(1)) : null
                    },
                    NextLink = attempt == 1 ? "page-2" : null
                });
            })
            {
                OnSubmit = () => { attempt++; return Task.CompletedTask; }
            };
            var persistence = new CountingPersistence();
            var job = await RegressionImporter(state, source, persistence,
                () => expired ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero).AdvanceLatestAsync();
            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(1L, job.RecordsImported);

            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence)
                .AdvanceLatestAsync();
            Assert.AreEqual(1, job.PendingSlices.Count);
            Assert.AreEqual(0, job.InFlightSlices.Count);

            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence)
                .AdvanceLatestAsync();

            Assert.AreEqual(2, source.Submits);
            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State,
                "A recognised record from an earlier query attempt cannot validate its replacement.");
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, job.LastErrorCode);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Importer_LegacyContinuationReplaysToRecoverQueryMappingEvidence(bool recognisedPrefix)
        {
            var store = new InMemoryKeyValueStore();
            var state = new CopilotAuditBackfillStateStore(store, true);
            await state.CreateAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), "admin@contoso.com");
            var job = await state.GetLatestAsync();
            var slice = job.PendingSlices.Single();
            job.PendingSlices.Clear();
            slice.QueryId = "legacy-query";
            slice.AttemptCount = 1;
            slice.ImportingRecords = true;
            slice.RecordsNextLink = "page-2";
            job.InFlightSlices.Add(slice);
            job.State = CopilotAuditBackfillStates.Running;
            job.RecordsImported = recognisedPrefix ? 1 : 0;
            await state.SaveAsync(job);
            var links = new List<string>();
            var source = new CallbackSource(next =>
            {
                links.Add(next);
                return Task.FromResult(new CopilotAuditSearchRecordPage
                {
                    Records = new List<CopilotAuditSearchRecord>
                    {
                        next == null && recognisedPrefix ? SyntheticRecord(SyntheticId(1)) : null
                    },
                    NextLink = next == null ? "page-2" : null
                });
            });
            var existing = new FakeExistingEventFilter();
            existing.Existing.Add(new Guid(SyntheticId(1)));
            var persistence = new CountingPersistence();

            job = await RegressionImporter(new CopilotAuditBackfillStateStore(store, true), source, persistence,
                existing: existing).AdvanceLatestAsync();

            Assert.AreEqual(recognisedPrefix ? CopilotAuditBackfillStates.CompletedWithGaps : CopilotAuditBackfillStates.Failed, job.State);
            CollectionAssert.AreEqual(new string[] { null, "page-2" }, links,
                "An old cursor without recognition evidence must replay the existing query, not guess.");
            Assert.AreEqual(0, source.Submits, "Recovering evidence must not consume another query submission.");
            Assert.AreEqual(0, persistence.CommitCount, "Previously committed records remain de-duplicated.");
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
            CollectionAssert.AreEqual(new[] { "2026-10-01" }, job.CompletedDays);
            var status = await new BackfillStatusService(state,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }).GetStatusAsync();
            CollectionAssert.AreEqual(new[] { "2026-10-01" }, status.LatestJob.CompletedDays);
            CollectionAssert.AreEqual(new[] { "2026-10-01" }, status.LatestJob.IncompleteDays,
                "Per-record mapping gaps do not mean the query's day window was left unread.");
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

        /// <summary>
        /// A double click, two tabs or two administrators: every start reads the latest job before any of them writes,
        /// so every one passes the "no active job" check. Exactly one may create a job; the rest are refused as
        /// <c>jobActive</c>, and no job row is left behind for them. In the same second they pick the same id.
        /// </summary>
        [DataTestMethod]
        [DataRow(true, DisplayName = "Same second, same id")]
        [DataRow(false, DisplayName = "Different seconds, different ids")]
        public async Task Start_ConcurrentStartsCreateExactlyOneJob(bool sameSecond)
        {
            const int starts = 4;
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var tick = 0;
            var store = new LatestJobBarrierStore(starts) { Holding = true };
            var state = new CopilotAuditBackfillStateStore(store, true,
                () => sameSecond ? now : now.AddSeconds(System.Threading.Interlocked.Increment(ref tick)));
            var service = new BackfillStatusService(state,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, () => now);

            var outcomes = await Task.WhenAll(Enumerable.Range(0, starts).Select(i => Task.Run(async () =>
            {
                try { return (await service.StartAsync(null, "admin" + i + "@contoso.com")).RequestedBy; }
                catch (BackfillRequestException ex) { return ex.Status + ":" + ex.Code; }
            })));
            store.Holding = false;

            var winner = outcomes.Single(o => o.EndsWith("@contoso.com", StringComparison.Ordinal));
            Assert.AreEqual(starts - 1, outcomes.Count(o => o == HttpStatusCode.Conflict + ":jobActive"), string.Join(", ", outcomes));
            var latest = await state.GetLatestAsync();
            Assert.AreEqual(winner, latest.RequestedBy, "The latest job is the one the winner was told it started.");
            Assert.IsTrue(latest.CopilotImportEnabled);
            var rows = new List<string>();
            foreach (var key in store.JobRowsWritten.Distinct())
            {
                if (await store.Inner.GetStringAsync(key) != null) rows.Add(key);
            }
            CollectionAssert.AreEqual(new[] { "Job:" + latest.Id }, rows, "A start that lost the race leaves no job row behind.");
        }

        [TestMethod]
        public async Task Start_AfterTheLatestJobFinishesStartsANewOne()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var clock = now;
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true, () => clock);
            var service = new BackfillStatusService(state,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, () => clock);
            var first = await service.StartAsync(null, "admin@contoso.com");

            var refused = await Assert.ThrowsExceptionAsync<BackfillRequestException>(() => service.StartAsync(null, "admin@contoso.com"));
            Assert.AreEqual("jobActive", refused.Code);

            var finished = await state.GetAsync(first.Id);
            finished.State = CopilotAuditBackfillStates.Completed;
            await state.SaveAsync(finished);
            clock = now.AddHours(1);
            var second = await service.StartAsync(null, "admin@contoso.com");

            Assert.IsTrue(second.Id > first.Id);
            Assert.AreEqual(second.Id, (await state.GetLatestAsync()).Id);
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, (await state.GetAsync(first.Id)).State, "The finished job is kept, not overwritten.");
        }

        [TestMethod]
        public async Task Start_RecordsTheRequestersSignInNameNotTheirDisplayName()
        {
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), true);
            var service = new BackfillStatusService(state, new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } });
            var identity = new ClaimsIdentity("synthetic-test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-portal-user"));
            identity.AddClaim(new Claim(ClaimTypes.Name, "Contoso Administrator"));
            identity.AddClaim(new Claim("upn", "admin@contoso.com"));
            identity.AddClaim(new Claim("roles", PortalRoles.Administration));
            using (var host = new PortalTestHost(new[] { typeof(BackfillAPIController) }, new ClaimsPrincipal(identity),
                PortalAccessPolicy.Enforcing, _ => new BackfillAPIController(() => service)))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/CopilotAuditBackfill/start")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            })
            {
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
                }
            }

            Assert.AreEqual("admin@contoso.com", (await state.GetLatestAsync()).RequestedBy);
        }

        /// <summary>
        /// An in-memory store whose latest-job pointer write can meet what the runtime state table's can: the write lands
        /// but its reply is lost, and the SDK's If-Match retry then fails on the ETag its own first attempt changed, so the
        /// caller is told "not written"; or another start lands first. Optionally the next read of the pointer fails.
        /// </summary>
        private sealed class LatestJobReplyStore : IConditionalKeyValueStore
        {
            public InMemoryKeyValueStore Inner { get; } = new InMemoryKeyValueStore();
            public bool LoseTheReply { get; set; }
            public string AnotherStartLandsFirst { get; set; }
            public bool PointerUnreadableAfterWrite { get; set; }
            /// <summary>The job row's insert lands but its reply is lost, so the SDK's retry is refused with 409.</summary>
            public bool LoseTheJobInsertReply { get; set; }
            /// <summary>Another start inserts its own row under the same job id first.</summary>
            public string AnotherStartInsertsTheSameIdFirst { get; set; }
            public List<string> JobRowsWritten { get; } = new List<string>();
            private bool _failNextPointerRead;
            public string Description => Inner.Description;

            private void Record(string key)
            {
                if (key.StartsWith("Job:", StringComparison.Ordinal) && !key.EndsWith(":Cancel", StringComparison.Ordinal)
                    && !JobRowsWritten.Contains(key)) JobRowsWritten.Add(key);
            }

            public Task<VersionedValue> GetVersionedAsync(string key, System.Threading.CancellationToken cancellationToken = default)
            {
                if (key == "LatestJob" && _failNextPointerRead)
                {
                    _failNextPointerRead = false;
                    throw new Azure.RequestFailedException(503, "Simulated: the state table could not be reached.");
                }
                return Inner.GetVersionedAsync(key, cancellationToken);
            }

            public async Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, System.Threading.CancellationToken cancellationToken = default)
            {
                Record(key);
                if (key != "LatestJob" && expectedVersionToken == null && !key.EndsWith(":Cancel", StringComparison.Ordinal))
                {
                    if (AnotherStartInsertsTheSameIdFirst != null)
                        await Inner.SetStringAsync(key, AnotherStartInsertsTheSameIdFirst, null, cancellationToken);
                    var inserted = await Inner.TrySetStringAsync(key, value, null, cancellationToken);
                    return inserted && !LoseTheJobInsertReply;
                }
                if (key != "LatestJob") return await Inner.TrySetStringAsync(key, value, expectedVersionToken, cancellationToken);
                if (AnotherStartLandsFirst != null) await Inner.SetStringAsync(key, AnotherStartLandsFirst, null, cancellationToken);
                var landed = await Inner.TrySetStringAsync(key, value, expectedVersionToken, cancellationToken);
                _failNextPointerRead = PointerUnreadableAfterWrite;
                return landed && !LoseTheReply;
            }

            public Task<string> GetStringAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, System.Threading.CancellationToken cancellationToken = default)
            {
                Record(key);
                return Inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }
            public Task<bool> DeleteAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);
            public Task<bool> TryDeleteAsync(string key, string expectedVersionToken, System.Threading.CancellationToken cancellationToken = default)
                => Inner.TryDeleteAsync(key, expectedVersionToken, cancellationToken);
            public Task<bool> ExistsAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.ExistsAsync(key, cancellationToken);
        }

        [TestMethod]
        public async Task Start_WhoseLatestPointerReplyIsLost_IsStillTheStartedJob()
        {
            // Release critique of #909 (#918): "not written" from the pointer's If-Match write was taken as a lost race,
            // and the job row was deleted - though the pointer could already name it.
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new LatestJobReplyStore { LoseTheReply = true };
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);

            var job = await state.TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin@contoso.com", copilotImportEnabled: true);

            Assert.IsNotNull(job, "The start happened; the administrator must not be told it was refused.");
            var latest = await state.GetLatestAsync();
            Assert.IsNotNull(latest, "The row the pointer names must still exist.");
            Assert.AreEqual(job.Id, latest.Id);
        }

        [TestMethod]
        public async Task Start_ThatLosesTheRace_IsRefusedAndRemovesItsRow()
        {
            // The other direction: a start that genuinely lost is still refused and cleans up, and the winner is untouched.
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new LatestJobReplyStore();
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);
            var winner = await state.TryCreateWhenIdleAsync(now.AddDays(-2), now, "first@contoso.com", copilotImportEnabled: true);
            winner.State = CopilotAuditBackfillStates.Completed;
            await state.SaveAsync(winner);
            var winnerId = (winner.Id + 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
            store.AnotherStartLandsFirst = winnerId;

            var lost = await state.TryCreateWhenIdleAsync(now.AddDays(-2), now.AddSeconds(1), "second@contoso.com", copilotImportEnabled: true);

            Assert.IsNull(lost, "A start that lost the race is refused.");
            Assert.AreEqual(winnerId, await store.Inner.GetStringAsync("LatestJob"), "The winner's pointer is left alone.");
            Assert.AreEqual(2, store.JobRowsWritten.Count, "The fixture must have written the finished job's row and the losing start's.");
            var surviving = new List<string>();
            foreach (var key in store.JobRowsWritten)
                if (await store.Inner.GetStringAsync(key) != null) surviving.Add(key);
            CollectionAssert.AreEqual(new[] { "Job:" + winner.Id }, surviving,
                "The losing start's row is deleted; the finished job's is kept.");
        }

        [TestMethod]
        public async Task Start_WhosePointerCannotBeReReadAfterALostReply_IsUnknown_AndLeavesTheRowInPlace()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new LatestJobReplyStore { LoseTheReply = true, PointerUnreadableAfterWrite = true };
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);

            await Assert.ThrowsExceptionAsync<CopilotAuditBackfillStateUnavailableException>(
                () => state.TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin@contoso.com", copilotImportEnabled: true),
                "Unknown is reported as 'state unavailable' - neither as started nor as 'a job is active'.");
            var latest = await state.GetLatestAsync();
            Assert.IsNotNull(latest, "Unknown must not delete a row the pointer may name; it expires if nothing does.");
        }

        [TestMethod]
        public async Task Start_WhoseJobRowInsertReplyIsLost_IsStillTheStartedJob()
        {
            // Release critique of #909 (#918), round 2: the job row's insert-if-absent has the same hazard as the pointer
            // write. The insert lands, its reply is lost, the SDK's retry is refused with 409 - and the start was told
            // "a job is active" while none was, leaving a row with no time-to-live behind.
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var store = new LatestJobReplyStore { LoseTheJobInsertReply = true };
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);

            var job = await state.TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin@contoso.com", copilotImportEnabled: true);

            Assert.IsNotNull(job, "The insert happened; the administrator must not be told a job is already active.");
            var latest = await state.GetLatestAsync();
            Assert.IsNotNull(latest, "The started job becomes the latest job.");
            Assert.AreEqual(job.Id, latest.Id);
            Assert.AreEqual("admin@contoso.com", latest.RequestedBy);
        }

        [TestMethod]
        public async Task Start_WhoseJobIdIsTakenByAnotherStart_IsRefused_AndLeavesTheOtherRowAlone()
        {
            // The other direction: a row under the same id that this call did NOT write is another start's. Refused, and
            // neither that row nor the pointer is touched.
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            const string otherStartsRow = "{\"Id\":0,\"RequestedBy\":\"other@contoso.com\"}";
            var store = new LatestJobReplyStore { AnotherStartInsertsTheSameIdFirst = otherStartsRow };
            var state = new CopilotAuditBackfillStateStore(store, true, () => now);

            var job = await state.TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin@contoso.com", copilotImportEnabled: true);

            Assert.IsNull(job, "Another start owns that id.");
            Assert.AreEqual(1, store.JobRowsWritten.Count, "The fixture must have attempted exactly one job row.");
            Assert.AreEqual(otherStartsRow, await store.Inner.GetStringAsync(store.JobRowsWritten[0]),
                "The other start's row is neither overwritten nor deleted.");
            Assert.IsNull(await store.Inner.GetStringAsync("LatestJob"), "A refused start never moves the pointer.");
        }

        /// <summary>
        /// An in-memory store that holds every read of the latest-job pointer until all the starts have made it, so
        /// they all race from the same version of it.
        /// </summary>
        private sealed class LatestJobBarrierStore : IConditionalKeyValueStore
        {
            private readonly System.Threading.Barrier _barrier;
            public LatestJobBarrierStore(int participants) { _barrier = new System.Threading.Barrier(participants); }
            public InMemoryKeyValueStore Inner { get; } = new InMemoryKeyValueStore();
            public bool Holding { get; set; }
            public System.Collections.Concurrent.ConcurrentQueue<string> JobRowsWritten { get; } = new System.Collections.Concurrent.ConcurrentQueue<string>();
            public string Description => Inner.Description;

            private void Gate(string key)
            {
                if (Holding && key == "LatestJob")
                {
                    if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Not every start read the latest job.");
                    // Every start has now made its first read. Later reads (a start re-checking the pointer after
                    // its write was refused) must not wait for a second round of starts that will never come.
                    Holding = false;
                }
            }

            private void Record(string key)
            {
                if (key.StartsWith("Job:", StringComparison.Ordinal) && !key.EndsWith(":Cancel", StringComparison.Ordinal)) JobRowsWritten.Enqueue(key);
            }

            public Task<string> GetStringAsync(string key, System.Threading.CancellationToken cancellationToken = default)
            {
                Gate(key);
                return Inner.GetStringAsync(key, cancellationToken);
            }

            public Task<VersionedValue> GetVersionedAsync(string key, System.Threading.CancellationToken cancellationToken = default)
            {
                Gate(key);
                return Inner.GetVersionedAsync(key, cancellationToken);
            }

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, System.Threading.CancellationToken cancellationToken = default)
            {
                Record(key);
                return Inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }

            public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, System.Threading.CancellationToken cancellationToken = default)
            {
                Record(key);
                return Inner.TrySetStringAsync(key, value, expectedVersionToken, cancellationToken);
            }

            public Task<bool> DeleteAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);
            public Task<bool> TryDeleteAsync(string key, string expectedVersionToken, System.Threading.CancellationToken cancellationToken = default)
                => Inner.TryDeleteAsync(key, expectedVersionToken, cancellationToken);
            public Task<bool> ExistsAsync(string key, System.Threading.CancellationToken cancellationToken = default) => Inner.ExistsAsync(key, cancellationToken);
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
