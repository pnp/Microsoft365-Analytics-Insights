using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// What the Teams Explorer tells an admin when a data source is missing, and the JSON shape the
    /// SPA is typed against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The availability model is the whole reason an empty tab is useful rather than mystifying, so
    /// every combination that produces a different message is pinned here.
    /// </para>
    /// <para>
    /// The casing assertions exist because a camel-case mismatch between a C# model and the SPA's
    /// TypeScript types compiles cleanly on both sides and only fails in a browser, as
    /// <c>undefined</c> where a number should be. That has already happened once in this codebase
    /// (see <c>DlpApiContractTests</c>), and these models are serialised with a naming strategy
    /// attribute that is easy to forget on a new class.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TeamsExplorerAvailabilityTests
    {
        private static TeamsExplorerSources AllOn()
        {
            return new TeamsExplorerSources
            {
                UsageReports = true,
                Calls = true,
                TeamsAnalytics = true,
                Cognitive = true,
                UserMetadata = true,
                ServiceBus = true,
            };
        }

        [TestMethod]
        public void EverythingOnAndAuthorisedProducesNoComplaints()
        {
            var model = TeamsExplorerAvailability.Build(AllOn(), authorisedTeams: 4, totalTeams: 10);

            Assert.IsTrue(model.Available);
            Assert.AreEqual(0, model.Reasons.Count, string.Join(" | ", model.Reasons));
            Assert.AreEqual(4, model.AuthorisedTeams);
            Assert.AreEqual(10, model.TotalTeams);
        }

        [TestMethod]
        public void NothingOnIsReportedAsUnavailableWithAReasonPerSource()
        {
            var model = TeamsExplorerAvailability.Build(new TeamsExplorerSources());

            Assert.IsFalse(model.Available);
            Assert.AreEqual(5, model.Reasons.Count,
                "Expected one reason each for usage reports, calls, Teams analytics, cognitive and user metadata.");
        }

        [TestMethod]
        public void CognitiveOnItsOwnDoesNotMakeThePageWorthShowing()
        {
            // Cognitive enrichment decorates Teams data; it never produces any on its own.
            var sources = new TeamsExplorerSources { Cognitive = true };
            var model = TeamsExplorerAvailability.Build(sources);

            Assert.IsFalse(model.Available);
        }

        [TestMethod]
        public void EveryReasonNamesTheSwitchThatFixesIt()
        {
            var model = TeamsExplorerAvailability.Build(new TeamsExplorerSources());

            foreach (var reason in model.Reasons)
            {
                Assert.IsTrue(
                    reason.Contains("installer") || reason.Contains("CognitiveEndpoint"),
                    $"A reason that does not say what to switch on is not actionable: '{reason}'");
            }
        }

        [TestMethod]
        public void CallsWithoutServiceBusIsCalledOutSpecifically()
        {
            // The failure mode this catches is genuinely confusing in the field: the import looks
            // enabled, but the webhook endpoint answers 503 and no call ever arrives.
            var sources = AllOn();
            sources.ServiceBus = false;

            var model = TeamsExplorerAvailability.Build(sources, 1, 1);
            var reason = model.Reasons.Single();

            StringAssert.Contains(reason, "Service Bus");
            StringAssert.Contains(reason, "503");
        }

        [TestMethod]
        public void NoAuthorisedTeamIsDistinguishedFromNoTeamsAtAll()
        {
            var noTeams = TeamsExplorerAvailability.Build(AllOn(), authorisedTeams: 0, totalTeams: 0);
            StringAssert.Contains(noTeams.Reasons.Single(), "no teams have been discovered");

            var noneAuthorised = TeamsExplorerAvailability.Build(AllOn(), authorisedTeams: 0, totalTeams: 12);
            StringAssert.Contains(noneAuthorised.Reasons.Single(), "authorised");
            StringAssert.Contains(noneAuthorised.Reasons.Single(), "Teams permissions");
        }

        [TestMethod]
        public void UnknownTeamCountsAreNotReportedAsZeroAuthorised()
        {
            // The store returns null when the count query failed. Telling an admin "no teams are
            // authorised" on the strength of a failed query would send them to re-authorise teams
            // that are already fine.
            var model = TeamsExplorerAvailability.Build(AllOn(), authorisedTeams: null, totalTeams: null);

            Assert.AreEqual(0, model.Reasons.Count,
                "A failed count must not manufacture an authorisation complaint.");
        }

        #region JSON contract

        [TestMethod]
        public void AvailabilitySerialisesInTheCasingTheSpaExpects()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(
                TeamsExplorerAvailability.Build(AllOn(), 1, 2)));

            foreach (var property in new[]
            {
                "usageReportsAvailable", "callsAvailable", "teamsAnalyticsAvailable",
                "cognitiveAvailable", "userMetadataAvailable", "authorisedTeams", "totalTeams",
                "available", "reasons",
            })
            {
                Assert.IsNotNull(json[property], $"api/TeamsExplorer/availability is missing '{property}'.");
            }
        }

        [TestMethod]
        public void SectionModelsSerialiseInTheCasingTheSpaExpects()
        {
            AssertCamelCase(new TeamsOverview(), "window", "queries", "kpis", "trend", "segmentMix", "judgements");
            AssertCamelCase(new TeamsAdoption(), "groupBy", "rhythm", "segmentTrend", "breakdown", "devices", "lifecycle");
            AssertCamelCase(new TeamsMeetings(), "workingDayStartHour", "kpis", "heatmap", "periodOfDay", "modalityMix", "quality");
            AssertCamelCase(new TeamsCollaboration(), "kpis", "teams", "channels", "ownerlessTeams", "dormantTeams", "tabUsage");
            AssertCamelCase(new TeamsConversations(), "cognitiveAvailable", "scoredChannelDays", "keywords", "sentimentTrend");
            AssertCamelCase(new TeamsPeople(), "namesObfuscated", "champions", "dormant", "championsByDepartment");
        }

        [TestMethod]
        public void RowModelsSerialiseInTheCasingTheSpaExpects()
        {
            AssertCamelCase(new TeamsPersonRow(), "userPrincipalName", "activeDays", "channelMessages", "callsHosted", "segment");
            AssertCamelCase(new TeamsTeamRow(), "name", "members", "owners", "sentiment", "authorised");
            AssertCamelCase(new TeamsHeatCell(), "dayOfWeek", "hour", "calls");
            AssertCamelCase(new TeamsExplorerQueryInfo(), "key", "sql", "error", "elapsedMs");
            AssertCamelCase(new TeamsDemographicRow(), "knownUsers", "activeUsers", "reachPct", "messagesPerActiveUser");
        }

        private static void AssertCamelCase(object model, params string[] expected)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(model));

            foreach (var property in expected)
            {
                Assert.IsNotNull(
                    json[property],
                    $"{model.GetType().Name} is missing '{property}'. Available: "
                    + string.Join(", ", json.Properties().Select(p => p.Name)));
            }
        }

        #endregion
    }
}
