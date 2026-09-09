using Common.Entities.Config;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataUtils;
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
            Assert.AreEqual(7, row.Users);
            Assert.AreEqual("Generative answer", row.FeatureName);
            Assert.AreEqual("gpt-4o", row.LlmModel);
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
                    Consumed = 10m, NonBillableQuantity = 1m, Users = 5, AsOfDate = new DateTime(2026, 9, 3),
                },
                new CopilotStudioCreditRow
                {
                    ResourceId = "agent-1", EnvironmentId = "env-1", FeatureName = "Generative answer",
                    Consumed = 4m, NonBillableQuantity = 2m, Users = 3, AsOfDate = new DateTime(2026, 9, 3),
                },
            };

            var mapped = CopilotStudioCreditImporter.MapAndAggregate(rows, from, to, null, DateTime.UtcNow);

            Assert.AreEqual(1, mapped.Count, "Two rows on the same dimension slice share an upsert key and must combine.");
            Assert.AreEqual(14m, mapped[0].BilledCredits, "Credits are money: they add up.");
            Assert.AreEqual(3m, mapped[0].NonBilledCredits);
            Assert.AreEqual(5, mapped[0].DistinctUsers,
                "User counts overlap between slices, so the combined value is the MAX - adding them would overstate reach.");
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
                    new CopilotStudioCreditRow { ResourceId = "a", FeatureName = "Generative answer", LlmModel = "gpt-4o", Consumed = 1m },
                    new CopilotStudioCreditRow { ResourceId = "a", FeatureName = "Generative answer", LlmModel = "gpt-4o-mini", Consumed = 1m },
                },
                new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), null, DateTime.UtcNow);

            Assert.AreEqual(2, mapped.Count, "The model is part of the billing tuple, so these are different slices.");
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
            // The licensing API aggregates over the range it is given: a seven-day request returns ONE
            // consumed value per dimension slice covering the whole week, not seven daily rows. Asking for a
            // range and then splitting it by the (undocumented) asOfDate would pile a week's spend onto a
            // single day. One request per day makes the usage date a fact about the request.
            var source = new FakeCreditSource();
            var clock = new FixedClock(new DateTime(2026, 9, 9, 6, 0, 0, DateTimeKind.Utc));

            await new CopilotStudioCreditImporter(Logger, source, new RecordingAgentCostStore(), 3, clock).ImportAsync();

            Assert.AreEqual(3, source.RequestedRanges.Count, "A three-day window must be three requests.");
            foreach (var range in source.RequestedRanges)
            {
                Assert.AreEqual(range.Item1, range.Item2,
                    $"Every request must be for a single day, but got {range.Item1:yyyy-MM-dd}..{range.Item2:yyyy-MM-dd}.");
            }

            CollectionAssert.AreEquivalent(
                new[] { new DateTime(2026, 9, 7), new DateTime(2026, 9, 8), new DateTime(2026, 9, 9) },
                source.RequestedRanges.Select(r => r.Item1).ToList(),
                "The trailing window must end today and cover exactly the configured number of days.");
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
        public async Task CreditImporter_WhenThePerUserRouteIsUnavailable_TheImportStillSucceeds()
        {
            // These routes are new (July 2026). A tenant whose API does not offer them is a legitimate
            // state, not a failure - and must not stop the per-agent figures being recorded as up to date.
            var source = new FakeCreditSource(); // Its per-user method returns null.
            var store = new RecordingAgentCostStore();

            var outcome = await new CopilotStudioCreditImporter(Logger, source, store, 2).ImportUserCreditsAsync();

            Assert.IsTrue(outcome.Succeeded, "An unavailable route is not an error.");
            Assert.AreEqual(0, outcome.Log.RowsRead);
            Assert.AreEqual(0, store.UserCredits.Count);
        }

        [TestMethod]
        public async Task CreditImporter_ADayWithNoPerUserData_DoesNotDiscardTheOtherDays()
        {
            // A quiet day comes back as 204 / an empty body, which is NOT the same as the route being
            // absent. Treating the two alike let one empty day abort the window and throw away every row
            // already read - and, because that path carries no error, report success while storing nothing.
            var source = new UserCreditSource();
            source.PagesByDay[new DateTime(2026, 9, 8)] = new CopilotStudioUserCreditPage(
                new[] { new CopilotStudioUserCreditRow { UserId = "u1", Consumed = 5m } }, null);
            // 2026-09-09 deliberately absent => an empty page for that day.

            var store = new RecordingAgentCostStore();
            var clock = new FixedClock(new DateTime(2026, 9, 9, 6, 0, 0, DateTimeKind.Utc));

            var outcome = await new CopilotStudioCreditImporter(Logger, source, store, 2, clock).ImportUserCreditsAsync();

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(1, store.UserCredits.Count, "The day that DID have data must still be stored.");
            Assert.AreEqual(5m, store.UserCredits[0].BilledCredits);
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

        /// <summary>A per-user source that answers from a per-day map; an unlisted day yields an empty page.</summary>
        private class UserCreditSource : ICopilotStudioCreditSource
        {
            public Dictionary<DateTime, CopilotStudioUserCreditPage> PagesByDay { get; }
                = new Dictionary<DateTime, CopilotStudioUserCreditPage>();

            public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
            {
                return Task.FromResult(PagesByDay.TryGetValue(fromDate.Date, out var page)
                    ? page
                    : new CopilotStudioUserCreditPage(new List<CopilotStudioUserCreditRow>(), null));
            }

            public Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
                => Task.FromResult(new CopilotStudioCreditPage(new List<CopilotStudioCreditRow>(), null));

            public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync() => Task.FromResult<CopilotStudioCapacitySnapshot>(null);

            public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
                => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
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

        [TestMethod]
        public async Task CreditImporter_PagesUntilTheContinuationTokenRunsOut()
        {
            // A one-day window, so this asserts PAGING alone rather than paging times the number of days.
            var source = new FakeCreditSource();
            source.Pages.Add(new CopilotStudioCreditPage(
                new[] { new CopilotStudioCreditRow { ResourceId = "a", Consumed = 1m } }, "token-1"));
            source.Pages.Add(new CopilotStudioCreditPage(
                new[] { new CopilotStudioCreditRow { ResourceId = "b", Consumed = 2m } }, null));

            var store = new RecordingAgentCostStore();
            var outcome = await new CopilotStudioCreditImporter(Logger, source, store, trailingWindowDays: 1).ImportAsync();
            var log = outcome.Log;

            Assert.AreEqual(2, log.RowsRead);
            Assert.AreEqual(2, source.Calls);
            Assert.AreEqual("token-1", source.LastContinuationToken, "The second call must carry the first page's token.");
            Assert.IsTrue(string.IsNullOrEmpty(log.Error));
        }

        [TestMethod]
        public async Task CreditImporter_RepeatedContinuationToken_FailsInsteadOfStoringDuplicatedSpend()
        {
            // A server that keeps handing back the same token would otherwise page for ever while appending
            // the same rows - and those rows aggregate by dimension hash, so the repeats would be SUMMED into
            // inflated spend. Failing the run keeps the last good figures instead.
            var source = new RepeatingTokenCreditSource();
            var store = new RecordingAgentCostStore();

            var outcome = await new CopilotStudioCreditImporter(Logger, source, store, 7).ImportAsync();
            var log = outcome.Log;

            Assert.IsTrue(source.Calls <= 3, $"Expected the importer to stop quickly, but it made {source.Calls} calls.");
            Assert.IsFalse(string.IsNullOrEmpty(log.Error),
                "An incomplete read must be recorded as a failure, not stamped as a successful import.");
            Assert.AreEqual(0, store.Credits.Count, "A partial window must not be written.");
        }

        [TestMethod]
        public async Task CreditImporter_AuthorisationFailure_IsRecordedWithTheRemedyAndDoesNotThrow()
        {
            var store = new RecordingAgentCostStore();
            var source = new FailingCreditSource(new AgentCostAuthorisationException(
                "The Power Platform licensing API refused the request ... assign the 'Power Platform reader' role ..."));

            var outcome = await new CopilotStudioCreditImporter(Logger, source, store, 7).ImportAsync();
            var log = outcome.Log;

            Assert.IsFalse(string.IsNullOrEmpty(log.Error), "A refused import must not look like an empty one.");
            StringAssert.Contains(log.Error, "Power Platform reader",
                "The stored error is the whole value of the log row - it must carry the fix, not just 'Forbidden'.");
            Assert.AreEqual(1, store.Logs.Count);
        }

        [TestMethod]
        public async Task CreditImporter_EnvironmentNameLookupFailure_DoesNotCostTheBillingData()
        {
            // An environment name is a nicety. Losing it must not lose the figures it decorates.
            var source = new FakeCreditSource { ThrowOnEnvironmentNames = true };
            source.Pages.Add(new CopilotStudioCreditPage(
                new[] { new CopilotStudioCreditRow { ResourceId = "a", Consumed = 5m } }, null));

            var log = (await new CopilotStudioCreditImporter(Logger, source, new RecordingAgentCostStore(), 7).ImportAsync()).Log;

            Assert.IsFalse(string.IsNullOrEmpty(log.Error),
                "The failure is still surfaced - it is the real adapter that swallows it, not the importer.");
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

        private class FakeCreditSource : ICopilotStudioCreditSource
        {
            public List<CopilotStudioCreditPage> Pages { get; } = new List<CopilotStudioCreditPage>();
            public int Calls { get; private set; }
            public string LastContinuationToken { get; private set; }
            public bool ThrowOnEnvironmentNames { get; set; }

            /// <summary>Every (fromDate, toDate) pair the importer asked for, in order.</summary>
            public List<Tuple<DateTime, DateTime>> RequestedRanges { get; } = new List<Tuple<DateTime, DateTime>>();

            public Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
            {
                LastContinuationToken = continuationToken;
                RequestedRanges.Add(Tuple.Create(fromDate, toDate));
                var page = Calls < Pages.Count ? Pages[Calls] : new CopilotStudioCreditPage(new List<CopilotStudioCreditRow>(), null);
                Calls++;
                return Task.FromResult(page);
            }

            public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
                => Task.FromResult<CopilotStudioUserCreditPage>(null);
            public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync()
                => Task.FromResult<CopilotStudioCapacitySnapshot>(null);

            public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
            {
                if (ThrowOnEnvironmentNames) throw new InvalidOperationException("environment lookup failed");
                return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
            }
        }

        /// <summary>Always returns the same continuation token, to prove the loop guard works.</summary>
        private class RepeatingTokenCreditSource : ICopilotStudioCreditSource
        {
            public int Calls { get; private set; }

            public Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
            {
                Calls++;
                return Task.FromResult(new CopilotStudioCreditPage(
                    new[] { new CopilotStudioCreditRow { ResourceId = "a", Consumed = 1m } }, "same-token"));
            }

            public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
                => Task.FromResult<CopilotStudioUserCreditPage>(null);
            public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync() => Task.FromResult<CopilotStudioCapacitySnapshot>(null);

            public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync()
                => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        private class FailingCreditSource : ICopilotStudioCreditSource
        {
            private readonly Exception _exception;
            public FailingCreditSource(Exception exception) { _exception = exception; }

            public Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
                => throw _exception;

            public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
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

        #endregion
    }
}
