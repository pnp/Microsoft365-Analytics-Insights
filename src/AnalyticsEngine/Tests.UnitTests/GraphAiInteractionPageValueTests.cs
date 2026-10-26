using Common.Entities;
using Common.Entities.Config;
using Common.Entities.PromptCategories;
using Common.Entities.State;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;

namespace Tests.UnitTests
{
    /// <summary>
    /// Copilot interaction history pages its own reads, all-or-nothing, because a partial read that looks complete
    /// advances the user's watermark past interactions it never saw. A 200 page without its <c>value</c> collection is
    /// such a partial read: its interactions are unknown, not absent.
    /// </summary>
    [TestClass]
    public class GraphAiInteractionPageValueTests
    {
        private const string PromptText = "synthetic prompt text";

        private static readonly Common.Entities.User PilotUser = new Common.Entities.User
        {
            AzureAdId = "00000000-0000-0000-0000-000000000001",
            UserPrincipalName = "pilot@contoso.com",
        };

        [TestMethod]
        public async Task APageWithoutItsValueCollection_FailsTheWholeUser_AndReturnsNoInteractions()
        {
            var handler = new ScriptedHandler(PageOne(), "{\"@odata.nextLink\":null}");

            var result = await Load(handler);

            Assert.AreEqual("Graph returned page 2 without its 'value' collection.", result.Error);
            Assert.IsFalse(result.IsCompleteSuccess, "The importer leaves the watermark alone for a failed user, so nothing is committed.");
            Assert.AreEqual(0, result.Interactions.Count, "Page 1's interactions are not returned either: the read is all-or-nothing.");
            Assert.AreEqual(2, handler.Requests);
        }

        [TestMethod]
        public async Task ValueNull_IsAFailedPageToo_AndTheErrorCarriesNoPromptText()
        {
            var handler = new ScriptedHandler(PageOne(), "{\"value\":null}");

            var result = await Load(handler);

            Assert.AreEqual("Graph returned page 2 without its 'value' collection.", result.Error);
            Assert.IsFalse(result.Error.Contains(PromptText) || result.Error.Contains("graph.microsoft.com"),
                "The error is persisted to last_error and logged: no body, no URL.");
            Assert.AreEqual(0, result.Interactions.Count);
        }

        [TestMethod]
        public async Task NormalPages_AreStillReadInFull()
        {
            var handler = new ScriptedHandler(PageOne(), "{\"@odata.nextLink\":null,\"value\":[" + Interaction("2") + "]}");

            var result = await Load(handler);

            Assert.IsTrue(result.IsCompleteSuccess);
            Assert.AreEqual(2, result.Interactions.Count);
        }

        [TestMethod]
        public async Task Import_APageWithoutItsValueCollection_SavesNothing_AndLeavesTheWatermarkAlone()
        {
            var connection = Common.Entities.Config.AnalyticsConfig.ConnectionStrings["SPOInsightsEntities"]?.ConnectionString;
            if (connection == null || new SqlConnectionStringBuilder(connection).DataSource.IndexOf("(localdb)", StringComparison.OrdinalIgnoreCase) < 0)
                Assert.Inconclusive("An isolated synthetic LocalDB test database is required.");

            var upn = "interaction-page-value-" + Guid.NewGuid().ToString("N") + "@contoso.com";
            var userId = 0;
            var runId = 0;
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var user = new Common.Entities.User { UserPrincipalName = upn, AccountEnabled = true };
                    db.users.Add(user);
                    await db.SaveChangesAsync();
                    userId = user.ID;
                }

                var handler = new ScriptedHandler(PageOne(), "{\"@odata.nextLink\":null}");
                var importer = new CopilotInteractionHistoryImporter(AnalyticsLogger.ConsoleOnlyTracer(),
                    new AppConfig { CopilotInteractionHistoryMaxUsersPerCycle = 1 },
                    new ReadAccessGranted(NewLoader(handler)), NullInteractionCognitiveEnricher.Instance,
                    new SinglePilot(upn), new UserGroupsFilterModel("Contoso synthetic pilot"),
                    promptClassifier: new PromptCategoryClassifier(PromptCategoryConfiguration.Defaults(), null),
                    promptRunStore: new PromptCategoryRunStore(new InMemoryKeyValueStore()));

                var run = await importer.ImportAsync();
                runId = run.ID;

                Assert.AreEqual(2, handler.Requests, "The user was read, and both pages were asked for.");
                using (var db = new AnalyticsEntitiesContext())
                {
                    var watermark = await db.CopilotInteractionUserWatermarks.SingleAsync(w => w.UserId == userId);
                    Assert.IsNull(watermark.LastInteractionUtc, "The watermark must not move past interactions that were never read.");
                    Assert.AreEqual("Graph returned page 2 without its 'value' collection.", watermark.LastError);
                    Assert.AreEqual(0, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.copilot_interactions WHERE user_id=@id",
                        new SqlParameter("@id", userId)).SingleAsync(), "Page 1's interaction must not be saved either.");
                }
                Assert.AreEqual(1, run.UsersFailed);
                Assert.AreEqual(0, run.InteractionsSaved);
            }
            finally
            {
                if (userId > 0)
                    using (var db = new AnalyticsEntitiesContext())
                    {
                        await db.Database.ExecuteSqlCommandAsync(
                            @"DELETE dbo.copilot_interactions WHERE user_id=@id;
                              DELETE dbo.copilot_interaction_sessions WHERE user_id=@id;
                              DELETE dbo.copilot_interaction_user_watermarks WHERE user_id=@id;
                              DELETE dbo.users WHERE id=@id;", new SqlParameter("@id", userId));
                        if (runId > 0)
                            await db.Database.ExecuteSqlCommandAsync("DELETE dbo.copilot_interaction_import_log WHERE id=@id", new SqlParameter("@id", runId));
                    }
            }
        }

        private static GraphAiInteractionSourceLoader NewLoader(ScriptedHandler handler)
            => new GraphAiInteractionSourceLoader(new ManualGraphCallClient(handler, NullLogger.Instance), null, NullLogger.Instance);

        private static Task<AiInteractionLoadResult> Load(ScriptedHandler handler)
            => NewLoader(handler).LoadInteractionsForUserAsync(PilotUser,
                new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));

        private static string PageOne()
            => "{\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/copilot/users/00000000-0000-0000-0000-000000000001/interactionHistory/getAllEnterpriseInteractions?$skiptoken=page-2\"," +
               "\"value\":[" + Interaction("1") + "]}";

        private static string Interaction(string id)
            => "{\"id\":\"" + id + "\",\"sessionId\":\"session-1\",\"requestId\":\"request-" + id + "\",\"interactionType\":\"userPrompt\"," +
               "\"createdDateTime\":\"2026-10-01T10:00:00Z\",\"body\":{\"contentType\":\"text\",\"content\":\"" + PromptText + "\"}}";

        /// <summary>The real loader, minus the token-based permission probe this test has no token for.</summary>
        private sealed class ReadAccessGranted : IAiInteractionSourceLoader
        {
            private readonly IAiInteractionSourceLoader _inner;

            public ReadAccessGranted(IAiInteractionSourceLoader inner)
            {
                _inner = inner;
            }

            public Task<bool> HasInteractionReadAccessAsync() => Task.FromResult(true);

            public Task<AiInteractionLoadResult> LoadInteractionsForUserAsync(Common.Entities.User user, DateTime fromUtc, DateTime toUtc)
                => _inner.LoadInteractionsForUserAsync(user, fromUtc, toUtc);
        }

        private sealed class SinglePilot : IPilotGroupMemberResolver
        {
            private readonly string _upn;

            public SinglePilot(string upn)
            {
                _upn = upn;
            }

            public Task<PilotGroupResolution> GetMemberUpnsAsync(UserGroupsFilterModel filter)
                => Task.FromResult(new PilotGroupResolution(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _upn }));
        }

        /// <summary>Answers each request with the next scripted 200 body, and counts the requests.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Queue<string> _bodies;

            public ScriptedHandler(params string[] bodies)
            {
                _bodies = new Queue<string>(bodies);
            }

            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                if (_bodies.Count == 0)
                {
                    return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Unscripted request."));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_bodies.Dequeue()),
                    RequestMessage = request,
                });
            }
        }
    }
}
