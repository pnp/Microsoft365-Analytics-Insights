extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using DlpGovernanceLabelShare = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceLabelShare;
using DlpGovernanceMaths = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceMaths;
using DlpGovernanceMixRow = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceMixRow;
using DlpGovernanceRate = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceRate;
using DlpGovernanceSummary = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceSummary;

namespace Tests.UnitTests
{
    /// <summary>
    /// The DLP page's governance section (#648): the rate arithmetic and the "not reported is not clean" rule,
    /// without a database. <see cref="DlpApiSqlIntegrationTests"/> runs the SQL that feeds these.
    /// </summary>
    [TestClass]
    public class DlpGovernanceTests
    {
        [TestMethod]
        public void Rate_IsFlaggedInteractionsPerTenThousandReportedOnes()
        {
            Assert.AreEqual(2.5, DlpGovernanceMaths.PerTenThousand(3, 12000).Value, 1e-9);
            Assert.AreEqual(10000.0, DlpGovernanceMaths.PerTenThousand(4, 4).Value, 1e-9, "Every reported interaction flagged.");
        }

        [TestMethod]
        public void Rate_IsUnknown_NotZero_WhenTheFlagWasReportedOnNothing()
        {
            Assert.IsNull(DlpGovernanceMaths.PerTenThousand(0, 0),
                "No interaction reported the flag, so the rate is unknown. 0 would claim a clean bill of health.");

            var rate = new DlpGovernanceRate { FlaggedInteractions = 0, ReportedInteractions = 0 };
            Assert.IsNull(rate.RatePer10000);
        }

        [TestMethod]
        public void Rate_IsAGenuineZero_WhenTheFlagWasReportedAndNeverRaised()
        {
            var rate = new DlpGovernanceRate { FlaggedInteractions = 0, ReportedInteractions = 5000 };
            Assert.AreEqual(0.0, rate.RatePer10000.Value, 1e-9);
        }

        /// <summary>
        /// The rule the denominator exists for. 10,000 interactions, 6,000 of which reported the flag, 3 of those
        /// flagged: the rate is 5 per 10,000. Treating the 4,000 unreported ones as clean would report 3.
        /// </summary>
        [TestMethod]
        public void Rate_UsesTheReportedInteractionsAsItsDenominator_NotEveryInteraction()
        {
            var rate = new DlpGovernanceRate { FlaggedInteractions = 3, ReportedInteractions = 6000 };

            Assert.AreEqual(5.0, rate.RatePer10000.Value, 1e-9);
            Assert.AreNotEqual(DlpGovernanceMaths.PerTenThousand(3, 10000).Value, rate.RatePer10000.Value,
                "Counting an unreported flag as 'no attack' would dilute the rate.");
        }

        [TestMethod]
        public void LabelShare_IsLabelledOverEveryResourceUsed_AndUnknownWhenCopilotUsedNone()
        {
            Assert.AreEqual(0.25, new DlpGovernanceLabelShare { LabelledResources = 25, Resources = 100 }.Share.Value, 1e-9);
            Assert.IsNull(new DlpGovernanceLabelShare { LabelledResources = 0, Resources = 0 }.Share,
                "No resource used means no share, not a share of 0.");
            Assert.AreEqual(0.0, new DlpGovernanceLabelShare { LabelledResources = 0, Resources = 40 }.Share.Value, 1e-9);
        }

        [TestMethod]
        public void Mix_RanksMostUsedFirst_BreaksTiesByName_AndListsAtMostTen()
        {
            var rows = Enumerable.Range(1, 12)
                .Select(i => new DlpGovernanceMixRow { Name = "contoso-model-" + i.ToString("00"), Interactions = i % 4 + 1 })
                .ToList();

            var mix = DlpGovernanceMaths.Mix(rows, interactions: 1000);

            Assert.AreEqual(DlpGovernanceMaths.TopMix, mix.Count);
            CollectionAssert.AreEqual(
                new[] { "contoso-model-03", "contoso-model-07", "contoso-model-11", "contoso-model-02", "contoso-model-06" },
                mix.Take(5).Select(r => r.Name).ToArray(),
                "Most interactions first; equal counts in name order, so the list does not shuffle between loads.");
        }

        [TestMethod]
        public void Mix_SharesAreOfEveryInteractionInTheWindow_NotOfTheListedOnes()
        {
            var mix = DlpGovernanceMaths.Mix(
                new[]
                {
                    new DlpGovernanceMixRow { Name = "BingWebSearch", Interactions = 150 },
                    new DlpGovernanceMixRow { Name = "Contoso.Tickets", Interactions = 50 },
                },
                interactions: 1000);

            Assert.AreEqual(0.15, mix[0].Share.Value, 1e-9);
            Assert.AreEqual(0.05, mix[1].Share.Value, 1e-9,
                "Shares do not add up to 1: most interactions use no plugin, and one interaction can use several.");
        }

        [TestMethod]
        public void Mix_ShareIsUnknown_WhenTheWindowHasNoInteractions()
        {
            var mix = DlpGovernanceMaths.Mix(new[] { new DlpGovernanceMixRow { Name = "DEEP_LEO", Interactions = 1 } }, interactions: 0);
            Assert.IsNull(mix.Single().Share);
        }

        [TestMethod]
        public void Mix_KeepsMicrosoftsNamesExactly_AndDropsRowsWithNoNameOrNoUse()
        {
            var mix = DlpGovernanceMaths.Mix(
                new[]
                {
                    new DlpGovernanceMixRow { Name = "DEEP_LEO", Interactions = 7 },
                    new DlpGovernanceMixRow { Name = "Καλημέρα κόσμε", Interactions = 3 },
                    new DlpGovernanceMixRow { Name = null, Interactions = 40 },
                    new DlpGovernanceMixRow { Name = "  ", Interactions = 40 },
                    new DlpGovernanceMixRow { Name = "contoso-unused", Interactions = 0 },
                    null,
                },
                interactions: 100);

            CollectionAssert.AreEqual(new[] { "DEEP_LEO", "Καλημέρα κόσμε" }, mix.Select(r => r.Name).ToArray(),
                "Names come from Microsoft's payload and are shown as reported - never rewritten or translated.");
            Assert.AreEqual(0, DlpGovernanceMaths.Mix(null, 100).Count);
        }

        /// <summary>The SPA tells "unknown" from "zero" by a JSON null, so an unknown rate must serialise as one.</summary>
        [TestMethod]
        public void UnknownRatesAndShares_SerialiseAsNull_NeverAsZero()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(new DlpGovernanceSummary()));

            Assert.AreEqual(JTokenType.Null, json["jailbreak"]["ratePer10000"].Type);
            Assert.AreEqual(JTokenType.Null, json["xpia"]["ratePer10000"].Type);
            Assert.AreEqual(JTokenType.Null, json["sensitivityLabels"]["share"].Type);
            Assert.AreEqual(0, (long)json["jailbreak"]["reportedInteractions"]);
        }
    }
}
