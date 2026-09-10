using Azure.Messaging.ServiceBus;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Teams;
using Common.Entities.Models;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Tests.UnitTests.FakeControllers;
using Tests.UnitTests.FakeEntities;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphImportTests
    {
        [TestMethod]
        public void MessageCognitiveStatsTests()
        {
            var stats = new MessageCognitiveStats(new ChannelWithReactions(), DateTime.Now);
            stats.ChatsCount = 4;
            stats.Sentiment = 0;

            var existingLog = new ChannelStatsLog();
            existingLog.ChatsCount = 1;
            existingLog.SentimentScore = 1;

            stats.IncrementMessageStatsWithThis(existingLog);
            Assert.IsTrue(existingLog.SentimentScore == 0.2);
            Assert.IsTrue(existingLog.ChatsCount == 5);
        }

#if DEBUG
        // Live Graph integration test: requires real tenant credentials in config to run.
        // Excluded from CI Release builds (CI does not have a test tenant). Run locally in Debug.
        [TestMethod]
        [TestCategory("Integration")]
        public async Task MessageImportTests()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(logger, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);

            var finder = new TeamsFinder(logger, config, graphClient);
            var allGroups = await finder.FindGroupsWithTeamToCrawl(TeamsCrawlConfig.AllGroupsConfig);

            Assert.IsTrue(allGroups.Count > 0, "No teams found to load messages for");

            using (var db = new AnalyticsEntitiesContext())
            {
                var context = new TeamsLoadContext(graphClient);
                var team = await O365Team.LoadTeamFull(allGroups[0], context, logger, config, db);
                var channel = team.Channels.First();
                var user = new Identity { Id = team.OwnerUserAccounts[0].Id };

                // Delete previous db record + dependencies
                var oldRec = await db.TeamChannels.Where(c => c.GraphID == channel.Id).SingleOrDefaultAsync();
                if (oldRec != null)
                {
                    db.TeamChannels.Remove(oldRec);
                    db.ChannelTabLogs.RemoveRange(db.ChannelTabLogs.Where(l => l.ChannelID == oldRec.ID).ToList());

                    var channelStats = db.TeamChannelStats.Where(l => l.ChannelID == oldRec.ID).ToList();
                    db.TeamChannelStats.RemoveRange(channelStats);
                    foreach (var cs in channelStats)
                    {
                        db.TeamChannelStatLanguages.RemoveRange(db.TeamChannelStatLanguages.Where(l => l.ChannelStatsLogID == cs.ID).ToList());
                        db.TeamChannelStatKeywords.RemoveRange(db.TeamChannelStatKeywords.Where(l => l.ChannelStatsLogID == cs.ID).ToList());
                    }
                    await db.SaveChangesAsync();
                }

                // Add fake messages as we can't load real ones in unit-testing
                var msgRoot = new ChatMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    From = new ChatMessageFromIdentitySet { User = user },
                    CreatedDateTime = DateTime.Now.AddDays(-1),
                    MessageType = ChatMessageType.Message,
                    Body = new ItemBody { Content = "Super happy message - everything is so awesome. Amazing,", ContentType = BodyType.Text },
                    Reactions = new List<ChatMessageReaction>
                    {
                        new ChatMessageReaction { ReactionType = "Like", User = new ChatMessageReactionIdentitySet { User = user }, CreatedDateTime = DateTime.Now }
                    }
                };
                msgRoot.Replies = new List<ChatMessage> {
                    new ChatMessage
                    {
                        Id = Guid.NewGuid().ToString(),
                        From = new ChatMessageFromIdentitySet { User = user },
                        CreatedDateTime = DateTime.Now,
                        MessageType = ChatMessageType.Message,
                        Body = new ItemBody { Content = "Bloody awful reply, everything's shit. Proper bollucks.", ContentType = BodyType.Text },
                        Reactions = new List<ChatMessageReaction>
                        {
                            new ChatMessageReaction { ReactionType = "Dislike", User = new ChatMessageReactionIdentitySet { User = user }, CreatedDateTime = DateTime.Now }
                        }
                    }
                };
                if (channel.Messages == null)
                {
                    channel.Messages = new List<ChatMessage>();
                }
                channel.Messages.Add(msgRoot);
                channel.CalculateAndSetNewMessagesAndReactions(channel.Messages, DateTime.Now.AddDays(-7), logger);

                // Rebuild reactions from msgs now we have fake messages
                await team.ProcessAllReactionsFromMessages(context, channel);


                var sqlTeam = await team.SaveToSQL(new TeamsAndCallsDBLookupManager(db), config, logger);
                Assert.IsNotNull(sqlTeam);

                // Check we see reactions & stats
                var channelFull = await db.TeamChannels.Where(c => c.GraphID == channel.Id)
                    .Include(t => t.DailyStats).Include(t => t.Reactions).SingleOrDefaultAsync();

                // Stats should be x2 - one for each day
                Assert.IsTrue(channelFull.DailyStats.Count == 2);
                Assert.IsTrue(channelFull.Reactions.Count == 2);
            }

        }
#endif

        [TestMethod]
        public void TeamsCrawlConfigTests()
        {
            var c = new TeamsCrawlConfig();

            Assert.IsFalse(c.WhitelistTeamsIds.Any());
            Assert.IsFalse(c.BlacklistTeamsIds.Any());

            var randomGuid = Guid.NewGuid().ToString();

            // No config - allow all
            Assert.IsTrue(c.CrawlGroup(randomGuid));
            Assert.IsTrue(c.CrawlGroup(Guid.NewGuid().ToString()));

            // Only allow whitelisted 
            c.WhitelistTeamsIds.Add(randomGuid);
            Assert.IsTrue(c.CrawlGroup(randomGuid));
            Assert.IsFalse(c.CrawlGroup(Guid.NewGuid().ToString()));

            c.WhitelistTeamsIds.Clear();


            // Specifically deny blacklisted but allow anything else as no whitelist 
            c.BlacklistTeamsIds.Add(randomGuid);
            Assert.IsFalse(c.CrawlGroup(randomGuid));
            Assert.IsTrue(c.CrawlGroup(Guid.NewGuid().ToString()));

        }

        private int _callsProcessed = 0;

        /// <summary>
        /// Insert a bunch of fake calls to SB so our calls processor picks it up. We then use a fake HTTP server to simulate Graph, and insert it in the DB
        /// </summary>
        [TestMethod]
        public async Task CallQueueProcessorTest()
        {
            const int CALLS_TO_ADD = 10;
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            var httpConfig = new HttpConfiguration();
            httpConfig.MapHttpAttributeRoutes();
            var server = new HttpServer(httpConfig);        // Will use fake controllers in test project

            // Create new SB client (updated to RBAC credential auth)
            var config = new AppConfig();
            var conString = config.ConnectionStrings.ServiceBusConnectionString;
            var sbConnectionProps = ServiceBusConnectionStringProperties.Parse(conString);
            var fqNamespace = sbConnectionProps.Endpoint.Host; // e.g. namespace.servicebus.windows.net
            var queueName = sbConnectionProps.EntityPath; // queue name

            using (var db = new AnalyticsEntitiesContext())
            {
                var callCountInitial = await db.CallRecords.CountAsync();
                using (var client = new ManualGraphCallClient(server, logger))
                {
                    using (var callProcessor = new CallQueueProcessor(config, config.TenantGUID.ToString()))
                    {
                        await callProcessor.Init(client);
                        callProcessor.CallProcessed += CallProcessor_CallProcessed;

                        // Start listening to SB
                        _ = callProcessor.BeginProcessCallsQueue();

                        // START TEST: Send fake msgs through SB - remember IDs
                        var sbSender = callProcessor.ServiceBusClient.CreateSender(queueName);

                        var testIds = new List<string>();
                        for (int i = 0; i < CALLS_TO_ADD; i++)
                        {
                            var newCallId = Guid.NewGuid();
                            var change = new GraphChangeNotification { ResourceData = new Common.Entities.Models.ResourceData { Id = newCallId.ToString() } };
                            await CallQueueProcessor.AddChangeMsgToQueue(new List<GraphChangeNotification> { change }, logger, sbSender);
                            testIds.Add(newCallId.ToString());
                        }

                        bool wait = true;
                        var waitStart = DateTime.Now;
                        while (wait)
                        {
                            lock (this)
                            {
                                wait = _callsProcessed < CALLS_TO_ADD;
                            }

                            // Only wait a few minutes to get messages back
                            if (waitStart < DateTime.Now.AddMinutes(-5))
                            {
                                throw new Exception("Test didn't work - timeout");
                            }

                            // Wait for messages to be processed
                            await Task.Delay(1000);
                        }

                        // Check again
                        var callCountPost = await db.CallRecords.CountAsync();

                        // Make sure we can see the new call
                        Assert.IsTrue(callCountPost > callCountInitial);

                        // Check each call added
                        // IMPORTANT: If the testing shares _any_ other queue processor, this will fail as it'll pick up other messages
                        var idsInserted = await db.CallRecords.Select(c => c.GraphID).ToListAsync();
                        foreach (var idAdded in testIds)
                        {
                            Assert.IsTrue(idsInserted.Contains(idAdded));
                        }
                    }
                }
            }
        }

        private void CallProcessor_CallProcessed(object sender, EventArgs e)
        {
            lock (this)
            {
                _callsProcessed++;
            }
        }

        /// <summary>
        /// Tests LoadAllPagesWithThrottleRetries works
        /// </summary>
        [TestMethod]
        public async Task LoadAllPagesWithThrottleRetriesTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            var server = new HttpServer(config);

            using (var client = new ManualGraphCallClient(server, logger))
            {
                await TestPageResponse(client, logger, 100, 10);
                await TestPageResponse(client, logger, 100, 1);
                await TestPageResponse(client, logger, 102, 10);
                await TestPageResponse(client, logger, 1000, 10);

            }
        }

        private async Task TestPageResponse(ManualGraphCallClient client, ILogger logger, int v1, int v2)
        {
            var url = FakePageableResultsController.GetUrl(0, v1, v2);
            var results = await client.LoadAllPagesWithThrottleRetries<FakePagedResult>(url, logger);

            Assert.IsTrue(results.Count == v1);
        }

        [TestMethod]
        public async Task SingleTeamSaveToSQLTest()
        {
            var testDate = DateTime.Now.Date;
            var testTeam = new O365Team() { DisplayName = "Unit testing team", Id = GetRandomID() };
            var channelWithMsgsOnDifferentDays = new ChannelWithReactions()
            {
                DisplayName = "Unit testing channel",
                Id = GetRandomID(),
                Messages = new List<ChatMessage>()
                {   
                    // Negative sentiment data
                    new ChatMessage
                    {
                        Body = new ItemBody { Content = "Terrible old message - everything sucks", ContentType = BodyType.Text },
                        CreatedDateTime = testDate.AddDays(-1),
                        Id = GetRandomID()
                    },
                    new ChatMessage
                    {
                        Body = new ItemBody { Content = "Terrible message - this is awful", ContentType = BodyType.Text },
                        CreatedDateTime = testDate,
                        Id = GetRandomID()
                    }
                }
            };
            testTeam.Channels.Add(channelWithMsgsOnDifferentDays);

            var settings = new AppConfig();

            // Save & load
            using (var db = new AnalyticsEntitiesContext())
            {
                // PREP: Delete all stats for test-date
                var testLogs = await db.TeamChannelStats.ToListAsync();
                var testLogKeywords = await db.TeamChannelStatKeywords.ToListAsync();
                var testLogLangs = await db.TeamChannelStatLanguages.ToListAsync();

                db.TeamChannelStatKeywords.RemoveRange(testLogKeywords);
                db.TeamChannelStatLanguages.RemoveRange(testLogLangs);
                db.TeamChannelStats.RemoveRange(testLogs);
                await db.SaveChangesAsync();

                var lookupManager = new TeamsAndCallsDBLookupManager(db);
                var preTestChannelLogCount = await db.TeamChannelStats.CountAsync();

                // Save 
                await testTeam.SaveToSQL(lookupManager, settings, AnalyticsLogger.ConsoleOnlyTracer());

                // Check SQL data. Should be 1 log per day of messages. 
                var firstPostTestChannelLogCount = await db.TeamChannelStats.CountAsync();
                int msgsInserted = firstPostTestChannelLogCount - preTestChannelLogCount;
                Assert.IsTrue(msgsInserted == channelWithMsgsOnDifferentDays.Messages.Count, "Unexpected channel stats inserted");

                // Check last inserted log data
                Assert.IsTrue(await db.TeamChannelStats.Where(l => l.Date == testDate).CountAsync() > 0, "Couldn't find any stats by test date");
                var lastInserted = await db.TeamChannelStats.OrderByDescending(s => s.ID).FirstOrDefaultAsync();

                if (settings.IsValidCognitiveConfig)
                    Assert.IsTrue(lastInserted.SentimentScore.HasValue, "Last inserted log has no sentiment score");
                else
                    Assert.Inconclusive("No cognitive config provided, can't test sentiment score presence or value");

                // Last inserted log should have msg count for messages on that day only
                Assert.IsTrue(lastInserted.ChatsCount == channelWithMsgsOnDifferentDays.Messages
                    .Where(m => m.CreatedDateTime.Value.Date == lastInserted.Date).Count(), "Last inserted log has unexpected messages count");

                // Save again
                await testTeam.SaveToSQL(lookupManager, settings, AnalyticsLogger.ConsoleOnlyTracer());
                var secondPostTestChannelLogCount = await db.TeamChannelStats.CountAsync();

                // 3rd count should be same as post 1st-save count as we already have logs for those days, so no changes should be made
                Assert.IsTrue(secondPostTestChannelLogCount == firstPostTestChannelLogCount, "Unexpected channel stats inserted for 2nd save of same data");

                // Add 3 happy msgs on new day
                channelWithMsgsOnDifferentDays.Messages.Add(
                    new ChatMessage
                    {
                        Body = new ItemBody { Content = "Super-awesome message - everything is tip-top", ContentType = BodyType.Text },
                        CreatedDateTime = testDate.AddDays(1),
                        Id = GetRandomID()
                    }
                );
                channelWithMsgsOnDifferentDays.Messages.Add(
                    new ChatMessage
                    {
                        Body = new ItemBody { Content = "Super-awesome old message - everything is amazing", ContentType = BodyType.Text },
                        CreatedDateTime = testDate.AddDays(1),
                        Id = GetRandomID()
                    }
                );
                channelWithMsgsOnDifferentDays.Messages.Add(
                    new ChatMessage
                    {
                        Body = new ItemBody { Content = "Neutral message - this is ok", ContentType = BodyType.Text },
                        CreatedDateTime = testDate.AddDays(1),
                        Id = GetRandomID()
                    }
                );

                // Save one more time
                await testTeam.SaveToSQL(lookupManager, settings, AnalyticsLogger.ConsoleOnlyTracer());
                var thirdPostTestChannelLogCount = await db.TeamChannelStats.CountAsync();

                // 4th count should only include the last happy msgs - 1 log as they're all on the same day
                Assert.IsTrue(thirdPostTestChannelLogCount == 3, "Unexpected channel stats inserted for 2nd save of same data");
                var lastInsertedHappy = await db.TeamChannelStats.OrderByDescending(s => s.ID).FirstOrDefaultAsync();


                if (settings.IsValidCognitiveConfig)
                    Assert.IsTrue(lastInsertedHappy.SentimentScore > lastInserted.SentimentScore, "Happier sentiment sentances aren't apparently more happy");
                else
                    Assert.Inconclusive("No cognitive config provided, can't test sentiment score presence or value");
            }
        }

        [TestMethod]
        public async Task AllTeamsLoadTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(logger, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
            await auth.InitClientCredential();

            var importer = new TeamsImporter(logger, new AppConfig(), new GraphServiceClient(auth.Creds));

            // Use a filter for tests?
            const string SEP = ";";
            var selectedTeamsAll = System.Configuration.ConfigurationManager.AppSettings.Get("UnitTestsTeamsWhiteList");
            var filter = TeamsCrawlConfig.AllGroupsConfig;
            if (!string.IsNullOrEmpty(selectedTeamsAll))
            {
                var teamIds = selectedTeamsAll.Split(SEP.ToCharArray());
                foreach (var teamId in teamIds)
                {
                    var g = Guid.Empty;
                    if (Guid.TryParse(teamId, out g))
                    {
                        filter.WhitelistTeamsIds.Add(teamId);
                    }
                }
            }
            await importer.RefreshAndSaveAllTeamsData(filter);
        }

        static string GetRandomID()
        {
            return Guid.NewGuid().ToString();
        }
    }
}
