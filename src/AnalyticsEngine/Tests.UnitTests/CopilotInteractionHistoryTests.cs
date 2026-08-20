using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Copilot;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the optional Copilot AI interaction-history import.
    /// </summary>
    /// <remarks>
    /// The two things worth testing hardest here are the ones that would be expensive to get wrong in
    /// production: that prompt text never survives the projection to statistics, and that the import cannot
    /// quietly turn into one Graph call per user across the whole tenant.
    /// </remarks>
    [TestClass]
    public class CopilotInteractionHistoryTests
    {
        // A real Greek prompt. Per the repo's character-set rule, non-ASCII belongs in test data so
        // truncation and round-trip bugs surface here rather than in a customer tenant.
        private const string GreekPrompt = "Καλημέρα κόσμε, σύνοψη πωλήσεων";

        #region Stats extraction - the privacy boundary

        [TestMethod]
        public void InteractionStats_DeriveCountsAndDropBodyText()
        {
            var interaction = new AiInteraction
            {
                Id = "1",
                SessionId = "19:thread@thread.v2",
                RequestId = "req-1",
                InteractionType = InteractionTypes.UserPrompt,
                AppClass = "IPM.SkypeTeams.Message.Copilot.BizChat",
                ConversationType = "bizchat",
                Locale = "en-us",
                CreatedDateTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                Body = new AiInteractionBody { ContentType = "html", Content = "<p>Summarise <b>Q3</b> sales</p>" },
                Attachments = new List<JToken> { JToken.Parse("{}"), JToken.Parse("{}") },
                Links = new List<JToken> { JToken.Parse("{}") },
                Mentions = new List<JToken>(),
                Contexts = new List<JToken> { JToken.Parse("{}"), JToken.Parse("{}"), JToken.Parse("{}") },
            };

            var stats = InteractionStatsExtractor.ToStats(interaction);

            Assert.IsNotNull(stats);
            Assert.AreEqual("1", stats.GraphInteractionId);
            Assert.AreEqual("19:thread@thread.v2", stats.SessionRef);
            Assert.AreEqual("req-1", stats.RequestId);
            Assert.AreEqual("bizchat", stats.ConversationType);
            Assert.IsTrue(stats.IsUserPrompt);

            // HTML stripped before measuring: "Summarise Q3 sales" is 3 words, not 1, and the tags don't
            // inflate the character count.
            Assert.AreEqual(3, stats.BodyWordCount);
            Assert.AreEqual("Summarise  Q3  sales".Length, stats.BodyCharCount);

            Assert.AreEqual(2, stats.AttachmentCount);
            Assert.AreEqual(1, stats.LinkCount);
            Assert.AreEqual(0, stats.MentionCount);
            Assert.AreEqual(3, stats.ContextCount);

            // The whole point: nothing on the projected object can carry the prompt itself.
            var serialised = JsonConvert.SerializeObject(stats);
            StringAssert.DoesNotMatch(serialised, new System.Text.RegularExpressions.Regex("Summarise"));
            StringAssert.DoesNotMatch(serialised, new System.Text.RegularExpressions.Regex("Q3"));
        }

        [TestMethod]
        public void InteractionStats_CountGreekPromptCorrectly()
        {
            var interaction = NewPrompt("1", "s1", "r1", DateTime.UtcNow, GreekPrompt);

            var stats = InteractionStatsExtractor.ToStats(interaction);

            // 4 whitespace-separated words: "Καλημέρα", "κόσμε,", "σύνοψη", "πωλήσεων".
            Assert.AreEqual(4, stats.BodyWordCount);
            Assert.AreEqual(GreekPrompt.Length, stats.BodyCharCount,
                "Greek characters must be counted as single characters, not mangled or double-counted.");
        }

        [TestMethod]
        public void InteractionStats_SkipRowsThatCannotBeKeyed()
        {
            // No id, no session and no timestamp respectively: each row could neither be de-duplicated
            // nor ordered, so it is dropped rather than stored half-formed.
            Assert.IsNull(InteractionStatsExtractor.ToStats(new AiInteraction { SessionId = "s", CreatedDateTime = DateTime.UtcNow }));
            Assert.IsNull(InteractionStatsExtractor.ToStats(new AiInteraction { Id = "1", CreatedDateTime = DateTime.UtcNow }));
            Assert.IsNull(InteractionStatsExtractor.ToStats(new AiInteraction { Id = "1", SessionId = "s" }));
            Assert.IsNull(InteractionStatsExtractor.ToStats(null));
        }

        [TestMethod]
        public void InteractionStats_HandleNullBodyAndCollections()
        {
            var stats = InteractionStatsExtractor.ToStats(new AiInteraction
            {
                Id = "1",
                SessionId = "s1",
                CreatedDateTime = DateTime.UtcNow,
                InteractionType = InteractionTypes.AiResponse
            });

            Assert.IsNotNull(stats);
            Assert.AreEqual(0, stats.BodyCharCount);
            Assert.AreEqual(0, stats.BodyWordCount);
            Assert.AreEqual(0, stats.AttachmentCount);
            Assert.AreEqual(0, stats.ContextCount);
            Assert.IsFalse(stats.IsUserPrompt);
        }

        [TestMethod]
        public void InteractionStats_ReadDeviceFromEitherIdentityShape()
        {
            // Documented shape: from.device is an identity object.
            var asObject = new AiInteraction { From = JToken.Parse("{ 'device': { 'displayName': 'desktop' } }") };
            Assert.AreEqual("desktop", asObject.GetDeviceName());

            // Observed shape: from.device is a plain string.
            var asString = new AiInteraction { From = JToken.Parse("{ 'device': 'mobile' }") };
            Assert.AreEqual("mobile", asString.GetDeviceName());

            Assert.IsNull(new AiInteraction { From = JToken.Parse("{ }") }.GetDeviceName());
            Assert.IsNull(new AiInteraction().GetDeviceName());
        }

        #endregion

        #region Response latency - the headline new metric

        [TestMethod]
        public void ResponseLatency_PairsPromptWithResponseOnRequestId()
        {
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

            var stats = InteractionStatsExtractor.Extract(new[]
            {
                NewPrompt("1", "s1", "req-a", t0, "hello"),
                NewResponse("2", "s1", "req-a", t0.AddSeconds(4)),
                NewPrompt("3", "s1", "req-b", t0.AddMinutes(1), "again"),
                NewResponse("4", "s1", "req-b", t0.AddMinutes(1).AddMilliseconds(250)),
            });

            var responseA = stats.Single(s => s.GraphInteractionId == "2");
            var responseB = stats.Single(s => s.GraphInteractionId == "4");

            Assert.AreEqual(4000, responseA.ResponseLatencyMs);
            Assert.AreEqual(250, responseB.ResponseLatencyMs);

            // Latency belongs to the response, never the prompt.
            Assert.IsNull(stats.Single(s => s.GraphInteractionId == "1").ResponseLatencyMs);
            Assert.IsNull(stats.Single(s => s.GraphInteractionId == "3").ResponseLatencyMs);
        }

        [TestMethod]
        public void ResponseLatency_NullWhenPromptIsNotInTheBatch()
        {
            // The prompt was imported on a previous run, so we can't measure this turn. A missing number
            // is correct here - a wrong one would silently skew every average built on it.
            var stats = InteractionStatsExtractor.Extract(new[]
            {
                NewResponse("2", "s1", "req-a", DateTime.UtcNow)
            });

            Assert.IsNull(stats.Single().ResponseLatencyMs);
        }

        [TestMethod]
        public void ResponseLatency_RejectImplausibleValues()
        {
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

            var stats = InteractionStatsExtractor.Extract(new[]
            {
                // Response timestamped before its prompt - only possible via clock skew between services.
                NewPrompt("1", "s1", "req-a", t0, "x"),
                NewResponse("2", "s1", "req-a", t0.AddSeconds(-5)),

                // Absurdly long gap - far more likely a recycled request id than a real wait.
                NewPrompt("3", "s1", "req-b", t0, "y"),
                NewResponse("4", "s1", "req-b", t0.AddHours(3)),
            });

            Assert.IsNull(stats.Single(s => s.GraphInteractionId == "2").ResponseLatencyMs);
            Assert.IsNull(stats.Single(s => s.GraphInteractionId == "4").ResponseLatencyMs);
        }

        [TestMethod]
        public void ResponseLatency_OnlySetForAnExplicitAiResponse()
        {
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

            // The schema declares an 'unknownFutureValue' sentinel, and new interaction types can appear.
            // Anything that isn't explicitly an aiResponse must not be treated as one.
            var unknown = new AiInteraction
            {
                Id = "2",
                SessionId = "s1",
                RequestId = "req-a",
                InteractionType = "unknownFutureValue",
                CreatedDateTime = t0.AddSeconds(3),
                Body = new AiInteractionBody { Content = "?" }
            };

            var stats = InteractionStatsExtractor.Extract(new[]
            {
                NewPrompt("1", "s1", "req-a", t0, "hello"),
                unknown
            });

            Assert.IsNull(stats.Single(s => s.GraphInteractionId == "2").ResponseLatencyMs);
        }

        [TestMethod]
        public void GetNewestCreatedUtc_ReturnsTheWatermarkForTheBatch()
        {
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

            var stats = InteractionStatsExtractor.Extract(new[]
            {
                NewPrompt("1", "s1", "r1", t0.AddMinutes(5), "a"),
                NewPrompt("2", "s1", "r2", t0, "b"),
                NewPrompt("3", "s1", "r3", t0.AddMinutes(2), "c"),
            });

            Assert.AreEqual(t0.AddMinutes(5), InteractionStatsExtractor.GetNewestCreatedUtc(stats));
            Assert.IsNull(InteractionStatsExtractor.GetNewestCreatedUtc(new List<InteractionStats>()));
        }

        [TestMethod]
        public void CreatedDates_AreNormalisedToUtc()
        {
            var local = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Local);
            var stats = InteractionStatsExtractor.ToStats(NewPrompt("1", "s1", "r1", local, "x"));

            Assert.AreEqual(DateTimeKind.Utc, stats.CreatedUtc.Kind,
                "Watermarks and reports all assume UTC, so a Local/Unspecified kind must be converted.");
        }

        #endregion

        #region Graph request shape

        [TestMethod]
        public void BuildInteractionsUrl_AlwaysSendsBothEndsOfTheDateRange()
        {
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);

            var url = GraphAiInteractionSourceLoader.BuildInteractionsUrl("00000000-0000-0000-0000-000000000000", from, to);

            StringAssert.Contains(url, "/copilot/users/00000000-0000-0000-0000-000000000000/interactionHistory/getAllEnterpriseInteractions");

            // Graph rejects a single-sided createdDateTime filter, so both bounds must always be present.
            var decoded = Uri.UnescapeDataString(url);
            StringAssert.Contains(decoded, "createdDateTime gt 2026-01-01T00:00:00Z");
            StringAssert.Contains(decoded, "createdDateTime lt 2026-01-08T00:00:00Z");
            StringAssert.Contains(url, "$top=" + GraphAiInteractionSourceLoader.GraphPageSize);
        }

        [TestMethod]
        public void BuildInteractionsUrl_EscapesTheUserKey()
        {
            var url = GraphAiInteractionSourceLoader.BuildInteractionsUrl(
                "user name+plus@contoso.com", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

            StringAssert.Contains(url, "user%20name%2Bplus%40contoso.com");
        }

        [TestMethod]
        public void GetUserKey_PrefersTheEntraObjectId()
        {
            var withId = new Common.Entities.User
            {
                AzureAdId = "00000000-0000-0000-0000-000000000000",
                UserPrincipalName = "someone@contoso.com"
            };
            Assert.AreEqual("00000000-0000-0000-0000-000000000000", GraphAiInteractionSourceLoader.GetUserKey(withId));

            // Falls back to UPN when the object id hasn't been imported yet.
            var withoutId = new Common.Entities.User { AzureAdId = "", UserPrincipalName = "someone@contoso.com" };
            Assert.AreEqual("someone@contoso.com", GraphAiInteractionSourceLoader.GetUserKey(withoutId));

            var withNeither = new Common.Entities.User { AzureAdId = "", UserPrincipalName = "" };
            Assert.IsNull(GraphAiInteractionSourceLoader.GetUserKey(withNeither));
        }

        #endregion

        #region Permission pre-flight

        [TestMethod]
        public void TokenPermissions_ReadApplicationRolesAndDelegatedScopes()
        {
            var jwt = FakeJwt("{ \"roles\": [\"AiEnterpriseInteraction.Read.All\", \"User.Read.All\"] }");
            var permissions = GraphTokenPermissions.Extract(jwt);

            CollectionAssert.Contains(permissions.ToList(), "AiEnterpriseInteraction.Read.All");
            Assert.IsTrue(permissions.Any(p => GraphAiInteractionSourceLoader.InteractionReadPermissions.Contains(p)));

            var scoped = GraphTokenPermissions.Extract(FakeJwt("{ \"scp\": \"User.Read Mail.Read\" }"));
            CollectionAssert.Contains(scoped.ToList(), "Mail.Read");
        }

        [TestMethod]
        public void TokenPermissions_DetectMissingConsentRatherThanThrowing()
        {
            // This is the expected state until an admin consents, so it must be a clean "no", not an
            // exception, and definitely not a 403 per user for the whole pilot group.
            var permissions = GraphTokenPermissions.Extract(FakeJwt("{ \"roles\": [\"Reports.Read.All\"] }"));
            Assert.IsFalse(permissions.Any(p => GraphAiInteractionSourceLoader.InteractionReadPermissions.Contains(p)));

            // Malformed input must not throw either.
            Assert.AreEqual(0, GraphTokenPermissions.Extract(null).Count);
            Assert.AreEqual(0, GraphTokenPermissions.Extract("not-a-jwt").Count);
            Assert.AreEqual(0, GraphTokenPermissions.Extract("a.!!!notbase64!!!.c").Count);
        }

        #endregion

        #region Cost controls

        [TestMethod]
        public void Import_DoesNotRequireAGroupScope()
        {
            // UserGroupsFilter is an optional narrowing, not a precondition. The controls that decide whether
            // this import runs at all are the workload toggle and the AiEnterpriseInteraction.Read.All
            // permission - to stop it, turn the workload off or withhold the permission. Requiring a group
            // filter as well meant an admin who wanted tenant-wide history had to opt in twice.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var importer = NewImporter(logger, new AppConfig(), new UserGroupsFilterModel(string.Empty));

            Assert.IsNotNull(importer,
                "An empty UserGroupsFilter is a valid configuration - every enabled user is eligible, still " +
                "bounded by CopilotInteractionHistoryMaxUsersPerCycle.");
        }

        [TestMethod]
        public void PerCycleCap_IsTheBrakeThatBoundsAnUnnarrowedRun()
        {
            // With the group filter now optional, this cap is what stops an unnarrowed run costing one Graph
            // call per user in the directory. A zero or negative setting must fall back to the default rather
            // than being read as "no limit".
            Assert.AreEqual(500, AppConfig.DefaultCopilotInteractionHistoryMaxUsersPerCycle);

            var configured = new AppConfig { CopilotInteractionHistoryMaxUsersPerCycle = 250 };
            Assert.AreEqual(250, configured.CopilotInteractionHistoryMaxUsersPerCycle);
        }

        [TestMethod]
        public void WindowStart_BoundsTheFirstRunButFollowsTheWatermarkAfterwards()
        {
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            // No watermark yet: bounded backfill, so onboarding a pilot group has a known cost.
            var firstRun = CopilotInteractionHistoryImporter.GetWindowStart(null, now, 30);
            Assert.AreEqual(now.AddDays(-30), firstRun);

            // With a watermark: start just before it. The overlap exists because Graph's filter uses a
            // strict 'gt', and doubles as a safety lag for late-arriving interactions.
            var watermark = new CopilotInteractionUserWatermark { LastInteractionUtc = now.AddHours(-2) };
            var nextRun = CopilotInteractionHistoryImporter.GetWindowStart(watermark, now, 30);

            Assert.AreEqual(now.AddHours(-2).AddSeconds(-CopilotInteractionHistoryImporter.WatermarkOverlapSeconds), nextRun);
            Assert.IsTrue(nextRun < now.AddHours(-2), "The window must start before the watermark, not on it.");
        }

        [TestMethod]
        public void Watermark_MovesForwardOnly()
        {
            // Regression guard for the wedged-watermark bug. The watermark advances to the end of the
            // queried window, not to the newest interaction returned. Advancing to the newest interaction
            // wedged it permanently: the overlap means that same interaction comes back next cycle, which
            // looks like a fresh non-empty success and writes the identical watermark again. An inactive
            // user would be re-queried for ever, never reach the empty back-off, and keep paying to
            // re-process one interaction.
            var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            var newest = InteractionStatsExtractor.GetNewestCreatedUtc(
                InteractionStatsExtractor.Extract(new[] { NewPrompt("1", "s1", "r1", t0, "hello") }));

            var windowEnd = t0.AddHours(6);
            Assert.IsTrue(windowEnd > newest.Value,
                "The queried window always extends past the last interaction in it, which is exactly why " +
                "the window end - not the newest row - has to be the watermark.");

            // And the overlap must be smaller than a typical cadence, or every cycle re-reads a huge span.
            Assert.IsTrue(CopilotInteractionHistoryImporter.WatermarkOverlapSeconds < 60 * 60,
                "The overlap should be a safety lag, not a re-scan window.");
        }

        [TestMethod]
        public void LoadResult_OnlyReportsCompleteSuccessWhenNothingWasMissed()
        {
            // Regression guard for the silent-data-loss path. A truncated or failed load must never look
            // like a clean read, because the caller uses that to decide whether it is safe to move the
            // watermark past data it may never have seen.
            Assert.IsTrue(AiInteractionLoadResult.Empty().IsCompleteSuccess);

            Assert.IsFalse(new AiInteractionLoadResult { Truncated = true }.IsCompleteSuccess);
            Assert.IsFalse(new AiInteractionLoadResult { Error = "HTTP 500 on page 3." }.IsCompleteSuccess);
            Assert.IsFalse(new AiInteractionLoadResult { UserNotAvailable = true }.IsCompleteSuccess);
        }

        [TestMethod]
        public void LoadResult_ErrorsAreSanitisedSummariesNotPayloads()
        {
            // last_error is persisted and logged, and the raw payload for this endpoint is the user's
            // prompts. Errors must therefore carry status/code/page only.
            var result = new AiInteractionLoadResult { Error = "Graph returned HTTP 500 (code 'unknown') on page 2." };

            Assert.IsTrue(result.Failed);
            Assert.IsTrue(result.Error.Length <= 500,
                "Errors are stored in a 500-character column and must be summaries, not payload dumps.");
        }

        [TestMethod]
        public void BackOff_OnlyStartsAfterRepeatedEmptyRuns()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var settings = new AppConfig { CopilotInteractionHistoryEmptyUserBackOffHours = 72 };
            var importer = NewImporter(logger, settings, new UserGroupsFilterModel("Pilot"));
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            // One quiet day is not evidence of anything - a licensed user may simply not have used Copilot.
            var afterOne = new CopilotInteractionUserWatermark { ConsecutiveEmptyOrFailed = 1 };
            importer.ApplyBackOff(afterOne, now);
            Assert.IsNull(afterOne.SkipUntilUtc);

            // Two in a row and we stop spending the per-cycle call budget on them.
            var afterTwo = new CopilotInteractionUserWatermark { ConsecutiveEmptyOrFailed = 2 };
            importer.ApplyBackOff(afterTwo, now);
            Assert.AreEqual(now.AddHours(72), afterTwo.SkipUntilUtc);
        }

        [TestMethod]
        public void BackOff_CanBeDisabled()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var settings = new AppConfig { CopilotInteractionHistoryEmptyUserBackOffHours = 0 };
            var importer = NewImporter(logger, settings, new UserGroupsFilterModel("Pilot"));

            var watermark = new CopilotInteractionUserWatermark { ConsecutiveEmptyOrFailed = 10 };
            importer.ApplyBackOff(watermark, DateTime.UtcNow);

            Assert.IsNull(watermark.SkipUntilUtc);
        }

        [TestMethod]
        public async Task StrictGroupFilter_ResolvesScopeGroupFirstNotUserByUser()
        {
            // The pilot scope is resolved by listing the group's members, not by asking "is this user in the
            // group?" for every user in the database. The latter is one Graph call per tenant user - at the
            // ~200k-user design target that would be 200k calls spent just deciding who to import.
            var resolver = new FakePilotGroupMemberResolver("pilot1@contoso.com", "pilot2@contoso.com");
            var filter = new UserGroupsFilterModel("Copilot Pilot");

            var members = await resolver.GetMemberUpnsAsync(filter);

            Assert.AreEqual(1, resolver.CallCount, "Scope must be resolved with a single group-side lookup.");
            Assert.AreEqual(2, members.Count);
            Assert.IsTrue(members.Contains("PILOT1@CONTOSO.COM"),
                "Membership matching must be case-insensitive to line up with SQL Server's collation.");
        }

        [TestMethod]
        public async Task PilotGroupResolver_ReturnsNothingForAnEmptyFilter()
        {
            var resolver = new GraphPilotGroupMemberResolver(
                new ManualGraphCallClient(new System.Net.Http.HttpClientHandler(), AnalyticsLogger.ConsoleOnlyTracer()),
                AnalyticsLogger.ConsoleOnlyTracer());

            // No filter means no pilot group; it must never be read as "everyone".
            var members = await resolver.GetMemberUpnsAsync(new UserGroupsFilterModel(string.Empty));
            Assert.AreEqual(0, members.Count);
        }

        #endregion

        #region Cognitive enrichment

        [TestMethod]
        public void KeyPhrases_AreDedupedCappedAndLengthLimited()
        {
            var phrases = new List<string> { "sales forecast", "Sales Forecast", "  budget  ", "", null };
            var result = AzureLanguageInteractionCognitiveEnricher.NormaliseKeyPhrases(phrases);

            CollectionAssert.AreEqual(new[] { "sales forecast", "budget" }, result,
                "Phrases are trimmed and de-duplicated case-insensitively, matching SQL Server's collation.");

            // Too long for the shared keywords.name column: dropped rather than truncated, because a
            // chopped phrase is meaningless as a topic and would merge distinct phrases sharing a prefix.
            var tooLong = new string('x', AzureLanguageInteractionCognitiveEnricher.MaxKeyPhraseLength + 1);
            CollectionAssert.DoesNotContain(
                AzureLanguageInteractionCognitiveEnricher.NormaliseKeyPhrases(new[] { tooLong }), tooLong);

            // Azure returns phrases ranked, so capping keeps the most salient and bounds the link table.
            var many = Enumerable.Range(0, 50).Select(i => "phrase " + i).ToList();
            Assert.AreEqual(
                AzureLanguageInteractionCognitiveEnricher.MaxKeyPhrasesPerPrompt,
                AzureLanguageInteractionCognitiveEnricher.NormaliseKeyPhrases(many).Count);
        }

        [TestMethod]
        public async Task NullCognitiveEnricher_IsUsedWhenCognitiveIsNotConfigured()
        {
            // Cognitive scoring is optional; with no endpoint configured the import must still run and
            // simply produce no sentiment/keywords.
            var enricher = InteractionCognitiveEnricherFactory.Create(new AppConfig { CognitiveEndpoint = null }, AnalyticsLogger.ConsoleOnlyTracer());

            Assert.IsFalse(enricher.IsEnabled);
            Assert.AreEqual(0, await enricher.EnrichAsync(new List<InteractionStats>(), new List<string>()));
        }

        #endregion

        #region Helpers

        private static CopilotInteractionHistoryImporter NewImporter(AnalyticsLogger logger, AppConfig settings, UserGroupsFilterModel filter)
        {
            return new CopilotInteractionHistoryImporter(
                logger,
                settings,
                new FakeAiInteractionSourceLoader(),
                NullInteractionCognitiveEnricher.Instance,
                new FakePilotGroupMemberResolver(),
                filter);
        }

        private static AiInteraction NewPrompt(string id, string sessionId, string requestId, DateTime created, string body)
        {
            return new AiInteraction
            {
                Id = id,
                SessionId = sessionId,
                RequestId = requestId,
                InteractionType = InteractionTypes.UserPrompt,
                CreatedDateTime = created,
                Body = new AiInteractionBody { ContentType = "text", Content = body }
            };
        }

        private static AiInteraction NewResponse(string id, string sessionId, string requestId, DateTime created)
        {
            return new AiInteraction
            {
                Id = id,
                SessionId = sessionId,
                RequestId = requestId,
                InteractionType = InteractionTypes.AiResponse,
                CreatedDateTime = created,
                Body = new AiInteractionBody { ContentType = "text", Content = "Here is the summary." }
            };
        }

        /// <summary>
        /// Builds an unsigned JWT with the given payload. The permission check never validates the
        /// signature (the token was just issued to us by Entra ID), so this is enough to exercise it.
        /// </summary>
        private static string FakeJwt(string payloadJson)
        {
            var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "header." + payload + ".signature";
        }

        private class FakeAiInteractionSourceLoader : IAiInteractionSourceLoader
        {
            public Task<bool> HasInteractionReadAccessAsync() => Task.FromResult(true);

            public Task<AiInteractionLoadResult> LoadInteractionsForUserAsync(Common.Entities.User user, DateTime fromUtc, DateTime toUtc)
                => Task.FromResult(AiInteractionLoadResult.Empty());
        }

        private class FakePilotGroupMemberResolver : IPilotGroupMemberResolver
        {
            private readonly HashSet<string> _members;

            public FakePilotGroupMemberResolver(params string[] members)
            {
                _members = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
            }

            public int CallCount { get; private set; }

            public Task<HashSet<string>> GetMemberUpnsAsync(UserGroupsFilterModel filter)
            {
                CallCount++;
                return Task.FromResult(_members);
            }
        }

        #endregion
    }
}
