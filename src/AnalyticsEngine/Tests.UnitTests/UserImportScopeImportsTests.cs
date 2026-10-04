using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Models;
using Common.Entities.UserOrgs;
using Common.Entities.UserScope;
using DbUser = Common.Entities.User;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.AgentCosts;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests
{
    /// <summary>
    /// <c>UserGroupsFilter</c> applied by each import that used to ignore it - calls, the Teams crawl, web traffic,
    /// sent email, Copilot Studio per-user credits and the user directory import - and by the usage reports, which now
    /// check the shared scope instead of calling Graph per user.
    /// </summary>
    [TestClass]
    public class UserImportScopeImportsTests
    {
        private const string Pilot = "pilot@contoso.com";
        private const string PilotId = "bbbbbbbb-0000-0000-0000-000000000001";
        private const string Outsider = "outsider@contoso.com";
        private const string OutsiderId = "bbbbbbbb-0000-0000-0000-000000000002";

        private static UserImportScope PilotOnly() => TestUserScopes.OfMembers((PilotId, Pilot, Pilot));

        #region Calls

        private static CallRecordDTO Call(string organiser, params (string Caller, string Callee)[] sessions)
        {
            return new CallRecordDTO
            {
                GraphCallID = Guid.NewGuid().ToString(),
                OrganizerEmail = organiser,
                CallType = "groupCall",
                StartDateTime = DateTime.UtcNow.AddHours(-1),
                EndDateTime = DateTime.UtcNow,
                Sessions = sessions.Select(s => new CallSessionDTO
                {
                    Caller = new ParticipantEndpointDTO { UserEmailAddress = s.Caller },
                    Callee = new ParticipantEndpointDTO { UserEmailAddress = s.Callee },
                    StartDateTime = DateTime.UtcNow.AddMinutes(-50),
                    EndDateTime = DateTime.UtcNow.AddMinutes(-10),
                }).ToList()
            };
        }

        [TestMethod]
        public void CallRules_AnyoneOnTheCall_OrganiserOrParticipant_KeepsIt()
        {
            var scope = PilotOnly();

            Assert.IsTrue(CallRecordScopeRules.AnyParticipantInScope(Call(Pilot, (Pilot, Outsider)), scope), "In-scope organiser.");
            Assert.IsTrue(CallRecordScopeRules.AnyParticipantInScope(Call(Outsider, (Outsider, Pilot)), scope), "In-scope attendee of someone else's call.");
            Assert.IsFalse(CallRecordScopeRules.AnyParticipantInScope(Call(Outsider, (Outsider, "another.outsider@contoso.com")), scope),
                "Nobody on the call is in scope.");
            Assert.IsTrue(CallRecordScopeRules.AnyParticipantInScope(Call(Outsider, (Outsider, "x@contoso.com")), UserImportScope.Unfiltered));
        }

        [TestMethod]
        public void GetOtherUserEmail_ComparesTheRealIdentities()
        {
            var session = new CallSessionDTO
            {
                Caller = new ParticipantEndpointDTO { UserEmailAddress = "ORGANISER@contoso.com" },
                Callee = new ParticipantEndpointDTO { UserEmailAddress = Outsider },
            };

            Assert.AreEqual(Outsider, CallRecordDTO.GetOtherUserEmail(session, "organiser@contoso.com"),
                "Decided before anyone is anonymised: two different people outside the scope are both 'Unknown User'.");
        }

        [TestMethod]
        public async Task CallRecordImporter_NobodyInScope_IsProcessedButNotSaved()
        {
            var call = Call(Outsider, (Outsider, "another.outsider@contoso.com"));
            var source = new FakeCallRecordSourceLoader().Returning(call.GraphCallID, call);
            var store = new InMemoryCallRecordPersistenceManager();
            var importer = new CallRecordImporter(source, store, NullLogger.Instance, TestUserScopes.Provider(PilotOnly()));

            var result = await importer.ImportFromNotification(new GraphChangeNotification { ResourceData = new Common.Entities.Models.ResourceData { Id = call.GraphCallID } });

            Assert.IsNotNull(result, "Processed - returning null would make the queue retry a call that can never be saved.");
            Assert.AreEqual(0, store.Saved.Count, "...but not saved: nobody on it is in scope.");
        }

        [TestMethod]
        public async Task CallRecordImporter_SomeoneInScope_SavesWithTheScope()
        {
            var call = Call(Outsider, (Outsider, Pilot));
            var source = new FakeCallRecordSourceLoader().Returning(call.GraphCallID, call);
            var store = new InMemoryCallRecordPersistenceManager();
            var scope = PilotOnly();
            var importer = new CallRecordImporter(source, store, NullLogger.Instance, TestUserScopes.Provider(scope));

            await importer.ImportFromNotification(new GraphChangeNotification { ResourceData = new Common.Entities.Models.ResourceData { Id = call.GraphCallID } });

            Assert.AreEqual(1, store.Saved.Count);
            Assert.AreSame(scope, store.ScopesUsed.Single(), "The store is given the scope so it can anonymise the outsiders.");
        }

        /// <summary>
        /// Against LocalDB: an out-of-scope organiser and attendee are stored as the anonymous "Unknown User", and the
        /// feedback (rating and free text) an out-of-scope participant gave is not stored at all.
        /// </summary>
        [TestMethod]
        public async Task SaveCall_OutOfScopePeople_AreStoredAsUnknownUserWithoutTheirFeedback()
        {
            var testDate = DateTime.UtcNow;
            var outsider = $"outsider.{Guid.NewGuid():N}@contoso.local";
            var pilot = $"pilot.{Guid.NewGuid():N}@contoso.local";
            var scope = TestUserScopes.Of(pilot);

            var call = new CallRecordDTO
            {
                GraphCallID = Guid.NewGuid().ToString(),
                StartDateTime = testDate.AddHours(-2),
                EndDateTime = testDate.AddHours(-1),
                CallType = "Unit test call",
                OrganizerEmail = outsider,
                Sessions = new List<CallSessionDTO>
                {
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO { UserEmailAddress = outsider, Feedback = new UserFeedbackDTO { Rating = "bad", Text = "outsider's own words" } },
                        Callee = new ParticipantEndpointDTO { UserEmailAddress = pilot, Feedback = new UserFeedbackDTO { Rating = "good", Text = "pilot's own words" } },
                        StartDateTime = testDate.AddMinutes(-110),
                        EndDateTime = testDate.AddMinutes(-70),
                    }
                }
            };

            using (var db = new AnalyticsEntitiesContext())
            {
                var saved = await call.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), NullLogger.Instance, scope);

                Assert.AreEqual("Unknown User", saved.Organizer.UserPrincipalName, "The out-of-scope organiser is anonymous.");
                Assert.AreEqual(pilot, saved.Sessions.Single().Attendee.UserPrincipalName, "The in-scope attendee is recorded as themselves.");
                Assert.IsFalse(await db.users.AnyAsync(u => u.UserPrincipalName == outsider), "No user row is created for the outsider.");

                var feedback = await db.CallFeedback.Where(f => f.Call.ID == saved.ID).ToListAsync();
                Assert.AreEqual(1, feedback.Count, "Only the in-scope participant's feedback is stored.");
                Assert.AreEqual("pilot's own words", feedback[0].Text);
            }
        }

        /// <summary>
        /// Against LocalDB: an in-scope organiser's call with someone outside the scope is kept, the outsider is the
        /// anonymous attendee, and their rating and words are not stored.
        /// </summary>
        [TestMethod]
        public async Task SaveCall_AnOutsidersFeedback_IsNeverStored_WhenTheyJoinAnInScopeOrganisersCall()
        {
            var testDate = DateTime.UtcNow;
            var outsider = $"outsider.{Guid.NewGuid():N}@contoso.local";
            var pilot = $"pilot.{Guid.NewGuid():N}@contoso.local";

            var call = new CallRecordDTO
            {
                GraphCallID = Guid.NewGuid().ToString(),
                StartDateTime = testDate.AddHours(-2),
                EndDateTime = testDate.AddHours(-1),
                CallType = "Unit test call",
                OrganizerEmail = pilot,
                Sessions = new List<CallSessionDTO>
                {
                    new CallSessionDTO
                    {
                        Caller = new ParticipantEndpointDTO { UserEmailAddress = pilot, Feedback = new UserFeedbackDTO { Rating = "good", Text = "pilot's own words" } },
                        Callee = new ParticipantEndpointDTO { UserEmailAddress = outsider, Feedback = new UserFeedbackDTO { Rating = "bad", Text = "outsider's own words" } },
                        StartDateTime = testDate.AddMinutes(-110),
                        EndDateTime = testDate.AddMinutes(-70),
                    }
                }
            };

            using (var db = new AnalyticsEntitiesContext())
            {
                var saved = await call.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), NullLogger.Instance, TestUserScopes.Of(pilot));

                Assert.AreEqual(pilot, saved.Organizer.UserPrincipalName);
                Assert.AreEqual("Unknown User", saved.Sessions.Single().Attendee.UserPrincipalName, "The out-of-scope attendee is anonymous.");
                var feedback = await db.CallFeedback.Where(f => f.Call.ID == saved.ID).Select(f => f.Text).ToListAsync();
                CollectionAssert.DoesNotContain(feedback, "outsider's own words", "Nothing the outsider wrote is stored.");
            }
        }

        #endregion

        #region Teams crawl

        private static ChatMessage MessageFrom(string userId) =>
            new ChatMessage { Id = Guid.NewGuid().ToString(), From = new ChatMessageFromIdentitySet { User = userId == null ? null : new Identity { Id = userId } } };

        private static ChatMessageReaction ReactionBy(string userId) =>
            new ChatMessageReaction { ReactionType = "like", User = new ChatMessageReactionIdentitySet { User = new Identity { Id = userId } } };

        [TestMethod]
        public void TeamsChannel_MessagesAndReactionsByPeopleOutsideTheScope_AreLeftOut()
        {
            var channel = new ChannelWithReactions
            {
                Messages = new List<ChatMessage> { MessageFrom(PilotId), MessageFrom(OutsiderId), MessageFrom(null) },
                Reactions = new List<ChatMessageReaction> { ReactionBy(PilotId), ReactionBy(OutsiderId) },
            };

            var removed = ChannelMessageScopeRules.RestrictToUserScope(channel, PilotOnly());

            Assert.AreEqual(3, removed);
            Assert.AreEqual(1, channel.Messages.Count, "The outsider's message and an app/bot message (no user) never reach the channel statistics.");
            Assert.AreEqual(PilotId, channel.Messages[0].From.User.Id);
            Assert.AreEqual(PilotId, channel.Reactions.Single().User.User.Id);
        }

        [TestMethod]
        public void TeamsChannel_Unfiltered_IsUntouched()
        {
            var channel = new ChannelWithReactions
            {
                Messages = new List<ChatMessage> { MessageFrom(OutsiderId), MessageFrom(null) },
                Reactions = new List<ChatMessageReaction> { ReactionBy(OutsiderId) },
            };

            Assert.AreEqual(0, ChannelMessageScopeRules.RestrictToUserScope(channel, UserImportScope.Unfiltered));
            Assert.AreEqual(2, channel.Messages.Count);
            Assert.AreEqual(1, channel.Reactions.Count);
        }

        #endregion

        #region Web traffic

        [TestMethod]
        public void WebTraffic_DropsWhatOutsidersDid_KeepsPageMetadata_FiltersCommentsAndLikesByAuthor()
        {
            var pageViews = new PageViewCollection();
            pageViews.Rows.Add(new PageViewAppInsightsQueryResult { Username = Pilot });
            pageViews.Rows.Add(new PageViewAppInsightsQueryResult { Username = Outsider });
            pageViews.Rows.Add(new PageViewAppInsightsQueryResult { Username = null });

            var pageUpdate = new PageUpdateEventAppInsightsQueryResult { Username = Outsider };
            pageUpdate.CustomProperties.Url = "https://contoso.sharepoint.com/sites/news/SitePages/Καλημέρα κόσμε.aspx";
            pageUpdate.CustomProperties.CommentsString =
                $"[{{\"id\":1,\"email\":\"{Pilot}\",\"comment\":\"Great\"}},{{\"id\":2,\"email\":\"{Outsider}\",\"comment\":\"Hmm\"}}]";
            pageUpdate.CustomProperties.LikesString = $"[{{\"id\":7,\"email\":\"{Outsider}\"}}]";

            var events = new CustomEventsResultCollection();
            events.Rows.Add(new ClickEventAppInsightsQueryResult { Username = Pilot });
            events.Rows.Add(new SearchEventAppInsightsQueryResult { Username = Outsider });
            events.Rows.Add(pageUpdate);

            var removed = AppInsightsUserScopeRules.Apply(pageViews, events, PilotOnly());

            Assert.AreEqual(2, removed.PageViewsRemoved, "The outsider's page view, and one that names nobody.");
            Assert.AreEqual(Pilot, pageViews.Rows.Single().Username);
            Assert.AreEqual(1, removed.EventsRemoved, "The outsider's search.");
            Assert.IsTrue(events.Rows.Contains(pageUpdate), "The page update is about the page, so it is kept whoever viewed it...");
            Assert.AreEqual(Pilot, pageUpdate.CustomProperties.PageComments.Single().Email, "...but only in-scope authors' comments survive...");
            Assert.AreEqual(0, pageUpdate.CustomProperties.Likes.Count, "...and likes.");
            Assert.AreEqual(1, removed.CommentsRemoved);
            Assert.AreEqual(1, removed.LikesRemoved);
        }

        [TestMethod]
        public void WebTraffic_Unfiltered_RemovesNothing()
        {
            var pageViews = new PageViewCollection();
            pageViews.Rows.Add(new PageViewAppInsightsQueryResult { Username = Outsider });
            var events = new CustomEventsResultCollection();
            events.Rows.Add(new SearchEventAppInsightsQueryResult { Username = null });

            Assert.AreEqual(0, AppInsightsUserScopeRules.Apply(pageViews, events, UserImportScope.Unfiltered).Total);
            Assert.AreEqual(1, pageViews.Rows.Count);
            Assert.AreEqual(1, events.Rows.Count);
        }

        [TestMethod]
        public void ScanWatermark_OnlyMovesForward_AndIsUtc()
        {
            var watermark = new AppInsightsScanWatermark();
            Assert.IsNull(watermark.NewestScannedUtc);

            var t1 = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
            watermark.Advance(t1);
            watermark.Advance(t1.AddHours(-3));
            watermark.Advance(null);

            Assert.AreEqual(t1, watermark.NewestScannedUtc);
            Assert.AreEqual(DateTimeKind.Utc, watermark.NewestScannedUtc.Value.Kind);

            watermark.Advance(t1.AddMinutes(5));
            Assert.AreEqual(t1.AddMinutes(5), watermark.NewestScannedUtc);
        }

        #endregion

        #region Sent emails, Copilot Studio credits, user directory

        [TestMethod]
        public void SentEmails_OnlyMailboxesOfPeopleInScopeAreRead()
        {
            var users = new List<DbUser>
            {
                new DbUser { ID = 1, UserPrincipalName = Pilot, Mail = Pilot },
                new DbUser { ID = 2, UserPrincipalName = "renamed.upn@contoso.com", Mail = "renamed@contoso.com", AzureAdId = PilotId },
                new DbUser { ID = 3, UserPrincipalName = Outsider, Mail = Outsider, AzureAdId = OutsiderId },
            };

            var selected = SentEmailImporter.SelectUsersInScope(users, PilotOnly());

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, selected.Select(u => u.ID).ToList(),
                "Matched by UPN, or by object id when the UPN has been renamed since the user was imported.");
            Assert.AreSame(users, SentEmailImporter.SelectUsersInScope(users, UserImportScope.Unfiltered));
        }

        [TestMethod]
        public void CopilotStudioCredits_PerUserRowsOfPeopleOutsideTheScopeAreNotStored()
        {
            var rows = new List<CopilotStudioUserCreditRow>
            {
                new CopilotStudioUserCreditRow { UserId = PilotId, Consumed = 10 },
                new CopilotStudioUserCreditRow { UserId = OutsiderId, Consumed = 20 },
                null,
            };

            var kept = CopilotStudioCreditImporter.SelectRowsInScope(rows, PilotOnly());

            Assert.AreEqual(PilotId, kept.Single().UserId, "The licensing API identifies people by Entra object id.");
            Assert.AreEqual(3, CopilotStudioCreditImporter.SelectRowsInScope(rows, UserImportScope.Unfiltered).Count);
        }

        [TestMethod]
        public void UserDirectory_OnlyPeopleInScopeAreWritten()
        {
            var graphUsers = new List<GraphUser>
            {
                new GraphUser { Id = PilotId, UserPrincipalName = Pilot },
                new GraphUser { Id = OutsiderId, UserPrincipalName = Outsider },
            };

            var selected = UserMetadataUpdater.SelectGraphUsersInScope(graphUsers, PilotOnly());

            Assert.AreEqual(PilotId, selected.Single().Id);
            Assert.IsTrue(UserMetadataUpdater.IsDbUserInScope(new DbUser { UserPrincipalName = "old.name@contoso.com", AzureAdId = PilotId }, PilotOnly()),
                "An existing row is recognised by object id even after a UPN rename.");
            Assert.IsFalse(UserMetadataUpdater.IsDbUserInScope(new DbUser { UserPrincipalName = Outsider }, PilotOnly()));
        }

        #endregion

        #region Pipelines: each import applies the scope where it reads, not only in its rules

        /// <summary>A sent-email source that records whose mailbox it was asked to read, and returns nothing.</summary>
        private sealed class RecordingSentEmailSource : ISentEmailSourceLoader
        {
            public List<string> MailboxesRead { get; } = new List<string>();

            public Task<bool> HasMailReadAccessAsync() => Task.FromResult(true);

            public Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(DbUser user, bool includeBody)
            {
                lock (MailboxesRead)
                {
                    MailboxesRead.Add(user.UserPrincipalName);
                }
                return Task.FromResult(SentEmailLoadResult.Empty);
            }
        }

        [TestMethod]
        public async Task SentEmails_Import_NeverReadsTheMailboxOfSomeoneOutsideTheScope()
        {
            var token = "ugf-" + Guid.NewGuid().ToString("N");
            var pilot = $"{token}-pilot@contoso.local";
            var outsider = $"{token}-outsider@contoso.local";
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new DbUser { UserPrincipalName = pilot, Mail = pilot, AzureAdId = Guid.NewGuid().ToString() });
                db.users.Add(new DbUser { UserPrincipalName = outsider, Mail = outsider, AzureAdId = Guid.NewGuid().ToString() });
                await db.SaveChangesAsync();
            }

            try
            {
                var source = new RecordingSentEmailSource();
                var importer = new SentEmailImporter(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), source,
                    NullSentEmailSentimentScorer.Instance, userScopeProvider: TestUserScopes.Provider(TestUserScopes.Of(pilot)));

                await importer.ImportSentEmails();

                CollectionAssert.AreEqual(new[] { pilot }, source.MailboxesRead,
                    "Only the in-scope mailbox is read - not the outsider's, nor any other user already in the table.");
            }
            finally
            {
                await DeleteUsersAsync(token);
            }
        }

        /// <summary>Answers Graph requests from canned JSON by path suffix, recording every path asked for.</summary>
        private sealed class RoutingGraphHandler : HttpMessageHandler
        {
            public Dictionary<string, string> Routes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Requests { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri.AbsolutePath;
                lock (Requests)
                {
                    Requests.Add(path);
                }

                var body = Routes.Where(r => path.EndsWith(r.Key, StringComparison.OrdinalIgnoreCase)).Select(r => r.Value).FirstOrDefault();
                var response = body == null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Not found.\"}}", Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        private static string GraphUserJson(string id) => $"{{\"@odata.type\":\"#microsoft.graph.user\",\"id\":\"{id}\"}}";

        [TestMethod]
        public async Task TeamsCrawl_OwnersAndMembersOutsideTheScope_AreNeitherRecordedNorLookedUp()
        {
            var teamId = Guid.NewGuid().ToString();
            var pilotId = Guid.NewGuid().ToString();
            var outsiderId = Guid.NewGuid().ToString();
            var pilotUpn = $"pilot.{Guid.NewGuid():N}@contoso.local";

            var handler = new RoutingGraphHandler();
            handler.Routes[$"/teams/{teamId}"] = $"{{\"id\":\"{teamId}\",\"displayName\":\"Καλημέρα κόσμε\"}}";
            handler.Routes[$"/groups/{teamId}"] = $"{{\"id\":\"{teamId}\",\"owners\":[{GraphUserJson(pilotId)},{GraphUserJson(outsiderId)}]}}";
            handler.Routes[$"/groups/{teamId}/members"] = $"{{\"value\":[{GraphUserJson(pilotId)},{GraphUserJson(outsiderId)}]}}";
            handler.Routes[$"/users/{pilotId}"] = $"{{\"id\":\"{pilotId}\",\"userPrincipalName\":\"{pilotUpn}\"}}";
            handler.Routes[$"/users/{outsiderId}"] = $"{{\"id\":\"{outsiderId}\",\"userPrincipalName\":\"outsider@contoso.local\"}}";
            handler.Routes[$"/teams/{teamId}/channels"] = "{\"value\":[]}";

            var graph = new GraphServiceClient(new HttpClient(handler), new BearerTokenAuthenticationProvider("test-token"));
            var context = new TeamsLoadContext(graph) { UserScope = TestUserScopes.OfMembers((pilotId, pilotUpn, pilotUpn)) };
            var config = new AppConfig();
            config.ConnectionStrings.StorageConnectionString = null;     // no stored Teams tokens, so no channel-message read: this is about people

            O365Team team;
            using (var db = new AnalyticsEntitiesContext())
            {
                team = await O365Team.LoadTeamFull(new Group { Id = teamId, DisplayName = "Καλημέρα κόσμε" }, context, NullLogger.Instance, config, db);
            }

            Assert.AreEqual(pilotId, team.OwnerUserAccounts.Single().Id, "Only the in-scope owner is recorded.");
            Assert.AreEqual(pilotUpn, team.Users.Single().UserPrincipalName, "Only the in-scope member's membership is recorded.");
            Assert.IsFalse(handler.Requests.Any(p => p.EndsWith("/users/" + outsiderId, StringComparison.OrdinalIgnoreCase)),
                "The outsider is not even looked up.");
        }

        [TestMethod]
        public async Task WebTraffic_Importer_SavesOnlyWhatPeopleInScopeDid_AndResumesFromWhatItHasRead()
        {
            var nowUtc = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
            var day = new DateTime(2026, 5, 9);
            var source = new FakeAppInsightsSourceLoader { PageViewUsername = (d, i) => i == 0 ? Pilot : Outsider };
            source.PageViewCountByDay[day] = 3;
            var persistence = new InMemoryAppInsightsDayPersistenceManager();
            var newestStoredHit = new InMemoryHitWatermarkStore(new DateTime(2026, 5, 7, 14, 0, 0, DateTimeKind.Utc));
            var scanWatermark = new AppInsightsScanWatermark();

            Task Run() => new AppInsightsImporter(null, AnalyticsLogger.ConsoleOnlyTracer(), new FixedClock(nowUtc), source,
                new FakeSiteFilterLoader(), newestStoredHit, persistence, userScope: PilotOnly(), scanWatermark: scanWatermark)
                .ImportAndSave(saveRestResponses: false, daysBeforeOverride: null);

            await Run();

            Assert.AreEqual(Pilot, persistence.SavedPageViews.Single().Rows.Single().Username, "The outsiders' page views are never saved...");
            Assert.AreEqual(day.AddMinutes(2), scanWatermark.NewestScannedUtc, "...but the import remembers that it read them.");

            // Nothing the pilot did is newer than the 7th, so resuming from the newest STORED hit would re-read the 7th
            // and 8th every cycle, and a pilot who stopped browsing would make that window grow for ever.
            source.PageViewDaysRequested.Clear();
            await Run();

            Assert.AreEqual(day, source.PageViewDaysRequested.First(), "It resumes from the newest page view it has read.");
        }

        private static GraphUser DirectoryUser(string id, string upn, string managerId = null) => new GraphUser
        {
            Id = id,
            UserPrincipalName = upn,
            Mail = upn,
            AccountEnabled = true,
            ManagerInfo = managerId == null ? new List<ManagerInfo>() : new List<ManagerInfo> { new ManagerInfo { Id = managerId } },
        };

        private static async Task<List<DbUser>> UsersNamedLikeAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.users.Where(u => u.UserPrincipalName.Contains(token)).ToListAsync();
            }
        }

        private static async Task DeleteUsersAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.users WHERE user_name LIKE @p0", "%" + token + "%");
            }
        }

        [TestMethod]
        public async Task UserDirectory_Import_WritesOnlyPeopleInScope_AndLinksNoManagerOutsideIt()
        {
            var token = Guid.NewGuid().ToString("N");
            var manager = DirectoryUser(Guid.NewGuid().ToString(), $"manager-{token}@contoso.local");
            var pilot = DirectoryUser(Guid.NewGuid().ToString(), $"pilot-{token}@contoso.local", managerId: manager.Id);
            var newcomer = DirectoryUser(Guid.NewGuid().ToString(), $"newcomer-{token}@contoso.local", managerId: manager.Id);
            var other = DirectoryUser(Guid.NewGuid().ToString(), $"other-{token}@contoso.local");
            var scope = TestUserScopes.OfMembers((pilot.Id, pilot.UserPrincipalName, pilot.Mail), (newcomer.Id, newcomer.UserPrincipalName, newcomer.Mail));
            var loader = new FakeUserMetadataLoader(new List<GraphUser> { manager, pilot, newcomer, other });

            // Imported before the filter was set: the pilot is in the users table, linked to their manager - who is
            // outside the scope, and whose row has not been purged.
            using (var db = new AnalyticsEntitiesContext())
            {
                var managerRow = new DbUser { UserPrincipalName = manager.UserPrincipalName, Mail = manager.Mail, AzureAdId = manager.Id, AccountEnabled = true };
                db.users.Add(managerRow);
                await db.SaveChangesAsync();
                db.users.Add(new DbUser { UserPrincipalName = pilot.UserPrincipalName, Mail = pilot.Mail, AzureAdId = pilot.Id, AccountEnabled = true, ManagerId = managerRow.ID });
                await db.SaveChangesAsync();
            }

            Task<bool> Import() => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .WithUserScope(TestUserScopes.Provider(scope), new InMemoryImportLastRunStore())
                .InsertAndUpdateDatabaseFromExternalUsers();

            DbUser Row(List<DbUser> rows, GraphUser user) => rows.Single(u => u.UserPrincipalName == user.UserPrincipalName);

            try
            {
                Assert.IsTrue(await Import());
                var written = await UsersNamedLikeAsync(token);
                CollectionAssert.AreEquivalent(new[] { manager.UserPrincipalName, pilot.UserPrincipalName, newcomer.UserPrincipalName },
                    written.Select(u => u.UserPrincipalName).ToList(), "The newcomer in scope is added, and nobody outside it.");
                Assert.IsNull(Row(written, pilot).ManagerId, "An existing link to a manager outside the scope is removed...");
                Assert.IsNull(Row(written, newcomer).ManagerId, "...and a new one is not made.");

                // With the tenant's licences readable, existing users take the bulk SQL path instead, which resolves
                // managers straight from the users table - where the manager's old row still is.
                loader.SetFakeState(new List<GraphUser> { manager, pilot, newcomer, other }, new List<SubscribedSku>(), null);
                Assert.IsTrue(await Import());
                var bulkUpdated = await UsersNamedLikeAsync(token);
                Assert.IsNull(Row(bulkUpdated, pilot).ManagerId, "The bulk update path does not link them either.");
                Assert.IsNull(Row(bulkUpdated, newcomer).ManagerId);
            }
            finally
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    await db.Database.ExecuteSqlCommandAsync("UPDATE dbo.users SET manager_id = NULL WHERE user_name LIKE @p0", "%" + token + "%");
                }
                await DeleteUsersAsync(token);
            }
        }

        [TestMethod]
        public async Task UserDirectory_SomeoneAddedToTheGroupLater_IsAddedByAFullReRead_AtMostOnceADay()
        {
            var token = Guid.NewGuid().ToString("N");
            var pilot = DirectoryUser(Guid.NewGuid().ToString(), $"pilot-{token}@contoso.local");
            var newcomer = DirectoryUser(Guid.NewGuid().ToString(), $"newcomer-{token}@contoso.local");
            var latecomer = DirectoryUser(Guid.NewGuid().ToString(), $"latecomer-{token}@contoso.local");

            // After the first (full) read, an incremental read returns nobody: none of these user objects changes.
            // Joining a group changes the group, not the user.
            var loader = new FakeUserMetadataLoader(new List<GraphUser> { pilot, newcomer, latecomer }) { DeltaUsersOverride = new List<GraphUser>() };
            var lastRun = new InMemoryImportLastRunStore();
            var clock = new FixedClock(new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));

            Task<bool> Import(params GraphUser[] members) => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader, clock)
                .WithUserScope(TestUserScopes.Provider(TestUserScopes.OfMembers(members.Select(m => (m.Id, m.UserPrincipalName, m.Mail)).ToArray())), lastRun)
                .InsertAndUpdateDatabaseFromExternalUsers();

            try
            {
                await Import(pilot);
                CollectionAssert.AreEquivalent(new[] { pilot.UserPrincipalName }, (await UsersNamedLikeAsync(token)).Select(u => u.UserPrincipalName).ToList());

                await Import(pilot, newcomer);
                CollectionAssert.AreEquivalent(new[] { pilot.UserPrincipalName, newcomer.UserPrincipalName },
                    (await UsersNamedLikeAsync(token)).Select(u => u.UserPrincipalName).ToList(),
                    "The newcomer is missing from the users table, so the directory is read in full again and they are added.");

                clock.Advance(TimeSpan.FromHours(1));
                await Import(pilot, newcomer, latecomer);
                Assert.IsFalse((await UsersNamedLikeAsync(token)).Any(u => u.UserPrincipalName == latecomer.UserPrincipalName),
                    "A full re-read already ran in the last day, so the latecomer waits for the next one.");

                clock.Advance(TimeSpan.FromHours(UserMetadataUpdater.ScopeCatchUpIntervalHours));
                await Import(pilot, newcomer, latecomer);
                Assert.IsTrue((await UsersNamedLikeAsync(token)).Any(u => u.UserPrincipalName == latecomer.UserPrincipalName),
                    "A day later, the latecomer is added.");
            }
            finally
            {
                await DeleteUsersAsync(token);
            }
        }

        [TestMethod]
        public async Task UserDirectory_ExistingRowThatEntersScope_GetsCurrentMetadataFromAFullReRead()
        {
            var token = Guid.NewGuid().ToString("N");
            var pilot = DirectoryUser(Guid.NewGuid().ToString(), $"pilot-{token}@contoso.local");
            var existing = DirectoryUser(Guid.NewGuid().ToString(), $"existing-{token}@contoso.local");
            existing.PostalCode = "CURRENT";

            var loader = new FakeUserMetadataLoader(new List<GraphUser> { pilot, existing })
            {
                DeltaUsersOverride = new List<GraphUser>(),
            };
            var marker = new InMemoryUserImportScopeMarkerStore();
            var lastRun = new InMemoryImportLastRunStore();
            var clock = new FixedClock(new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));

            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new DbUser
                {
                    UserPrincipalName = existing.UserPrincipalName,
                    Mail = existing.Mail,
                    AzureAdId = existing.Id,
                    AccountEnabled = true,
                    PostalCode = "STALE",
                });
                await db.SaveChangesAsync();
            }

            Task<bool> Import(params GraphUser[] members) => new UserMetadataUpdater(
                    AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader, clock)
                .WithUserScope(
                    TestUserScopes.Provider(TestUserScopes.OfMembers(
                        members.Select(m => (m.Id, m.UserPrincipalName, m.Mail)).ToArray())),
                    lastRun,
                    marker)
                .InsertAndUpdateDatabaseFromExternalUsers();

            try
            {
                Assert.IsTrue(await Import(pilot));
                Assert.IsNotNull(marker.MembershipFingerprint, "The completed full read records which membership its delta token covers.");
                Assert.AreEqual("STALE", (await UsersNamedLikeAsync(token)).Single(u => u.AzureAdId == existing.Id).PostalCode);

                Assert.IsTrue(await Import(pilot, existing));

                Assert.AreEqual("CURRENT", (await UsersNamedLikeAsync(token)).Single(u => u.AzureAdId == existing.Id).PostalCode,
                    "The row's existence must not be mistaken for current metadata: joining the group forces a full Graph read.");
            }
            finally
            {
                await DeleteUsersAsync(token);
            }
        }

        [TestMethod]
        public async Task UserDirectory_FilterRemoved_TheNextImportReadsTheWholeDirectory()
        {
            var token = Guid.NewGuid().ToString("N");
            var pilot = DirectoryUser(Guid.NewGuid().ToString(), $"pilot-{token}@contoso.local");
            var outsider = DirectoryUser(Guid.NewGuid().ToString(), $"outsider-{token}@contoso.local");
            var loader = new FakeUserMetadataLoader(new List<GraphUser> { pilot, outsider }) { DeltaUsersOverride = new List<GraphUser>() };
            var marker = new InMemoryUserImportScopeMarkerStore();
            var filter = new UserGroupsFilterModel("Copilot pilot");
            var filtered = new FixedUserImportScopeProvider(TestUserScopes.OfMembers((pilot.Id, pilot.UserPrincipalName, pilot.Mail)), filter: filter);

            try
            {
                await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                    .WithUserScope(filtered, new InMemoryImportLastRunStore(), marker)
                    .InsertAndUpdateDatabaseFromExternalUsers();

                Assert.IsFalse((await UsersNamedLikeAsync(token)).Any(u => u.UserPrincipalName == outsider.UserPrincipalName));
                Assert.AreEqual(filter.Fingerprint, marker.Fingerprint, "The checkpoint is recorded as belonging to this filter.");

                // The filter is removed, which restarts the web job. The outsider has not changed, so an incremental
                // read from the stored checkpoint would never return them.
                await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                    .WithUserScope(TestUserScopes.Provider(UserImportScope.Unfiltered), new InMemoryImportLastRunStore(), marker)
                    .InsertAndUpdateDatabaseFromExternalUsers();

                Assert.IsTrue((await UsersNamedLikeAsync(token)).Any(u => u.UserPrincipalName == outsider.UserPrincipalName),
                    "The checkpoint taken under the old filter is discarded, so the whole directory is read.");
                Assert.IsNull(marker.Fingerprint);
            }
            finally
            {
                await DeleteUsersAsync(token);
            }
        }

        [TestMethod]
        public async Task UserDirectory_NoFilter_KeepsReadingIncrementally()
        {
            var token = Guid.NewGuid().ToString("N");
            var first = DirectoryUser(Guid.NewGuid().ToString(), $"first-{token}@contoso.local");
            var second = DirectoryUser(Guid.NewGuid().ToString(), $"second-{token}@contoso.local");
            var loader = new FakeUserMetadataLoader(new List<GraphUser> { first });
            var marker = new InMemoryUserImportScopeMarkerStore();

            Task<bool> Import() => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .WithUserScope(TestUserScopes.Provider(UserImportScope.Unfiltered), new InMemoryImportLastRunStore(), marker)
                .InsertAndUpdateDatabaseFromExternalUsers();

            try
            {
                await Import();

                // A full read would now return the second user; an incremental one returns nobody.
                loader.SetFakeState(new List<GraphUser> { first, second }, null, null);
                loader.DeltaUsersOverride = new List<GraphUser>();
                await Import();

                Assert.IsFalse((await UsersNamedLikeAsync(token)).Any(u => u.UserPrincipalName == second.UserPrincipalName),
                    "With no filter, and none before, the stored checkpoint is kept: the read stays incremental.");
                Assert.IsNull(marker.Fingerprint, "Nothing is recorded for a tenant that has never had a filter.");
            }
            finally
            {
                await DeleteUsersAsync(token);
            }
        }

        /// <summary>
        /// Each stored delta token is judged by the filter it was taken under. A token kept for one set of Entra
        /// organisation attributes is resumed when that set is configured again - so if the filter changed meanwhile,
        /// and the full read that the change started never finished, resuming it would never return the people the
        /// new filter lets in.
        /// </summary>
        [TestMethod]
        public async Task UserDirectory_TokenKeptForAnotherOrgSelection_IsJudgedByTheFilterItWasTakenUnder()
        {
            var token = Guid.NewGuid().ToString("N");
            var pilot = DirectoryUser(Guid.NewGuid().ToString(), $"pilot-{token}@contoso.local");
            var newcomer = DirectoryUser(Guid.NewGuid().ToString(), $"newcomer-{token}@contoso.local");
            var loader = new FakeUserMetadataLoader(new List<GraphUser> { pilot, newcomer }) { DeltaUsersOverride = new List<GraphUser>() };
            var marker = new InMemoryUserImportScopeMarkerStore();
            var orgTypes = new SwitchableEntraOrgType();
            var pilotOnly = new FixedUserImportScopeProvider(TestUserScopes.OfMembers((pilot.Id, pilot.UserPrincipalName, pilot.Mail)),
                filter: new UserGroupsFilterModel("Copilot pilot"));
            var widened = new FixedUserImportScopeProvider(TestUserScopes.OfMembers((pilot.Id, pilot.UserPrincipalName, pilot.Mail), (newcomer.Id, newcomer.UserPrincipalName, newcomer.Mail)),
                filter: new UserGroupsFilterModel("Copilot pilot;Copilot wave 2"));

            // No last-run store, so the daily catch-up for missing members is off: only the filter records decide.
            Task<bool> Import(IUserImportScopeProvider scope) => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader,
                    DefaultAnalyticsDbContextFactory.Instance, null, orgTypes, new NoOrgAssignments())
                .WithUserScope(scope, lastRunStore: null, scopeMarkerStore: marker)
                .InsertAndUpdateDatabaseFromExternalUsers();

            try
            {
                orgTypes.Attribute = "extensionAttribute1";
                Assert.IsTrue(await Import(pilotOnly), "A full read under the pilot-only filter, whose token is kept.");

                // The filter widens while another attribute is configured, and the full read that starts never finishes.
                orgTypes.Attribute = "extensionAttribute2";
                loader.SetFakeState(new List<GraphUser> { pilot }, null, null);
                loader.SimulateIncompleteDeltaRead = true;
                Assert.IsFalse(await Import(widened));

                // The first attribute is configured again: its kept token was taken under the pilot-only filter.
                orgTypes.Attribute = "extensionAttribute1";
                loader.SetFakeState(new List<GraphUser> { pilot, newcomer }, null, null);
                loader.SimulateIncompleteDeltaRead = false;
                Assert.IsTrue(await Import(widened));

                Assert.IsTrue((await UsersNamedLikeAsync(token)).Any(u => u.UserPrincipalName == newcomer.UserPrincipalName),
                    "The kept token is discarded and the directory read in full, so the newcomer is added.");
                Assert.AreEqual(new UserGroupsFilterModel("Copilot pilot;Copilot wave 2").Fingerprint,
                    marker.Fingerprints[GraphUserOrgSelection.FromTypes(orgTypes.Current()).DeltaKeyQualifier]);
            }
            finally
            {
                await DeleteUsersAsync(token);
            }
        }

        /// <summary>One enabled Entra organisation type, whose attribute a test can change between imports.</summary>
        private sealed class SwitchableEntraOrgType : IUserOrgTypeStore
        {
            public string Attribute { get; set; }

            public IReadOnlyList<UserOrgType> Current() => new[]
            {
                new UserOrgType { Id = 1, Name = "Business unit", SourceKind = UserOrgSourceKind.EntraAttribute, EntraAttributeName = Attribute, IsEnabled = true },
            };

            public Task<IReadOnlyList<UserOrgType>> GetEnabledEntraTypesAsync(CancellationToken cancellationToken = default(CancellationToken)) => Task.FromResult(Current());

            public Task<IReadOnlyList<UserOrgType>> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken)) => Task.FromResult(Current());

            public Task<UserOrgType> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken)) => Task.FromResult(Current().FirstOrDefault(t => t.Id == id));

            public Task<IReadOnlyList<UserOrgTypeSummary>> GetSummariesAsync(CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgTypeSummary>>(Current().Select(t => new UserOrgTypeSummary { Type = t }).ToList());

            public Task<int> CreateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken)) => Task.FromResult(0);

            public Task UpdateAsync(UserOrgType type, bool clearAssignments, bool bumpGeneration, CancellationToken cancellationToken = default(CancellationToken),
                int? expectedGeneration = null, int? expectedRevision = null, int? confirmedDiscardCount = null) => Task.CompletedTask;

            public Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken), int? expectedRevision = null) => Task.CompletedTask;

            public Task<int> RecordEntraRefreshAsync(IReadOnlyDictionary<int, int> expectedGenerations, DateTime refreshedUtc, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(expectedGenerations.Count);

            public Task<int> RecordListValuedAsync(IReadOnlyDictionary<int, int> expectedGenerations, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(expectedGenerations.Count);
        }

        /// <summary>Accepts organisation values without storing them: these tests are about which users are read.</summary>
        private sealed class NoOrgAssignments : IUserOrgAssignmentStore
        {
            public Task<UserOrgMergeResult> MergeAsync(IReadOnlyList<UserOrgAssignmentUpdate> updates, UserOrgSourceKind? expectedSourceKind = null,
                IReadOnlyDictionary<int, int> expectedGenerations = null, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(new UserOrgMergeResult { Applied = updates.Count });

            public Task<IReadOnlyList<UserOrgValueForUser>> GetForUserAsync(int userId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgValueForUser>>(new UserOrgValueForUser[0]);

            public Task<int> ClearAllForTypeAsync(int orgTypeId, CancellationToken cancellationToken = default(CancellationToken)) => Task.FromResult(0);
        }

        #endregion

        #region Usage reports

        private sealed class ScopeProbeLoader : TeamsUserUsageLoader
        {
            public ScopeProbeLoader(UserImportScope scope) : base(null, scope, NullLogger.Instance) { }
            public Task<bool> Probe(string upn) => IdInScope(upn);
        }

        [TestMethod]
        public async Task UsageReports_CheckTheSharedScope_NotGraph()
        {
            var probe = new ScopeProbeLoader(PilotOnly());

            Assert.IsTrue(await probe.Probe(Pilot));
            Assert.IsFalse(await probe.Probe(Outsider));
            Assert.IsFalse(await probe.Probe("0123456789abcdef0123456789abcdef"),
                "A concealed (hashed) report identity cannot be shown to be in scope, so it is not imported under a filter.");
            Assert.IsTrue(await new ScopeProbeLoader(null).Probe(Outsider), "No scope means unfiltered.");
        }

        #endregion
    }
}
