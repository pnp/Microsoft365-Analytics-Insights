using Azure;
using Azure.AI.TextAnalytics;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    [TestClass]
    public class TeamsTests
    {
        [TestMethod()]
        public async Task ChatMessageLoadCognitiveStatsFromCacheOrAITests()
        {

            var cognitiveConfig = new AppConfig();
            if (!cognitiveConfig.IsValidCognitiveConfig)
            {
                Assert.Inconclusive("Cognitive config is not set, cannot run test");
                return;
            }

            var client = cognitiveConfig.CreateCognitiveServicesClient(AnalyticsLogger.ConsoleOnlyTracer());
            if (client == null)
            {
                Assert.Inconclusive("Cognitive config is not set, cannot run test");
                return;
            }

            var msg = new ChatMessage();
            msg.Id = "Test " + DateTime.Now.Ticks;
            msg.CreatedDateTime = DateTimeOffset.Now;
            msg.Body = new ItemBody();
            msg.Body.Content = "This is a test";
            msg.Body.ContentType = BodyType.Text;
            msg.Replies = new List<ChatMessage>() { new ChatMessage { Id = "Reply ", Body = new ItemBody { Content = "Fantastic work", ContentType = BodyType.Text } } };

            var channel = new ChannelWithReactions();
            channel.Id = "Test";
            channel.DisplayName = "Test";

            // Should use AI
            var (stats, fromCache) = await msg.LoadCognitiveStatsFromCacheOrAI(client, AnalyticsLogger.ConsoleOnlyTracer(), channel);
            Assert.IsFalse(fromCache);
            Assert.IsNotNull(stats);

            // Should use cache
            (stats, fromCache) = await msg.LoadCognitiveStatsFromCacheOrAI(client, AnalyticsLogger.ConsoleOnlyTracer(), channel);
            Assert.IsTrue(fromCache);
            Assert.IsNotNull(stats);
        }

        [TestMethod()]
        public async Task UniqueConstraintsTests()
        {

            using (var db = new AnalyticsEntitiesContext())
            {
                var uniqueName = "Test" + DateTime.Now.Ticks;

                // Lookups
                db.CallModalities.Add(new Common.Entities.Entities.Teams.CallModality() { Name = uniqueName });
                await db.SaveChangesAsync();
                db.CallModalities.Add(new Common.Entities.Entities.Teams.CallModality() { Name = uniqueName });
                await Assert.ThrowsExceptionAsync<System.Data.Entity.Infrastructure.DbUpdateException>(() => db.SaveChangesAsync());
            }
        }
    }
}
