using Common.Entities;
using Common.Entities.Config;
using Common.Entities.PromptCategories;
using Common.Entities.State;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;

namespace Tests.UnitTests
{
    [TestClass]
    public class PromptCategoryImportTests
    {
        [TestMethod]
        public async Task Import_PersistsOnlyPromptFacts_ImmutableVersions_CapsAndFailures_AndDisabledStillImports()
        {
            var connection = System.Configuration.ConfigurationManager.ConnectionStrings["SPOInsightsEntities"]?.ConnectionString;
            if (connection == null || new SqlConnectionStringBuilder(connection).DataSource.IndexOf("(localdb)", StringComparison.OrdinalIgnoreCase) < 0)
                Assert.Inconclusive("An isolated synthetic LocalDB test database is required.");
            var upn = "prompt-fixture-" + Guid.NewGuid().ToString("N") + "@contoso.com";
            var userId = 0;
            var runIds = new List<int>();
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var user = new User { UserPrincipalName = upn, AccountEnabled = true };
                    db.users.Add(user);
                    await db.SaveChangesAsync();
                    userId = user.ID;
                }
                var config = PromptCategoryConfiguration.Defaults();
                config.Enabled = true;
                config.MaxPromptsPerCycle = 1;
                config.Categories[0].HumanMode = "directing";
                config.ValidateAndVersion();
                var oldVersion = config.Version;
                var backend = new Backend();
                var source = new Source(1);
                var run = await Import(upn, source, config, backend);
                runIds.Add(run.ID);
                Assert.IsNull(run.Error);
                Assert.AreEqual(3, run.InteractionsSaved);
                using (var db = new AnalyticsEntitiesContext())
                {
                    var facts = await db.Database.SqlQuery<Fact>(
                        @"SELECT c.category_id AS CategoryId,c.taxonomy_version AS Version,c.human_mode AS Mode
                          FROM dbo.copilot_prompt_classifications c JOIN dbo.copilot_interactions i ON i.id=c.interaction_id
                          WHERE i.user_id=@id", new SqlParameter("@id", userId)).ToListAsync();
                    Assert.AreEqual(2, facts.Count, "Responses have no classification fact.");
                    Assert.AreEqual(1, facts.Count(f => f.CategoryId == "meeting-summary" && f.Mode == "directing"));
                    Assert.AreEqual(1, facts.Count(f => f.CategoryId == "not-classified" && f.Mode == null));
                    Assert.IsTrue(facts.All(f => f.Version == oldVersion));
                }
                var calls = backend.Documents;
                run = await Import(upn, source, config, backend);
                runIds.Add(run.ID);
                Assert.AreEqual(0, run.InteractionsSaved, "Watermark overlap de-duplicates before external enrichment.");
                Assert.AreEqual(calls, backend.Documents);

                config.Categories[0].Name = "Contoso Καλημέρα";
                config.Categories[0].HumanMode = "supervising";
                config.ValidateAndVersion();
                Assert.AreNotEqual(oldVersion, config.Version);
                run = await Import(upn, new Source(2), config, backend);
                runIds.Add(run.ID);
                using (var db = new AnalyticsEntitiesContext())
                {
                    var versions = await db.Database.SqlQuery<string>(
                        @"SELECT DISTINCT c.taxonomy_version FROM dbo.copilot_prompt_classifications c
                          JOIN dbo.copilot_interactions i ON i.id=c.interaction_id WHERE i.user_id=@id",
                        new SqlParameter("@id", userId)).ToListAsync();
                    CollectionAssert.AreEquivalent(new[] { oldVersion, config.Version }, versions);
                    var historical = await db.Database.SqlQuery<string>(
                        "SELECT categories_json FROM dbo.copilot_prompt_taxonomies WHERE version=@v",
                        new SqlParameter("@v", oldVersion)).SingleAsync();
                    Assert.IsFalse(historical.Contains("Contoso Καλημέρα"), "Historical labels cannot be silently rewritten.");
                    var current = await db.Database.SqlQuery<string>(
                        "SELECT categories_json FROM dbo.copilot_prompt_taxonomies WHERE version=@v",
                        new SqlParameter("@v", config.Version)).SingleAsync();
                    StringAssert.Contains(current, "Contoso Καλημέρα", "Unicode customer category names cross the real storage boundary.");
                }

                backend.Fail = true;
                run = await Import(upn, new Source(3), config, backend);
                runIds.Add(run.ID);
                Assert.IsNull(run.Error, "A model failure must not fail interaction import.");
                Assert.AreEqual(3, run.InteractionsSaved);
                config.Enabled = false;
                var before = backend.Documents;
                run = await Import(upn, new Source(4), config, backend);
                runIds.Add(run.ID);
                Assert.IsNull(run.Error);
                Assert.AreEqual(3, run.InteractionsSaved);
                Assert.AreEqual(before, backend.Documents, "Disabled enrichment never contacts the model.");
                using (var db = new AnalyticsEntitiesContext())
                {
                    var count = await db.Database.SqlQuery<int>(
                        @"SELECT COUNT(*) FROM dbo.copilot_prompt_classifications c JOIN dbo.copilot_interactions i ON i.id=c.interaction_id WHERE i.user_id=@id",
                        new SqlParameter("@id", userId)).SingleAsync();
                    Assert.AreEqual(6, count, "Only the three enabled cycles create two prompt facts each.");
                    var counters = JsonConvert.SerializeObject(await RunStore.RecentAsync());
                    StringAssert.Contains(counters, "service-failure");
                    Assert.IsFalse(counters.Contains("synthetic confidential text"));
                    Assert.AreEqual(runIds.Count, (await RunStore.RecentAsync()).Count, "Each cycle records one counters entry.");
                }
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
                        foreach (var id in runIds)
                            await db.Database.ExecuteSqlCommandAsync("DELETE dbo.copilot_interaction_import_log WHERE id=@id", new SqlParameter("@id", id));
                    }
            }
        }

        private static Task<Common.Entities.Entities.Copilot.CopilotInteractionImportLog> Import(
            string upn, Source source, PromptCategoryConfiguration taxonomy, Backend backend)
        {
            var settings = new AppConfig { CopilotInteractionHistoryMaxUsersPerCycle = 1 };
            var importer = new CopilotInteractionHistoryImporter(AnalyticsLogger.ConsoleOnlyTracer(), settings,
                source, new DisabledCognitiveEnricher(), new Pilot(upn), new UserGroupsFilterModel("Contoso synthetic pilot"),
                promptClassifier: new PromptCategoryClassifier(taxonomy, backend), promptRunStore: RunStore);
            return importer.ImportAsync();
        }

        private static readonly PromptCategoryRunStore RunStore = new PromptCategoryRunStore(new InMemoryKeyValueStore());

        private sealed class DisabledCognitiveEnricher : IInteractionCognitiveEnricher
        {
            public bool IsEnabled => false;
            public Task<int> EnrichAsync(IReadOnlyList<InteractionStats> stats, IReadOnlyList<string> bodies) =>
                throw new InvalidOperationException("Disabled cognitive enrichment must never be invoked.");
        }

        public sealed class Fact
        {
            public string CategoryId { get; set; }
            public string Version { get; set; }
            public string Mode { get; set; }
        }
        private sealed class Pilot : IPilotGroupMemberResolver
        {
            private readonly string _upn;
            public Pilot(string upn) { _upn = upn; }
            public Task<PilotGroupResolution> GetMemberUpnsAsync(UserGroupsFilterModel filter) =>
                Task.FromResult(new PilotGroupResolution(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _upn }));
        }
        private sealed class Backend : IPromptCategoryBackend
        {
            public int Documents;
            public bool Fail;
            public Task<PromptCategoryBatchResult> ClassifyAsync(PromptCategoryConfiguration taxonomy,
                IReadOnlyList<string> prompts, CancellationToken cancellationToken)
            {
                Documents += prompts.Count;
                if (Fail) throw new InvalidOperationException("synthetic confidential text");
                return Task.FromResult(new PromptCategoryBatchResult { Categories = prompts.Select(_ => "meeting-summary").ToArray() });
            }
        }
        private sealed class Source : IAiInteractionSourceLoader
        {
            private readonly int _cycle;
            public Source(int cycle) { _cycle = cycle; }
            public Task<bool> HasInteractionReadAccessAsync() => Task.FromResult(true);
            public Task<AiInteractionLoadResult> LoadInteractionsForUserAsync(User user, DateTime fromUtc, DateTime toUtc) =>
                Task.FromResult(new AiInteractionLoadResult
                {
                    Interactions = new[]
                    {
                        Item("prompt-one", InteractionTypes.UserPrompt), Item("response", InteractionTypes.AiResponse),
                        Item("prompt-two", InteractionTypes.UserPrompt)
                    }
                });
            private AiInteraction Item(string id, string type) => new AiInteraction
            {
                Id = "synthetic-" + _cycle + "-" + id, SessionId = "synthetic-thread", RequestId = "synthetic-" + _cycle,
                InteractionType = type, CreatedDateTime = DateTime.UtcNow.AddMinutes(-1),
                Body = new AiInteractionBody { ContentType = "text", Content = "synthetic confidential text Καλημέρα" }
            };
        }
    }
}
