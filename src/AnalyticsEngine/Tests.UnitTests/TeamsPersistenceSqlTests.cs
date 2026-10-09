using Common.Entities;
using Common.Entities.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    [TestClass]
    public class TeamsPersistenceSqlTests
    {
        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task SqlAdapter_InsertsAndUpdatesStatsRelationshipsAndTokenMetadata()
        {
            var suffix = Guid.NewGuid().ToString();
            var date = new DateTime(2026, 1, 10);
            var channel = new ChannelWithReactions
            {
                Id = "channel-" + suffix, DisplayName = "Καλημέρα κόσμε",
                Tabs = new List<TeamsTab> { new TeamsTab { Id = "tab-" + suffix, DisplayName = "Contoso Καλημέρα", WebUrl = "https://contoso.example/Καλημέρα" } }
            };
            var source = new O365Team
            {
                Id = "team-" + suffix, DisplayName = "Contoso Καλημέρα", HasRefreshToken = true, LastRefreshed = date,
                Channels = new List<ChannelWithReactions> { channel },
                OwnerUserAccounts = new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = "owner-" + suffix + "@contoso.example" } },
                Users = new List<BaseUser> { new BaseUser { UserPrincipalName = "member-" + suffix + "@contoso.example" } },
                AllReactions = new List<UserReaction> { new UserReaction { ChannelId = channel.Id, GraphUser = null, Reaction = "like", When = date } }
            };
            var cognitive = new FixedStats(date);
            var tokens = new InMemoryTeamChannelDeltaTokenStore();
            source.PendingChannelDeltaTokenCommits.Add(new TeamChannelDeltaTokenCommit(channel.Id, new TeamChannelDeltaTokenInfo { Token = "synthetic-checkpoint", LastUpdated = date }));
            var logger = new RecordingLogger();

            using (var db = new AnalyticsEntitiesContext())
            using (var transaction = db.Database.BeginTransaction())
            {
                var lookup = new TeamsAndCallsDBLookupManager(db);
                TeamDefinition saved;
                using (var adapter = new SqlTeamsPersistenceStore(lookup))
                {
                    Assert.IsFalse(db.Configuration.AutoDetectChangesEnabled);
                    saved = await source.SaveToSQL(adapter, cognitive, tokens, logger);
                }
                Assert.IsTrue(db.Configuration.AutoDetectChangesEnabled);
                Assert.IsNotNull(saved);
                var savedChannel = await db.TeamChannels.AsNoTracking().SingleAsync(c => c.GraphID == channel.Id);
                Assert.AreEqual(channel.DisplayName, savedChannel.Name, "Unicode crosses the real SQL boundary.");
                var stats = await db.TeamChannelStats.AsNoTracking().SingleAsync(s => s.ChannelID == savedChannel.ID);
                Assert.AreEqual(2, stats.ChatsCount);
                Assert.AreEqual(0.75, stats.SentimentScore.Value, 0.001);
                Assert.AreEqual(1, await db.TeamChannelStatKeywords.CountAsync(s => s.ChannelStatsLogID == stats.ID));
                Assert.AreEqual(1, await db.TeamChannelStatLanguages.CountAsync(s => s.ChannelStatsLogID == stats.ID));
                Assert.AreEqual(1, await db.TeamOwners.CountAsync(o => o.TeamID == saved.ID));
                Assert.AreEqual(1, await db.TeamMembershipLogs.CountAsync(m => m.TeamID == saved.ID));
                Assert.AreEqual(1, await db.ChannelTabLogs.CountAsync(t => t.ChannelID == savedChannel.ID));
                Assert.AreEqual(1, await db.TeamsUserReactions.CountAsync(r => r.ChannelID == savedChannel.ID));
                Assert.AreEqual("synthetic-checkpoint", (await tokens.GetDeltaToken(source.Id, channel.Id)).Token);

                cognitive.Sentiment = 0.25;
                source.HasRefreshToken = false;
                source.LastRefreshed = date.AddDays(1);
                using (var adapter = new SqlTeamsPersistenceStore(lookup))
                    await source.SaveToSQL(adapter, cognitive, tokens, logger);
                var updated = await db.TeamChannelStats.AsNoTracking().SingleAsync(s => s.ID == stats.ID);
                Assert.AreEqual(4, updated.ChatsCount, "DetectChanges must persist in-place updates, not just new rows.");
                Assert.AreEqual(0.5, updated.SentimentScore.Value, 0.001);
                var updatedTeam = await db.Teams.AsNoTracking().SingleAsync(t => t.ID == saved.ID);
                Assert.IsFalse(updatedTeam.HasRefreshToken);
                Assert.AreEqual(source.LastRefreshed, updatedTeam.LastRefreshed);
                Assert.AreEqual(1, await db.TeamOwners.CountAsync(o => o.TeamID == saved.ID));
                Assert.AreEqual(1, await db.TeamMembershipLogs.CountAsync(m => m.TeamID == saved.ID));
                Assert.AreEqual(1, await db.ChannelTabLogs.CountAsync(t => t.ChannelID == savedChannel.ID));
                Assert.AreEqual(1, await db.TeamChannelStatKeywords.CountAsync(s => s.ChannelStatsLogID == stats.ID));
                Assert.AreEqual(1, await db.TeamChannelStatLanguages.CountAsync(s => s.ChannelStatsLogID == stats.ID));
                Assert.AreEqual(2, await db.TeamsUserReactions.CountAsync(r => r.ChannelID == savedChannel.ID), "Existing append-only reaction semantics are unchanged.");
                Assert.IsTrue(db.Configuration.AutoDetectChangesEnabled);
                transaction.Rollback();
            }
        }

        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task SqlAdapter_ImplicitLookupFlush_PreservesOwnerRelationshipAndRestoresTrackingOnFailure()
        {
            var suffix = Guid.NewGuid().ToString();
            using (var db = new AnalyticsEntitiesContext())
            using (var transaction = db.Database.BeginTransaction())
            {
                var source = new O365Team { Id = "team-" + suffix, DisplayName = "Contoso" };
                var lookup = new TeamsAndCallsDBLookupManager(db);
                using (var adapter = new SqlTeamsPersistenceStore(lookup))
                {
                    var team = await adapter.GetOrCreateTeam(source);
                    await adapter.SaveOwners(new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { UserPrincipalName = "owner-" + suffix + "@contoso.example" } }, team);
                    await adapter.SaveMembers(new List<BaseUser> { new BaseUser { UserPrincipalName = "member-" + suffix + "@contoso.example" } }, source);
                    Assert.AreEqual(1, await db.TeamOwners.CountAsync(o => o.TeamID == team.ID),
                        "The new member's lookup still flushes the pending owner link, before the final Team save.");
                    source.HasRefreshToken = true;
                    await adapter.SaveChanges(source, team);
                }
                Assert.IsTrue(db.Configuration.AutoDetectChangesEnabled);

                db.Configuration.AutoDetectChangesEnabled = false;
                try
                {
                    using (var adapter = new SqlTeamsPersistenceStore(lookup))
                        await adapter.SaveChannel(new ChannelWithReactions { Id = null }, new TeamDefinition());
                    Assert.Fail("Invalid channel should fail before commit.");
                }
                catch (ArgumentNullException)
                {
                    Assert.IsFalse(db.Configuration.AutoDetectChangesEnabled, "Restore the incoming setting, not always true.");
                }
                finally
                {
                    db.Configuration.AutoDetectChangesEnabled = true;
                }
                transaction.Rollback();
            }
        }

        private sealed class FixedStats : ITeamsCognitiveStatsLoader
        {
            private readonly DateTime _date;
            public FixedStats(DateTime date) { _date = date; }
            public double Sentiment { get; set; } = 0.75;
            public Task<List<MessageCognitiveStats>> LoadStats(ChannelWithReactions channel, ILogger logger) =>
                Task.FromResult(new List<MessageCognitiveStats> { new MessageCognitiveStats(channel, _date)
                {
                    ChatsCount = 2, Sentiment = Sentiment, KeyWords = new Dictionary<string, int> { ["Contoso Καλημέρα"] = 1 }, Languages = new List<string> { "en" }
                } });
        }
    }
}
