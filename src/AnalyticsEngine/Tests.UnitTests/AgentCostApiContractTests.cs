using Common.Entities.AgentCosts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins the JSON field names of every model the Agent costs page reads.
    ///
    /// <para><b>Why this test exists.</b> The Web project registers no camelCase contract resolver, so
    /// Newtonsoft serialises a property under its C# name unless an explicit <c>[JsonProperty]</c>
    /// says otherwise. A model written without those attributes therefore returns <c>BilledCredits</c>
    /// where <c>types/agentCosts.ts</c> declares <c>billedCredits</c>. TypeScript cannot catch this - the
    /// response is typed by assertion at the fetch boundary, not validated - so every field silently reads
    /// <c>undefined</c> and the page dies at the first <c>.map</c> with
    /// <c>Uncaught TypeError: Cannot read properties of undefined (reading 'map')</c>.</para>
    ///
    /// <para>The expected names below are copied from <c>Web/Scripts/portal/src/types/agentCosts.ts</c>.
    /// They are written out literally rather than derived by camelCasing the C# property name: a rule that
    /// computes the expectation from the code under test would agree with any renaming on either side and
    /// prove nothing. If a name here stops matching the TypeScript interface, that is the bug this test is
    /// for - fix the model or the .ts file, do not edit this list to match.</para>
    /// </summary>
    [TestClass]
    public class AgentCostApiContractTests
    {
        /// <summary>
        /// Serialises exactly as the Web API does. <c>AgentCostsAPIController</c> returns the model
        /// via <c>Ok(...)</c>, and no resolver is registered anywhere in the Web project, so this is the
        /// default serialiser.
        /// </summary>
        private static ISet<string> KeysOf(object model)
        {
            var json = JsonConvert.SerializeObject(model);
            return new HashSet<string>(JObject.Parse(json).Properties().Select(p => p.Name), StringComparer.Ordinal);
        }

        private static void AssertKeys(object model, params string[] expected)
        {
            var actual = KeysOf(model);
            var wanted = new HashSet<string>(expected, StringComparer.Ordinal);

            var missing = wanted.Except(actual, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var extra = actual.Except(wanted, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.AreEqual(0, missing.Count,
                $"{model.GetType().Name} does not serialise the field(s) the portal reads: {string.Join(", ", missing)}. "
                + "Add [JsonProperty(\"<camelCaseName>\")] - the Web project has no camelCase resolver.");

            Assert.AreEqual(0, extra.Count,
                $"{model.GetType().Name} serialises field(s) the portal does not know about: {string.Join(", ", extra)}. "
                + "Either the [JsonProperty] name is wrong (PascalCase leaking through) or types/agentCosts.ts is out of step.");
        }

        [TestMethod]
        public void Availability_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostAvailability(),
                "copilotStudioCreditsEnabled", "azureCostsEnabled",
                "hasCopilotStudioCreditData", "hasAzureCostData",
                "copilotStudioCreditsHasRunCleanly", "azureCostsHaveRunCleanly",
                "hasPerUserCreditData",
                "copilotStudioCreditsLastImportUtc", "azureCostsLastImportUtc",
                "copilotStudioCreditsLastError", "azureCostsLastError",
                "perUserCreditsLastImportUtc", "perUserCreditsLastError", "capacityLastError",
                "earliestUsageDate", "latestUsageDate",
                "azureDimensionsWithData", "messages");
        }

        [TestMethod]
        public void Summary_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostSummary(),
                "billedCredits", "nonBilledCredits", "distinctAgents", "distinctEnvironments",
                "daysWithUsage", "peakDistinctUsersOnASlice", "unclassifiedHarnessCredits",
                "capacity", "azureCost");
        }

        [TestMethod]
        public void AzureCostByCurrency_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AzureCostByCurrency(), "currency", "cost", "includesEstimates");
        }

        [TestMethod]
        public void CapacitySnapshot_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new CopilotCapacitySnapshot(),
                "snapshotUtc", "consumptionAsOf", "entitled", "consumed", "consumptionType",
                "allocated", "available", "payAsYouGoConsumed", "status");
        }

        [TestMethod]
        public void DailyPoint_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostDailyPoint(), "date", "billedCredits", "nonBilledCredits");
        }

        [TestMethod]
        public void BreakdownRow_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostBreakdownRow(),
                "key", "label", "billedCredits", "nonBilledCredits", "activeDays", "peakDistinctUsers");
        }

        [TestMethod]
        public void DetailRow_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostDetailRow(),
                "usageDate", "environmentId", "environmentName", "agentId", "agentName", "harness",
                "featureName", "channelId", "llmModel", "toolInvoked", "knowledgeSources",
                "billedCredits", "nonBilledCredits", "distinctUsers");
        }

        [TestMethod]
        public void DetailPage_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostDetailPage(), "rows", "totalRows", "page", "pageSize");
        }

        [TestMethod]
        public void AzureBreakdownRow_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AzureCostBreakdownRow(),
                "key", "label", "currency", "cost", "quantity", "includesEstimates");
        }

        [TestMethod]
        public void UserRow_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostUserRow(), "userId", "billedCredits", "activeDays");
        }

        [TestMethod]
        public void FilterOptions_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostFilterOptions(),
                "agents", "environments", "harnesses", "features", "models", "tools",
                "knowledgeSources", "channels");
        }

        [TestMethod]
        public void FilterOption_SerialisesTheFieldNamesThePortalReads()
        {
            AssertKeys(new AgentCostFilterOption(), "id", "label");
        }

        /// <summary>
        /// The failure this whole class guards against, asserted directly: no field may serialise under a
        /// PascalCase name. A single one is enough to break the page, and it is invisible in C#.
        /// </summary>
        [TestMethod]
        public void NoAgentCostModelLeaksAPascalCaseFieldName()
        {
            var models = new object[]
            {
                new AgentCostAvailability(), new AgentCostSummary(), new AzureCostByCurrency(),
                new CopilotCapacitySnapshot(), new AgentCostDailyPoint(), new AgentCostBreakdownRow(),
                new AgentCostDetailRow(), new AgentCostDetailPage(), new AzureCostBreakdownRow(),
                new AgentCostUserRow(), new AgentCostFilterOptions(), new AgentCostFilterOption(),
            };

            foreach (var model in models)
            {
                var offenders = KeysOf(model).Where(k => char.IsUpper(k[0])).ToList();

                Assert.AreEqual(0, offenders.Count,
                    $"{model.GetType().Name} serialises {string.Join(", ", offenders)} in PascalCase. The portal "
                    + "reads camelCase, so these would arrive as undefined and the page would throw at the first .map().");
            }
        }

        /// <summary>
        /// Nested models must carry their names through the parent. Serialising the parent is the only way
        /// to prove it - a child asserted in isolation still passes if the parent renames it.
        /// </summary>
        [TestMethod]
        public void NestedModelsKeepTheirFieldNamesThroughTheParent()
        {
            var summary = new AgentCostSummary
            {
                Capacity = new CopilotCapacitySnapshot { Entitled = 100m },
                AzureCost = { new AzureCostByCurrency { Currency = "GBP", Cost = 1.23m } },
            };

            var json = JObject.Parse(JsonConvert.SerializeObject(summary));

            Assert.IsNotNull(json["capacity"], "AgentCostSummary.Capacity must serialise as 'capacity'.");
            Assert.IsNotNull(json["capacity"]["entitled"], "CopilotCapacitySnapshot must keep camelCase inside the summary.");
            Assert.AreEqual("GBP", (string)json["azureCost"][0]["currency"],
                "AzureCostByCurrency must keep camelCase inside the summary's azureCost array.");

            var page = new AgentCostDetailPage { Rows = { new AgentCostDetailRow { AgentName = "Contoso agent" } } };
            var pageJson = JObject.Parse(JsonConvert.SerializeObject(page));

            Assert.AreEqual("Contoso agent", (string)pageJson["rows"][0]["agentName"],
                "AgentCostDetailRow must keep camelCase inside the detail page's rows array.");

            var options = new AgentCostFilterOptions { Agents = { new AgentCostFilterOption { Id = "a", Label = "A" } } };
            var optionsJson = JObject.Parse(JsonConvert.SerializeObject(options));

            Assert.AreEqual("a", (string)optionsJson["agents"][0]["id"],
                "AgentCostFilterOption must keep camelCase inside the filter options.");
        }
    }
}
