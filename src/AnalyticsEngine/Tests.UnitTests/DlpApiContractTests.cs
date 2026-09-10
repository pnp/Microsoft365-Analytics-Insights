extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DlpAvailability = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpAvailability;
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
        /// <summary>
        /// Every public property on a model returned by the DLP API must declare its wire name, and
        /// that name must be camelCase. A missing attribute is the exact defect described above.
        /// </summary>
        [TestMethod]
        public void EveryDlpApiModelPropertyDeclaresACamelCaseWireName()
        {
            foreach (var type in new[] { typeof(DlpAvailability), typeof(DlpImpactRow), typeof(DlpTrendPoint), typeof(DlpSummary) })
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
        /// The TypeScript interfaces are the other half of this contract, so they are read here too -
        /// a field renamed in C# without the matching rename in dlp.ts fails the build.
        /// </summary>
        [TestMethod]
        public void TheTypeScriptTypesDeclareTheSameFieldNames()
        {
            var typings = DlpTypeScriptSource();

            var expected = typeof(DlpSummary).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName)
                .Where(n => !string.IsNullOrEmpty(n));

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
