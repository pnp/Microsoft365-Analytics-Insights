using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.State;
using DataUtils;
using DataUtils.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotAuditBackfillTests
    {
        [TestMethod]
        public void Slicer_UsesNewestDayFirstAndClampsToRetention()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var start = CopilotAuditBackfillSlicer.ClampStart(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), now);
            Assert.AreEqual(now.AddDays(-180), start);

            var slices = CopilotAuditBackfillSlicer.BuildDaySlices(now.AddDays(-2), now);
            Assert.AreEqual(3, slices.Count);
            Assert.AreEqual(now, slices[0].EndUtc);
            Assert.AreEqual(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), slices[0].StartUtc);
            Assert.AreEqual(4, CopilotAuditBackfillSlicer.SplitForTruncation(new CopilotAuditBackfillSlice
            {
                StartUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            }).Count);
        }

        [TestMethod]
        public async Task Importer_MissingPermissionFailsOnceBeforeSubmittingQuery()
        {
            var source = new FakeAuditSearchSource { Access = AppTokenPermissionAccess.NotGranted };
            var state = await NewStateWithJobAsync();
            var importer = NewImporter(state, source, new CountingPersistence());

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.MissingPermission, job.LastErrorCode);
            Assert.AreEqual(0, source.SubmitCount);
        }

        [TestMethod]
        public async Task Importer_RefusesWhenCopilotAuditImportIsOff()
        {
            var state = await NewStateWithJobAsync();
            var settings = new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = false } };
            var importer = new CopilotAuditBackfillImporter(state, new FakeAuditSearchSource(), new CountingPersistence(), settings, NullLogger.Instance);

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.CopilotImportOff, job.LastErrorCode);
        }

        [TestMethod]
        public async Task Importer_SubmitsUpToFourQueriesBeforePolling()
        {
            var state = await NewStateWithJobAsync(days: 10);
            var source = new FakeAuditSearchSource();
            var importer = NewImporter(state, source, new CountingPersistence());

            await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillImporter.MaxInFlightQueries, source.SubmitCountBeforeFirstPoll);
        }

        [TestMethod]
        public async Task Importer_TruncatedDayIsSplitIntoHoursAndNotImported()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Queries = new Queue<CopilotAuditSearchQuery>(new[] { new CopilotAuditSearchQuery { Status = "succeeded", IsTruncated = true } }),
            };
            var persistence = new CountingPersistence();
            var importer = NewImporter(state, source, persistence);

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(1, job.SlicesSplit);
            Assert.AreEqual(0, persistence.CommitCount);
        }

        [TestMethod]
        public async Task Importer_RetriesFailedSliceThenCompletesWithGaps()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "failed", Error = "syntheticFailure" } };
            var importer = NewImporter(state, source, new CountingPersistence());

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.CompletedWithGaps, job.State);
            Assert.AreEqual(CopilotAuditBackfillImporter.MaxSliceAttempts, source.SubmitCount);
            Assert.AreEqual(1, job.Gaps.Count);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryFailed, job.Gaps[0].ErrorCode);
        }

        [TestMethod]
        public async Task Importer_TruncatedHourIsRecordedIncompleteAndContinues()
        {
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), false, () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
            var job = await state.CreateAsync(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc), "admin@contoso.com");
            job.PendingSlices[0].SplitLevel = 2;
            await state.SaveAsync(job);
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "succeeded", IsTruncated = true } };
            var importer = NewImporter(state, source, new CountingPersistence());

            job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.CompletedWithGaps, job.State);
            Assert.AreEqual(1, job.Gaps.Count);
            Assert.IsTrue(job.Gaps[0].Incomplete);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryTruncated, job.Gaps[0].ErrorCode);
        }

        [TestMethod]
        public async Task Importer_ImportsPagedRecordsInBatches()
        {
            var state = await NewStateWithJobAsync();
            var records = Enumerable.Range(1, 12001)
                .Select(i => SyntheticRecord($"00000000-0000-0000-0000-{i:000000000000}"))
                .ToList();
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[] { new CopilotAuditSearchRecordPage { Records = records } })
            };
            var persistence = new CountingPersistence();
            var importer = NewImporter(state, source, persistence);

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(12001, job.RecordsSeen);
            Assert.AreEqual(12001, job.RecordsImported);
            Assert.AreEqual(3, persistence.CommitCount);
            CollectionAssert.AreEqual(new[] { 5000, 5000, 2001 }, persistence.BatchSizes);
        }

        [TestMethod]
        public async Task Importer_CommitsMappedCopilotInteractionsThroughNormalPersistencePath()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[]
                {
                    new CopilotAuditSearchRecordPage
                    {
                        Records = new List<CopilotAuditSearchRecord>
                        {
                            SyntheticRecord(
                                "00000000-0000-0000-0000-000000000014",
                                "\"AppHost\":\"Word\",\"Contexts\":[{\"Id\":\"https://contoso.sharepoint.com/sites/example/Shared Documents/Plan.docx\",\"Type\":\"File\"}],\"AccessedResources\":[{\"Id\":\"resource-1\",\"Name\":\"Plan.docx\",\"Type\":\"File\"}],\"Messages\":[{\"Id\":\"message-1\",\"isPrompt\":true,\"JailbreakDetected\":false}]",
                                "\"AgentName\":\"Copilot Cowork\",\"AppIdentity\":\"Copilot.M365Copilot.CoworkChat\",")
                        }
                    }
                })
            };
            var persistence = new CountingPersistence();
            var importer = NewImporter(state, source, persistence);

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(1, persistence.CommitCount);
            var content = persistence.LastActivities.Single() as CopilotAuditLogContent;
            Assert.IsNotNull(content);
            Assert.AreEqual("CopilotInteraction", content.Operation);
            Assert.AreEqual("Copilot.M365Copilot.CoworkChat", content.AgentId);
            Assert.AreEqual("Word", content.CopilotEventData.AppHost);
            Assert.AreEqual(1, content.CopilotEventData.Contexts.Count);
            Assert.AreEqual(1, content.CopilotEventData.AccessedResources.Count);
            Assert.AreEqual(1, content.ParsedAuditEvent.Messages.Count);
        }


        [TestMethod]
        public void Query_FromJson_UsesDocumentedLimitExceededFlag()
        {
            var q = CopilotAuditSearchQuery.FromJson("{\"id\":\"q\",\"status\":\"succeeded\",\"isRecordCountLimitExceeded\":true,\"approximateReturnedRecordCount\":10,\"recordCountLimit\":1000000}");
            Assert.IsTrue(q.IsTruncated);

            q = CopilotAuditSearchQuery.FromJson("{\"id\":\"q\",\"status\":\"succeeded\",\"isRecordCountLimitExceeded\":false,\"approximateReturnedRecordCount\":2000000,\"recordCountLimit\":1000000}");
            Assert.IsFalse(q.IsTruncated, "The documented Boolean is authoritative when present.");
        }

        [TestMethod]
        public void Query_FromJson_FallsBackToApproximateCountOnlyWhenFlagIsAbsent()
        {
            var q = CopilotAuditSearchQuery.FromJson("{\"id\":\"q\",\"status\":\"succeeded\",\"approximateReturnedRecordCount\":1000000,\"recordCountLimit\":1000000}");
            Assert.IsTrue(q.IsTruncated);

            q = CopilotAuditSearchQuery.FromJson("{\"id\":\"q\",\"status\":\"succeeded\",\"approximateReturnedRecordCount\":999999,\"recordCountLimit\":1000000}");
            Assert.IsFalse(q.IsTruncated);
        }

        [TestMethod]
        public async Task GraphSource_SubmitQuery_Translates429ToTypedThrottleWithRetryAfter()
        {
            var client = new ManualGraphCallClient(new StaticResponseHandler(_ =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(123));
                return response;
            }), NullLogger.Instance);
            var source = new GraphCopilotAuditSearchSource(client, null, NullLogger.Instance);

            var ex = await Assert.ThrowsExceptionAsync<CopilotAuditSearchThrottledException>(() =>
                source.SubmitQueryAsync(new CopilotAuditBackfillSlice
                {
                    StartUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                }));

            Assert.AreEqual(123, ex.RetryAfterSeconds);
        }


        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task Importer_WithRealSqlPersistence_SkipsAlreadyImportedOldAuditEventAndImportsNewOne()
        {
            var existingId = new Guid("00000000-0000-0000-0000-000000000041");
            var newId = new Guid("00000000-0000-0000-0000-000000000042");
            var existingOperation = "Already imported CopilotInteraction " + Guid.NewGuid().ToString("N");
            var config = new TestsAppConfig
            {
                ImportJobSettings = new ImportTaskSettings { Copilot = true },
                ResolveCopilotResourceMetadata = false,
                DaysBeforeNowToDownload = 7,
            };

            using (var db = TestDb())
            {
                CleanupEvents(db, existingId, newId);
                db.Database.ExecuteSqlCommand(@"
DECLARE @operationId int = (SELECT id FROM dbo.event_operations WHERE operation_name = @p2);
IF @operationId IS NULL
BEGIN
    INSERT INTO dbo.event_operations (operation_name) VALUES (@p2);
    SET @operationId = SCOPE_IDENTITY();
END
DECLARE @userId int = (SELECT id FROM dbo.users WHERE user_name = @p3);
IF @userId IS NULL
BEGIN
    INSERT INTO dbo.users (user_name, mail, azure_ad_id, postalcode) VALUES (@p3, '', @p4, '');
    SET @userId = SCOPE_IDENTITY();
END
INSERT INTO dbo.audit_events (id, time_stamp, operation_id, user_id)
VALUES (@p0, @p1, @operationId, @userId);",
                    existingId,
                    new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
                    existingOperation,
                    "jane.doe@contoso.com",
                    "00000000-0000-0000-0000-000000000041");
            }

            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[]
                {
                    new CopilotAuditSearchRecordPage
                    {
                        Records = new List<CopilotAuditSearchRecord>
                        {
                            SyntheticRecord(existingId.ToString()),
                            SyntheticRecord(newId.ToString()),
                        }
                    }
                })
            };
            var persistence = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance, config);
            var importer = new CopilotAuditBackfillImporter(state, source, persistence, config, NullLogger.Instance,
                () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), _ => Task.CompletedTask,
                new SqlCopilotAuditBackfillExistingEventFilter(TestDb));

            var job = await importer.AdvanceLatestAsync();

            using (var db = TestDb())
            {
                var existing = db.AuditEventsCommon.Single(e => e.Id == existingId);
                var inserted = db.AuditEventsCommon.SingleOrDefault(e => e.Id == newId);
                Assert.IsNotNull(inserted, "The genuinely new backfill record should be committed through the real SQL path.");
                Assert.AreEqual(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), existing.TimeStamp, "The existing old audit event must be left untouched.");
                Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
                Assert.AreEqual(2, job.RecordsSeen);
                Assert.AreEqual(1, job.RecordsImported);
                Assert.AreEqual(1, job.RecordsAlreadyPresent);
                CleanupEvents(db, existingId, newId);
            }
        }

        [TestMethod]
        public async Task Importer_DropsRecordsAlreadyPresentBeforeCommitAndCountsThem()
        {
            var state = await NewStateWithJobAsync();
            var existingId = new Guid("00000000-0000-0000-0000-000000000021");
            var filter = new FakeExistingEventFilter();
            filter.Existing.Add(existingId);
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[]
                {
                    new CopilotAuditSearchRecordPage
                    {
                        Records = new List<CopilotAuditSearchRecord>
                        {
                            SyntheticRecord(existingId.ToString()),
                            SyntheticRecord("00000000-0000-0000-0000-000000000022"),
                        }
                    }
                })
            };
            var persistence = new CountingPersistence();
            var importer = new CopilotAuditBackfillImporter(state, source, persistence,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), _ => Task.CompletedTask,
                filter);

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(2, job.RecordsSeen);
            Assert.AreEqual(1, job.RecordsImported);
            Assert.AreEqual(1, job.RecordsAlreadyPresent);
            Assert.AreEqual(1, persistence.CommitCount);
            Assert.AreEqual(new Guid("00000000-0000-0000-0000-000000000022"), persistence.LastActivities.Single().Id);
        }

        [TestMethod]
        public async Task Importer_ThrottledSubmissionPausesWithoutBurningAttempts()
        {
            var state = await NewStateWithJobAsync(days: 3);
            var source = new FakeAuditSearchSource { SubmitFailure = new CopilotAuditSearchThrottledException(3600, null) };
            var importer = NewImporter(state, source, new CountingPersistence());

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(1, source.SubmitCount);
            Assert.AreEqual(0, job.PendingSlices[0].AttemptCount);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryThrottled, job.LastErrorCode);
            Assert.IsTrue(job.SubmissionsPausedUntilUtc > new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
        }

        [TestMethod]
        public async Task Importer_BadRequestSubmissionFailsJobAsQueryRejected()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource { SubmitFailure = new GraphHttpException(HttpStatusCode.BadRequest, "https://graph.example", "{}", new Exception("bad"), "POST", null) };
            var importer = NewImporter(state, source, new CountingPersistence());

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryRejected, job.LastErrorCode);
            Assert.AreEqual(1, source.SubmitCount);
        }

        [TestMethod]
        public async Task Importer_SubmitOutagePausesAndDoesNotCompleteWithPendingSlices()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var state = await NewStateWithJobAsync(days: 3);
            var source = new FakeAuditSearchSource { SubmitFailure = new HttpRequestException("503") };
            var importer = new CopilotAuditBackfillImporter(state, source, new CountingPersistence(),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => now, _ => Task.CompletedTask, new FakeExistingEventFilter());

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual(1, source.SubmitCount);
            Assert.AreEqual(3, job.PendingSlices.Count);
            Assert.AreEqual(1, job.PendingSlices.Last().AttemptCount);
            Assert.AreEqual(now.AddMinutes(5), job.SubmissionsPausedUntilUtc);
        }

        [TestMethod]
        public async Task Importer_SubmissionBudgetPausesAtOneHundredPerRollingDay()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), false, () => now);
            var job = await state.CreateAsync(now.AddDays(-2), now, "admin@contoso.com");
            job.SubmissionTimestampsUtc = Enumerable.Range(0, CopilotAuditBackfillImporter.DailySubmissionBudget)
                .Select(i => now.AddHours(-23).AddMinutes(i))
                .ToList();
            await state.SaveAsync(job);
            var source = new FakeAuditSearchSource();
            var importer = new CopilotAuditBackfillImporter(state, source, new CountingPersistence(),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => now, _ => Task.CompletedTask, new FakeExistingEventFilter());

            job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(0, source.SubmitCount);
            Assert.IsTrue(job.SubmissionsPausedUntilUtc > now);
        }


        [TestMethod]
        public async Task Importer_RetriesUnknownFutureQueryStatusAfterBoundedAge()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), false, () => now);
            var job = await state.CreateAsync(now.AddDays(-1), now, "admin@contoso.com");
            job.State = CopilotAuditBackfillStates.Running;
            var slice = job.PendingSlices[0];
            job.PendingSlices.Clear();
            slice.QueryId = "query-1";
            slice.SubmittedUtc = now.Subtract(CopilotAuditBackfillImporter.MaxUnknownStatusAge).AddMinutes(-1);
            job.InFlightSlices.Add(slice);
            await state.SaveAsync(job);
            var source = new FakeAuditSearchSource { Query = new CopilotAuditSearchQuery { Status = "unknownFutureValue" } };
            var importer = new CopilotAuditBackfillImporter(state, source, new CountingPersistence(),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => now, _ => Task.CompletedTask, new FakeExistingEventFilter());

            job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(0, job.InFlightSlices.Count);
            Assert.IsTrue(job.PendingSlices.Count == 1 || job.Gaps.Count == 1, "The unknown status must be retried or gapped, not left in flight forever.");
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.QueryFailed, job.LastErrorCode);
        }

        [TestMethod]
        public async Task Importer_PausesRecordPagingAtCycleBudgetAndResumesNextLink()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[]
                {
                    new CopilotAuditSearchRecordPage { Records = new List<CopilotAuditSearchRecord> { SyntheticRecord("00000000-0000-0000-0000-000000000031") }, NextLink = "next-page" },
                    new CopilotAuditSearchRecordPage { Records = new List<CopilotAuditSearchRecord> { SyntheticRecord("00000000-0000-0000-0000-000000000032") } },
                })
            };
            var ticks = 0;
            var importer = new CopilotAuditBackfillImporter(state, source, new CountingPersistence(),
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), _ => Task.CompletedTask,
                new FakeExistingEventFilter(), () => ++ticks >= 2 ? CopilotAuditBackfillImporter.MaxCycleBudget : TimeSpan.Zero);

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Running, job.State);
            Assert.AreEqual("next-page", job.InFlightSlices.Single().RecordsNextLink);
            Assert.IsTrue(job.InFlightSlices.Single().ImportingRecords);
        }

        [TestMethod]
        public async Task Importer_FailsWhenSucceededQueryReturnsOnlyUnrecognisedRecords()
        {
            var state = await NewStateWithJobAsync();
            var source = new FakeAuditSearchSource
            {
                Query = new CopilotAuditSearchQuery { Status = "succeeded" },
                Pages = new Queue<CopilotAuditSearchRecordPage>(new[]
                {
                    new CopilotAuditSearchRecordPage { Records = new List<CopilotAuditSearchRecord> { new CopilotAuditSearchRecord { Id = "x", AuditData = JObject.Parse("{\"Workload\":\"SharePoint\",\"RecordType\":4}") } } }
                })
            };
            var importer = NewImporter(state, source, new CountingPersistence());

            var job = await importer.AdvanceLatestAsync();

            Assert.AreEqual(CopilotAuditBackfillStates.Failed, job.State);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, job.LastErrorCode);
            Assert.AreEqual(1, job.MappingFailureCounts["notCopilotInteraction"]);
        }

        [TestMethod]
        public async Task SafeRunner_SwallowsBackfillFailureSoLiveActivityImportIsNotMislabelled()
        {
            var called = false;

            await CopilotAuditBackfillSafeRunner.AdvanceSafely(() =>
            {
                called = true;
                throw new CopilotAuditBackfillStateUnavailableException("state unavailable", new InvalidOperationException());
            }, AnalyticsLogger.ConsoleOnlyTracer());

            Assert.IsTrue(called);
        }

        [TestMethod]
        public void HealthSemantics_OptionalBackfillDoesNotTurnRunningOrFailedJobsUnhealthy()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(HealthStatus.Healthy, CopilotAuditBackfillImporter.ResolveHealth(new CopilotAuditBackfillJob { State = CopilotAuditBackfillStates.Running }).Status);
            Assert.AreEqual(HealthStatus.Healthy, CopilotAuditBackfillImporter.ResolveHealth(new CopilotAuditBackfillJob { State = CopilotAuditBackfillStates.Completed }).Status);
            Assert.AreEqual(HealthStatus.Healthy, CopilotAuditBackfillImporter.ResolveHealth(new CopilotAuditBackfillJob { State = CopilotAuditBackfillStates.Cancelled }).Status);
            Assert.AreEqual(HealthStatus.Degraded, CopilotAuditBackfillImporter.ResolveHealth(new CopilotAuditBackfillJob { State = CopilotAuditBackfillStates.CompletedWithGaps, CompletedUtc = now.AddDays(-1) }, now).Status);
            Assert.AreEqual(HealthStatus.Degraded, CopilotAuditBackfillImporter.ResolveHealth(new CopilotAuditBackfillJob { State = CopilotAuditBackfillStates.Failed, LastErrorCode = CopilotAuditBackfillErrorCodes.MissingPermission, CompletedUtc = now.AddDays(-1) }, now).Status);
            var aged = CopilotAuditBackfillImporter.ResolveHealth(new CopilotAuditBackfillJob { State = CopilotAuditBackfillStates.Failed, LastErrorCode = CopilotAuditBackfillErrorCodes.MissingPermission, CompletedUtc = now.AddDays(-8) }, now);
            Assert.AreEqual(HealthStatus.Healthy, aged.Status);
            Assert.AreEqual("copilotAuditBackfill.lastJobOld", aged.ReasonKey);
        }

        [TestMethod]
        public void Mapper_UsesDocumentedAuditDataObjectAndRejectsOtherWorkloads()
        {
            var mapped = CopilotAuditSearchRecordMapper.Map(SyntheticRecord("00000000-0000-0000-0000-000000000003"), NullLogger.Instance);
            Assert.IsNotNull(mapped.Content);
            Assert.IsTrue(mapped.IdMatched);
            Assert.AreEqual(new Guid("00000000-0000-0000-0000-000000000003"), mapped.Content.Id);

            var rejected = CopilotAuditSearchRecordMapper.Map(new CopilotAuditSearchRecord { Id = "x", AuditData = JObject.Parse("{\"Workload\":\"SharePoint\",\"RecordType\":4}") }, NullLogger.Instance);
            Assert.IsNull(rejected.Content);
            Assert.AreEqual("notCopilotInteraction", rejected.ErrorCode);
        }


        [TestMethod]
        public void Mapper_AcceptsDynamicPropertiesAuditDataShapeAndStripsODataMetadata()
        {
            var id = "00000000-0000-0000-0000-000000000051";
            var inner = SyntheticRecord(id).AuditData as JObject;
            inner["@odata.type"] = "#microsoft.graph.security.copilotInteractionAuditRecord";
            var record = SyntheticRecord(id);
            record.AuditData = new JObject
            {
                ["@odata.type"] = "#microsoft.graph.security.auditData",
                ["dynamicProperties"] = inner,
            };

            var mapped = CopilotAuditSearchRecordMapper.Map(record, NullLogger.Instance);

            Assert.IsNotNull(mapped.Content);
            Assert.AreEqual(new Guid(id), mapped.Content.Id);
            Assert.IsFalse(mapped.Content.OriginalImportFileContents.Contains("@odata"));
        }

        [TestMethod]
        public void Mapper_CompletesMissingWorkloadFromGraphRecordWhenPayloadHasCopilotFields()
        {
            var id = "00000000-0000-0000-0000-000000000052";
            var payload = (JObject)SyntheticRecord(id).AuditData.DeepClone();
            payload.Remove("Workload");
            payload.Remove("RecordType");
            payload.Remove("Operation");
            var record = new CopilotAuditSearchRecord
            {
                Id = id,
                CreatedDateTime = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
                AuditLogRecordType = "CopilotInteraction",
                Operation = "CopilotInteraction",
                Service = "Copilot",
                UserPrincipalName = "jane.doe@contoso.com",
                AuditData = payload,
            };

            var mapped = CopilotAuditSearchRecordMapper.Map(record, NullLogger.Instance);

            Assert.IsNotNull(mapped.Content);
            Assert.AreEqual("Copilot", mapped.Content.Workload);
            Assert.AreEqual("CopilotInteraction", mapped.Content.Operation);
        }

        [TestMethod]
        public void Mapper_GraphCopilotRecordWithUnrecognisablePayloadUsesStableShapeError()
        {
            var mapped = CopilotAuditSearchRecordMapper.Map(new CopilotAuditSearchRecord
            {
                Id = "00000000-0000-0000-0000-000000000053",
                AuditLogRecordType = "CopilotInteraction",
                Operation = "CopilotInteraction",
                Service = "Copilot",
                UserPrincipalName = "jane.doe@contoso.com",
                AuditData = JObject.Parse("{\"dynamicProperties\":{\"Id\":\"00000000-0000-0000-0000-000000000053\"}}"),
            }, NullLogger.Instance);

            Assert.IsNull(mapped.Content);
            Assert.AreEqual(CopilotAuditBackfillErrorCodes.UnrecognisedAuditData, mapped.ErrorCode);
        }

        [TestMethod]
        public void Mapper_AcceptsDefensiveStringAuditDataForm()
        {
            var id = "00000000-0000-0000-0000-000000000004";
            var record = SyntheticRecord(id);
            record.AuditData = record.AuditData.ToString(Newtonsoft.Json.Formatting.None);

            var mapped = CopilotAuditSearchRecordMapper.Map(record, NullLogger.Instance);

            Assert.IsNotNull(mapped.Content);
            Assert.AreEqual(new Guid(id), mapped.Content.Id);
        }


        private static void CleanupEvents(AnalyticsEntitiesContext db, params Guid[] ids)
        {
            foreach (var id in ids)
            {
                db.Database.ExecuteSqlCommand("DELETE FROM dbo.copilot_chats WHERE event_id = @p0", id);
                db.Database.ExecuteSqlCommand("DELETE FROM dbo.event_meta_general WHERE event_id = @p0", id);
                db.Database.ExecuteSqlCommand("DELETE FROM dbo.audit_events WHERE id = @p0", id);
            }
        }

        private static AnalyticsEntitiesContext TestDb()
            => new AnalyticsEntitiesContext("name=SPOInsightsEntities", false, false);

        private static async Task<CopilotAuditBackfillStateStore> NewStateWithJobAsync(int days = 1)
        {
            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), false, () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
            await state.CreateAsync(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc).AddDays(-days), new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), "admin@contoso.com");
            return state;
        }

        private static CopilotAuditBackfillImporter NewImporter(CopilotAuditBackfillStateStore state, FakeAuditSearchSource source, CountingPersistence persistence)
            => new CopilotAuditBackfillImporter(state, source, persistence,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, NullLogger.Instance,
                () => new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc),
                _ => Task.CompletedTask, existingEvents: new FakeExistingEventFilter());

        private static CopilotAuditSearchRecord SyntheticRecord(string id, string copilotEventDataProperties = "\"ThreadId\":\"thread-1\",\"AccessedResources\":[],\"Contexts\":[],\"Messages\":[]", string topLevelProperties = "")
            => new CopilotAuditSearchRecord
            {
                Id = id,
                CreatedDateTime = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
                AuditLogRecordType = "microsoft.graph.security.auditLogRecordType",
                Operation = "CopilotInteraction",
                Service = "Copilot",
                UserPrincipalName = "jane.doe@contoso.com",
                AuditData = JObject.Parse("{\"@odata.type\":\"#microsoft.graph.security.auditData\",\"Id\":\"" + id + "\",\"RecordType\":261,\"CreationTime\":\"2026-10-01T10:00:00Z\",\"Operation\":\"CopilotInteraction\",\"Workload\":\"Copilot\",\"UserId\":\"jane.doe@contoso.com\",\"OrganizationId\":\"00000000-0000-0000-0000-000000000000\"," + topLevelProperties + "\"CopilotEventData\":{" + copilotEventDataProperties + "}}")
            };

        private sealed class FakeAuditSearchSource : ICopilotAuditSearchSource
        {
            public AppTokenPermissionAccess Access = AppTokenPermissionAccess.Granted;
            public CopilotAuditSearchQuery Query = new CopilotAuditSearchQuery { Status = "succeeded" };
            public Queue<CopilotAuditSearchQuery> Queries = new Queue<CopilotAuditSearchQuery>();
            public Queue<CopilotAuditSearchRecordPage> Pages = new Queue<CopilotAuditSearchRecordPage>();
            public Exception SubmitFailure;
            public int SubmitCount;
            public int PollCount;
            public int SubmitCountBeforeFirstPoll;

            public Task<AppTokenPermissionAccess> GetPermissionAccessAsync() => Task.FromResult(Access);

            public Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
            {
                SubmitCount++;
                if (PollCount == 0) SubmitCountBeforeFirstPoll = SubmitCount;
                if (SubmitFailure != null) throw SubmitFailure;
                return Task.FromResult(new CopilotAuditSearchQuery { Id = "query-" + SubmitCount, Status = "running" });
            }

            public Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
            {
                PollCount++;
                var query = Queries.Count > 0 ? Queries.Dequeue() : Query;
                return Task.FromResult(new CopilotAuditSearchQuery
                {
                    Id = queryId,
                    Status = query.Status,
                    Error = query.Error,
                    IsTruncated = query.IsTruncated,
                    ApproximateReturnedRecordCount = query.ApproximateReturnedRecordCount,
                    RecordCountLimit = query.RecordCountLimit,
                });
            }

            public Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null) => Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : new CopilotAuditSearchRecordPage());
        }

        private sealed class FakeExistingEventFilter : ICopilotAuditBackfillExistingEventFilter
        {
            public HashSet<Guid> Existing = new HashSet<Guid>();
            public Task<HashSet<Guid>> GetExistingIdsAsync(IEnumerable<Guid> ids)
                => Task.FromResult(new HashSet<Guid>((ids ?? Enumerable.Empty<Guid>()).Where(id => Existing.Contains(id))));
        }

        private sealed class StaticResponseHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

            public StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            {
                _response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_response(request));
        }

        private sealed class CountingPersistence : IActivityReportPersistenceManager
        {
            public int CommitCount;
            public int LastCount;
            public List<int> BatchSizes = new List<int>();
            public ActivityReportSet LastActivities;

            public Task<ImportStat> CommitAll(ActivityReportSet activities)
            {
                CommitCount++;
                LastCount = activities.Count;
                BatchSizes.Add(activities.Count);
                LastActivities = activities;
                return Task.FromResult(new ImportStat { Imported = activities.Count, Total = activities.Count });
            }
        }
    }
}
