using Common.Entities.CopilotAdoption;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class WorkloadProvenanceTests
    {
        private static readonly DateTime Day = new DateTime(2026, 5, 10);
        public TestContext TestContext { get; set; }

        private static List<UsageReportCoverage> Coverage(params string[] complete)
            => UsageReportCoverageStore.Sources.Select(source => new UsageReportCoverage
            {
                Source = source, Status = complete.Contains(source) ? "complete" : "unverified",
                FromUtc = Day, ThroughUtc = Day, ExpectedDays = 1,
                VerifiedDays = complete.Contains(source) ? 1 : 0,
            }).ToList();

        [TestMethod]
        public void MixedSource_MissingMailMustNotBecomeObservedZero()
        {
            var row = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "person@contoso.com",
                TeamsMessages = 100,
                TeamsMeetings = 10,
            });
            CopilotAdoptionWorkloadEvidence.Apply(row, Coverage("teams"));
            Assert.IsNull(row.EmailsSent, "No Outlook outcome exists in this synthetic fixture.");
            Assert.IsNull(row.OpportunityScore, "An unverified weighted component is not a low score.");
            Assert.AreEqual(100L, row.TeamsMessages);
            Assert.AreEqual("workloadUnavailable", row.QualificationTier);
            Assert.IsFalse(row.Recommended);
            Assert.AreEqual(0, CopilotAdoptionScoring.EstimateLicenceValue(new[] { row }).CohortUsers);
        }

        [TestMethod]
        public void ReverseMixedSource_PreservesMailAndFilesWithoutTeams_WithoutReweighting()
        {
            var row = CopilotAdoptionWorkloadEvidence.Apply(
                CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
                {
                    EmailsSent = 50, FilesViewedOrEdited = 70,
                    UnlicensedCopilotInteractions = 20, UnlicensedCopilotActiveDays = 5,
                }), Coverage("outlook", "sharepoint"));
            Assert.IsNull(row.TeamsMessages);
            Assert.AreEqual(50L, row.EmailsSent);
            Assert.AreEqual(70L, row.FilesViewedOrEdited, "Partial document observations survive the absent OneDrive outcome.");
            Assert.IsNull(row.DocumentScore);
            Assert.IsNull(row.OpportunityScore);
            Assert.IsTrue(row.Recommended, "Observed Copilot demand survives missing workload evidence.");
            Assert.AreEqual("provenDemand", row.QualificationTier);
            Assert.AreEqual(0, CopilotAdoptionScoring.EstimateLicenceValue(new[] { row }).CohortUsers);
        }

        [TestMethod]
        public void Cowork_UnknownIsNotLowFit_ObservedUseSurvives()
        {
            var unknown = CopilotAdoptionWorkloadEvidence.Apply(new CoworkReadinessRow
            {
                TeamsMessages = 0, EmailsSent = 0, CoordinationLoadScore = 0,
                Tier = CopilotAdoptionScoring.CoworkTiers.NotIndicated,
            }, Coverage());
            Assert.IsNull(unknown.TeamsMessages);
            Assert.IsNull(unknown.CoordinationLoadScore);
            Assert.AreEqual("workloadUnavailable", unknown.Tier);
            Assert.AreEqual("unavailable", unknown.Basis);
            Assert.IsFalse(unknown.RecommendForPolicy);

            var observed = CopilotAdoptionWorkloadEvidence.Apply(new CoworkReadinessRow
            {
                UsedCowork = true, CoworkInteractions = 20, CoworkActiveDays = 5,
                Tier = "established", Basis = "evidence", RecommendForPolicy = true,
                TeamsMessages = 20, EmailsSent = 0, CoordinationLoadScore = 50,
            }, Coverage("teams"));
            Assert.AreEqual("established", observed.Tier);
            Assert.IsTrue(observed.RecommendForPolicy);
            Assert.AreEqual(20L, observed.TeamsMessages);
            Assert.IsNull(observed.EmailsSent);
            Assert.IsNull(observed.CoordinationLoadScore);
        }

        [TestMethod]
        public void ApiAndCsv_KeepUnknownCellsNullOrBlank_WithAStableReason_AndNumericFiltersExcludeThem()
        {
            var row = CopilotAdoptionWorkloadEvidence.Apply(
                CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow { TeamsMessages = 20 }), Coverage("teams"));
            var json = JObject.FromObject(row);
            Assert.AreEqual(JTokenType.Null, json["emailsSent"].Type);
            Assert.AreEqual(JTokenType.Null, json["opportunityScore"].Type);
            Assert.AreEqual("workloadCoverageUnverified", (string)json["workloadUnavailableReason"]);
            var columns = CopilotAdoptionExports.LicenceOpportunityColumns()
                .Where(c => c.Header == "Opportunity score (0-100)" || c.Header == "Emails sent per active day"
                    || c.Header == "Workload evidence unavailable reason").ToList();
            Assert.AreEqual(3, columns.Count);
            StringAssert.EndsWith(CsvSerialiser.ToCsv(new[] { row }, columns), ",,workloadCoverageUnverified\r\n");
            Assert.AreEqual(0, CopilotAdoptionExports.Apply(new[] { row }, new LicenceOpportunityQuery { MinScore = 0 }).Count);
            Assert.AreEqual(1, CopilotAdoptionExports.Apply(new[] { row }, new LicenceOpportunityQuery
            {
                SortBy = LicenceOpportunitySortFields.Email,
            }).Count, "Unknown numeric cells remain sortable without nullable casts throwing.");
        }

        [TestMethod]
        public void NestedScopeAndSegments_DoNotTurnMissingSourcesIntoLowFitOrForecasts()
        {
            var options = CopilotAdoptionOptions.Default;
            options.MinSeatsPerSegment = 1;
            var analysis = new CopilotAdoptionAnalysis
            {
                CoworkSignals = new List<CoworkReadinessSignalRow>
                {
                    new CoworkReadinessSignalRow
                    {
                        UserId = 1, UserPrincipalName = "person@contoso.com", EmailDomain = "contoso.com",
                        Department = "Contoso", TeamsMessages = 20, AdoptionScore = 80,
                    },
                },
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow { UserId = 1, EmailDomain = "contoso.com", AdoptionScore = 80 },
                },
            };
            analysis.Summary.DataSources.WorkloadCoverage = Coverage("teams");
            analysis.Summary.DataSources.M365UsageReportsAvailable = false;
            var service = new CopilotAdoptionService(options);
            service.FinaliseSummary(analysis);
            var scoped = CopilotAdoptionScopeFilter.Apply(analysis,
                CopilotAdoptionScope.ForEmailDomain("contoso.com"), service.FinaliseSummary);
            Assert.AreEqual("unverified", scoped.Summary.DataSources.WorkloadCoverage.Single(c => c.Source == "outlook").Status);
            Assert.IsNull(scoped.Summary.CoworkPrimeCandidates);
            Assert.IsNull(scoped.Summary.CoworkByDepartment.Single().PrimeCandidates);
            Assert.IsNull(scoped.Summary.CoworkByDepartment.Single().AverageCoordinationLoad);
            Assert.AreEqual(0, scoped.Summary.CoworkQuadrant.Count);
            Assert.IsNull(scoped.Summary.CoworkNewAdoptionEstimate);
            Assert.AreEqual(20L, scoped.CoworkReadiness.Single().TeamsMessages);
            Assert.IsNull(scoped.CoworkReadiness.Single().EmailsSent);
        }

        [TestMethod]
        public async Task SuccessfulEmpty_AllFourSourcesSupportRealZero_WithoutFactRows()
        {
            var store = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            foreach (var source in UsageReportCoverageStore.Sources)
                await store.CompleteDayAsync(source, Day, true, true, true, Day.AddDays(3), 3);
            var coverage = await store.ReadAsync(Day, Day);
            Assert.IsTrue(coverage.All(c => c.Status == "empty" && c.EmptyDays == 1));
            var row = CopilotAdoptionWorkloadEvidence.Apply(
                CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow()), coverage);
            Assert.AreEqual(0L, row.EmailsSent);
            Assert.AreEqual(0d, row.OpportunityScore);
            Assert.IsNull(row.WorkloadUnavailableReason);
        }

        [TestMethod]
        public async Task MixedDailyOutcomes_DoNotCertifyGaps_Scope_RecentDays_OrLegacyStamps()
        {
            var state = new InMemoryKeyValueStore();
            var store = new UsageReportCoverageStore(state, true);
            await store.CompleteDayAsync("teams", Day, true, true, false, Day.AddDays(3), 3);
            await store.CompleteDayAsync("outlook", Day, false, true, true, Day.AddDays(3), 3);
            await store.CompleteDayAsync("sharepoint", Day, true, true, false, Day.AddDays(2), 3);
            await state.SetStringAsync("usage-report:onedrive", Day.AddDays(4).ToString("O"));
            await state.SetStringAsync("v1:onedrive:20260510", "unknownFutureOutcome");
            var coverage = await store.ReadAsync(Day, Day.AddDays(1));
            Assert.AreEqual("partial", coverage.Single(c => c.Source == "teams").Status);
            Assert.AreEqual("scopeUnverified", coverage.Single(c => c.Source == "outlook").Reason);
            Assert.AreEqual(1, coverage.Single(c => c.Source == "sharepoint").PartialDays);
            Assert.AreEqual("unverified", coverage.Single(c => c.Source == "onedrive").Status);
            Assert.IsFalse(CopilotAdoptionWorkloadEvidence.Complete(coverage));
            Assert.IsTrue((await store.ReadAsync(Day.AddDays(10), Day.AddDays(11))).All(c => !c.IsComplete));
        }

        [TestMethod]
        public async Task InterruptedRewrite_RevokesPreviousSuccess()
        {
            var store = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
            await store.CompleteDayAsync("teams", Day, true, true, false, Day.AddDays(3), 3);
            Assert.IsTrue((await store.ReadAsync(Day, Day)).Single(c => c.Source == "teams").IsComplete);
            await store.BeginDayAsync("teams", Day);
            Assert.AreEqual("partial", (await store.ReadAsync(Day, Day)).Single(c => c.Source == "teams").Status);
        }

        [TestMethod]
        public async Task MissingOrUnreachableDurableState_NeverUsesProcessSuccess_AndCancellationPropagates()
        {
            var state = new ProbeStore();
            var processOnly = new UsageReportCoverageStore(state, false);
            await processOnly.CompleteDayAsync("teams", Day, true, true, false, Day.AddDays(3), 3);
            Assert.IsTrue((await processOnly.ReadAsync(Day, Day)).All(c => c.Reason == "durableStateMissing"));
            Assert.AreEqual(0, state.Reads);
            state.Fail = true;
            var durable = new UsageReportCoverageStore(state, true);
            Assert.IsTrue((await durable.ReadAsync(Day, Day)).All(c => c.Reason == "durableStateUnavailable"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => durable.BeginDayAsync("teams", Day));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                    () => durable.ReadAsync(Day, Day, cancellation.Token));
            }
        }

        [TestMethod]
        public async Task SlowDurableState_HasAnOverallDeadline_AndCannotCertifyCoverage()
        {
            var state = new ProbeStore { DelayReads = true };
            var store = new UsageReportCoverageStore(state, true, TimeSpan.FromMilliseconds(1));
            var coverage = await store.ReadAsync(Day, Day.AddDays(365));
            Assert.IsTrue(coverage.All(c => c.Status == "unverified" && c.Reason == "durableStateUnavailable"));
            Assert.IsTrue(state.Reads < 4 * 366, "A timeout stops bounded workers rather than starting the remaining reads.");
        }

        [TestMethod]
        public async Task Synthetic200kUsers_ProvenanceReadsAreBoundedAndIndependentOfPeopleCount()
        {
            var state = new ProbeStore { DelayReads = true };
            var store = new UsageReportCoverageStore(state, true);
            var timer = Stopwatch.StartNew();
            var shortWindow = await store.ReadAsync(Day, Day.AddDays(27));
            var shortMs = timer.ElapsedMilliseconds;
            var wideWindow = await store.ReadAsync(Day, Day.AddDays(365));
            var reads = state.Reads;
            Assert.AreEqual(4 * (28 + 366), reads);
            Assert.IsTrue(state.Peak <= UsageReportCoverageStore.ReadConcurrency);
            Assert.IsFalse((await store.ReadAsync(Day, Day.AddDays(366))).Any(c => c.IsComplete));
            Assert.AreEqual(reads, state.Reads, "Unsupported windows do not perform unbounded reads.");
            timer.Restart();
            for (var user = 0; user < 200000; user++)
            {
                var row = CopilotAdoptionWorkloadEvidence.Apply(
                    CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
                    {
                        UserId = user, TeamsMessages = user % 100, EmailsSent = user % 50,
                    }), user % 2 == 0 ? shortWindow : wideWindow);
                if (row.OpportunityScore.HasValue) Assert.Fail("Unverified users must not regain a composite score.");
            }
            Assert.AreEqual(reads, state.Reads, "The publication pass makes no per-person state requests.");
            TestContext.WriteLine($"Synthetic 200,000 users: publication {timer.ElapsedMilliseconds}ms; " +
                $"28-day state read {shortMs}ms; {reads} total reads for 28+366 days, peak concurrency {state.Peak}. No SQL or HTTP.");
        }

        private sealed class ProbeStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            private int _active;
            public int Reads;
            public int Peak;
            public bool Fail;
            public bool DelayReads;
            public string Description => "Synthetic bounded state";
            public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Reads);
                var active = Interlocked.Increment(ref _active);
                int peak;
                do { peak = Peak; } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
                try
                {
                    if (Fail) throw new InvalidOperationException("Synthetic state outage");
                    if (DelayReads) await Task.Delay(1, cancellationToken);
                    return await _inner.GetStringAsync(key, cancellationToken);
                }
                finally { Interlocked.Decrement(ref _active); }
            }
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                if (Fail) throw new InvalidOperationException("Synthetic state outage");
                return _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
                => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
                => _inner.ExistsAsync(key, cancellationToken);
        }
    }
}
