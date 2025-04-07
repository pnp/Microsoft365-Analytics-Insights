using Azure.Messaging.ServiceBus;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Teams;
using Common.Entities.Models;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.SharePoint.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Tests.UnitTests.FakeControllers;
using Tests.UnitTests.FakeEntities;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphImportTests
    {
        [TestMethod]
        public async Task UserAppLoaderFakeTest()
        {
            const int users = 10000;
            var l = new FakeUserAppLoader(AnalyticsLogger.ConsoleOnlyTracer(), users);
            var updates = await l.LoadAndSave();
            Assert.IsTrue(updates == users);
        }


        // Removing test as devops environment has too many users and test times out
        //[TestMethod]
        public async Task UserAppLoaderRealTest()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(telemetry, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);

            // Do a users import first so we have users in the users table to read apps for
            var userUpdater = new UserMetadataUpdater(telemetry, config, auth.Creds, new ManualGraphCallClient(auth, telemetry));
            await userUpdater.InsertAndUpdateDatabaseUsersFromGraph();

            var updater = new UserAppLogUpdater(telemetry, new AppConfig());
            var sucess = await updater.UpdateUserInstalledApps(graphClient);
            Assert.IsTrue(sucess);
        }

        /// <summary>
        /// Check the app-log insert/update code works
        /// </summary>
        [TestMethod]
        public async Task UserAppSqlSaveTest()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(telemetry, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);
            using (var db = new AnalyticsEntitiesContext())
            {
                var userAppsLoader = new GraphAndSqlUserAppLoader(db, telemetry, graphClient);

                var testUser = new Common.Entities.User { AzureAdId = Guid.NewGuid().ToString(), UserPrincipalName = $"teamsappsuser{DateTime.Now.Ticks}@unitesting.local" };
                db.users.Add(testUser);

                var newAppDef1 = new Common.Entities.Teams.TeamAddOnDefinition { GraphID = Guid.NewGuid().ToString(), Name = "Test app 1+ " + DateTime.Now.Ticks };
                var newAppDef2 = new Common.Entities.Teams.TeamAddOnDefinition { GraphID = Guid.NewGuid().ToString(), Name = "Test app 2+ " + DateTime.Now.Ticks };
                db.TeamAddOns.AddRange(new Common.Entities.Teams.TeamAddOnDefinition[] { newAppDef1, newAppDef2 });
                await db.SaveChangesAsync();

                var testData = new Dictionary<string, List<UserTeamApp>>
                {
                    {
                        testUser.UserPrincipalName,
                        new List<UserTeamApp>
                        {
                            new UserTeamApp { TeamsAppDefinition = new TeamsAppDefinition { TeamsAppId = newAppDef1.GraphID, DisplayName = newAppDef1.Name } },
                            new UserTeamApp { TeamsAppDefinition = new TeamsAppDefinition { TeamsAppId = newAppDef2.GraphID, DisplayName = newAppDef2.Name } }
                        }
                    }
                };

                await userAppsLoader.Save(testData);

                // Find logs. Should only be two
                var logs = await db.UserAppsLog.Where(l => l.UserID == testUser.ID).ToListAsync();
                Assert.IsTrue(logs.Count == 2);

                // Save again. Should still only be two (same date)
                await userAppsLoader.Save(testData);
                logs = await db.UserAppsLog.Where(l => l.UserID == testUser.ID).ToListAsync();
                Assert.IsTrue(logs.Count == 2);

                // Fake logs as yesterdays
                logs[0].Date = logs[0].Date.AddDays(-1);
                logs[1].Date = logs[1].Date.AddDays(-1);
                await db.SaveChangesAsync();

                // Save should now insert new
                await userAppsLoader.Save(testData);
                logs = await db.UserAppsLog.Where(l => l.UserID == testUser.ID).ToListAsync();
                Assert.IsTrue(logs.Count == 4);
            }
        }

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

        [TestMethod]
        public async Task MessageImportTests()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(telemetry, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);

            await auth.InitClientCredential();
            var graphClient = new GraphServiceClient(auth.Creds);

            var finder = new TeamsFinder(telemetry, config, graphClient);
            var allGroups = await finder.FindGroupsWithTeamToCrawl(TeamsCrawlConfig.AllGroupsConfig);

            Assert.IsTrue(allGroups.Count > 0, "No teams found to load messages for");

            using (var db = new AnalyticsEntitiesContext())
            {
                var context = new TeamsLoadContext(graphClient);
                var team = await O365Team.LoadTeamFull(allGroups[0], context, telemetry, config, db);
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
                msgRoot.Replies = new ChatMessageRepliesCollectionPage {
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
                channel.CalculateAndSetNewMessagesAndReactions(channel.Messages, DateTime.Now.AddDays(-7), telemetry);

                // Rebuild reactions from msgs now we have fake messages
                await team.ProcessAllReactionsFromMessages(context, channel);


                var sqlTeam = await team.SaveToSQL(new TeamsAndCallsDBLookupManager(db), config, telemetry);
                Assert.IsNotNull(sqlTeam);

                // Check we see reactions & stats
                var channelFull = await db.TeamChannels.Where(c => c.GraphID == channel.Id)
                    .Include(t => t.DailyStats).Include(t => t.Reactions).SingleOrDefaultAsync();

                // Stats should be x2 - one for each day
                Assert.IsTrue(channelFull.DailyStats.Count == 2);
                Assert.IsTrue(channelFull.Reactions.Count == 2);
            }

        }

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
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            var httpConfig = new HttpConfiguration();
            httpConfig.MapHttpAttributeRoutes();
            var server = new HttpServer(httpConfig);        // Will use fake controllers in test project

            // Create new SB client
            var config = new AppConfig();
            var conString = config.ConnectionStrings.ServiceBusConnectionString;
            var sbClient = new ServiceBusClient(conString);
            var sbConnectionProps = ServiceBusConnectionStringProperties.Parse(conString);
            var sbSender = sbClient.CreateSender(sbConnectionProps.EntityPath);

            using (var db = new AnalyticsEntitiesContext())
            {
                var callCountInitial = await db.CallRecords.CountAsync();
                using (var client = new ManualGraphCallClient(server, telemetry))
                {
                    using (var callProcessor = await CallQueueProcessor.GetCallQueueProcessor(config,
                        config.TenantGUID.ToString(), client))
                    {
                        callProcessor.CallProcessed += CallProcessor_CallProcessed;

                        // Start listening to SB
                        _ = callProcessor.BeginProcessCallsQueue();

                        // START TEST: Send fake msgs through SB - remember IDs
                        var testIds = new List<string>();
                        for (int i = 0; i < CALLS_TO_ADD; i++)
                        {
                            var newCallId = Guid.NewGuid();
                            var change = new GraphChangeNotification { ResourceData = new Common.Entities.Models.ResourceData { Id = newCallId.ToString() } };
                            await CallQueueProcessor.AddChangeMsgToQueue(new List<GraphChangeNotification> { change }, telemetry, sbSender);
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

        [TestMethod]
        public void OfficeLicenseNameResolverTest()
        {
            var resolver = new OfficeLicenseNameResolver();
            Assert.IsTrue(resolver.GetDisplayNameFor("DYN365_BUSCENTRAL_ESSENTIAL") == "Dynamics 365 Business Central Essentials");

            Assert.IsNull(resolver.GetDisplayNameFor(""));
        }

        /// <summary>
        /// Tests LoadAllPagesWithThrottleRetries works
        /// </summary>
        [TestMethod]
        public async Task LoadAllPagesWithThrottleRetriesTest()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            var server = new HttpServer(config);

            using (var client = new ManualGraphCallClient(server, telemetry))
            {
                await TestPageResponse(client, telemetry, 100, 10);
                await TestPageResponse(client, telemetry, 100, 1);
                await TestPageResponse(client, telemetry, 102, 10);
                await TestPageResponse(client, telemetry, 1000, 10);

            }
        }

        private async Task TestPageResponse(ManualGraphCallClient client, ILogger telemetry, int v1, int v2)
        {
            var url = FakePageableResultsController.GetUrl(0, v1, v2);
            var results = await client.LoadAllPagesWithThrottleRetries<FakePagedResult>(url, telemetry);

            Assert.IsTrue(results.Count == v1);
        }

        // Removing test as devops environment has too many users and test times out
        //[TestMethod]
        public async Task UserMetadataUpdaterTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Update users

                var authConfig = new AppConfig();
                var auth = new GraphAppIndentityOAuthContext(AnalyticsLogger.ConsoleOnlyTracer(), authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
                await auth.InitClientCredential();
                var graphClient = new GraphServiceClient(auth.Creds);

                // Get Allan user from Graph & insert blanks into DB (needs license)
                var graphUsers = await graphClient.Users.Request().Filter("startswith(mail,'AllanD')").Top(1).GetAsync();
                var graphUser = graphUsers[0];

                // Run updater; force full load
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var userUpdater = new UserMetadataUpdater(telemetry, authConfig, auth.Creds, new ManualGraphCallClient(auth, telemetry));
                await userUpdater.GraphUserLoader.DeltaValueProvider.ClearDeltaToken();

                await userUpdater.InsertAndUpdateDatabaseUsersFromGraph();

                // Check our user just updated. Should be updated now with actual data
                var dbTestUser = await db.users
                    .Include(u => u.OfficeLocation)
                    .Include(u => u.UsageLocation)
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Include(u => u.JobTitle)
                    .Include(u => u.Department)
                    .Where(u => u.UserPrincipalName == graphUser.Mail).SingleOrDefaultAsync();

                Assert.IsTrue(dbTestUser.LicenseLookups.Count > 0);
                Assert.IsNotNull(dbTestUser.OfficeLocation);
                Assert.IsNotNull(dbTestUser.UsageLocation);
                Assert.IsNotNull(dbTestUser.Department);
                Assert.IsTrue(dbTestUser.AccountEnabled);

                // Update again. Should use the delta this time
                await userUpdater.InsertAndUpdateDatabaseUsersFromGraph();


                // Update again with no delta. Test logic for updating just existing
                await userUpdater.GraphUserLoader.DeltaValueProvider.ClearDeltaToken();
                await userUpdater.InsertAndUpdateDatabaseUsersFromGraph();
            }
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
                Assert.IsTrue(lastInserted.SentimentScore.HasValue, "Last inserted log has no sentiment score");

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
                Assert.IsTrue(lastInsertedHappy.SentimentScore > lastInserted.SentimentScore, "Happier sentiment sentances aren't apparently more happy");
            }
        }

        [TestMethod]
        public async Task AllTeamsLoadTest()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();
            var auth = new GraphAppIndentityOAuthContext(telemetry, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
            await auth.InitClientCredential();

            var importer = new TeamsImporter(telemetry, new AppConfig(), new GraphServiceClient(auth.Creds));

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
