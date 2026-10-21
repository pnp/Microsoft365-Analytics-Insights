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
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;
using RunTelemetry = Common.Entities.CopilotAdoption.ICopilotAdoptionRunTelemetry;
using SettingsProvider = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionScoreSettingsProvider;
using SettingsService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionSettingsService;
using SettingsSource = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionScoreSettingsSource;

namespace Tests.UnitTests
{
    /// <summary>
    /// The administrator's Copilot Adoption score settings (#683 weights, #684 band thresholds): validation, the
    /// versioned audited store, the per-instance provider, the analysis cache key, the exports and the admin API.
    /// All identities are synthetic.
    /// </summary>
    [TestClass]
    public partial class CopilotAdoptionScoreSettingsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime WindowStart = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);

        #region Model and validation

        private static CopilotAdoptionScoreSettings PhaseTwoSettings() => new CopilotAdoptionScoreSettings
        {
            FrequencyTargetRatio = 0.75, WorkingDaysPerWeek = 4, DepthTargetInteractionsPerActiveDay = 8.5,
            DepthMinActiveDays = 4, BreadthTargetApps = 4,
            HabitModerateMinDays = 7, HabitFrequentMinDays = 12, HabitDailyMinDays = 21,
            OpportunityRecommendScore = 60, OpportunityProvenDemandMinActiveDays = 4,
            OpportunityUnlicensedCopilotWeight = 40, OpportunityCollaborationWeight = 20,
            OpportunityEmailWeight = 25, OpportunityDocumentWeight = 15,
            CoworkLoadMinScore = 60, CoworkFluencyMinScore = 70, CoworkRegularMinActiveDays = 4,
            CoworkCollaborationWeight = 25, CoworkMeetingWeight = 30, CoworkEmailWeight = 30, CoworkDocumentWeight = 15,
            AgentReviewInactiveDays = 40, AgentRetireInactiveDays = 100, AgentNewDays = 40, AgentMinUsers = 4,
            ReclaimGraceDays = 45, MinSeatsPerSegment = 6,
        };

        [TestMethod]
        public void ExtendedDefaults_PreserveEveryOption_AndEverySavedFieldReachesTheScorer()
        {
            var before = CopilotAdoptionOptions.Default;
            var after = CopilotAdoptionOptions.Default;
            CopilotAdoptionScoreSettings.Defaults.ApplyTo(after);
            Assert.IsTrue(JToken.DeepEquals(JObject.FromObject(before), JObject.FromObject(after)));
            var custom = PhaseTwoSettings();
            Assert.AreEqual(0, custom.Validate().Count);
            custom.ApplyTo(after);
            var json = JObject.FromObject(custom);
            var applied = JObject.FromObject(after);
            foreach (var field in json.Properties().Where(p => !p.Name.EndsWith("WeightPercent", StringComparison.Ordinal)))
                Assert.AreEqual((double)field.Value, (double)applied[field.Name], field.Name);
            Assert.AreEqual(50d, after.EstablishedScore, "Cowork fluency is independent of the Established band.");
            Assert.AreEqual(70d, after.CoworkFluencyMinScore);
        }

        [TestMethod]
        public void ExtendedValidation_RejectsNonFiniteTargets_Ordering_AndWeakenedPrivacy()
        {
            Assert.AreEqual(0, With(s => s.DepthTargetInteractionsPerActiveDay = 0.5).Validate().Count);
            Assert.AreNotEqual(CopilotAdoptionScoreSettings.Defaults.Fingerprint(),
                With(s => s.FrequencyTargetRatio = 0.6000000000000001).Fingerprint(), "Fingerprints preserve every double's round-trip precision.");
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1, 1.1 })
                CollectionAssert.Contains(With(s => s.FrequencyTargetRatio = value).Validate().ToList(), "targetOutOfRange");
            foreach (var edit in new Action<CopilotAdoptionScoreSettings>[]
            {
                s => s.WorkingDaysPerWeek = 8, s => s.DepthMinActiveDays = 0, s => s.BreadthTargetApps = 0,
                s => s.DepthTargetInteractionsPerActiveDay = double.NaN,
                s => s.HabitModerateMinDays = 11, s => s.HabitDailyMinDays = 29,
                s => s.OpportunityEmailWeight = 0, s => s.CoworkMeetingWeight = -1,
                s => s.OpportunityRecommendScore = 101, s => s.CoworkFluencyMinScore = 0,
                s => s.CoworkRegularMinActiveDays = 0, s => s.OpportunityProvenDemandMinActiveDays = 0,
                s => s.AgentReviewInactiveDays = 90, s => s.AgentRetireInactiveDays = 121, s => s.AgentNewDays = 0,
                s => s.AgentMinUsers = 2, s => s.MinSeatsPerSegment = 4,
                s => s.ReclaimGraceDays = 0,
            })
            {
                var settings = With(edit);
                Assert.IsTrue(settings.Validate().Count > 0);
                Assert.ThrowsException<CopilotAdoptionScoreSettingsRejectedException>(() => settings.ApplyTo(CopilotAdoptionOptions.Default));
            }
        }

        [TestMethod]
        public void AgentNewness_IsBoundedByInclusiveQueryHistory_AndCannotMaskRetirement()
        {
            var historyStart = CopilotAdoptionScoring.WindowStartUtc(Now, CopilotAdoptionOptions.Default.AgentHistoryDays);
            Assert.AreEqual(119, (Now.Date - historyStart).Days);
            var sql = CopilotAdoptionSql.AgentUsageSql(new[] { 1 });
            StringAssert.Contains(sql, "MIN(c.time_stamp) AS FirstUsedUtc");
            StringAssert.Contains(sql, "c.time_stamp >= @historyFrom");
            Func<int[], AgentUsageQueryRow> boundedRow = ages =>
            {
                var observed = ages.Select(age => Now.AddDays(-age)).Where(time => time >= historyStart && time < Now).ToList();
                return new AgentUsageQueryRow
                {
                    AgentId = 1, Name = "Contoso Agent", Users = 5,
                    FirstUsedUtc = observed.Min(), LastUsedUtc = observed.Max(),
                };
            };
            var censored = boundedRow(new[] { 365, 95 });
            Assert.AreEqual(Now.AddDays(-95), censored.FirstUsedUtc, "The scorer receives the bounded SQL projection, not the actual first use.");
            Assert.AreEqual(AgentHealth.Retire, CopilotAdoptionScoring.ScoreAgent(censored, Now).Health);
            foreach (var days in new[] { 90, 95, 118, 119, 120 })
                CollectionAssert.Contains(With(s => s.AgentNewDays = days).Validate().ToList(), "agentSettingsInvalid");
            Assert.AreEqual(AgentHealth.Retire, CopilotAdoptionScoring.ScoreAgent(censored, Now, Apply(With(s => s.AgentNewDays = 89))).Health);

            foreach (var days in new[] { 119, 120 })
                CollectionAssert.Contains(With(s => { s.AgentNewDays = days; s.AgentRetireInactiveDays = 120; }).Validate().ToList(), "agentSettingsInvalid");
            var nearest = With(s => { s.AgentNewDays = 118; s.AgentRetireInactiveDays = 120; });
            Assert.AreEqual(0, nearest.Validate().Count);
            var options = Apply(nearest);
            Assert.AreEqual(AgentHealth.Review, CopilotAdoptionScoring.ScoreAgent(boundedRow(new[] { 365, 119 }), Now, options).Health);
            Assert.AreEqual(AgentHealth.New, CopilotAdoptionScoring.ScoreAgent(boundedRow(new[] { 118 }), Now, options).Health);
        }

        [TestMethod]
        public void ExtendedTargets_ChangeActualEngagementScoring()
        {
            var row = Row(20, 4, 2);
            var baseline = CopilotAdoptionScoring.Score(row, WindowStart, Now, true);
            foreach (var edit in new Action<CopilotAdoptionScoreSettings>[]
            {
                s => s.FrequencyTargetRatio = 0.3, s => s.WorkingDaysPerWeek = 4,
                s => s.DepthTargetInteractionsPerActiveDay = 10, s => s.DepthMinActiveDays = 8,
                s => s.BreadthTargetApps = 6,
            })
                Assert.AreNotEqual(baseline.AdoptionScore,
                    CopilotAdoptionScoring.Score(row, WindowStart, Now, true, Apply(With(edit))).AdoptionScore);
        }

        [TestMethod]
        public void ExtendedDecisionSettings_ChangeHabit_Licence_Cowork_Agent_AndReclaimResults()
        {
            var custom = Apply(PhaseTwoSettings());
            Assert.AreEqual("Moderate", CopilotAdoptionScoring.HabitBucketFor(6));
            Assert.AreEqual("Infrequent", CopilotAdoptionScoring.HabitBucketFor(6, custom));
            var demand = new UnlicensedUserSignalRow { UnlicensedCopilotInteractions = 20, UnlicensedCopilotActiveDays = 3 };
            Assert.IsTrue(CopilotAdoptionScoring.ScoreOpportunity(demand).Recommended);
            Assert.IsFalse(CopilotAdoptionScoring.ScoreOpportunity(demand, custom).Recommended);
            Assert.AreEqual(40d, CopilotAdoptionScoring.ScoreOpportunity(demand, custom).OpportunityScore);
            var workload = new UnlicensedUserSignalRow { TeamsMessages = 60, EmailsSent = 80 };
            Assert.IsFalse(CopilotAdoptionScoring.ScoreOpportunity(workload).Recommended);
            Assert.IsTrue(CopilotAdoptionScoring.ScoreOpportunity(workload, Apply(With(s => s.OpportunityRecommendScore = 40))).Recommended);
            var regular = new CoworkReadinessSignalRow { CoworkInteractions = 3, CoworkActiveDays = 3 };
            Assert.IsTrue(CopilotAdoptionScoring.ScoreCoworkReadiness(regular).RegularCoworkUser);
            Assert.IsFalse(CopilotAdoptionScoring.ScoreCoworkReadiness(regular, custom).RegularCoworkUser);
            var fluent = new CoworkReadinessSignalRow { AdoptionScore = 60, TeamsMessages = 50, TeamsMeetings = 5, EmailsSent = 80, FilesViewedOrEdited = 30 };
            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.PrimeCandidate, CopilotAdoptionScoring.ScoreCoworkReadiness(fluent).Tier);
            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.BuildFluencyFirst, CopilotAdoptionScoring.ScoreCoworkReadiness(fluent, custom).Tier);
            var collaboration = new CoworkReadinessSignalRow { TeamsMessages = 50 };
            Assert.AreEqual(20d, CopilotAdoptionScoring.ScoreCoworkReadiness(collaboration).CoordinationLoadScore);
            Assert.AreEqual(25d, CopilotAdoptionScoring.ScoreCoworkReadiness(collaboration, custom).CoordinationLoadScore);
            Assert.AreEqual(AgentHealth.Review, CopilotAdoptionScoring.AgentHealthFor(200, 35, 5));
            Assert.AreEqual(AgentHealth.Keep, CopilotAdoptionScoring.AgentHealthFor(200, 35, 5, custom));
            Assert.AreEqual(AgentHealth.Retire, CopilotAdoptionScoring.AgentHealthFor(200, 95, 5));
            Assert.AreEqual(AgentHealth.Review, CopilotAdoptionScoring.AgentHealthFor(200, 95, 5, custom));
            Assert.AreEqual(AgentHealth.New, CopilotAdoptionScoring.AgentHealthFor(35, 1, 5, custom));
            Assert.AreEqual(AgentHealth.Keep, CopilotAdoptionScoring.AgentHealthFor(200, 1, 3));
            Assert.AreEqual(AgentHealth.Review, CopilotAdoptionScoring.AgentHealthFor(200, 1, 3, custom));
            var starter = Row(0, 0, 0);
            starter.AccountCreatedUtc = Now.AddDays(-35);
            Assert.IsFalse(CopilotAdoptionScoring.IsTooNewToJudge(starter, Now, CopilotAdoptionOptions.Default));
            Assert.IsTrue(CopilotAdoptionScoring.IsTooNewToJudge(starter, Now, custom));
        }

        [TestMethod]
        public async Task ExtendedStore_LegacyDefaults_AllFieldsAudited_CacheKeysAndReset()
        {
            var values = new InMemoryKeyValueStore();
            await values.SetStringAsync(CopilotAdoptionScoreSettingsStore.DocumentKey,
                "{\"schemaVersion\":1,\"version\":1,\"settings\":{\"championScore\":80}}");
            var store = new CopilotAdoptionScoreSettingsStore(values, true);
            var legacy = await store.GetAsync();
            Assert.AreEqual(0.6, legacy.Settings.FrequencyTargetRatio);
            Assert.AreEqual(5, legacy.Settings.MinSeatsPerSegment);
            var saved = await store.SaveAsync(PhaseTwoSettings(), 1, "Contoso Admin");
            Assert.AreEqual(28, saved.History[0].Changes.Count, "27 new settings plus the old Champion override reset to default.");
            Assert.AreEqual(0.75, saved.History[0].Changes.Single(c => c.Field == "frequencyTargetRatio").NewValue);
            Assert.AreNotEqual(legacy.ToEffective().CacheKeySuffix, saved.ToEffective().CacheKeySuffix);
            var remote = await new CopilotAdoptionScoreSettingsStore(values, true).GetAsync();
            Assert.IsTrue(saved.Settings.Equals(remote.Settings));
            var reset = await store.ResetAsync(2, "Contoso Admin");
            Assert.IsTrue(reset.Settings.IsDefault);
            Assert.AreEqual(27, reset.History[0].Changes.Count);
            Assert.AreEqual(string.Empty, reset.ToEffective().CacheKeySuffix);
            var workbook = WorkbookText(CopilotAdoptionWorkbook.Build(Analysis(saved.ToEffective())));
            StringAssert.Contains(workbook, "Tenant setting: Frequency Target Ratio");
            StringAssert.Contains(workbook, "CUSTOMISED. Built-in default: 0.6");
            StringAssert.Contains(workbook, "Tenant setting: Min Seats Per Segment");
        }

        [TestMethod]
        public void Defaults_MatchTheBuiltInOptionsExactly()
        {
            var builtIn = CopilotAdoptionOptions.Default;
            var applied = CopilotAdoptionOptions.Default;
            CopilotAdoptionScoreSettings.Defaults.ApplyTo(applied);

            Assert.AreEqual(builtIn.FrequencyWeight, applied.FrequencyWeight);
            Assert.AreEqual(builtIn.DepthWeight, applied.DepthWeight);
            Assert.AreEqual(builtIn.BreadthWeight, applied.BreadthWeight);
            Assert.AreEqual(builtIn.DevelopingScore, applied.DevelopingScore);
            Assert.AreEqual(builtIn.EstablishedScore, applied.EstablishedScore);
            Assert.AreEqual(builtIn.ChampionScore, applied.ChampionScore);
            Assert.IsTrue(CopilotAdoptionScoreSettings.Defaults.IsDefault);
            Assert.AreEqual(0, CopilotAdoptionScoreSettings.Defaults.Validate().Count);
            Assert.IsFalse(builtIn.ScoreSettings.Customised, "The built-in options are not customised.");
        }

        [TestMethod]
        public void DefaultSettings_ScoreEveryUserExactlyAsBefore()
        {
            var applied = CopilotAdoptionOptions.Default;
            CopilotAdoptionEffectiveScoreSettings.Defaults.ApplyTo(applied);

            foreach (var (interactions, days, apps) in new[] { (0L, 0, 0), (3L, 1, 1), (40L, 8, 2), (60L, 12, 3), (120L, 20, 4), (7L, 3, 2) })
            {
                var before = CopilotAdoptionScoring.Score(Row(interactions, days, apps), WindowStart, Now, true);
                var after = CopilotAdoptionScoring.Score(Row(interactions, days, apps), WindowStart, Now, true, applied);
                Assert.AreEqual(before.AdoptionScore, after.AdoptionScore);
                Assert.AreEqual(before.Band, after.Band);
            }
        }

        [TestMethod]
        public void Validate_ReportsEveryRuleThatIsBroken()
        {
            CollectionAssert.Contains(With(s => s.FrequencyWeightPercent = -1, s => s.DepthWeightPercent = 81).Validate().ToList(),
                CopilotAdoptionScoreSettingsErrorCodes.WeightOutOfRange);
            CollectionAssert.Contains(With(s => s.FrequencyWeightPercent = 101).Validate().ToList(),
                CopilotAdoptionScoreSettingsErrorCodes.WeightOutOfRange);
            CollectionAssert.AreEqual(new[] { CopilotAdoptionScoreSettingsErrorCodes.WeightsMustTotal100 },
                With(s => s.FrequencyWeightPercent = 60).Validate().ToList());
            CollectionAssert.AreEqual(new[] { CopilotAdoptionScoreSettingsErrorCodes.ThresholdOutOfRange },
                With(s => s.DevelopingScore = 0).Validate().ToList());
            CollectionAssert.AreEqual(new[] { CopilotAdoptionScoreSettingsErrorCodes.ThresholdOutOfRange },
                With(s => s.ChampionScore = 101).Validate().ToList());
            CollectionAssert.AreEqual(new[] { CopilotAdoptionScoreSettingsErrorCodes.ThresholdsNotAscending },
                With(s => s.EstablishedScore = 75).Validate().ToList());
            CollectionAssert.AreEqual(new[] { CopilotAdoptionScoreSettingsErrorCodes.ThresholdsNotAscending },
                With(s => s.DevelopingScore = 60).Validate().ToList());
            CollectionAssert.AreEquivalent(
                new[] { CopilotAdoptionScoreSettingsErrorCodes.WeightsMustTotal100, CopilotAdoptionScoreSettingsErrorCodes.ThresholdsNotAscending },
                With(s => s.BreadthWeightPercent = 0, s => s.ChampionScore = 40).Validate().ToList());

            // The edges are allowed: a zero weight, and the widest legal band ladder.
            Assert.AreEqual(0, With(s => s.FrequencyWeightPercent = 100, s => s.DepthWeightPercent = 0, s => s.BreadthWeightPercent = 0).Validate().Count);
            Assert.AreEqual(0, With(s => s.DevelopingScore = 1, s => s.EstablishedScore = 2, s => s.ChampionScore = 100).Validate().Count);
        }

        [TestMethod]
        public void CustomisedFields_NameOnlyWhatDiffersFromTheDefaults()
        {
            var custom = With(s => s.ChampionScore = 90, s => s.FrequencyWeightPercent = 40, s => s.BreadthWeightPercent = 30);
            CollectionAssert.AreEquivalent(
                new[] { CopilotAdoptionScoreSettingsFields.ChampionScore, CopilotAdoptionScoreSettingsFields.FrequencyWeightPercent, CopilotAdoptionScoreSettingsFields.BreadthWeightPercent },
                custom.CustomisedFields.ToList());
            Assert.AreEqual(0, CopilotAdoptionScoreSettings.Defaults.CustomisedFields.Count);
        }

        #endregion

        #region Scoring with custom settings

        [TestMethod]
        public void CustomThresholds_MoveUsersBetweenBands()
        {
            var options = Apply(With(s => s.DevelopingScore = 10, s => s.EstablishedScore = 60, s => s.ChampionScore = 90));

            Assert.AreEqual(AdoptionBand.Champion, CopilotAdoptionScoring.BandFor(80, true, false), "By default 80 is a champion.");
            Assert.AreEqual(AdoptionBand.Established, CopilotAdoptionScoring.BandFor(80, true, false, options), "80 is no longer a champion when Champion starts at 90.");
            Assert.AreEqual(AdoptionBand.Champion, CopilotAdoptionScoring.BandFor(90, true, false, options), "The threshold itself is inclusive.");
            Assert.AreEqual(AdoptionBand.Developing, CopilotAdoptionScoring.BandFor(55, true, false, options));
            Assert.AreEqual(AdoptionBand.Developing, CopilotAdoptionScoring.BandFor(10, true, false, options));
            Assert.AreEqual(AdoptionBand.Trialling, CopilotAdoptionScoring.BandFor(9.9, true, false, options));
            Assert.AreEqual(AdoptionBand.Dormant, CopilotAdoptionScoring.BandFor(0, false, true, options), "Dormant and never-used do not depend on the thresholds.");
        }

        [TestMethod]
        public void CustomWeights_ChangeTheEngagementScore()
        {
            // Every working day, one interaction a day, one app: full frequency, low depth and breadth.
            var byDefault = CopilotAdoptionScoring.Score(Row(20, 20, 1), WindowStart, Now, true);
            var frequencyOnly = CopilotAdoptionScoring.Score(Row(20, 20, 1), WindowStart, Now, true,
                Apply(With(s => s.FrequencyWeightPercent = 100, s => s.DepthWeightPercent = 0, s => s.BreadthWeightPercent = 0)));

            Assert.AreEqual(100, frequencyOnly.AdoptionScore, 0.01, "All weight on frequency scores the daily user at 100.");
            Assert.IsTrue(byDefault.AdoptionScore < frequencyOnly.AdoptionScore);
            Assert.AreEqual(AdoptionBand.Champion, frequencyOnly.Band);
        }

        [TestMethod]
        public void EffectiveSettings_StampTheOptionsTheReportCarries()
        {
            var effective = new CopilotAdoptionEffectiveScoreSettings(7, With(s => s.ChampionScore = 90), Now);
            var options = CopilotAdoptionOptions.Default;
            effective.ApplyTo(options);

            Assert.AreEqual(90, options.ChampionScore);
            Assert.AreEqual(7, options.ScoreSettings.Version);
            Assert.IsTrue(options.ScoreSettings.Customised);
            CollectionAssert.AreEqual(new[] { CopilotAdoptionScoreSettingsFields.ChampionScore }, options.ScoreSettings.CustomisedFields.ToList());
            Assert.AreEqual(75, options.ScoreSettings.DefaultValues.ChampionScore);

            var json = JObject.FromObject(options);
            Assert.AreEqual(true, (bool)json["scoreSettings"]["customised"]);
            Assert.AreEqual(75, (int)json["scoreSettings"]["defaults"]["championScore"]);
            Assert.IsNull(json["scoreSettings"]["updatedBy"], "The report is not the place for who changed the settings.");
        }

        #endregion
    }
}
