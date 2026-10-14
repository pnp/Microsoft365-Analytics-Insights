extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DlpAvailability = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpAvailability;
using DlpGovernanceLabelShare = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceLabelShare;
using DlpGovernanceMixRow = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceMixRow;
using DlpGovernanceRate = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceRate;
using DlpGovernanceSummary = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceSummary;
using DlpImpactRow = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpImpactRow;
using DlpSummary = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpSummary;
using DlpTrendPoint = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpTrendPoint;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins the JSON contract of the DLP API models to what the SPA actually reads.
    /// </summary>
    /// <remarks>
    /// This exists because of a real bug: the models originally carried no
    /// <see cref="JsonPropertyAttribute"/> at all. This web application configures NO camelCase
    /// contract resolver - every API model names its own wire fields explicitly - so the DLP models
    /// serialised in PascalCase, every field the TypeScript read came back <c>undefined</c>, and the
    /// page died on <c>Cannot read properties of undefined (reading 'map')</c>.
    ///
    /// The failure is invisible from C#: the controller, the SQL and the models were all correct in
    /// isolation, and nothing except loading the page in a browser would have caught it. So the
    /// contract is asserted here instead - a property added without a JsonProperty name fails the
    /// build rather than the page.
    /// </remarks>
    [TestClass]
    public class DlpApiContractTests
    {
        /// <summary>Every model the DLP API returns, including the governance section's (#648).</summary>
        private static readonly System.Type[] ApiModelTypes =
        {
            typeof(DlpAvailability), typeof(DlpImpactRow), typeof(DlpTrendPoint), typeof(DlpSummary),
            typeof(DlpGovernanceSummary), typeof(DlpGovernanceRate), typeof(DlpGovernanceLabelShare), typeof(DlpGovernanceMixRow),
        };
        /// <summary>
        /// Every public property on a model returned by the DLP API must declare its wire name, and
        /// that name must be camelCase. A missing attribute is the exact defect described above.
        /// </summary>
        [TestMethod]
        public void EveryDlpApiModelPropertyDeclaresACamelCaseWireName()
        {
            foreach (var type in ApiModelTypes)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var attribute = property.GetCustomAttribute<JsonPropertyAttribute>();

                    Assert.IsNotNull(attribute,
                        $"{type.Name}.{property.Name} has no [JsonProperty]. This app has no camelCase contract "
                        + "resolver, so it would serialise in PascalCase and the SPA would read undefined.");

                    Assert.IsFalse(string.IsNullOrWhiteSpace(attribute.PropertyName),
                        $"{type.Name}.{property.Name} has an empty [JsonProperty] name.");

                    Assert.IsTrue(char.IsLower(attribute.PropertyName[0]),
                        $"{type.Name}.{property.Name} serialises as '{attribute.PropertyName}', which is not camelCase.");
                }
            }
        }

        /// <summary>
        /// The exact field names the SPA's <c>types/dlp.ts</c> reads. Asserted against real serialised
        /// JSON so a rename on either side is caught here rather than in a browser.
        /// </summary>
        [TestMethod]
        public void SerialisedAvailabilityAndSummaryCarryTheFieldsTheSpaReads()
        {
            var availability = JObject.Parse(JsonConvert.SerializeObject(new DlpAvailability
            {
                CopilotDlpAvailable = true,
                Reasons = new List<string> { "because" },
            }));

            foreach (var field in new[] { "copilotDlpAvailable", "tenantDlpAvailable", "available", "reasons" })
            {
                Assert.IsNotNull(availability[field], $"api/Dlp/availability must expose '{field}'.");
            }

            // The one that actually broke: the page does availability.reasons.map(...), so this must be
            // a JSON array and never absent.
            Assert.AreEqual(JTokenType.Array, availability["reasons"].Type);
            Assert.IsTrue((bool)availability["available"], "Available is computed and must still be serialised.");

            var summary = JObject.Parse(JsonConvert.SerializeObject(new DlpSummary()));

            var scalars = new[]
            {
                "fromUtc", "toUtc", "copilotBlockedCount", "copilotAuditedCount", "usersImpacted",
                "agentsImpacted", "policiesInvolved", "tenantBlockedCount", "tenantAuditedCount",
            };
            foreach (var field in scalars)
            {
                Assert.IsNotNull(summary[field], $"api/Dlp/summary must expose '{field}'.");
            }

            // Every collection the page calls .map() on must serialise as an array on a default-constructed
            // summary - i.e. an empty period yields [] rather than null.
            var collections = new[] { "topAgents", "topUsers", "topPolicies", "topSensitivityLabels", "trend", "tenantTopPolicies" };
            foreach (var field in collections)
            {
                Assert.IsNotNull(summary[field], $"api/Dlp/summary must expose '{field}'.");
                Assert.AreEqual(JTokenType.Array, summary[field].Type,
                    $"'{field}' is rendered with .map() and must always be an array.");
            }
        }

        /// <summary>A populated row keeps its wire names, including the computed total.</summary>
        [TestMethod]
        public void ImpactRowSerialisesTheFieldsTheTableRenders()
        {
            var row = JObject.Parse(JsonConvert.SerializeObject(new DlpImpactRow
            {
                Id = "policy-1",
                Name = "Contoso policy",
                BlockedCount = 3,
                AuditedCount = 2,
                UsersAffected = 4,
            }));

            Assert.AreEqual("policy-1", (string)row["id"]);
            Assert.AreEqual("Contoso policy", (string)row["name"]);
            Assert.AreEqual(3, (int)row["blockedCount"]);
            Assert.AreEqual(2, (int)row["auditedCount"]);
            Assert.AreEqual(4, (int)row["usersAffected"]);
            Assert.AreEqual(5, (int)row["totalCount"]);
        }

        /// <summary>
        /// The governance section's wire names (#648), asserted on real serialised JSON. Every rate travels with
        /// its numerator and denominator, and the lists the page calls .map() on are arrays even when empty.
        /// </summary>
        [TestMethod]
        public void SerialisedGovernanceCarriesTheFieldsTheSpaReads()
        {
            var governance = JObject.Parse(JsonConvert.SerializeObject(new DlpGovernanceSummary
            {
                Interactions = 20000,
                Jailbreak = new DlpGovernanceRate { FlaggedInteractions = 3, ReportedInteractions = 12000 },
                Xpia = new DlpGovernanceRate { FlaggedInteractions = 1, ReportedInteractions = 8000 },
                SensitivityLabels = new DlpGovernanceLabelShare { LabelledResources = 25, Resources = 100, InteractionsWithResources = 60 },
                InteractionsWithModel = 900,
                Models = new List<DlpGovernanceMixRow> { new DlpGovernanceMixRow { Name = "DEEP_LEO", Interactions = 600, Share = 0.03 } },
                InteractionsWithPlugin = 2800,
            }));

            foreach (var field in new[] { "fromUtc", "toUtc", "interactions", "jailbreak", "xpia", "sensitivityLabels", "interactionsWithModel", "models", "interactionsWithPlugin", "plugins" })
            {
                Assert.IsNotNull(governance[field], $"api/Dlp/governance must expose '{field}'.");
            }

            foreach (var rate in new[] { "jailbreak", "xpia" })
            {
                foreach (var field in new[] { "flaggedInteractions", "reportedInteractions", "ratePer10000" })
                {
                    Assert.IsNotNull(governance[rate][field], $"'{rate}' must carry '{field}': a rate is never shown without its denominator.");
                }
            }

            Assert.AreEqual(2.5, (double)governance["jailbreak"]["ratePer10000"], 1e-9);
            Assert.AreEqual(0.25, (double)governance["sensitivityLabels"]["share"], 1e-9);
            Assert.AreEqual(100, (long)governance["sensitivityLabels"]["resources"]);
            Assert.AreEqual(60, (long)governance["sensitivityLabels"]["interactionsWithResources"]);
            Assert.AreEqual("DEEP_LEO", (string)governance["models"][0]["name"]);
            Assert.AreEqual(600, (long)governance["models"][0]["interactions"]);
            Assert.AreEqual(0.03, (double)governance["models"][0]["share"], 1e-9);
            Assert.AreEqual(JTokenType.Array, governance["plugins"].Type, "An empty list must still be an array.");
        }

        /// <summary>
        /// The TypeScript interfaces are the other half of this contract, so they are read here too -
        /// a field renamed in C# without the matching rename in dlp.ts fails the build.
        /// </summary>
        [TestMethod]
        public void TheTypeScriptTypesDeclareTheSameFieldNames()
        {
            var typings = DlpTypeScriptSource();

            var expected = new[] { typeof(DlpSummary), typeof(DlpGovernanceSummary), typeof(DlpGovernanceRate), typeof(DlpGovernanceLabelShare), typeof(DlpGovernanceMixRow) }
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Select(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct();

            foreach (var field in expected)
            {
                StringAssert.Contains(typings, field + ":",
                    $"types/dlp.ts does not declare '{field}', so the SPA cannot read it.");
            }
        }

        private static string DlpTypeScriptSource()
        {
            // Walk up from the test binaries to the repo's Web project.
            var directory = new System.IO.DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !System.IO.Directory.Exists(System.IO.Path.Combine(directory.FullName, "Web")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Could not locate the solution directory from the test output folder.");

            var path = System.IO.Path.Combine(directory.FullName, "Web", "Scripts", "portal", "src", "types", "dlp.ts");
            Assert.IsTrue(System.IO.File.Exists(path), "Expected the SPA's DLP typings at " + path);
            return System.IO.File.ReadAllText(path);
        }
    }
}
