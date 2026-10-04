using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
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
            job.PendingSlices[0].SplitLevel = 1;
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
        public void Mapper_AcceptsDefensiveStringAuditDataForm()
        {
            var id = "00000000-0000-0000-0000-000000000004";
            var record = SyntheticRecord(id);
            record.AuditData = record.AuditData.ToString(Newtonsoft.Json.Formatting.None);

            var mapped = CopilotAuditSearchRecordMapper.Map(record, NullLogger.Instance);

            Assert.IsNotNull(mapped.Content);
            Assert.AreEqual(new Guid(id), mapped.Content.Id);
        }

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
                _ => Task.CompletedTask);

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
            public int SubmitCount;
            public int PollCount;
            public int SubmitCountBeforeFirstPoll;

            public Task<AppTokenPermissionAccess> GetPermissionAccessAsync() => Task.FromResult(Access);

            public Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
            {
                SubmitCount++;
                if (PollCount == 0) SubmitCountBeforeFirstPoll = SubmitCount;
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
                    RecordCount = query.RecordCount,
                });
            }

            public Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null) => Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : new CopilotAuditSearchRecordPage());
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
