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
