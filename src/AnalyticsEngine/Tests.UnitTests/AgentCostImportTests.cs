using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataUtils;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.AgentCosts;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the agent-cost imports: billed Copilot Studio credits from the Power Platform licensing
    /// API, and daily Azure spend from Microsoft Cost Management.
    ///
    /// These are pure/in-memory - no HTTP, no SQL - because the risky parts of this feature are the parsing
    /// of two only-partly-documented response shapes and the arithmetic that turns them into upsertable rows.
    /// </summary>
    [TestClass]
    public class AgentCostImportTests
    {
        private static ILogger Logger => NullLogger.Instance;

        // A deliberately non-Latin agent name. Agent display names come from a customer tenant and are
        // Unicode; the columns that store them are nvarchar, and nothing in the pipeline may assume ASCII.
        private const string GreekAgentName = "Καλημέρα κόσμε";

        #region Harness classification

        [TestMethod]
        public void HarnessClassifier_ProcessAgent_IsTheGitHubCopilotHarness()
        {
            Assert.AreEqual(CopilotStudioHarness.GitHubCopilot,
                CopilotStudioHarnessClassifier.Classify("Process Agent"));

            Assert.AreEqual(CopilotStudioHarness.GitHubCopilot,
                CopilotStudioHarnessClassifier.Classify("  process agent  "),
                "Matching must ignore case and surrounding whitespace - the API's casing is not ours to rely on.");
        }

        [TestMethod]
        public void HarnessClassifier_KnownBillingFeatures_AreStandardOrCopilotChat()
        {
            foreach (var feature in new[] { "Generative answer", "Tenant graph grounding", "Agent action", "Classic answer" })
            {
                Assert.AreEqual(CopilotStudioHarness.StandardOrCopilotChat,
                    CopilotStudioHarnessClassifier.Classify(feature),
                    $"'{feature}' is emitted by both the Standard and Copilot Chat harnesses, so it can only narrow to the pair.");
            }
        }

        [TestMethod]
        public void HarnessClassifier_UnrecognisedFeature_StaysUnknownRatherThanBeingGuessed()
        {
            // The whole point of the Unknown bucket: if Microsoft adds or renames a feature, the spend must
            // show up as visibly unclassified rather than be absorbed into a harness it may not belong to.
            Assert.AreEqual(CopilotStudioHarness.Unknown,
                CopilotStudioHarnessClassifier.Classify("Some Future Feature Microsoft Has Not Invented Yet"));

            Assert.AreEqual(CopilotStudioHarness.Unknown,
                CopilotStudioHarnessClassifier.Classify("Generative answers"),
                "Matching is exact, not a prefix/substring: a near-miss must not be absorbed into an existing bucket.");
        }

        [TestMethod]
        public void HarnessClassifier_NoFeature_IsNotAssessed()
        {
            Assert.AreEqual(CopilotStudioHarness.NotAssessed, CopilotStudioHarnessClassifier.Classify(null));
            Assert.AreEqual(CopilotStudioHarness.NotAssessed, CopilotStudioHarnessClassifier.Classify(string.Empty));
            Assert.AreEqual(CopilotStudioHarness.NotAssessed, CopilotStudioHarnessClassifier.Classify("   "));
        }

        #endregion

        #region Row hashing

        [TestMethod]
        public void RowHasher_NullAndEmptyComponents_DoNotCollide()
        {
            // These are different facts - "Microsoft reported no LLM model" vs "Microsoft reported an empty
            // one" - and they must not share an upsert key, or one would silently overwrite the other.
            var withNull = AgentCostRowHasher.Hash("2026-09-01", "agent", null);
            var withEmpty = AgentCostRowHasher.Hash("2026-09-01", "agent", string.Empty);

            Assert.AreNotEqual(withNull, withEmpty);
        }

        [TestMethod]
        public void RowHasher_ComponentBoundaries_AreRespected()
        {
            // Without a separator that cannot occur in the data, ("ab","c") and ("a","bc") would concatenate
            // to the same string and collide.
            Assert.AreNotEqual(AgentCostRowHasher.Hash("ab", "c"), AgentCostRowHasher.Hash("a", "bc"));
        }

        [TestMethod]
        public void RowHasher_IsStableAndFixedWidth()
        {
            var first = AgentCostRowHasher.Hash("2026-09-01", GreekAgentName, "Generative answer");
            var second = AgentCostRowHasher.Hash("2026-09-01", GreekAgentName, "Generative answer");

            Assert.AreEqual(first, second, "The same dimensions must always produce the same key, or a re-import duplicates.");
            Assert.AreEqual(64, first.Length, "The hash must fit the nvarchar(64) column it is stored in.");
        }

        #endregion

        #region Copilot Studio consumption parsing

        [TestMethod]
        public void CreditParser_FlatEnvelope_IsParsed()
        {
            // The shape Microsoft's REST reference documents.
            //
            // LLMModel is left in the fixture deliberately even though the parser no longer reads it: it is
            // a metadata key Microsoft has never actually been observed sending, and keeping it here proves
            // an unrecognised key is ignored rather than breaking the parse.
            var json = JObject.Parse(@"{
                ""value"": [
                    {
                        ""resourceId"": ""00000000-0000-0000-0000-000000000001"",
                        ""environmentId"": ""00000000-0000-0000-0000-0000000000e1"",
                        ""consumed"": 12.5,
                        ""unit"": ""Count"",
                        ""lastRefreshedDate"": ""2026-09-02T03:00:00Z"",
                        ""metadata"": {
                            ""ResourceName"": ""Contoso Helpdesk"",
                            ""NonBillableQuantity"": 1.25,
                            ""Users"": 7,
                            ""FeatureName"": ""Generative answer"",
                            ""LLMModel"": ""gpt-4o""
                        }
                    }
                ],
                ""continuationtoken"": """"
            }");

            var page = CopilotStudioCreditParser.ParseConsumptionPage(json);

            Assert.AreEqual(1, page.Rows.Count);
            Assert.IsFalse(page.HasMore, "An empty continuation token means this was the last page.");

            var row = page.Rows[0];
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", row.ResourceId);
            Assert.AreEqual(12.5m, row.Consumed);
            Assert.AreEqual(1.25m, row.NonBillableQuantity);
            Assert.AreEqual("Generative answer", row.FeatureName);
        }

        [TestMethod]
        public void CreditParser_NestedEnvelope_IsAlsoParsed()
        {
            // The shape a live tenant actually returns when includeFields is supplied. Only one of the two
            // shapes is documented and only the other has been observed, so both must work.
            var json = JObject.Parse(@"{
                ""value"": [
                    {
                        ""resources"": [
                            {
                                ""environmentId"": ""00000000-0000-0000-0000-0000000000e1"",
                                ""resourceId"": ""00000000-0000-0000-0000-000000000002"",
                                ""consumed"": 4,
                                ""asOfDate"": ""2026-09-03T00:00:00"",
                                ""metadata"": {
                                    ""ResourceName"": """ + GreekAgentName + @""",
                                    ""Users"": 3,
                                    ""FeatureName"": ""Process Agent""
                                }
                            }
                        ]
                    }
                ],
                ""continuationtoken"": ""abc123""
            }");

            var page = CopilotStudioCreditParser.ParseConsumptionPage(json);

            Assert.AreEqual(1, page.Rows.Count);
            Assert.IsTrue(page.HasMore);
            Assert.AreEqual("abc123", page.ContinuationToken);
            Assert.AreEqual(GreekAgentName, page.Rows[0].ResourceName,
                "A non-Latin agent name must survive parsing unchanged.");
            Assert.AreEqual(new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), page.Rows[0].AsOfDate);
        }

        [TestMethod]
        public void CreditParser_OffsetlessTimestamp_IsReadAsUtcNotLocal()
        {
            // "2026-09-03T00:00:00" carries no offset. Read through the host's local zone it would land on a
            // different usage day for anyone not on UTC, and the usage day is part of the upsert key.
            var json = JObject.Parse(@"{ ""value"": [ { ""resourceId"": ""a"", ""consumed"": 1, ""asOfDate"": ""2026-09-03T00:00:00"" } ] }");

            var row = CopilotStudioCreditParser.ParseConsumptionPage(json).Rows.Single();

            Assert.IsTrue(row.AsOfDate.HasValue);
            Assert.AreEqual(DateTimeKind.Utc, row.AsOfDate.Value.Kind,
                "On a host at UTC the ticks would match either way, so the Kind is what proves the conversion happened.");
            Assert.AreEqual(new DateTime(2026, 9, 3), row.AsOfDate.Value.Date);
        }

        [TestMethod]
        public void CreditParser_UnexpectedEnvelope_YieldsNoRowsRatherThanThrowing()
        {
            // A shape we do not recognise must be recorded as "read 0 rows" in the import log, not abort the
            // cycle: the response schema is not ours and can change without warning.
            var page = CopilotStudioCreditParser.ParseConsumptionPage(JObject.Parse(@"{ ""somethingElse"": 42 }"));

            Assert.AreEqual(0, page.Rows.Count);
            Assert.IsFalse(page.HasMore);
        }

        [TestMethod]
        public void CreditParser_QuotedNumbers_UseInvariantCulture()
        {
            var json = JObject.Parse(@"{ ""value"": [ { ""resourceId"": ""a"", ""consumed"": ""12.75"" } ] }");

            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A culture where ',' is the decimal separator. Parsing "12.75" with it would give 1275.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var row = CopilotStudioCreditParser.ParseConsumptionPage(json).Rows.Single();
                Assert.AreEqual(12.75m, row.Consumed);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void CreditParser_Capacity_IsParsed()
        {
            var json = JObject.Parse(@"{
                ""entitlementId"": ""MCSMessages"",
                ""entitlement"": {
                    ""unit"": ""Count"",
                    ""capacity"": {
                        ""entitled"": { ""value"": 25000 },
                        ""consumed"": { ""value"": 4321.5, ""consumptionType"": ""MonthToDate"", ""lastUpdatedOn"": ""2026-09-08T00:00:00Z"" },
                        ""allocated"": { ""value"": 1000 },
                        ""availableQuantity"": 20678.5,
                        ""status"": ""WithinCapacity""
                    },
                    ""payGo"": { ""consumed"": { ""value"": 12 } }
                }
            }");

            var capacity = CopilotStudioCreditParser.ParseCapacity(json);

            Assert.IsNotNull(capacity);
            Assert.AreEqual(25000m, capacity.Entitled);
            Assert.AreEqual(4321.5m, capacity.Consumed);
            Assert.AreEqual("MonthToDate", capacity.ConsumptionType);
            Assert.AreEqual(20678.5m, capacity.Available);
            Assert.AreEqual(12m, capacity.PayAsYouGoConsumed);
            Assert.AreEqual("WithinCapacity", capacity.Status);
        }

        [TestMethod]
        public void CreditParser_NoEntitlement_ReturnsNullSoZeroIsNotInvented()
        {
            // "No entitlement information" and "an entitlement of zero" are different answers.
            Assert.IsNull(CopilotStudioCreditParser.ParseCapacity(JObject.Parse(@"{ ""entitlementId"": ""MCSMessages"" }")));
        }

        #endregion

        #region Mapping and aggregation

        [TestMethod]
        public void MapAndAggregate_DuplicateSlices_AreCombinedRatherThanOverwriting()
        {
            var from = new DateTime(2026, 9, 1);
            var to = new DateTime(2026, 9, 7);

            var rows = new[]
            {
                new CopilotStudioCreditRow
                {
                    ResourceId = "agent-1", EnvironmentId = "env-1", FeatureName = "Generative answer",
                    Consumed = 10m, NonBillableQuantity = 1m, UserId = "u1", AsOfDate = new DateTime(2026, 9, 3),
                },
                new CopilotStudioCreditRow
                {
                    ResourceId = "agent-1", EnvironmentId = "env-1", FeatureName = "Generative answer",
                    Consumed = 4m, NonBillableQuantity = 2m, UserId = "u2", AsOfDate = new DateTime(2026, 9, 3),
                },
                new CopilotStudioCreditRow
                {
                    ResourceId = "agent-1", EnvironmentId = "env-1", FeatureName = "Generative answer",
                    Consumed = 1m, UserId = "u2", AsOfDate = new DateTime(2026, 9, 3),
                },
            };

            var mapped = CopilotStudioCreditImporter.MapAndAggregate(rows, from, to, null, DateTime.UtcNow);

            Assert.AreEqual(1, mapped.Count, "Rows on the same dimension slice share an upsert key and must combine.");
            Assert.AreEqual(15m, mapped[0].BilledCredits, "Credits are money: they add up.");
            Assert.AreEqual(3m, mapped[0].NonBilledCredits);
            Assert.AreEqual(2, mapped[0].DistinctUsers, "The same person on two rows counts once.");
        }

        [TestMethod]
        public void MapAndAggregate_WithASingleDayRequest_TakesTheUsageDateFromTheRequestNotTheResponse()
        {
            // The importer asks for exactly one day per request, so the requested day is the authoritative
            // usage date. It must NOT defer to asOfDate: that field is absent from the documented response
            // model, and the usage date is part of the upsert key - a key that depended on an undocumented
            // field would start producing duplicates the day Microsoft stopped returning it.
            var day = new DateTime(2026, 9, 3);

            var mapped = CopilotStudioCreditImporter.MapAndAggregate(
                new[]
                {
                    new CopilotStudioCreditRow { ResourceId = "a", Consumed = 1m, AsOfDate = new DateTime(2026, 8, 1) },
                    new CopilotStudioCreditRow { ResourceId = "b", Consumed = 1m, AsOfDate = null },
                },
                day, day, null, DateTime.UtcNow);

            Assert.IsTrue(mapped.All(r => r.UsageDate == day),
                "Every row from a single-day request belongs to that day, whatever the response claims.");
        }

        [TestMethod]
        public void MapAndAggregate_ReReadingTheSameDay_ProducesTheSameKeySoItUpdatesRatherThanDuplicates()
        {
            // The trailing window slides forward every run. If the same day produced a different key on a
            // later run, the re-read would insert a second copy and spend would climb on its own.
            var day = new DateTime(2026, 9, 3);
            var row = new CopilotStudioCreditRow { ResourceId = "a", Consumed = 1m, FeatureName = "Generative answer" };

            var runOne = CopilotStudioCreditImporter.MapAndAggregate(new[] { row }, day, day, null, DateTime.UtcNow);
            var runTwo = CopilotStudioCreditImporter.MapAndAggregate(new[] { row }, day, day, null, DateTime.UtcNow.AddDays(1));

            Assert.AreEqual(runOne.Single().UsageDate, runTwo.Single().UsageDate);
            Assert.AreEqual(runOne.Single().DimensionHash, runTwo.Single().DimensionHash,
                "A stable key is what makes the second run an UPDATE rather than a duplicate.");
        }

        [TestMethod]
        public void MapAndAggregate_NoAsOfDate_UsesTheRequestedDay()
        {
            var day = new DateTime(2026, 9, 7);

            var mapped = CopilotStudioCreditImporter.MapAndAggregate(
                new[] { new CopilotStudioCreditRow { ResourceId = "a", Consumed = 1m } },
                day, day, null, DateTime.UtcNow);

            Assert.AreEqual(day, mapped.Single().UsageDate);
        }

        [TestMethod]
        public void MapAndAggregate_ClassifiesHarnessAndResolvesEnvironmentName()
        {
            var environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "env-1", "Contoso Production" },
            };

            var mapped = CopilotStudioCreditImporter.MapAndAggregate(
                new[]
                {
                    new CopilotStudioCreditRow
                    {
                        ResourceId = "a", EnvironmentId = "env-1", FeatureName = "Process Agent",
                        ResourceName = GreekAgentName, Consumed = 2m,
                    },
                },
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), environments, DateTime.UtcNow);

            var row = mapped.Single();
            Assert.AreEqual(CopilotStudioHarness.GitHubCopilot, row.Harness);
            Assert.AreEqual("Contoso Production", row.EnvironmentName);
            Assert.AreEqual(GreekAgentName, row.AgentName);
            Assert.AreEqual("Process Agent", row.FeatureName, "The raw feature is kept as well as the classification.");
        }

        [TestMethod]
        public void MapAndAggregate_DifferentDimensions_StayAsSeparateRows()
        {
            var mapped = CopilotStudioCreditImporter.MapAndAggregate(
                new[]
                {
                    new CopilotStudioCreditRow { ResourceId = "a", FeatureName = "Generative answer", Consumed = 1m },
                    new CopilotStudioCreditRow { ResourceId = "a", FeatureName = "Tenant graph grounding", Consumed = 1m },
                },
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), null, DateTime.UtcNow);

            Assert.AreEqual(2, mapped.Count,
                "The billing feature is part of the upsert key, so these are different slices.");
        }

        #endregion

        #region Azure cost parsing

        [TestMethod]
        public void AzureParser_ReadsByColumnNameNotPosition()
        {
            // Cost Management does not guarantee column order, and it varies with the grouping requested.
            // Reading positionally would silently swap cost and quantity the first time Azure reorders them.
            var json = JObject.Parse(@"{
                ""properties"": {
                    ""columns"": [
                        { ""name"": ""MeterName"", ""type"": ""String"" },
                        { ""name"": ""UsageDate"", ""type"": ""Number"" },
                        { ""name"": ""PreTaxCost"", ""type"": ""Number"" },
                        { ""name"": ""Currency"", ""type"": ""String"" }
                    ],
                    ""rows"": [
                        [ ""Copilot Studio"", 20260901, 1.2345, ""GBP"" ]
                    ]
                }
            }");

            var rows = AzureCostQueryParser.ParsePage(json);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("Copilot Studio", rows[0].MeterName);
            Assert.AreEqual(1.2345m, rows[0].Cost);
            Assert.AreEqual("GBP", rows[0].Currency);
            Assert.AreEqual(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), rows[0].UsageDate);
        }

        [TestMethod]
        public void AzureParser_DailyGranularity_ReturnsTheDateAsAYyyyMmDdNumber()
        {
            // This is the quirk that catches people out: with Daily granularity the usage date arrives as the
            // NUMBER 20260901, not a date string.
            var json = JObject.Parse(@"{ ""properties"": {
                ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""PreTaxCost"" } ],
                ""rows"": [ [ 20260215, 3 ] ] } }");

            Assert.AreEqual(new DateTime(2026, 2, 15), AzureCostQueryParser.ParsePage(json).Single().UsageDate.Date);
        }

        [TestMethod]
        public void AzureParser_IsoDateString_IsAlsoAccepted()
        {
            var json = JObject.Parse(@"{ ""properties"": {
                ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""PreTaxCost"" } ],
                ""rows"": [ [ ""2026-02-15T00:00:00"", 3 ] ] } }");

            Assert.AreEqual(new DateTime(2026, 2, 15), AzureCostQueryParser.ParsePage(json).Single().UsageDate.Date);
        }

        [TestMethod]
        public void AzureParser_RowWithNoUsableDate_IsDroppedNotDatedByGuess()
        {
            // Without a date the row cannot be placed on a timeline or de-duplicated. Storing it against a
            // guessed date would put real money on the wrong day.
            var json = JObject.Parse(@"{ ""properties"": {
                ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""PreTaxCost"" } ],
                ""rows"": [ [ null, 3 ], [ 20260215, 4 ] ] } }");

            var rows = AzureCostQueryParser.ParsePage(json);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(4m, rows[0].Cost);
        }

        [TestMethod]
        public void AzureParser_CostColumnAliases_AreAccepted()
        {
            // The cost column is named differently by agreement type.
            var json = JObject.Parse(@"{ ""properties"": {
                ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""Cost"" } ],
                ""rows"": [ [ 20260215, 9.5 ] ] } }");

            Assert.AreEqual(9.5m, AzureCostQueryParser.ParsePage(json).Single().Cost);
        }

        [TestMethod]
        public void AzureParser_NextLink_IsFoundOnEitherEnvelope()
        {
            Assert.AreEqual("https://contoso.example/next",
                AzureCostQueryParser.GetNextLink(JObject.Parse(@"{ ""properties"": { ""nextLink"": ""https://contoso.example/next"" } }")));

            Assert.IsNull(AzureCostQueryParser.GetNextLink(JObject.Parse(@"{ ""properties"": { ""nextLink"": null } }")));
        }

        [TestMethod]
        public void AzureParser_EmptyResult_IsNotAnError()
        {
            // A filter that matched nothing is a legitimate answer.
            var json = JObject.Parse(@"{ ""properties"": { ""columns"": [ { ""name"": ""UsageDate"" } ], ""rows"": [] } }");
            Assert.AreEqual(0, AzureCostQueryParser.ParsePage(json).Count);
        }

        #endregion

        #region Azure cost mapping

        [TestMethod]
        public void AzureImporter_CostsOnTheSameIdentity_AreSummed()
        {
            var importer = NewAzureImporter(new AzureCostImportSettings { Scopes = new[] { "/subscriptions/x" } });

            var mapped = importer.MapAndAggregate(
                new[]
                {
                    new AzureCostRow { UsageDate = new DateTime(2026, 9, 1), MeterName = "m", Currency = "GBP", Cost = 1.5m, Quantity = 2m },
                    new AzureCostRow { UsageDate = new DateTime(2026, 9, 1), MeterName = "m", Currency = "GBP", Cost = 2.25m, Quantity = 3m },
                },
                "/subscriptions/x", new DateTime(2026, 9, 9), DateTime.UtcNow);

            Assert.AreEqual(1, mapped.Count);
            Assert.AreEqual(3.75m, mapped[0].Cost);
            Assert.AreEqual(5m, mapped[0].Quantity);
        }

        [TestMethod]
        public void AzureImporter_DifferentCurrencies_AreDifferentRows()
        {
            var importer = NewAzureImporter(new AzureCostImportSettings { Scopes = new[] { "/subscriptions/x" } });

            var mapped = importer.MapAndAggregate(
                new[]
                {
                    new AzureCostRow { UsageDate = new DateTime(2026, 9, 1), MeterName = "m", Currency = "GBP", Cost = 1m },
                    new AzureCostRow { UsageDate = new DateTime(2026, 9, 1), MeterName = "m", Currency = "USD", Cost = 1m },
                },
                "/subscriptions/x", new DateTime(2026, 9, 9), DateTime.UtcNow);

            Assert.AreEqual(2, mapped.Count,
                "Amounts in different currencies are different quantities and must never be combined.");
        }

        [TestMethod]
        public void AzureImporter_OpenBillingPeriod_IsFlaggedAsAnEstimate()
        {
            // Azure keeps amending an open period, and only finalises it a few days after it ends.
            Assert.IsTrue(AzureCostImporter.IsStillProvisional(new DateTime(2026, 9, 1), new DateTime(2026, 9, 9)),
                "A day in the current month is still being re-estimated.");

            Assert.IsTrue(AzureCostImporter.IsStillProvisional(new DateTime(2026, 8, 31), new DateTime(2026, 9, 3)),
                "Charges can still change for a few days after the period ends.");

            Assert.IsFalse(AzureCostImporter.IsStillProvisional(new DateTime(2026, 8, 31), new DateTime(2026, 9, 9)),
                "Once the finalisation window has passed the figure is invoiced.");
        }

        [TestMethod]
        public async Task AzureImporter_WithNoScopeConfigured_DeclinesLoudlyInsteadOfSilentlyDoingNothing()
        {
            var store = new RecordingAgentCostStore();
            var importer = new AzureCostImporter(Logger, new ThrowingAzureCostSource(), store, new AzureCostImportSettings());

            var outcome = await importer.ImportAsync();
            var log = outcome.Log;

            Assert.IsFalse(string.IsNullOrEmpty(log.Error),
                "The toggle can be on while the scope setting is missing; that must be recorded, not read as 'no spend'.");
            StringAssert.Contains(log.Error, "AzureCostScopes",
                "The message must name the setting the admin has to fill in.");
            Assert.AreEqual(1, store.Logs.Count, "The outcome must reach agent_cost_import_log.");
            Assert.IsTrue(outcome.IsAuthorisationFailure,
                "A missing setting cannot fix itself, so the cadence gate must back off rather than re-ask every cycle.");
        }

        [TestMethod]
        public async Task CreditImporter_AsksForOneDayAtATime()
        {
            // One request per day makes the usage date a fact about the request, not an inference about the
            // response - for the /users read and for every per-user read.
            var source = new ScriptedCreditSource();
            var clock = new FixedClock(new DateTime(2026, 9, 9, 6, 0, 0, DateTimeKind.Utc));
            source.AddUsers(new DateTime(2026, 9, 8), null, null, new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 1m });

            await new CopilotStudioCreditImporter(Logger, source, new RecordingAgentCostStore(), 3, clock).ImportConsumptionAsync();

            CollectionAssert.AreEquivalent(
                new[] { new DateTime(2026, 9, 7), new DateTime(2026, 9, 8), new DateTime(2026, 9, 9) },
                source.UserCalls.Select(c => c.Item1).ToList(),
                "The trailing window must end today and cover exactly the configured number of days, one /users read each.");
            Assert.AreEqual(new DateTime(2026, 9, 8), source.ResourceCalls.Single().Item2,
                "The per-user-by-agent read is for the single day the user was active.");
        }

        [TestMethod]
        public async Task AzureSource_TransientGatewayFailure_IsRetriedWithTheSameQuery()
        {
            // The query POST is a read. Before it was flagged replayable, one 503 from Cost Management failed the
            // scope for the whole cycle, where a GET would have been retried.
            const string scope = "/subscriptions/00000000-0000-0000-0000-000000000000";
            var clock = new AutoThrottleHttpClientTests.FakeRetryClock(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
            var handler = new RecordingPostHandler(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{ ""properties"": { ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""PreTaxCost"" } ],
                        ""rows"": [ [ 20260916, 5 ] ] } }", Encoding.UTF8, "application/json"),
                });

            using (var client = new DataUtils.Http.AutoThrottleHttpClient(handler, Logger, clock) { MaxTotalRetryBudgetSeconds = 600 })
            {
                var source = new AzureCostManagementSource(client, new AzureCostImportSettings { Scopes = new[] { scope } }, Logger);

                var rows = await source.GetDailyCostsAsync(scope, new DateTime(2026, 9, 16), new DateTime(2026, 9, 16));

                Assert.AreEqual(1, rows.Count, "The retried page must be read.");
                Assert.AreEqual(5m, rows[0].Cost);
            }

            Assert.AreEqual(2, handler.Bodies.Count, "The 503 must be retried exactly once.");
            Assert.AreEqual(handler.Bodies[0], handler.Bodies[1], "The retry must re-post the identical query.");
        }

        [TestMethod]
        public void AzureQuery_NeverAsksForMoreThanTwoGroupings()
        {
            // Cost Management's QueryDataset schema declares grouping with maxItems: 2 in every API version.
            // A request carrying more is rejected with HTTP 400 - so exceeding it would not degrade the
            // import, it would stop it working entirely, on every run, for every customer.
            var settings = new AzureCostImportSettings
            {
                Scopes = new[] { "/subscriptions/00000000-0000-0000-0000-000000000000" },
                GroupBy = new[] { "ResourceId", "Meter", "ServiceName", "MeterCategory" },
            };

            var source = new AzureCostManagementSource(
                new DataUtils.Http.AutoThrottleHttpClient(false, Logger), settings, Logger);

            var body = JObject.Parse(source.BuildRequestBody(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)));
            var grouping = (JArray)body["dataset"]["grouping"];

            Assert.IsTrue(grouping.Count <= AzureCostImportSettings.MaxGroupByDimensions,
                $"Cost Management accepts at most {AzureCostImportSettings.MaxGroupByDimensions} groupings, but the "
                + $"request asked for {grouping.Count}.");

            var aggregation = (JObject)body["dataset"]["aggregation"];
            Assert.IsTrue(aggregation.Count <= 2, "The same maxItems: 2 limit applies to aggregation.");
        }

        [DataTestMethod]
        [DataRow("fi-FI")] // ':' time separator is '.'
        [DataRow("th-TH")] // Buddhist calendar: 2026 renders as 2569
        [DataRow("fa-IR")] // Persian calendar
        [DataRow("ar-SA")] // Hijri calendar
        [DataRow("oc-FR")] // 'h' time separator
        public void AzureQuery_TimePeriod_IsTheSameIsoTimestampOnEveryHostCulture(string cultureName)
        {
            // A custom date format takes its ':' separator and its calendar from the CURRENT culture, so on these
            // hosts the request asked Cost Management for "2026-09-01T00.00.00Z", or for the year 2569.
            var source = new AzureCostManagementSource(
                new DataUtils.Http.AutoThrottleHttpClient(false, Logger),
                new AzureCostImportSettings { Scopes = new[] { "/subscriptions/00000000-0000-0000-0000-000000000000" } },
                Logger);

            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                var raw = source.BuildRequestBody(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

                // Parsed WITHOUT date handling: Json.NET would otherwise turn the ISO strings into DateTime tokens
                // and re-render them, so the assertion would compare Json.NET's formatting rather than the text
                // this product actually sends.
                var body = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(raw,
                    new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None });

                Assert.AreEqual("2026-09-01T00:00:00Z", (string)body["timePeriod"]["from"]);
                Assert.AreEqual("2026-09-07T23:59:59Z", (string)body["timePeriod"]["to"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [DataTestMethod]
        [DataRow("th-TH")]
        [DataRow("fa-IR")]
        public void LicensingQueryDate_IsGregorianOnEveryHostCulture(string cultureName)
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);

                Assert.AreEqual("2026-09-01", PowerPlatformLicensingCreditSource.QueryDate(new DateTime(2026, 9, 1)),
                    "An interpolated {date:yyyy-MM-dd} would ask the licensing API for a Buddhist or Persian year.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void RowIdentity_DoesNotChangeWithTheHostCulture_ForAllThreeImports()
        {
            // The usage date is part of every agent-cost upsert key. Formatted in the host culture, the same row
            // would hash differently on a th-TH host than on an en-US one, so a culture change would re-insert
            // rows instead of updating them. Both cultures are set explicitly: computing the baseline under the
            // ambient culture would let the test pass on a th-TH build machine even with the fix reverted.
            var importer = NewAzureImporter(new AzureCostImportSettings { Scopes = new[] { "/subscriptions/x" } });
            var azureRow = NewTaggedRow("Cowork", 10m);
            const string scope = "/subscriptions/00000000-0000-0000-0000-000000000000";
            var day = new DateTime(2026, 9, 16);
            var creditRow = new CopilotStudioCreditRow { ResourceId = "a", EnvironmentId = "env-1", FeatureName = "Generative answer", Consumed = 1m, AsOfDate = day };
            var userRow = new CopilotStudioUserCreditRow { UserId = "00000000-0000-0000-0000-0000000000a1", EnvironmentId = "env-1", Consumed = 2m };

            Func<string[]> hashes = () => new[]
            {
                importer.MapAndAggregate(new[] { azureRow }, scope, day, DateTime.UtcNow).Single().RowHash,
                CopilotStudioCreditImporter.MapAndAggregate(new[] { creditRow }, day, day, null, DateTime.UtcNow).Single().DimensionHash,
                CopilotStudioCreditImporter.MapUserRows(new[] { userRow }, day, null, DateTime.UtcNow).Single().DimensionHash,
            };

            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                var invariant = hashes();

                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                var thai = hashes();

                Assert.AreEqual(invariant[0], thai[0], "The Azure cost row identity must be calendar-independent.");
                Assert.AreEqual(invariant[1], thai[1], "The per-agent credit row identity must be calendar-independent.");
                Assert.AreEqual(invariant[2], thai[2], "The per-user credit row identity must be calendar-independent.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void AzureQuery_DefaultGrouping_KeepsTheDimensionsOtherFieldsCanBeRecoveredFrom()
        {
            // ResourceId is preferred over ServiceName because the subscription and resource group can be
            // parsed back out of it, so two groupings still yield four dimensions.
            CollectionAssert.Contains(AzureCostImportSettings.DefaultGroupBy.ToList(), "ResourceId");

            const string resourceId =
                "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/contoso-rg/providers/Microsoft.Foo/bar/baz";

            Assert.AreEqual("00000000-0000-0000-0000-000000000001",
                AzureCostImporter.SubscriptionIdFromResourceId(resourceId));
            Assert.AreEqual("contoso-rg", AzureCostImporter.ResourceGroupFromResourceId(resourceId));

            // Azure is not case-consistent about this segment.
            Assert.AreEqual("contoso-rg",
                AzureCostImporter.ResourceGroupFromResourceId(resourceId.Replace("resourceGroups", "resourcegroups")));

            Assert.IsNull(AzureCostImporter.SubscriptionIdFromResourceId(null));
            Assert.IsNull(AzureCostImporter.ResourceGroupFromResourceId("not-a-resource-id"));
        }

        [TestMethod]
        public void AzureQuery_TagGrouping_IsSentAsATagKeyClauseNotADimension()
        {
            // "TagKey:<name>" is the only way the query API returns tags at all. Sent as a Dimension it would
            // be rejected as an unknown dimension, and the tag columns would never come back.
            var settings = new AzureCostImportSettings
            {
                Scopes = new[] { "/subscriptions/00000000-0000-0000-0000-000000000000" },
                GroupBy = new[] { "ResourceId", "TagKey:serviceName" },
            };

            var source = new AzureCostManagementSource(
                new DataUtils.Http.AutoThrottleHttpClient(false, Logger), settings, Logger);

            var body = JObject.Parse(source.BuildRequestBody(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)));
            var grouping = (JArray)body["dataset"]["grouping"];

            Assert.AreEqual(2, grouping.Count, "A tag grouping is one clause, not two - it returns two COLUMNS.");
            Assert.AreEqual("Dimension", (string)grouping[0]["type"]);
            Assert.AreEqual("ResourceId", (string)grouping[0]["name"]);
            Assert.AreEqual("TagKey", (string)grouping[1]["type"],
                "A TagKey: entry must become a TagKey clause; as a Dimension the API rejects it.");
            Assert.AreEqual("serviceName", (string)grouping[1]["name"],
                "The prefix must be stripped, leaving the bare tag name.");

            // A plain dimension must be untouched by the prefix handling.
            Assert.AreEqual("Dimension", (string)AzureCostManagementSource.GroupByClause("Meter")["type"]);

            // "TagKey:" with nothing after it is a misconfiguration; treating it as a tag would ask Azure to
            // group by a nameless tag. Falling back to a dimension surfaces it as a clear API error instead.
            Assert.AreEqual("Dimension", (string)AzureCostManagementSource.GroupByClause("TagKey:")["type"]);
        }

        [TestMethod]
        public void AzureParser_TagColumns_AreReadFromTagKeyAndTagValue()
        {
            // Grouping by a tag returns the fixed columns TagKey and TagValue - NOT a column named after the
            // tag. Reading a column named after the tag instead would collide with the identically-named
            // dimension (the column index is case-insensitive, so "serviceName" and "ServiceName" are one key).
            var json = JObject.Parse(@"{ ""properties"": {
                ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""TagKey"" }, { ""name"": ""TagValue"" },
                               { ""name"": ""ServiceName"" }, { ""name"": ""PreTaxCost"" } ],
                ""rows"": [ [ 20260916, ""serviceName"", ""Cowork"", ""Microsoft Copilot Studio"", 100 ] ] } }");

            var row = AzureCostQueryParser.ParsePage(json).Single();

            Assert.AreEqual("serviceName", row.TagKey);
            Assert.AreEqual("Cowork", row.TagValue);
            Assert.AreEqual("Microsoft Copilot Studio", row.ServiceName,
                "The tag must not be read into the service name, nor the service name into the tag.");
        }

        [TestMethod]
        public void AzureParser_UntaggedRowInATagGroupedQuery_LeavesTheTagValueNull()
        {
            // Cost Management returns everything untagged as a row with an empty tag value. That is a real
            // answer ("this spend is not Cowork"), so the row must survive rather than be dropped.
            var json = JObject.Parse(@"{ ""properties"": {
                ""columns"": [ { ""name"": ""UsageDate"" }, { ""name"": ""TagKey"" }, { ""name"": ""TagValue"" },
                               { ""name"": ""PreTaxCost"" } ],
                ""rows"": [ [ 20260916, """", """", 7 ] ] } }");

            var row = AzureCostQueryParser.ParsePage(json).Single();

            Assert.IsNull(row.TagValue);
            Assert.AreEqual(7m, row.Cost, "An untagged row still carries real spend and must not be discarded.");
        }

        [TestMethod]
        public void AzureImporter_RowsDifferingOnlyByTag_AreKeptApartNotSummed()
        {
            // This is the whole point of the feature. Microsoft bills Cowork, Work IQ API and Copilot Studio
            // through ONE meter on ONE resource, so with a tag grouping the ONLY difference between these two
            // rows is the tag. If the upsert key ignored it they would collapse into a single 30-credit row
            // and the split the tag grouping was configured to get would be destroyed.
            var rows = new[]
            {
                NewTaggedRow("Cowork", 10m),
                NewTaggedRow("WorkIQAPI", 20m),
            };

            var importer = NewAzureImporter(new AzureCostImportSettings { Scopes = new[] { "/subscriptions/x" } });

            var mapped = importer.MapAndAggregate(
                rows, "/subscriptions/00000000-0000-0000-0000-000000000000",
                new DateTime(2026, 9, 16), DateTime.UtcNow);

            Assert.AreEqual(2, mapped.Count, "Two tag values must produce two rows, not one summed row.");
            Assert.AreEqual(2, mapped.Select(r => r.RowHash).Distinct().Count(),
                "The upsert key must include the tag, or the second row silently replaces the first.");

            var cowork = mapped.Single(r => r.TagValue == "Cowork");
            Assert.AreEqual(10m, cowork.Cost);
            Assert.AreEqual("serviceName", cowork.TagKey);
            Assert.AreEqual(20m, mapped.Single(r => r.TagValue == "WorkIQAPI").Cost);
        }

        private static AzureCostRow NewTaggedRow(string tagValue, decimal cost)
        {
            return new AzureCostRow
            {
                UsageDate = new DateTime(2026, 9, 16),
                ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg"
                    + "/providers/Microsoft.PowerPlatform/accounts/Contoso All Users Policy",
                ServiceName = "Microsoft Copilot Studio",
                MeterName = "Pay As You Go Copilot Credit",
                Currency = "USD",
                Cost = cost,
                TagKey = "serviceName",
                TagValue = tagValue,
            };
        }

        [TestMethod]
        public void CreditParser_DocumentedMetadataNames_AreAcceptedAsWellAsObservedOnes()
        {
            // The REST reference documents metadata as "Feature, ProductName and nonBillableConsumed"; the
            // names this parser was originally written against are FeatureName, ResourceName and
            // NonBillableQuantity. Those are DIFFERENT names, not different casings, so a parser that reads
            // only one set returns null for ever against the other - taking the agent name, the billing
            // feature and (because the harness is classified FROM the feature name) the harness with it.
            //
            // Neither source can be treated as authoritative: the same reference also documents a flat
            // envelope that live tenants do not return. So both spellings must work.
            var documented = JObject.Parse(@"{
                ""value"": [
                    {
                        ""resourceId"": ""00000000-0000-0000-0000-000000000001"",
                        ""environmentId"": ""00000000-0000-0000-0000-0000000000e1"",
                        ""consumed"": 10,
                        ""metadata"": {
                            ""ProductName"": ""Contoso Helpdesk"",
                            ""Feature"": ""Generative answer"",
                            ""nonBillableConsumed"": 2.5
                        }
                    }
                ]
            }");

            var row = CopilotStudioCreditParser.ParseConsumptionPage(documented).Rows.Single();

            Assert.AreEqual("Contoso Helpdesk", row.ResourceName,
                "The documented ProductName must populate the agent name, or the agent pivot is empty.");
            Assert.AreEqual("Generative answer", row.FeatureName,
                "The documented Feature must populate the billing feature - the harness is classified from it.");
            Assert.AreEqual(2.5m, row.NonBillableQuantity,
                "The documented nonBillableConsumed must populate the 'credits not charged' figure.");

            // And the originally-observed spellings must still work.
            var observed = JObject.Parse(@"{
                ""value"": [
                    {
                        ""resourceId"": ""00000000-0000-0000-0000-000000000001"",
                        ""consumed"": 10,
                        ""metadata"": {
                            ""ResourceName"": ""Contoso Helpdesk"",
                            ""FeatureName"": ""Generative answer"",
                            ""NonBillableQuantity"": 2.5
                        }
                    }
                ]
            }");

            var observedRow = CopilotStudioCreditParser.ParseConsumptionPage(observed).Rows.Single();
            Assert.AreEqual("Contoso Helpdesk", observedRow.ResourceName);
            Assert.AreEqual("Generative answer", observedRow.FeatureName);
            Assert.AreEqual(2.5m, observedRow.NonBillableQuantity);
        }

        [TestMethod]
        public void AzureImporter_RefreshWindow_CoversEverythingStillProvisional()
        {
            // The trailing-days setting and the "still provisional" rule are different spans. If the window
            // were only the former, a charge dated the 1st would stop being re-read on the 5th while still
            // being labelled an estimate until the 5th of the NEXT month - frozen at its first value for ever.
            var today = new DateTime(2026, 9, 20);
            var earliest = AzureCostImporter.EarliestStillProvisionalDate(today);

            Assert.IsTrue(AzureCostImporter.IsStillProvisional(earliest, today),
                "The earliest refreshed date must itself still be provisional.");
            Assert.IsFalse(AzureCostImporter.IsStillProvisional(earliest.AddDays(-1), today),
                "And the day before it must not be - otherwise the window is too narrow.");

            // Inside the finalisation lag the previous month is still open, so it must still be refreshed.
            var earlyInMonth = new DateTime(2026, 9, 3);
            Assert.AreEqual(new DateTime(2026, 8, 1), AzureCostImporter.EarliestStillProvisionalDate(earlyInMonth));
        }

        [TestMethod]
        public void CreditParser_PerUserShapeObservedOnALiveTenant_IsParsedFully()
        {
            // Captured from a real tenant (values replaced with synthetic ones). Two things this pins that
            // the public documentation does not show: asOfDate IS present on this route without the
            // undocumented includeFields parameter, and the row carries tenantId plus a metadata object with
            // Resources and NonBillableQuantity.
            var json = JObject.Parse(@"{
                ""value"": [
                    { ""users"": [
                        {
                            ""tenantId"": ""00000000-0000-0000-0000-0000000000t1"",
                            ""userId"": ""00000000-0000-0000-0000-0000000000u1"",
                            ""consumed"": 0.0,
                            ""unit"": ""Messages"",
                            ""metadata"": { ""Resources"": 1, ""NonBillableQuantity"": 1.0 },
                            ""asOfDate"": ""2026-08-15T00:00:00""
                        }
                    ] }
                ]
            }");

            var row = CopilotStudioCreditParser.ParseUserConsumptionPage(json).Rows.Single();

            Assert.AreEqual("00000000-0000-0000-0000-0000000000u1", row.UserId);
            Assert.AreEqual(0m, row.Consumed);
            Assert.AreEqual("Messages", row.Unit);
            Assert.AreEqual(new DateTime(2026, 8, 15), row.AsOfDate.Value.Date);
            Assert.AreEqual(1.0m, row.NonBillableQuantity,
                "A user can be charged nothing while still having used an agent - that must not be lost.");
        }

        [TestMethod]
        public void CreditParser_CapacityWithPayGoEntitledButNoConsumed_LeavesConsumedNull()
        {
            // The live-tenant shape: payGo carries "entitled" and no "consumed". Reporting entitled as
            // consumed would be a straightforward lie on a spend page, so it must stay null.
            var json = JObject.Parse(@"{
                ""entitlement"": {
                    ""capacity"": {
                        ""entitled"": { ""value"": 25000.0 },
                        ""consumed"": { ""value"": 0.0, ""consumptionType"": ""NotSpecified"", ""writeOff"": 0.0 },
                        ""allocated"": { ""value"": 0.0, ""autoAllocated"": 0.0 },
                        ""availableQuantity"": 25000.0,
                        ""status"": ""WithinCapacity""
                    },
                    ""payGo"": { ""entitled"": { ""value"": 0.0 } }
                },
                ""entitlementId"": ""MCSMessages""
            }");

            var capacity = CopilotStudioCreditParser.ParseCapacity(json);

            Assert.AreEqual(25000m, capacity.Entitled);
            Assert.AreEqual(0m, capacity.Consumed);
            Assert.AreEqual(25000m, capacity.Available);
            Assert.AreEqual("WithinCapacity", capacity.Status);
            Assert.IsNull(capacity.PayAsYouGoConsumed,
                "payGo.entitled must NOT be reported as payGo consumption.");
        }

        [TestMethod]
        public void CreditParser_PerUserNestedEnvelope_IsParsed()
        {
            // A different envelope from the per-agent read: value[].users[], not value[].resources[].
            var json = JObject.Parse(@"{
                ""value"": [
                    { ""users"": [
                        { ""userId"": ""00000000-0000-0000-0000-0000000000u1"", ""environmentId"": ""env-1"",
                          ""consumed"": 42.5, ""unit"": ""Count"", ""asOfDate"": ""2026-09-03T00:00:00Z"" }
                    ] }
                ],
                ""continuationtoken"": """"
            }");

            var page = CopilotStudioCreditParser.ParseUserConsumptionPage(json);

            Assert.AreEqual(1, page.Rows.Count);
            Assert.AreEqual("00000000-0000-0000-0000-0000000000u1", page.Rows[0].UserId);
            Assert.AreEqual(42.5m, page.Rows[0].Consumed);
            Assert.IsFalse(page.HasMore);
        }

        [TestMethod]
        public void CreditParser_PerUserFlatEnvelope_IsAlsoParsed()
        {
            var json = JObject.Parse(@"{ ""value"": [ { ""userId"": ""u1"", ""consumed"": 3 } ] }");
            Assert.AreEqual("u1", CopilotStudioCreditParser.ParseUserConsumptionPage(json).Rows.Single().UserId);
        }

        [TestMethod]
        public void CreditParser_PerUserRowWithNoUser_IsDropped()
        {
            // Storing it under a null identifier would create a phantom "unknown user" that silently
            // accumulates other people's spend.
            var json = JObject.Parse(@"{ ""value"": [ { ""consumed"": 3 }, { ""userId"": ""  "", ""consumed"": 4 }, { ""userId"": ""u1"", ""consumed"": 5 } ] }");

            var rows = CopilotStudioCreditParser.ParseUserConsumptionPage(json).Rows;

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("u1", rows[0].UserId);
        }

        [TestMethod]
        public void MapUserRows_CombinesRepeatsAndUsesTheRequestedDay()
        {
            var day = new DateTime(2026, 9, 3);

            var mapped = CopilotStudioCreditImporter.MapUserRows(
                new[]
                {
                    new CopilotStudioUserCreditRow { UserId = "u1", EnvironmentId = "env-1", Consumed = 2m },
                    new CopilotStudioUserCreditRow { UserId = "u1", EnvironmentId = "env-1", Consumed = 3m },
                    new CopilotStudioUserCreditRow { UserId = "u1", EnvironmentId = "env-2", Consumed = 7m },
                },
                day, null, DateTime.UtcNow);

            Assert.AreEqual(2, mapped.Count, "Environment is part of the identity.");
            Assert.AreEqual(5m, mapped.Single(r => r.EnvironmentId == "env-1").BilledCredits);
            Assert.IsTrue(mapped.All(r => r.UsageDate == day));
        }

        [TestMethod]
        public async Task CreditImporter_WhenThePerUserRouteIsUnavailable_NothingIsStoredAndTheFailureIsRecorded()
        {
            // Without /users there is nothing to derive per-agent figures from, so the import must not claim
            // success: a missing route is recorded as a retryable failure on both logs.
            var source = new ScriptedCreditSource { UsersRouteMissing = true };
            var store = new RecordingAgentCostStore();

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 2).ImportConsumptionAsync();

            Assert.IsFalse(result.Users.Succeeded);
            Assert.IsFalse(result.Agents.Succeeded);
            Assert.IsTrue(result.Agents.IsTransientFailure);
            Assert.AreEqual(0, store.UserCredits.Count);
            Assert.AreEqual(0, store.Credits.Count);
        }

        [TestMethod]
        public async Task CreditImporter_ADayWithNoPerUserData_DoesNotDiscardTheOtherDays()
        {
            // A quiet day comes back as an empty page, which is NOT the same as the route being absent.
            var source = new ScriptedCreditSource();
            source.AddUsers(new DateTime(2026, 9, 8), null, null, new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 5m });
            // 2026-09-09 deliberately absent => an empty page for that day.

            var store = new RecordingAgentCostStore();
            var clock = new FixedClock(new DateTime(2026, 9, 9, 6, 0, 0, DateTimeKind.Utc));

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 2, clock).ImportConsumptionAsync();

            Assert.IsTrue(result.Users.Succeeded);
            Assert.AreEqual(1, store.UserCredits.Count, "The day that DID have data must still be stored.");
            Assert.AreEqual(5m, store.UserCredits[0].BilledCredits);
        }

        [TestMethod]
        public async Task CreditImporter_APersonWhoseRowsSpanTwoPages_KeepsTheCreditsFromBoth()
        {
            // Mapped a page at a time, a person whose rows straddled a page boundary kept only the last page's credits.
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, "page-2", new CopilotStudioUserCreditRow { UserId = "u1", EnvironmentId = "env-1", Consumed = 2m });
            source.AddUsers(day, "page-2", null, new CopilotStudioUserCreditRow { UserId = "u1", EnvironmentId = "env-1", Consumed = 3m });

            var store = new RecordingAgentCostStore();
            var clock = new FixedClock(new DateTime(2026, 9, 9, 6, 0, 0, DateTimeKind.Utc));

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, clock).ImportConsumptionAsync();

            Assert.IsTrue(result.Users.Succeeded);
            Assert.AreEqual(2, result.Users.Log.RowsRead, "both pages must be read");
            Assert.AreEqual(1, store.UserCredits.Count, "one person-day in one environment is one identity");
            Assert.AreEqual(5m, store.UserCredits[0].BilledCredits, "the credits from both pages must be kept");
            Assert.AreEqual(1, source.ResourceCalls.Count, "The same user on two pages is fanned out once.");
        }

        [TestMethod]
        public async Task CreditImporter_PerUserRowsOfPeopleOutsideUserGroupsFilter_AreNotStoredButStillCountInPerAgentFigures()
        {
            const string pilotId = "bbbbbbbb-0000-0000-0000-000000000001";
            const string outsiderId = "bbbbbbbb-0000-0000-0000-000000000002";
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, null,
                new CopilotStudioUserCreditRow { UserId = pilotId, EnvironmentId = "env-1", Consumed = 5m },
                new CopilotStudioUserCreditRow { UserId = outsiderId, EnvironmentId = "env-1", Consumed = 7m });
            source.AddResources(pilotId, day, null, null, AgentRow("agent-1", 5m));
            source.AddResources(outsiderId, day, null, null, AgentRow("agent-1", 7m));
            var store = new RecordingAgentCostStore();
            var clock = new FixedClock(new DateTime(2026, 9, 9, 6, 0, 0, DateTimeKind.Utc));
            var scope = FakeLoaderClasses.TestUserScopes.OfMembers((pilotId, "pilot@contoso.com", "pilot@contoso.com"));

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, clock,
                userScopeProvider: FakeLoaderClasses.TestUserScopes.Provider(scope)).ImportConsumptionAsync();

            Assert.IsTrue(result.Users.Succeeded);
            Assert.AreEqual(2, result.Users.Log.RowsRead, "Both rows are read...");
            Assert.AreEqual(5m, store.UserCredits.Single().BilledCredits, "...but only the credits of the person in scope are stored per person.");

            var agent = store.Credits.Single();
            Assert.AreEqual(12m, agent.BilledCredits, "Per-agent figures stay tenant-wide: no identities are stored there.");
            Assert.AreEqual(2, agent.DistinctUsers);
        }

        private static CopilotStudioCreditRow AgentRow(string agentId, decimal consumed, string feature = "Generative answer",
            string env = "env-1", decimal? nonBilled = null) => new CopilotStudioCreditRow
            {
                EnvironmentId = env,
                ResourceId = agentId,
                ResourceName = "Contoso " + agentId,
                FeatureName = feature,
                Consumed = consumed,
                NonBillableQuantity = nonBilled,
            };

        [TestMethod]
        public async Task CreditImporter_DerivesPerAgentFiguresFromPerUserReads_SummingCreditsAndCountingDistinctUsers()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, null,
                new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 3m },
                new CopilotStudioUserCreditRow { UserId = "u2", Consumed = 4m },
                new CopilotStudioUserCreditRow { UserId = "u3", Consumed = 0m, NonBillableQuantity = 2m });
            source.AddResources("u1", day, null, null, AgentRow("a", 2m, nonBilled: 1m), AgentRow("b", 1m));
            source.AddResources("u2", day, null, null, AgentRow("a", 4m, nonBilled: 3m));
            source.AddResources("u3", day, null, null, AgentRow("a", 0m, nonBilled: 2m), AgentRow("a", 0m, feature: "Classic answer", nonBilled: 1m));
            var store = new RecordingAgentCostStore();
            var clock = new FixedClock(day.AddHours(6));

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, clock).ImportConsumptionAsync();

            Assert.IsTrue(result.Agents.Succeeded);
            Assert.AreEqual(3, store.Credits.Count, "Agent a/Generative answer, agent b, agent a/Classic answer.");

            var a = store.Credits.Single(r => r.AgentId == "a" && r.FeatureName == "Generative answer");
            Assert.AreEqual(6m, a.BilledCredits);
            Assert.AreEqual(6m, a.NonBilledCredits, "1 + 3 + 2");
            Assert.AreEqual(3, a.DistinctUsers, "u1, u2 and u3 all contributed.");
            Assert.AreEqual(day, a.UsageDate);
            Assert.AreEqual("Contoso a", a.AgentName);
            Assert.IsNull(a.LastRefreshedUtc, "Microsoft's refresh stamp is not available from the per-user route.");

            Assert.AreEqual(1, store.Credits.Single(r => r.AgentId == "b").DistinctUsers);
            Assert.AreEqual(1, store.Credits.Single(r => r.FeatureName == "Classic answer").DistinctUsers);
            Assert.IsTrue(store.Logs.Any(l => l.ImportName == AgentCostImportNames.CopilotStudioCredits && string.IsNullOrEmpty(l.Error)));
            Assert.IsTrue(store.Logs.Any(l => l.ImportName == AgentCostImportNames.CopilotStudioUserCredits && string.IsNullOrEmpty(l.Error)));
        }

        [TestMethod]
        public async Task CreditImporter_UsersWithNoConsumption_AreNotFannedOut()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, null,
                new CopilotStudioUserCreditRow { UserId = "idle-1", Consumed = 0m },
                new CopilotStudioUserCreditRow { UserId = "idle-2", Consumed = 0m, NonBillableQuantity = 0m, ResourceCount = 0m },
                new CopilotStudioUserCreditRow { UserId = "free-only", Consumed = 0m, NonBillableQuantity = 1m },
                new CopilotStudioUserCreditRow { UserId = "resources-only", Consumed = 0m, ResourceCount = 1m },
                new CopilotStudioUserCreditRow { UserId = "billed", Consumed = 2m });

            await new CopilotStudioCreditImporter(Logger, source, new RecordingAgentCostStore(), 1, new FixedClock(day.AddHours(6)))
                .ImportConsumptionAsync();

            CollectionAssert.AreEquivalent(new[] { "free-only", "resources-only", "billed" },
                source.ResourceCalls.Select(c => c.Item1).ToList());
        }

        [TestMethod]
        public async Task CreditImporter_APersonsAgentRowsSpanningPages_AreAllCounted()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, null, new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 3m });
            source.AddResources("u1", day, null, "t2", AgentRow("a", 1m));
            source.AddResources("u1", day, "t2", null, AgentRow("a", 2m), AgentRow("b", 5m));
            var store = new RecordingAgentCostStore();

            await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.AreEqual(3m, store.Credits.Single(r => r.AgentId == "a").BilledCredits);
            Assert.AreEqual(5m, store.Credits.Single(r => r.AgentId == "b").BilledCredits);
            Assert.AreEqual(1, store.Credits.Single(r => r.AgentId == "a").DistinctUsers, "One user across two pages is one user.");
        }

        [TestMethod]
        public async Task CreditImporter_RepeatedPerUserResourceContinuationToken_FailsWithoutStoringAPartialWindow()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, null, new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 3m });
            source.AddResources("u1", day, null, "same", AgentRow("a", 1m));
            source.AddResources("u1", day, "same", "same", AgentRow("a", 1m));
            var store = new RecordingAgentCostStore();

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.IsFalse(result.Agents.Succeeded);
            StringAssert.Contains(result.Agents.Log.Error, "already returned");
            Assert.AreEqual(0, store.Credits.Count, "A partial window must not be written.");
            Assert.IsTrue(result.Users.Succeeded, "The per-user rows were complete and stay stored.");
        }

        [TestMethod]
        public async Task CreditImporter_RepeatedUserListContinuationToken_FailsBothImports()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, "same", new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 3m });
            source.AddUsers(day, "same", "same", new CopilotStudioUserCreditRow { UserId = "u2", Consumed = 3m });
            var store = new RecordingAgentCostStore();

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.IsFalse(result.Agents.Succeeded);
            Assert.IsFalse(result.Users.Succeeded);
            Assert.AreEqual(0, store.Credits.Count);
            Assert.AreEqual(0, store.UserCredits.Count);
            Assert.AreEqual(0, source.ResourceCalls.Count, "No fan-out from an incomplete user list.");
        }

        [TestMethod]
        public async Task CreditImporter_APerUserResourceReadRefused_IsAnAuthorisationOutcome()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource
            {
                ResourcesError = new AgentCostAuthorisationException("Synthetic refusal"),
            };
            source.AddUsers(day, null, null,
                new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 3m },
                new CopilotStudioUserCreditRow { UserId = "u2", Consumed = 3m });
            var store = new RecordingAgentCostStore();

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.IsTrue(result.Agents.IsAuthorisationFailure);
            Assert.AreEqual("Synthetic refusal", result.Agents.Log.Error);
            Assert.AreEqual(0, store.Credits.Count);
            Assert.IsTrue(result.Users.Succeeded, "The per-user rows were stored before the fan-out began.");
        }

        [TestMethod]
        public async Task CreditImporter_FanOutConcurrencyIsBounded()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource { TrackConcurrency = true };
            var users = Enumerable.Range(0, 40).Select(i => new CopilotStudioUserCreditRow { UserId = "u" + i, Consumed = 1m }).ToArray();
            source.AddUsers(day, null, null, users);
            foreach (var u in users) source.AddResources(u.UserId, day, null, null, AgentRow("a", 1m));
            var store = new RecordingAgentCostStore();

            await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.AreEqual(40, source.ResourceCalls.Count);
            Assert.IsTrue(source.MaxConcurrency <= 8, $"Observed {source.MaxConcurrency} concurrent reads.");
            Assert.AreEqual(40, store.Credits.Single().DistinctUsers);
            Assert.AreEqual(40m, store.Credits.Single().BilledCredits);
        }

        [TestMethod]
        public async Task CreditImporter_WithNoConnection_ReadsNothingAndRecordsConnectionRequired()
        {
            var source = new ScriptedCreditSource { CanRead = false };
            var store = new RecordingAgentCostStore();

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 3).ImportConsumptionAsync();

            Assert.AreEqual(0, source.UserCalls.Count);
            Assert.AreEqual(0, source.ResourceCalls.Count);
            Assert.AreEqual(0, source.EnvironmentNameCalls, "The application identity is refused on the environment route too.");
            Assert.IsTrue(result.Agents.IsConnectionRequired);
            Assert.IsTrue(result.Agents.Succeeded, "Not a failure: nothing was refused.");
            Assert.IsFalse(result.Agents.IsTransientFailure);
            Assert.IsFalse(result.Agents.IsAuthorisationFailure);
            Assert.AreEqual(AgentCostImportNames.ConnectionRequired, store.Logs.Single(l => l.ImportName == AgentCostImportNames.CopilotStudioCredits).Error);
            Assert.AreEqual(AgentCostImportNames.ConnectionRequired, store.Logs.Single(l => l.ImportName == AgentCostImportNames.CopilotStudioUserCredits).Error);
        }

        [TestMethod]
        public void ConnectionRequiredKey_IsTheOneTheImporterAndPortalShare()
        {
            Assert.AreEqual(AgentCostDelegatedHttpClient.ConnectionRequiredDiagnostic, AgentCostImportNames.ConnectionRequired);
        }

        [TestMethod]
        public void ImportOutcome_SeparatesRefusedFromTransient()
        {
            var refused = new AgentCostImportOutcome(new AgentCostImportLog { Error = "403" }, isAuthorisationFailure: true);
            var transient = new AgentCostImportOutcome(new AgentCostImportLog { Error = "timeout" });
            var ok = new AgentCostImportOutcome(new AgentCostImportLog());

            Assert.IsTrue(refused.IsAuthorisationFailure);
            Assert.IsFalse(refused.IsTransientFailure, "A refusal is not transient.");
            Assert.IsTrue(transient.IsTransientFailure);
            Assert.IsFalse(transient.IsAuthorisationFailure);
            Assert.IsFalse(ok.IsTransientFailure, "A success is not a failure of any kind.");
            Assert.IsTrue(ok.Succeeded);
        }

        [TestMethod]
        public async Task AzureImporter_OneScopeRefusedAndAnotherTransient_IsNotTreatedAsRefused()
        {
            // Both scopes fail, so an "errors.Count == Scopes.Count" test would call the whole run refused
            // and back off for a day - suppressing the retry the timeout deserves.
            var settings = new AzureCostImportSettings { Scopes = new[] { "/subscriptions/a", "/subscriptions/b" } };
            var source = new PerScopeAzureCostSource
            {
                Failures =
                {
                    { "/subscriptions/a", new AgentCostAuthorisationException("refused") },
                    { "/subscriptions/b", new InvalidOperationException("timeout") },
                },
            };

            var outcome = await new AzureCostImporter(Logger, source, new RecordingAgentCostStore(), settings).ImportAsync();

            Assert.IsFalse(outcome.Succeeded);
            Assert.IsFalse(outcome.IsAuthorisationFailure,
                "A transient failure alongside a refusal must still earn a prompt retry.");
        }

        [TestMethod]
        public async Task AzureImporter_EveryScopeRefused_BacksOff()
        {
            var settings = new AzureCostImportSettings { Scopes = new[] { "/subscriptions/a", "/subscriptions/b" } };
            var source = new PerScopeAzureCostSource
            {
                Failures =
                {
                    { "/subscriptions/a", new AgentCostAuthorisationException("refused") },
                    { "/subscriptions/b", new AgentCostAuthorisationException("refused") },
                },
            };

            var outcome = await new AzureCostImporter(Logger, source, new RecordingAgentCostStore(), settings).ImportAsync();

            Assert.IsTrue(outcome.IsAuthorisationFailure,
                "Retrying cannot fix a role assignment, so an all-refused run waits for the normal interval.");
        }

        [TestMethod]
        public void AzureImporter_RefreshWindow_ReachesPastAPeriodsCloseSoItIsReadWhileFinal()
        {
            // If the window contracted the moment a period stopped being provisional, that period would never
            // once be read as final: its rows would keep their last estimate and stay flagged "estimate" for
            // ever, and any adjustment Azure made at close would be missed.
            var closesOn = new DateTime(2026, 9, 1).AddDays(AzureCostImporter.BillingPeriodFinalisationLagDays);

            // The day after August closes, August must still be in the refresh window...
            var dayAfterClose = closesOn.AddDays(1);
            Assert.IsFalse(AzureCostImporter.IsStillProvisional(new DateTime(2026, 8, 1), dayAfterClose),
                "August is final by this date.");
            Assert.AreEqual(new DateTime(2026, 8, 1), AzureCostImporter.EarliestRefreshDate(dayAfterClose),
                "...so it must still be re-read, in order to be stored as final.");

            // ...and later in the month it correctly drops out.
            Assert.AreEqual(new DateTime(2026, 9, 1), AzureCostImporter.EarliestRefreshDate(new DateTime(2026, 9, 20)));
        }

        [TestMethod]
        public async Task AzureImporter_ReplacesEachScopeAndWindowRatherThanOnlyAdding()
        {
            // A Cost Management result is a complete snapshot of its scope and window. Upserting only would
            // leave superseded rows behind, so narrowing the meter filter would keep the old wider rows and
            // changing the grouping would store the same money twice - both looking like a rise in spend.
            var settings = new AzureCostImportSettings { Scopes = new[] { "/subscriptions/a" } };
            var store = new RecordingAgentCostStore();
            var source = new PerScopeAzureCostSource();

            await new AzureCostImporter(Logger, source, store, settings).ImportAsync();

            Assert.AreEqual(1, store.AzureReplaceCalls.Count, "The write must go through the replace path.");
            Assert.AreEqual("/subscriptions/a", store.AzureReplaceCalls[0].Item1,
                "The scope must be passed, or the replace could delete another scope's rows.");
            Assert.IsNotNull(store.AzureReplaceCalls[0].Item2, "The window must be passed even when no rows came back.");
        }

        /// <summary>An Azure source that throws a configured exception per scope.</summary>
        private class PerScopeAzureCostSource : IAzureCostSource
        {
            public Dictionary<string, Exception> Failures { get; } = new Dictionary<string, Exception>();

            public Task<IReadOnlyList<AzureCostRow>> GetDailyCostsAsync(string scope, DateTime fromDate, DateTime toDate)
            {
                if (Failures.TryGetValue(scope, out var ex)) throw ex;
                return Task.FromResult<IReadOnlyList<AzureCostRow>>(new List<AzureCostRow>());
            }
        }

        [TestMethod]
        public async Task Phase_WithNoConnection_StampsTheGateAndStillImportsCapacity()
        {
            // The application identity can only read capacity. Calling the consumption routes would be a 403 every
            // cycle, so the importer records "connection required" and the cadence gate treats that as a clean run.
            var settings = new AppConfig
            {
                ImportJobSettings = new ImportTaskSettings { CopilotStudioCredits = true },
                CopilotStudioCreditsIntervalHours = 24,
            };
            var lastRunStore = new InMemoryImportLastRunStore();
            var store = new RecordingAgentCostStore();
            var source = new ScriptedCreditSource
            {
                CanRead = false,
                Capacity = new CopilotStudioCapacitySnapshot { Entitled = 100m, Consumed = 10m },
            };

            var phase = new AgentCostImportPhase(
                Logger,
                settings,
                lastRunStore,
                creditImporterFactory: () => new CopilotStudioCreditImporter(Logger, source, store, 1),
                azureCostImporterFactory: () => new AzureCostImporter(Logger, new ThrowingAzureCostSource(), store, new AzureCostImportSettings()));

            await phase.RunAsync();

            Assert.IsNotNull(await lastRunStore.GetLastRunUtc(AgentCostImportPhase.CopilotStudioCreditsLastImportedKey),
                "Connection-required is a settled state, not a failure to retry every cycle.");
            Assert.AreEqual(0, source.UserCalls.Count);
            Assert.AreEqual(0, source.ResourceCalls.Count);
            Assert.AreEqual(1, store.Capacities.Count, "Capacity must still be imported app-only.");
        }

        [TestMethod]
        public async Task Phase_ConnectionChangesBypassTheOldGateAndNeverChangeAzuresGate()
        {
            var settings = new AppConfig
            {
                ImportJobSettings = new ImportTaskSettings { CopilotStudioCredits = true, AzureCostManagement = true },
                CopilotStudioCreditsIntervalHours = 24,
                AzureCostImport = new AzureCostImportSettings { IntervalHours = 24 },
            };
            var stamps = new InMemoryImportLastRunStore();
            var now = DateTime.UtcNow;
            await stamps.SetLastRunUtc(AgentCostImportPhase.CopilotStudioCreditsLastImportedKey, now);
            await stamps.SetLastRunUtc(AgentCostImportPhase.AzureCostLastImportedKey, now);
            var source = new ScriptedCreditSource();
            var store = new RecordingAgentCostStore();
            var version = "synthetic-generation-1";
            var phase = new AgentCostImportPhase(Logger, settings, stamps,
                () => new CopilotStudioCreditImporter(Logger, source, store, 1),
                () => throw new AssertFailedException("Azure's existing gate must be unchanged."),
                creditConnectionVersion: () => Task.FromResult(version));
            await phase.RunAsync();
            Assert.AreEqual(1, source.UserCalls.Count);
            await phase.RunAsync();
            Assert.AreEqual(1, source.UserCalls.Count, "The same connection must keep its daily cadence.");
            version = "synthetic-disconnected-generation";
            await phase.RunAsync();
            Assert.AreEqual(2, source.UserCalls.Count, "Disconnect must allow the app-only path a prompt retry too.");
            Assert.AreEqual(now, await stamps.GetLastRunUtc(AgentCostImportPhase.AzureCostLastImportedKey));
        }

        [TestMethod]
        public async Task Phase_UnreachableDelegatedStateDoesNotStopAzureCosts()
        {
            var settings = new AppConfig
            {
                ImportJobSettings = new ImportTaskSettings { CopilotStudioCredits = true, AzureCostManagement = true },
            };
            var store = new RecordingAgentCostStore();
            var azureConstructed = false;
            var phase = new AgentCostImportPhase(Logger, settings, new InMemoryImportLastRunStore(),
                () => throw new AssertFailedException("Do not silently fall back to app-only on a Storage outage."),
                () =>
                {
                    azureConstructed = true;
                    return new AzureCostImporter(Logger, new ThrowingAzureCostSource(), store, new AzureCostImportSettings());
                },
                creditConnectionVersion: () => throw new InvalidOperationException("Synthetic Storage outage"));
            await phase.RunAsync();
            Assert.IsTrue(azureConstructed);
        }

        [TestMethod]
        public async Task DelegatedSource_RefusedConsumptionKeepsAppOnlyCapacityAndRequiresReconnect()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            await store.ConnectAsync("synthetic-encrypted-cache");
            var head = await store.GetHeadAsync();
            var source = new DelegatedAgentCostSource(
                new FailingCreditSource(new AgentCostAuthorisationException("Synthetic delegated refusal")),
                new ScriptedCreditSource(), store, head.Version);
            var error = await Assert.ThrowsExceptionAsync<AgentCostAuthorisationException>(() =>
                source.GetUserConsumptionPageAsync(DateTime.UtcNow, DateTime.UtcNow, null));
            Assert.AreEqual(AgentCostDelegatedHttpClient.ReconnectDiagnostic, error.Message);
            Assert.IsNull(await source.GetCapacityAsync(), "Capacity must use the independent app-only source.");
            Assert.AreEqual("reconnectNeeded", await store.GetStatusAsync());
        }

        [TestMethod]
        public async Task DelegatedSource_RefusedPerUserResourceReadRequiresReconnect()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            await store.ConnectAsync("synthetic-encrypted-cache");
            var head = await store.GetHeadAsync();
            var source = new DelegatedAgentCostSource(
                new FailingCreditSource(new AgentCostAuthorisationException("Synthetic per-user refusal")),
                new ScriptedCreditSource(), store, head.Version);
            Assert.IsTrue(source.CanReadConsumption);
            var error = await Assert.ThrowsExceptionAsync<AgentCostAuthorisationException>(() =>
                source.GetUserResourceConsumptionPageAsync("u1", DateTime.UtcNow, null));
            Assert.AreEqual(AgentCostDelegatedHttpClient.ReconnectDiagnostic, error.Message);
            Assert.AreEqual("reconnectNeeded", await store.GetStatusAsync());
        }

        /// <summary>
        /// A fully scriptable source. Pages are keyed by (day, continuation token) - the first page's token being
        /// empty - so a test can return a day in several pages. An unlisted page is empty and final.
        /// </summary>
        private class ScriptedCreditSource : ICopilotStudioCreditSource
        {
            private readonly Dictionary<Tuple<DateTime, string>, CopilotStudioUserCreditPage> _users
                = new Dictionary<Tuple<DateTime, string>, CopilotStudioUserCreditPage>();
            private readonly Dictionary<Tuple<string, DateTime, string>, CopilotStudioCreditPage> _resources
                = new Dictionary<Tuple<string, DateTime, string>, CopilotStudioCreditPage>();
            private readonly object _gate = new object();
            private int _current;

            public bool CanRead { get; set; } = true;
            public bool UsersRouteMissing { get; set; }
            public Exception ResourcesError { get; set; }
            public bool TrackConcurrency { get; set; }
            public bool ThrowOnEnvironmentNames { get; set; }
            public CopilotStudioCapacitySnapshot Capacity { get; set; }
            public int EnvironmentNameCalls { get; private set; }
            public int MaxConcurrency { get; private set; }

            /// <summary>(from date, continuation token) of every /users read.</summary>
            public List<Tuple<DateTime, string>> UserCalls { get; } = new List<Tuple<DateTime, string>>();

            /// <summary>(user id, day, continuation token) of every per-user-by-agent read.</summary>
            public List<Tuple<string, DateTime, string>> ResourceCalls { get; } = new List<Tuple<string, DateTime, string>>();

            public bool CanReadConsumption => CanRead;

            public void AddUsers(DateTime day, string token, string next, params CopilotStudioUserCreditRow[] rows)
                => _users[Tuple.Create(day.Date, token ?? string.Empty)] = new CopilotStudioUserCreditPage(rows, next);

            public void AddResources(string userId, DateTime day, string token, string next, params CopilotStudioCreditRow[] rows)
                => _resources[Tuple.Create(userId, day.Date, token ?? string.Empty)] = new CopilotStudioCreditPage(rows, next);

            public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
            {
                lock (_gate) UserCalls.Add(Tuple.Create(fromDate.Date, continuationToken));
                if (UsersRouteMissing) return Task.FromResult<CopilotStudioUserCreditPage>(null);
                return Task.FromResult(_users.TryGetValue(Tuple.Create(fromDate.Date, continuationToken ?? string.Empty), out var page)
                    ? page
                    : new CopilotStudioUserCreditPage(new List<CopilotStudioUserCreditRow>(), null));
            }

            public async Task<CopilotStudioCreditPage> GetUserResourceConsumptionPageAsync(string userId, DateTime day, string continuationToken)
            {
                lock (_gate)
                {
                    ResourceCalls.Add(Tuple.Create(userId, day.Date, continuationToken));
                    _current++;
                    if (_current > MaxConcurrency) MaxConcurrency = _current;
                }
                try
                {
                    if (TrackConcurrency) await Task.Delay(5);
                    if (ResourcesError != null) throw ResourcesError;
                    return _resources.TryGetValue(Tuple.Create(userId, day.Date, continuationToken ?? string.Empty), out var page)
                        ? page
                        : new CopilotStudioCreditPage(new List<CopilotStudioCreditRow>(), null);
                }
                finally
                {
                    lock (_gate) _current--;
                }
            }

            public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync() => Task.FromResult(Capacity);

            public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
            {
                EnvironmentNameCalls++;
                if (ThrowOnEnvironmentNames) throw new InvalidOperationException("environment lookup failed");
                return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
            }
        }

        /// <summary>A clock fixed at a known instant, so window arithmetic is assertable.</summary>
        private class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) { UtcNow = utcNow; }
            public DateTime UtcNow { get; }
        }

        private static AzureCostImporter NewAzureImporter(AzureCostImportSettings settings)
            => new AzureCostImporter(Logger, new ThrowingAzureCostSource(), new RecordingAgentCostStore(), settings);

        #endregion

        #region Credit importer behaviour

        [DataTestMethod]
        [DataRow(HttpStatusCode.Unauthorized, "capacity")]
        [DataRow(HttpStatusCode.Forbidden, "capacity")]
        [DataRow(HttpStatusCode.Unauthorized, "users")]
        [DataRow(HttpStatusCode.Forbidden, "users")]
        [DataRow(HttpStatusCode.Unauthorized, "userResources")]
        [DataRow(HttpStatusCode.Forbidden, "userResources")]
        public async Task CreditSource_RefusedRequest_DoesNotDiagnoseAMissingRole(HttpStatusCode status, string route)
        {
            var handler = new RecordingPostHandler(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}"),
            });
            using (var client = new DataUtils.Http.AutoThrottleHttpClient(handler, Logger))
            {
                var source = new PowerPlatformLicensingCreditSource(client, Logger, delegated: route != "capacity");
                Func<Task> read;
                var day = new DateTime(2026, 1, 1);
                switch (route)
                {
                    case "capacity": read = () => source.GetCapacityAsync(); break;
                    case "users": read = () => source.GetUserConsumptionPageAsync(day, day, null); break;
                    case "userResources": read = () => source.GetUserResourceConsumptionPageAsync("bbbbbbbb-0000-0000-0000-000000000001", day, null); break;
                    default: throw new ArgumentOutOfRangeException(nameof(route));
                }
                var error = await Assert.ThrowsExceptionAsync<AgentCostAuthorisationException>(
                    read);

                StringAssert.Contains(error.Message, $"HTTP {(int)status}");
                Assert.IsFalse(error.Message.Contains("tenant-wide per-agent"));
                Assert.IsTrue(error.Message.Length <= 1000, "Guidance must fit the import log's SQL column.");
                if (route != "capacity")
                {
                    StringAssert.Contains(error.Message, "Reconnect in Administration");
                    return;
                }

                StringAssert.Contains(error.Message, "HTTP " + (int)status + " (" + status + ")");
                StringAssert.Contains(error.Message, "already verified");
                StringAssert.Contains(error.Message, "same runtime app identity");
                StringAssert.Contains(error.Message, "Other imports are unaffected");
                Assert.IsFalse(error.Message.Contains("requires a signed-in"));
                if (status == HttpStatusCode.Unauthorized)
                    StringAssert.Contains(error.Message, "token audience (https://api.powerplatform.com)");
                else
                    StringAssert.Contains(error.Message, "does not prove that a role is missing");
            }
        }

        [TestMethod]
        public async Task CreditSource_UserResourceRead_AsksForOneUserAndOneDay()
        {
            var handler = new RecordingPostHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[{\"resources\":[]}]}"),
            });
            using (var client = new DataUtils.Http.AutoThrottleHttpClient(handler, Logger))
            {
                var source = new PowerPlatformLicensingCreditSource(client, Logger, delegated: true);
                var page = await source.GetUserResourceConsumptionPageAsync(
                    "bbbbbbbb-0000-0000-0000-000000000001", new DateTime(2026, 9, 9), null);

                Assert.AreEqual(0, page.Rows.Count);
                var uri = handler.Uris.Single();
                StringAssert.Contains(uri, "/MCSMessages/users/bbbbbbbb-0000-0000-0000-000000000001/resources");
                StringAssert.Contains(uri, "fromDate=2026-09-09");
                StringAssert.Contains(uri, "toDate=2026-09-09");
                Assert.IsFalse(uri.Contains("includeFields"));
            }
        }

        [TestMethod]
        public async Task CreditSource_AppOnly_NeverCallsConsumptionRoutes()
        {
            var handler = new RecordingPostHandler();
            using (var client = new DataUtils.Http.AutoThrottleHttpClient(handler, Logger))
            {
                var source = new PowerPlatformLicensingCreditSource(client, Logger);
                Assert.IsFalse(source.CanReadConsumption);
                var names = await source.GetEnvironmentNamesAsync();
                Assert.AreEqual(0, names.Count);
                Assert.AreEqual(0, handler.Uris.Count, "No request may be sent by the application identity for consumption or names.");
            }
        }

        [TestMethod]
        public async Task CreditImporter_PagesUntilTheContinuationTokenRunsOut()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource();
            source.AddUsers(day, null, "token-1", new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 1m });
            source.AddUsers(day, "token-1", null, new CopilotStudioUserCreditRow { UserId = "u2", Consumed = 2m });

            var store = new RecordingAgentCostStore();
            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.AreEqual(2, result.Users.Log.RowsRead);
            Assert.AreEqual(2, source.UserCalls.Count, "/users is read once per day, page by page, and never twice.");
            Assert.AreEqual("token-1", source.UserCalls[1].Item2, "The second call must carry the first page's token.");
            Assert.IsTrue(string.IsNullOrEmpty(result.Users.Log.Error));
        }

        [TestMethod]
        public async Task CreditImporter_AuthorisationFailureOnTheUserList_IsRecordedWithTheRemedyAndDoesNotThrow()
        {
            var store = new RecordingAgentCostStore();
            var source = new FailingCreditSource(new AgentCostAuthorisationException(
                "The Power Platform licensing API refused the request ... assign the 'Power Platform reader' role ..."));

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 7).ImportConsumptionAsync();

            Assert.IsTrue(result.Agents.IsAuthorisationFailure);
            StringAssert.Contains(result.Agents.Log.Error, "Power Platform reader",
                "The stored error is the whole value of the log row - it must carry the fix, not just 'Forbidden'.");
            Assert.IsFalse(string.IsNullOrEmpty(result.Users.Log.Error));
        }

        [TestMethod]
        public async Task CreditImporter_EnvironmentNameLookupFailure_IsSurfacedAndNothingIsStored()
        {
            var day = new DateTime(2026, 9, 9);
            var source = new ScriptedCreditSource { ThrowOnEnvironmentNames = true };
            source.AddUsers(day, null, null, new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 5m });
            source.AddResources("u1", day, null, null, AgentRow("a", 5m));
            var store = new RecordingAgentCostStore();

            var result = await new CopilotStudioCreditImporter(Logger, source, store, 1, new FixedClock(day.AddHours(6))).ImportConsumptionAsync();

            Assert.AreEqual(1, source.EnvironmentNameCalls);
            Assert.IsFalse(result.Agents.Succeeded,
                "The failure is still surfaced - it is the real adapter that swallows it, not the importer.");
            Assert.AreEqual(0, store.Credits.Count);
        }

        #endregion

        #region Settings

        [TestMethod]
        public void AzureCostSettings_ParseList_TrimsDeduplicatesAndIgnoresBlanks()
        {
            var parsed = AzureCostImportSettings.ParseList("/subscriptions/a ; /subscriptions/b;; /subscriptions/A ");

            Assert.AreEqual(2, parsed.Count, "Blank entries are dropped and duplicates collapsed case-insensitively.");
            CollectionAssert.Contains(parsed.ToList(), "/subscriptions/a");
            CollectionAssert.Contains(parsed.ToList(), "/subscriptions/b");
        }

        [TestMethod]
        public void AzureCostSettings_WithNoScopes_IsNotConfigured()
        {
            Assert.IsFalse(new AzureCostImportSettings().IsConfigured);
            Assert.IsTrue(new AzureCostImportSettings { Scopes = new[] { "/subscriptions/x" } }.IsConfigured);
        }

        [TestMethod]
        public void AzureCostSettings_DefaultTrailingWindow_CoversAzuresRestatementLag()
        {
            // Azure keeps amending charges until roughly the fifth day after a period ends, so anything
            // shorter would freeze the first (lowest) estimate and never correct it.
            Assert.IsTrue(AzureCostImportSettings.DefaultTrailingWindowDays >= 5);
        }

        #endregion

        #region Fakes

        private class FailingCreditSource : ICopilotStudioCreditSource
        {
            private readonly Exception _exception;
            public FailingCreditSource(Exception exception) { _exception = exception; }

            public bool CanReadConsumption => true;

            public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
                => throw _exception;

            public Task<CopilotStudioCreditPage> GetUserResourceConsumptionPageAsync(string userId, DateTime day, string continuationToken)
                => throw _exception;

            public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync() => throw _exception;

            public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
                => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        private class ThrowingAzureCostSource : IAzureCostSource
        {
            public Task<IReadOnlyList<AzureCostRow>> GetDailyCostsAsync(string scope, DateTime fromDate, DateTime toDate)
                => throw new InvalidOperationException("this source should not be called");
        }

        private class RecordingAgentCostStore : IAgentCostStore
        {
            public List<CopilotStudioCreditDaily> Credits { get; } = new List<CopilotStudioCreditDaily>();
            public List<AzureCostDaily> Costs { get; } = new List<AzureCostDaily>();
            public List<AgentCostImportLog> Logs { get; } = new List<AgentCostImportLog>();
            public List<CopilotStudioCreditCapacity> Capacities { get; } = new List<CopilotStudioCreditCapacity>();

            public Task<int> UpsertCopilotStudioCreditsAsync(IReadOnlyList<CopilotStudioCreditDaily> rows)
            {
                Credits.AddRange(rows);
                return Task.FromResult(rows.Count);
            }

            public Task<int> UpsertAzureCostsAsync(IReadOnlyList<AzureCostDaily> rows)
            {
                Costs.AddRange(rows);
                return Task.FromResult(rows.Count);
            }

            /// <summary>Records the replace calls, so a test can assert the scope and window were passed.</summary>
            public List<Tuple<string, DateTime?, DateTime?>> AzureReplaceCalls { get; }
                = new List<Tuple<string, DateTime?, DateTime?>>();

            public Task<int> ReplaceAzureCostsAsync(IReadOnlyList<AzureCostDaily> rows, string scope, DateTime? from, DateTime? to)
            {
                AzureReplaceCalls.Add(Tuple.Create(scope, from, to));
                Costs.AddRange(rows ?? new List<AzureCostDaily>());
                return Task.FromResult(rows?.Count ?? 0);
            }

            public List<CopilotStudioCreditUserDaily> UserCredits { get; } = new List<CopilotStudioCreditUserDaily>();

            public Task<int> UpsertCopilotStudioUserCreditsAsync(IReadOnlyList<CopilotStudioCreditUserDaily> rows)
            {
                UserCredits.AddRange(rows);
                return Task.FromResult(rows.Count);
            }

            public Task SaveCapacitySnapshotAsync(CopilotStudioCreditCapacity snapshot)
            {
                Capacities.Add(snapshot);
                return Task.CompletedTask;
            }

            public Task SaveImportLogAsync(AgentCostImportLog log)
            {
                Logs.Add(log);
                return Task.CompletedTask;
            }
        }

        /// <summary>Returns the given responses in order and records each request body as sent.</summary>
        private sealed class RecordingPostHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;

            public RecordingPostHandler(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            public List<string> Bodies { get; } = new List<string>();
            public List<string> Uris { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Uris.Add(request.RequestUri.ToString());
                Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync());
                return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        }

        #endregion
    }
}
