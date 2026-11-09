extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.CopilotAdoption;
using Common.Entities.LeadershipCohort;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Cowork readiness CSV download, end to end through the real controller (#851, R3-7). Synthetic data only.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionCoworkExportTests
    {
        [TestMethod]
        public async Task CoworkCsvDownload_SaysOnEveryRowWhenTheFiguresAreIncompleteAndWhy()
        {
            var analysis = CopilotAdoptionWorkbookTests.SyntheticAnalysis();
            analysis.Summary.FiguresIncomplete = true;
            analysis.Summary.IncompleteReasons.Add("Contoso test reason");
            var warnings = string.Join(" | ", new[] { "Figures incomplete: Contoso test reason" }.Concat(analysis.Summary.Warnings));
            Assert.IsTrue(analysis.CoworkReadiness.Count > 0, "The fixture must have Cowork rows to export.");

            var coordinator = new AdoptionCoordinator(
                new FixedRunner(analysis),
                new DictionaryCache(),
                (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10));
            var leadership = new LeadershipComparisonProvider(() => new LeadershipCohortStore(new InMemoryKeyValueStore(), isDurable: true));

            using (var host = new PortalTestHost(
                new[] { typeof(CopilotAdoptionAPIController) },
                PortalTestHost.SignedIn(PortalRoles.SeePii),
                PortalAccessPolicy.Enforcing,
                _ => new CopilotAdoptionAPIController(coordinator) { LeadershipComparisons = leadership }))
            {
                var response = await host.Client.GetAsync("api/CopilotAdoption/cowork/export");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());

                var header = csv.Substring(0, csv.IndexOf("\r\n", StringComparison.Ordinal));
                StringAssert.Contains(header, ",Figures incomplete,Figure warnings,", "The same columns, in the same place relative to each other, as the other per-user exports.");
                Assert.AreEqual(
                    analysis.CoworkReadiness.Count,
                    CountOf(csv, ",Yes," + warnings + ","),
                    "Every exported row says the figures are incomplete, and why.");
            }
        }

        private static int CountOf(string text, string value)
        {
            var count = 0;
            for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private sealed class FixedRunner : AdoptionRunner
        {
            private readonly CopilotAdoptionAnalysis _analysis;
            internal FixedRunner(CopilotAdoptionAnalysis analysis) { _analysis = analysis; }

            public Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc, bool usesExplicitDates,
                List<int> seatLicenceTypeIds, CopilotAdoptionEffectiveScoreSettings scoreSettings, ICopilotAdoptionRunTelemetry telemetry) =>
                Task.FromResult(_analysis);
        }

        private sealed class DictionaryCache : AdoptionCache
        {
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _entries = new Dictionary<string, CopilotAdoptionAnalysis>();

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                lock (_entries) return _entries.TryGetValue(key, out analysis);
            }

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
                lock (_entries) _entries[key] = analysis;
            }
        }
    }
}
