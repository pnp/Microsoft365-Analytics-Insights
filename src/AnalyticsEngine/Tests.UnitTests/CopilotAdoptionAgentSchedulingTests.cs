using Common.Entities.CopilotAdoption;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotAdoptionAgentSchedulingTests
    {
        [TestMethod]
        public async Task QuietRollingPeriod_SchedulesYearAgoGrowthAndIndependentBuilders()
        {
            var service = new ScriptedService { AuthoringImported = true };
            service.AuditDates.Add(DateTime.UtcNow.AddDays(-500));
            service.AuditDates.Add(DateTime.UtcNow.AddDays(-370));

            var analysis = await service.AnalyseAsync();

            Assert.IsFalse(analysis.Summary.DataSources.AuditAvailable);
            Assert.AreEqual(14, analysis.Summary.Agents.Growth.Count);
            Assert.AreEqual(1, analysis.Summary.Agents.Growth.Single(w => w.WindowsAgo == 13).ActiveAgents);
            Assert.AreEqual(10, analysis.Summary.AgentBuilders);
            Assert.IsNull(analysis.Summary.AgentBreadthAgentUsers);
            Assert.IsNull(analysis.Summary.AgentDepthInteractions);
            Assert.IsNull(analysis.AgentReachRows);
            AssertStep(analysis, CopilotAdoptionSteps.AgentReach, false);
            AssertStep(analysis, CopilotAdoptionSteps.AgentGrowth, false);
            Assert.IsFalse(analysis.Summary.Diagnostics.Steps.Any(s => s.Step == CopilotAdoptionSteps.AgentEstate),
                "Growth availability must not broaden the selected-period inventory gate.");
            Assert.IsFalse(service.Calls.Any(c => c.Sql == CopilotAdoptionSql.AgentReachSql()),
                "A quiet rolling window must not query person/agent pairs just to assess builders.");
            var growth = service.Calls.Single(c => c.Sql == CopilotAdoptionSql.AgentGrowthSql);
            Assert.AreEqual(392, ((DateTime)growth.Parameters["@seriesToExclusive"] - (DateTime)growth.Parameters["@seriesFrom"]).Days);
        }

        [TestMethod]
        public async Task NoImportedSources_StayUnknownWithoutGrowthOrBuilderFactReads()
        {
            var service = new ScriptedService();

            var analysis = await service.AnalyseAsync();

            Assert.AreEqual(0, analysis.Summary.Agents.Growth.Count);
            Assert.IsNull(analysis.Summary.AgentBuilders);
            Assert.IsNull(analysis.Summary.AgentBreadthAgentUsers);
            Assert.IsNull(analysis.Summary.AgentDepthInteractions);
            Assert.IsFalse(analysis.AgentBuildersAssessed);
            Assert.IsFalse(service.Calls.Any(c => c.Sql == CopilotAdoptionSql.AgentGrowthAgentsSql
                || c.Sql == CopilotAdoptionSql.AgentGrowthSql || c.Sql == CopilotAdoptionSql.AgentGrowthBillingSql
                || c.Sql == CopilotAdoptionSql.AgentBuildersSql()));
            AssertStep(analysis, CopilotAdoptionSteps.AgentGrowth, false);
            AssertStep(analysis, CopilotAdoptionSteps.AgentReach, false);
        }

        [TestMethod]
        public async Task ImportedHistoryOlderThanTheSeries_KeepsQuietWindowsUnmeasured()
        {
            var service = new ScriptedService();
            service.AuditDates.Add(DateTime.UtcNow.AddDays(-500));

            var analysis = await service.AnalyseAsync();

            Assert.AreEqual(14, analysis.Summary.Agents.Growth.Count);
            Assert.IsTrue(analysis.Summary.Agents.Growth.All(w => w.ActiveAgents == null),
                "A window without any Copilot data stays unknown even when older history exists.");
            Assert.IsNull(analysis.Summary.AgentBuilders);
            Assert.IsNull(analysis.Summary.AgentBreadthAgentUsers);
            AssertStep(analysis, CopilotAdoptionSteps.AgentGrowth, false);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NormalAndHistoricalPeriods_KeepSelectedReachAndLatestGrowthScopes(bool historical)
        {
            var options = new CopilotAdoptionOptions { WindowDays = 28 };
            if (historical)
            {
                options.UsesExplicitDates = true;
                options.FromUtc = DateTime.UtcNow.Date.AddDays(-90);
                options.ToUtc = DateTime.UtcNow.Date.AddDays(-63);
                options.ToExclusiveUtc = options.ToUtc.Value.AddDays(1);
            }
            var service = new ScriptedService(options) { AuthoringImported = true };
            service.AuditDates.Add(DateTime.UtcNow.AddDays(historical ? -75 : -7));

            var analysis = await service.AnalyseAsync();

            Assert.IsTrue(analysis.Summary.DataSources.AuditAvailable);
            Assert.AreEqual(14, analysis.Summary.Agents.Growth.Count);
            Assert.AreEqual(10, analysis.Summary.AgentBuilders);
            AssertStep(analysis, CopilotAdoptionSteps.AgentEstate, false);
            AssertStep(analysis, CopilotAdoptionSteps.AgentReach, false);
            AssertStep(analysis, CopilotAdoptionSteps.AgentGrowth, false);
            var selectedReach = service.Calls.First(c => c.Sql == CopilotAdoptionSql.AgentReachSql());
            Assert.AreEqual(analysis.Summary.FromUtc, selectedReach.Parameters["@from"]);
            var builders = service.Calls.Single(c => c.Sql == CopilotAdoptionSql.AgentBuildersSql());
            Assert.AreEqual(analysis.Summary.FromUtc, builders.Parameters["@from"]);
            var growth = service.Calls.Single(c => c.Sql == CopilotAdoptionSql.AgentGrowthSql);
            Assert.IsTrue((DateTime)growth.Parameters["@seriesToExclusive"] > DateTime.UtcNow.Date.AddDays(-7),
                "Growth stays anchored to the latest settled day, even for a historical selected period.");
        }

        [TestMethod]
        public async Task GrowthAvailabilityFailure_IsIncomplete_NotMeasuredZero_AndDoesNotHideBuilders()
        {
            var service = new ScriptedService { AuthoringImported = true, FailGrowthProbe = true };
            service.AuditDates.Add(DateTime.UtcNow.AddDays(-370));

            var analysis = await service.AnalyseAsync();

            Assert.AreEqual(0, analysis.Summary.Agents.Growth.Count);
            Assert.AreEqual(10, analysis.Summary.AgentBuilders);
            Assert.IsTrue(analysis.Summary.FiguresIncomplete);
            AssertStep(analysis, CopilotAdoptionSteps.AgentGrowth, true);
            AssertWarning(analysis, CopilotAdoptionQueries.AuditDataProbe);
        }

        [TestMethod]
        public async Task AuthoringProbeFailure_LeavesBuildersUnknownAndGrowthAvailable()
        {
            var service = new ScriptedService { AuthoringImported = true, FailSql = CopilotAdoptionSql.CopilotStudioAuthoringImportedSql };
            service.AuditDates.Add(DateTime.UtcNow.AddDays(-370));

            var analysis = await service.AnalyseAsync();

            Assert.IsNull(analysis.Summary.AgentBuilders);
            Assert.IsFalse(analysis.AgentBuildersAssessed);
            Assert.AreEqual(14, analysis.Summary.Agents.Growth.Count);
            AssertStep(analysis, CopilotAdoptionSteps.AgentReach, true);
            AssertWarning(analysis, CopilotAdoptionQueries.AgentBuildersProbe);
        }

        [TestMethod]
        public async Task GrowthUsageFailure_LeavesSeriesEmptyWithItsExistingWarningAndFailedStep()
        {
            var service = new ScriptedService { FailSql = CopilotAdoptionSql.AgentGrowthSql };
            service.AuditDates.Add(DateTime.UtcNow.AddDays(-370));

            var analysis = await service.AnalyseAsync();

            Assert.AreEqual(0, analysis.Summary.Agents.Growth.Count);
            AssertStep(analysis, CopilotAdoptionSteps.AgentGrowth, true);
            AssertWarning(analysis, CopilotAdoptionQueries.AgentGrowth);
        }

        private static void AssertStep(CopilotAdoptionAnalysis analysis, string step, bool failed)
        {
            var timing = analysis.Summary.Diagnostics.Steps.SingleOrDefault(s => s.Step == step);
            Assert.IsNotNull(timing, $"The service must schedule {step} independently of the selected audit period.");
            Assert.AreEqual(failed, timing.Failed);
        }

        private static void AssertWarning(CopilotAdoptionAnalysis analysis, string query) =>
            Assert.IsTrue(analysis.Summary.WarningDetails.Any(w => w.Key == CopilotAdoptionWarningKeys.CouldNotLoad
                && w.Values.TryGetValue("query", out var value) && Equals(value, query)));

        private sealed class QueryCall
        {
            public string Sql { get; set; }
            public Dictionary<string, object> Parameters { get; set; }
        }

        private sealed class ScriptedService : CopilotAdoptionService
        {
            private int _auditProbes;
            public ScriptedService(CopilotAdoptionOptions options = null) : base(options) { }
            public List<DateTime> AuditDates { get; } = new List<DateTime>();
            public List<QueryCall> Calls { get; } = new List<QueryCall>();
            public bool AuthoringImported { get; set; }
            public bool FailGrowthProbe { get; set; }
            public string FailSql { get; set; }

            protected override Task<List<T>> QueryAsync<T>(string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = parameters.ToDictionary(p => p.ParameterName, p => p.Value);
                Calls.Add(new QueryCall { Sql = sql, Parameters = values });
                if (sql == FailSql) throw new InvalidOperationException("Synthetic query failure");
                if (sql == CopilotAdoptionSql.LicenceTypesSql)
                    return Rows<T>(new[] { new LicenceTypeRow { Id = 1, Name = "Office 365 E3", SkuPartNumber = "ENTERPRISEPACK" } });
                if (sql == CopilotAdoptionSql.HasCopilotAuditDataSql)
                {
                    if (++_auditProbes > 1 && FailGrowthProbe) throw new InvalidOperationException("Synthetic growth availability failure");
                    var from = (DateTime)values["@from"];
                    var to = (DateTime)values["@toExclusive"];
                    return Rows<T>(new int?[] { AuditDates.Any(d => d >= from && d < to) ? 1 : 0 });
                }
                if (sql == CopilotAdoptionSql.CopilotStudioAuthoringImportedSql)
                    return Rows<T>(new int?[] { AuthoringImported ? 1 : 0 });
                if (typeof(T) == typeof(int?)) return Rows<T>(new int?[] { 0 });
                if (typeof(T) == typeof(DateTime?)) return Rows<T>(new DateTime?[] { null });
                if (sql == CopilotAdoptionSql.AgentGrowthAgentsSql)
                    return Rows<T>(new[] { new AgentGrowthAgentRow { AgentId = 1, AgentKey = "Contoso agent", IsCustomAgent = true } });
                if (sql == CopilotAdoptionSql.AgentGrowthSql)
                    return Rows<T>(CopilotAdoptionAgentGrowth.Windows((DateTime)values["@lastSettledDay"]).Select(window =>
                    {
                        var active = AuditDates.Any(d => d >= window.FromUtc && d < window.ToUtc.AddDays(1));
                        return new AgentGrowthQueryRow
                        {
                            WindowsAgo = window.WindowsAgo,
                            ActiveAgents = active ? 1 : 0, AgentUsers = active ? 1 : 0, AgentInteractions = active ? 1 : 0,
                            HasCopilotData = active, FirstCopilotInteractionUtc = AuditDates.Min(),
                        };
                    }));
                if (sql == CopilotAdoptionSql.AgentBuildersSql())
                    return Rows<T>(Enumerable.Range(1, 10).Select(i => new AgentBuilderRow
                    { UserId = i, UserPrincipalName = $"builder{i}@contoso.com", Department = "Contoso department" }));
                return Task.FromResult(new List<T>());
            }

            private static Task<List<T>> Rows<T>(IEnumerable rows) => Task.FromResult(rows.Cast<T>().ToList());
        }
    }
}
