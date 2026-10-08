extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.CopilotAdoption;
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
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using DateRange = Common.Entities.CopilotAdoption.CopilotAdoptionDateRange;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;
using RunTelemetry = Common.Entities.CopilotAdoption.ICopilotAdoptionRunTelemetry;
using SettingsProvider = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionScoreSettingsProvider;
using SettingsService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionSettingsService;
using SettingsSource = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionScoreSettingsSource;

namespace Tests.UnitTests
{
    public partial class CopilotAdoptionScoreSettingsTests
    {
        #region Analysis cache

        [TestMethod]
        public async Task Report_StillBuildingNamesTheJoinedRun_WhenSettingsChangeDuringItsWait()
        {
            var source = new MutableSource();
            var runner = new HeldSettingsRunner();
            var serial = 0;
            var coordinator = new AdoptionCoordinator(
                runner, new DictionaryCache(),
                (window, hasOverride) => new IdentifiedTelemetry("synthetic-run-" + Interlocked.Increment(ref serial)),
                TimeSpan.FromMinutes(10), settingsSource: source);
            using (var host = new PortalTestHost(
                new[] { typeof(CopilotAdoptionAPIController) }, PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing, _ => new CopilotAdoptionAPIController(coordinator)))
            {
                var responseTask = host.Client.GetAsync("api/CopilotAdoption/summary");
                try
                {
                    await runner.FirstStarted.Task;
                    source.Current = new CopilotAdoptionEffectiveScoreSettings(1, With(s => s.ChampionScore = 90), Now);
                    var newerRun = coordinator.GetAsync(DateRange.Create(28, null, null, Now), new List<int>());
                    await runner.SecondStarted.Task;
                    var response = await responseTask;
                    Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual("synthetic-run-1", (string)body["runId"],
                        "This request joined the defaults, not the later customised generation.");
                    Assert.AreEqual("synthetic-run-1", response.Headers.GetValues(CopilotAdoptionAPIController.RunIdHeader).Single());
                    runner.Complete();
                    await newerRun;
                }
                finally { runner.Complete(); }
            }
        }

        private sealed class HeldSettingsRunner : AdoptionRunner
        {
            internal readonly TaskCompletionSource<bool> FirstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> SecondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _calls;
            public async Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc, bool usesExplicitDates, List<int> seatLicenceTypeIds, CopilotAdoptionEffectiveScoreSettings scoreSettings, RunTelemetry telemetry)
            {
                (Interlocked.Increment(ref _calls) == 1 ? FirstStarted : SecondStarted).TrySetResult(true);
                await _complete.Task;
                return Analysis(scoreSettings);
            }
            internal void Complete() => _complete.TrySetResult(true);
        }

        private sealed class IdentifiedTelemetry : AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisTelemetry
        {
            internal IdentifiedTelemetry(string runId) { RunId = runId; }
            public string RunId { get; }
            public long StepStarted(string step) => 0;
            public void StepCompleted(long operationId, string step, long durationMs, bool failed, CopilotAdoptionFailure failure = null) { }
            public long QueryStarted(string step, string query) => 0;
            public void QueryCompleted(long operationId, string step, string query, long durationMs, bool failed, CopilotAdoptionFailure failure = null) { }
            public void Checkpoint(string stage, long durationMs = 0) { }
            public void QueueCompletion(CopilotAdoptionAnalysis analysis) { }
            public bool QueueFailure(Exception exception) => true;
            public void HostStopping(string reason) { }
            public void Dispose() { }
        }

        [TestMethod]
        public void CacheKey_IsUnchangedForTheDefaults_AndDistinctPerCustomisedVersion()
        {
            var range = DateRange.Create(28, null, null, Now);
            var ids = new List<int> { 3, 1 };
            var legacy = AdoptionCoordinator.CacheKey(range, ids);

            Assert.AreEqual(legacy, AdoptionCoordinator.CacheKey(range, ids, CopilotAdoptionEffectiveScoreSettings.Defaults));
            Assert.AreEqual(legacy, AdoptionCoordinator.CacheKey(range, ids, new CopilotAdoptionEffectiveScoreSettings(9, CopilotAdoptionScoreSettings.Defaults, Now)),
                "A reset at any version shares the defaults' entries.");

            var v1 = AdoptionCoordinator.CacheKey(range, ids, new CopilotAdoptionEffectiveScoreSettings(1, With(s => s.ChampionScore = 90), Now));
            var v2 = AdoptionCoordinator.CacheKey(range, ids, new CopilotAdoptionEffectiveScoreSettings(2, With(s => s.ChampionScore = 85), Now));
            Assert.AreNotEqual(legacy, v1);
            Assert.AreNotEqual(v1, v2);
            StringAssert.StartsWith(v1, legacy);
        }

        [TestMethod]
        public async Task Coordinator_ScoresAgainAfterASave_AndReusesTheDefaultsAfterAReset()
        {
            var source = new MutableSource();
            var runner = new RecordingRunner();
            var coordinator = new AdoptionCoordinator(
                runner,
                new DictionaryCache(),
                (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10),
                settingsSource: source);
            var range = DateRange.Create(28, null, null, Now);

            await coordinator.GetAsync(range, new List<int>());
            await coordinator.GetAsync(range, new List<int>());
            Assert.AreEqual(1, runner.Runs.Count, "Cached under the defaults.");
            Assert.IsFalse(runner.Runs[0].IsCustomised);

            source.Current = new CopilotAdoptionEffectiveScoreSettings(1, With(s => s.ChampionScore = 90), Now);
            await coordinator.GetAsync(range, new List<int>());
            Assert.AreEqual(2, runner.Runs.Count, "A save is a cache miss: nothing scored with the old rules is served.");
            Assert.AreEqual(90, runner.Runs[1].GetValues().ChampionScore);

            source.Current = new CopilotAdoptionEffectiveScoreSettings(2, CopilotAdoptionScoreSettings.Defaults, Now);
            await coordinator.GetAsync(range, new List<int>());
            Assert.AreEqual(2, runner.Runs.Count, "A reset finds the defaults' result again.");
        }

        [TestMethod]
        public async Task Coordinators_OnTwoInstances_UseASaveFromEitherOnTheirNextRequest()
        {
            // One table row shared by two web instances, each with its own provider, coordinator and process cache.
            var table = new InMemoryKeyValueStore();
            CopilotAdoptionScoreSettingsStore NewStore() => new CopilotAdoptionScoreSettingsStore(table, isDurable: true);
            var storeA = NewStore();
            var storeB = NewStore();
            var providerA = new SettingsProvider(() => storeA);
            var providerB = new SettingsProvider(() => storeB);
            var runnerA = new RecordingRunner();
            var runnerB = new RecordingRunner();
            AdoptionCoordinator Coordinator(RecordingRunner runner, SettingsProvider provider) => new AdoptionCoordinator(
                runner, new DictionaryCache(), (windowDays, hasOverride) => NullAnalysisTelemetry.Instance, TimeSpan.FromMinutes(10), settingsSource: provider);
            var instanceA = Coordinator(runnerA, providerA);
            var instanceB = Coordinator(runnerB, providerB);
            var range = DateRange.Create(28, null, null, Now);

            await instanceA.GetAsync(range, new List<int>());
            await instanceB.GetAsync(range, new List<int>());
            Assert.IsFalse(runnerA.Runs.Single().IsCustomised);

            // An administrator saves through instance B; instance A is told nothing and no time passes.
            var saved = await new SettingsService(providerB).SaveAsync(
                new AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionSettingsSaveRequest { ExpectedVersion = 0, Settings = With(s => s.ChampionScore = 90) },
                "admin@contoso.com");
            Assert.AreEqual(1, saved.Version);

            var onA = await instanceA.GetAsync(range, new List<int>());
            Assert.AreEqual(2, runnerA.Runs.Count, "Instance A's next request is a fresh analysis, not its cached default one.");
            Assert.AreEqual(90, runnerA.Runs[1].GetValues().ChampionScore);
            Assert.AreEqual(1, onA.Summary.Options.ScoreSettings.Version);
            await instanceB.GetAsync(range, new List<int>());
            Assert.AreEqual(90, runnerB.Runs[1].GetValues().ChampionScore);

            // A reset through instance A: B's next request goes back to its cached default analysis.
            await new SettingsService(providerA).ResetAsync(
                new AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionSettingsResetRequest { ExpectedVersion = 1 }, "admin@contoso.com");
            var onB = await instanceB.GetAsync(range, new List<int>());
            Assert.AreEqual(2, runnerB.Runs.Count, "The defaults' result is reused.");
            Assert.IsFalse(onB.Summary.Options.ScoreSettings.Customised);
        }

        [TestMethod]
        public async Task Report_WhenTheSettingsCannotBeRead_IsRefusedWithAStableCode()
        {
            var coordinator = new AdoptionCoordinator(
                new RecordingRunner(),
                new DictionaryCache(),
                (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10),
                settingsSource: new MutableSource { Fail = true });

            using (var host = new PortalTestHost(
                new[] { typeof(CopilotAdoptionAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing,
                _ => new CopilotAdoptionAPIController(coordinator)))
            {
                var response = await host.Client.GetAsync("api/CopilotAdoption/summary");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.ReportSettingsUnavailable, (string)body["code"]);
            }
        }

        #endregion

        #region Workbook

        [TestMethod]
        public void Workbook_SaysWhenTheScoreSettingsAreCustomised()
        {
            var byDefault = WorkbookText(CopilotAdoptionWorkbook.Build(Analysis(CopilotAdoptionEffectiveScoreSettings.Defaults)));
            StringAssert.Contains(byDefault, "The engagement-score weights and band thresholds are the defaults.");
            Assert.IsFalse(byDefault.Contains("Customised by an administrator"));

            var custom = WorkbookText(CopilotAdoptionWorkbook.Build(Analysis(
                new CopilotAdoptionEffectiveScoreSettings(4, With(s => s.ChampionScore = 90, s => s.FrequencyWeightPercent = 40, s => s.DepthWeightPercent = 40), Now))));
            StringAssert.Contains(custom, "Customised (settings version 4)");
            StringAssert.Contains(custom, "Customised by an administrator (default 75).");
            StringAssert.Contains(custom, "Reset to defaults");
        }

        #endregion

        #region Admin API

        [TestMethod]
        public async Task Api_OnlyAdministratorsMayReadOrChangeTheSettings()
        {
            var service = Service(new InMemoryKeyValueStore(), durable: true);
            var cases = new[]
            {
                (Roles: new string[0], Policy: PortalAccessPolicy.Enforcing, Allowed: false),
                (Roles: new[] { PortalRoles.SeePii }, Policy: PortalAccessPolicy.Enforcing, Allowed: false),
                (Roles: new[] { PortalRoles.Administration }, Policy: PortalAccessPolicy.Enforcing, Allowed: true),
                (Roles: new[] { PortalRoles.Administration, PortalRoles.SeePii }, Policy: PortalAccessPolicy.Enforcing, Allowed: true),
                (Roles: new string[0], Policy: PortalAccessPolicy.NotEnforcing, Allowed: true),
            };

            foreach (var c in cases)
            {
                using (var host = SettingsHost(service, PortalTestHost.SignedIn(c.Roles), c.Policy))
                {
                    var label = string.Join("+", c.Roles) + (c.Policy == PortalAccessPolicy.NotEnforcing ? " (not enforcing)" : string.Empty);
                    var get = await host.Client.GetAsync("api/CopilotAdoptionSettings");
                    var save = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", SaveBody(null, 75)));
                    var reset = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings/reset", "{\"expectedVersion\":0}"));

                    foreach (var response in new[] { get, save, reset })
                    {
                        if (c.Allowed)
                        {
                            Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode, label);
                        }
                        else
                        {
                            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, label);
                            Assert.AreEqual("administration", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["permission"], label);
                        }
                    }

                    if (c.Allowed)
                    {
                        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode, label);
                    }
                }
            }
        }

        [TestMethod]
        public async Task Api_SaveRecordsTheAdministrator_AndResetRestoresTheDefaults()
        {
            var service = Service(new InMemoryKeyValueStore(), durable: true);
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                new Claim(ClaimTypes.Upn, "admin@contoso.com"),
                new Claim(ClaimTypes.Role, PortalRoles.Administration),
            }, "Test");

            using (var host = SettingsHost(service, new ClaimsPrincipal(identity), PortalAccessPolicy.Enforcing))
            {
                var initial = JObject.Parse(await (await host.Client.GetAsync("api/CopilotAdoptionSettings")).Content.ReadAsStringAsync());
                Assert.AreEqual(0, (long)initial["version"]);
                Assert.AreEqual(true, (bool)initial["durable"]);
                Assert.AreEqual(75, (int)initial["defaults"]["championScore"]);

                var saved = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", SaveBody(0, 90)));
                var savedBody = JObject.Parse(await saved.Content.ReadAsStringAsync());
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, savedBody.ToString());
                Assert.AreEqual(1, (long)savedBody["version"]);
                Assert.AreEqual("admin@contoso.com", (string)savedBody["updatedBy"]);
                Assert.AreEqual("admin@contoso.com", (string)savedBody["history"][0]["changedBy"]);
                CollectionAssert.Contains(savedBody["customisedFields"].Values<string>().ToList(), CopilotAdoptionScoreSettingsFields.ChampionScore);

                var stale = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", SaveBody(0, 85)));
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.VersionConflict, await Code(stale));

                var invalid = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", SaveBody(1, 40)));
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.ThresholdsNotAscending, await Code(invalid));
                Assert.AreEqual("application/json", invalid.Content.Headers.ContentType.MediaType);

                var missing = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", "{}"));
                Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest, await Code(missing));

                var reset = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings/reset", "{\"expectedVersion\":1}"));
                var resetBody = JObject.Parse(await reset.Content.ReadAsStringAsync());
                Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, resetBody.ToString());
                Assert.AreEqual(2, (long)resetBody["version"]);
                Assert.AreEqual(75, (int)resetBody["settings"]["championScore"]);
                Assert.AreEqual(0, resetBody["customisedFields"].Count());
                Assert.AreEqual(CopilotAdoptionScoreSettingsActions.Reset, (string)resetBody["history"][0]["action"]);
            }
        }

        [TestMethod]
        public async Task Api_WithoutStorage_ReadsTheDefaults_AndRefusesToSave()
        {
            using (var host = SettingsHost(Service(new InMemoryKeyValueStore(), durable: false), PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing))
            {
                var get = JObject.Parse(await (await host.Client.GetAsync("api/CopilotAdoptionSettings")).Content.ReadAsStringAsync());
                Assert.AreEqual(false, (bool)get["durable"]);

                var save = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", SaveBody(0, 90)));
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, save.StatusCode);
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.StorageNotConfigured, await Code(save));
            }
        }

        [TestMethod]
        public async Task Api_WhenStorageFails_SaysSo()
        {
            var failing = new FailingStore();
            using (var host = SettingsHost(Service(failing, durable: true), PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing))
            {
                var get = await host.Client.GetAsync("api/CopilotAdoptionSettings");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, get.StatusCode);
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.StateUnavailable, await Code(get));

                var save = await host.Client.SendAsync(Post("api/CopilotAdoptionSettings", SaveBody(0, 90)));
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, save.StatusCode);
                Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.StateUnavailable, await Code(save));
            }
        }

        [TestMethod]
        public async Task Api_WritesNeedTheSameOriginScriptHeader()
        {
            var values = new InMemoryKeyValueStore();
            using (var host = SettingsHost(Service(values, durable: true), PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing))
            {
                var noHeader = new HttpRequestMessage(HttpMethod.Post, "api/CopilotAdoptionSettings") { Content = new StringContent(SaveBody(0, 90), Encoding.UTF8, "application/json") };
                Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.SendAsync(noHeader)).StatusCode);

                var crossSite = Post("api/CopilotAdoptionSettings/reset", "{\"expectedVersion\":0}");
                crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);

                Assert.IsNull(await values.GetStringAsync(CopilotAdoptionScoreSettingsStore.DocumentKey), "Nothing was written.");
            }
        }

        #endregion
    }
}
