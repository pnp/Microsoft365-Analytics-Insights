using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.Teams;
using Common.Entities.TeamsExplorer;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs every Teams Explorer query against a real, throwaway SQL Server database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store executes hand-written SQL, so a typo, a wrong column name or a type that EF cannot
    /// materialise is invisible to the compiler and only appears when an admin opens the page. This
    /// test exists to make that failure happen in CI instead.
    /// </para>
    /// <para>
    /// It also pins the three definitions the page's credibility rests on, each of which is easy to
    /// get quietly wrong:
    /// </para>
    /// <list type="number">
    /// <item>A usage-report row with all-zero metrics is NOT an active user. Graph writes a row for
    /// every user in the report scope, so <c>COUNT(DISTINCT user_id)</c> would silently report the
    /// whole licensed population as active.</item>
    /// <item>Per-day metrics sum across the window. The report is requested per date, so the values
    /// are daily figures rather than rolling totals - if that ever changed, every total on the page
    /// would be multiplied by the window length.</item>
    /// <item>Channel sentiment is weighted by message count, and 0.5 is neutral.</item>
    /// </list>
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class TeamsExplorerSqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        /// <summary>
        /// "Καλημέρα κόσμε" on a genuinely Unicode column.
        /// </summary>
        /// <remarks>
        /// Deliberately a TEAM name, not a user principal name. <c>dbo.users.user_name</c> is
        /// <c>varchar(250)</c> because Entra restricts a UPN to ASCII, so a non-Latin value there
        /// would be corrupted at rest and a test asserting it round-trips would be asserting a bug.
        /// <c>teams.name</c> is <c>nvarchar</c>, so this crosses a real encoding boundary.
        /// </remarks>
        private const string GreekTeamName = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5";

        private static DateTime Today => DateTime.UtcNow.Date;

        /// <summary>
        /// The day the seeded calls happen on: a weekday, and inside the live reporting window.
        /// </summary>
        /// <remarks>
        /// Pinned to a weekday deliberately. Seeding calls on "today minus four days" makes the
        /// out-of-hours assertions depend on what day of the week the test happens to run: when that
        /// lands on a Saturday, a 10:00 call is correctly classified as out of hours and the expected
        /// value silently changes. The first run of this test failed exactly that way.
        /// </remarks>
        private static DateTime CallDay
        {
            get
            {
                var day = Today.AddDays(-4);
                while (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                {
                    day = day.AddDays(-1);
                }

                return day;
            }
        }

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = "TeamsExplorerIntegration_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, $"CREATE DATABASE [{_database}];");

            // The full migration chain, so the queries run against the schema customers actually get
            // rather than a hand-built subset that could omit the very column a query needs.
            var migrationConfig = new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(
                    _connectionString, "Microsoft.Data.SqlClient"),
            };
            new DbMigrator(migrationConfig).Update();

            Seed();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            if (_database == null) return;
            SqlConnection.ClearAllPools();
            Execute(MasterConnection,
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        private static SqlTeamsExplorerStore NewStore() =>
            new SqlTeamsExplorerStore(new ConnectionStringAnalyticsDbContextFactory(_connectionString));

        private static TeamsExplorerQuery NewQuery(int days = 28) =>
            TeamsExplorerQuery.Create(days, DateTime.UtcNow);

        #region Seeding

        /// <summary>
        /// A tiny but deliberately awkward tenant: one heavy user, one light user, one user the usage
        /// report covers but who did nothing, and one user who is in the directory but not in the
        /// report at all.
        /// </summary>
        private static void Seed()
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var engineering = new UserDepartment { Name = "Engineering" };
                var sales = new UserDepartment { Name = "Sales" };
                db.UserDepartments.Add(engineering);
                db.UserDepartments.Add(sales);
                db.SaveChanges();

                // UPNs are ASCII by Entra policy - see the Greek sample on the team name instead.
                var heavy = new User { UserPrincipalName = "ada@contoso.com", AccountEnabled = true, DepartmentId = engineering.ID };
                var light = new User { UserPrincipalName = "grace@contoso.com", AccountEnabled = true, DepartmentId = engineering.ID };
                var idle = new User { UserPrincipalName = "alan@contoso.com", AccountEnabled = true, DepartmentId = sales.ID };
                var unreported = new User { UserPrincipalName = "katherine@contoso.com", AccountEnabled = true, DepartmentId = sales.ID };
                db.users.Add(heavy);
                db.users.Add(light);
                db.users.Add(idle);
                db.users.Add(unreported);
                db.SaveChanges();

                SeedUsage(db, heavy, light, idle);
                SeedCalls(db, heavy, light);
                SeedTeams(db, heavy, light);

                db.SaveChanges();
            }
        }

        private static void SeedUsage(AnalyticsEntitiesContext db, User heavy, User light, User idle)
        {
            // Two days inside the usage window (which ends three days before today), so the totals
            // below prove that per-day values are summed rather than read from one snapshot.
            foreach (var offset in new[] { 5, 6 })
            {
                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                {
                    UserID = heavy.ID,
                    Date = Today.AddDays(-offset),
                    LastActivityDate = Today.AddDays(-5),
                    TeamChatMessageCount = 10,
                    PostMessages = 5,
                    ReplyMessages = 3,
                    PrivateChatMessageCount = 2,
                    MeetingsAttendedCount = 4,
                    MeetingsOrganizedCount = 1,
                    CallCount = 2,
                    AudioDurationSeconds = 3600,
                    VideoDurationSeconds = 1800,
                    ScreenShareDurationSeconds = 900,
                });

                // An all-zero row. Graph writes one of these for every user in the report scope, so
                // this is the case that makes COUNT(DISTINCT user_id) the wrong active-user measure.
                db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
                {
                    UserID = idle.ID,
                    Date = Today.AddDays(-offset),
                });
            }

            db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
            {
                UserID = light.ID,
                Date = Today.AddDays(-5),
                LastActivityDate = Today.AddDays(-5),
                PrivateChatMessageCount = 1,
            });

            db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog
            {
                UserID = heavy.ID,
                Date = Today.AddDays(-5),
                UsedWindows = true,
                UsedIOS = true,
                UsedWeb = false,
            });

            db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog
            {
                UserID = light.ID,
                Date = Today.AddDays(-5),
                UsedMac = true,
            });
        }

        private static void SeedCalls(AnalyticsEntitiesContext db, User heavy, User light)
        {
            var group = new CallType { Name = "groupCall" };
            var p2p = new CallType { Name = "peerToPeer" };
            db.CallTypes.Add(group);
            db.CallTypes.Add(p2p);

            var audio = new CallModality { Name = "audio" };
            var video = new CallModality { Name = "video" };
            db.CallModalities.Add(audio);
            db.CallModalities.Add(video);
            db.SaveChanges();

            // A daytime group call with two attendees, one of whom joined late.
            var daytime = new CallRecord
            {
                GraphID = Guid.NewGuid().ToString(),
                OrganizerID = heavy.ID,
                CallTypeID = group.ID,
                StartDateTime = CallDay.AddHours(10),
                EndDateTime = CallDay.AddHours(11),
            };
            db.CallRecords.Add(daytime);

            // A 22:00 one-to-one call on the same weekday: out of hours by time, not by weekend.
            var lateNight = new CallRecord
            {
                GraphID = Guid.NewGuid().ToString(),
                OrganizerID = light.ID,
                CallTypeID = p2p.ID,
                StartDateTime = CallDay.AddHours(22),
                EndDateTime = CallDay.AddHours(22).AddMinutes(15),
            };
            db.CallRecords.Add(lateNight);
            db.SaveChanges();

            var full = new CallSession
            {
                CallRecordID = daytime.ID,
                AttendeeUserID = heavy.ID,
                Start = daytime.StartDateTime,
                End = daytime.EndDateTime,
            };
            var latecomer = new CallSession
            {
                CallRecordID = daytime.ID,
                AttendeeUserID = light.ID,
                Start = daytime.StartDateTime.AddMinutes(30),
                End = daytime.EndDateTime,
            };
            var solo = new CallSession
            {
                CallRecordID = lateNight.ID,
                AttendeeUserID = light.ID,
                Start = lateNight.StartDateTime,
                End = lateNight.EndDateTime,
            };
            db.CallSessions.Add(full);
            db.CallSessions.Add(latecomer);
            db.CallSessions.Add(solo);
            db.SaveChanges();

            db.CallModalityLookups.Add(new CallSessionModalityLookup { CallSessionID = full.ID, CallModalityID = audio.ID });
            db.CallModalityLookups.Add(new CallSessionModalityLookup { CallSessionID = full.ID, CallModalityID = video.ID });
            db.CallModalityLookups.Add(new CallSessionModalityLookup { CallSessionID = latecomer.ID, CallModalityID = audio.ID });

            db.CallFeedback.Add(new CallFeedback { CallID = daytime.ID, UserID = light.ID, Rating = "poor", Text = "choppy audio" });
            db.CallFailures.Add(new CallFailureReasonLookup { CallID = lateNight.ID, Reason = "MediaTimeout", Stage = "midcall" });
        }

        private static void SeedTeams(AnalyticsEntitiesContext db, User heavy, User light)
        {
            // An authorised team with real channel conversation, and a Greek name so the query path
            // is proven to be Unicode-safe end to end.
            var busy = new TeamDefinition
            {
                Name = GreekTeamName,
                GraphID = "00000000-0000-0000-0000-000000000001",
                FirstDiscovered = Today.AddDays(-100),
                HasRefreshToken = true,
            };

            // An authorised team nobody has posted in: genuinely dormant.
            var quiet = new TeamDefinition
            {
                Name = "Contoso Dormant",
                GraphID = "00000000-0000-0000-0000-000000000002",
                FirstDiscovered = Today.AddDays(-200),
                HasRefreshToken = true,
            };

            // Never authorised: unmeasured rather than quiet, and must not be reported as dormant.
            var unauthorised = new TeamDefinition
            {
                Name = "Contoso Unauthorised",
                GraphID = "00000000-0000-0000-0000-000000000003",
                FirstDiscovered = Today.AddDays(-50),
                HasRefreshToken = false,
            };

            db.Teams.Add(busy);
            db.Teams.Add(quiet);
            db.Teams.Add(unauthorised);
            db.SaveChanges();

            // Only the busy team has an owner, so the other two are governance findings.
            db.TeamOwners.Add(new TeamOwners { TeamID = busy.ID, OwnerID = heavy.ID, Discovered = Today.AddDays(-100) });

            db.TeamMembershipLogs.Add(new TeamMembershipLog { TeamID = busy.ID, UserID = heavy.ID, Date = Today.AddDays(-4) });
            db.TeamMembershipLogs.Add(new TeamMembershipLog { TeamID = busy.ID, UserID = light.ID, Date = Today.AddDays(-4) });
            // An older snapshot that must NOT be added to the current membership count.
            db.TeamMembershipLogs.Add(new TeamMembershipLog { TeamID = busy.ID, UserID = heavy.ID, Date = Today.AddDays(-20) });

            var general = new TeamChannel { TeamID = busy.ID, Name = "General", GraphID = "channel-1" };
            var quietChannel = new TeamChannel { TeamID = quiet.ID, Name = "General", GraphID = "channel-2" };
            db.TeamChannels.Add(general);
            db.TeamChannels.Add(quietChannel);

            var tab = new TeamTabDefinition { Name = "Planner", GraphID = "tab-1" };
            db.TeamTabDefinitions.Add(tab);

            var keyword = new KeyWord { Name = "\u03B1\u03BD\u03AC\u03BB\u03C5\u03C3\u03B7" };
            db.KeyWords.Add(keyword);

            var language = new Language { Name = "Greek" };
            db.Languages.Add(language);

            var reactionType = new TeamsReactionType { Name = "like" };
            db.TeamsReactionTypes.Add(reactionType);
            db.SaveChanges();

            db.ChannelTabLogs.Add(new ChannelTabLog { ChannelID = general.ID, TabID = tab.ID, Date = Today.AddDays(-4) });

            // Two scored days with different message counts, so the weighted mean is not the simple
            // average: (0.0 * 10 + 1.0 * 30) / 40 = 0.75.
            var negativeDay = new ChannelStatsLog
            {
                ChannelID = general.ID,
                Date = Today.AddDays(-5),
                ChatsCount = 10,
                SentimentScore = 0.0,
            };
            var positiveDay = new ChannelStatsLog
            {
                ChannelID = general.ID,
                Date = Today.AddDays(-4),
                ChatsCount = 30,
                SentimentScore = 1.0,
            };
            db.TeamChannelStats.Add(negativeDay);
            db.TeamChannelStats.Add(positiveDay);
            db.SaveChanges();

            db.TeamChannelStatKeywords.Add(new ChannelLogKeyword
            {
                ChannelStatsLogID = positiveDay.ID,
                KeyWordID = keyword.ID,
                KeyWordCount = 7,
            });
            db.TeamChannelStatLanguages.Add(new ChannelLogLanguage
            {
                ChannelStatsLogID = positiveDay.ID,
                LanguageID = language.ID,
            });

            db.TeamsUserReactions.Add(new TeamsUserReaction
            {
                ChannelID = general.ID,
                UserID = light.ID,
                ReactionID = reactionType.ID,
                Date = Today.AddDays(-4),
            });
        }

        #endregion

        #region Tests

        /// <summary>
        /// Every section, in one pass. The assertion that matters most is that nothing errored: each
        /// query is raw SQL, so this is the only place a bad column name is caught.
        /// </summary>
        [TestMethod]
        public async Task EverySectionExecutesWithoutASqlError()
        {
            var store = NewStore();
            var query = NewQuery();

            var sections = new TeamsExplorerSection[]
            {
                await store.GetOverviewAsync(query, new TeamsExplorerSources()),
                await store.GetAdoptionAsync(query),
                await store.GetMeetingsAsync(query),
                await store.GetCollaborationAsync(query),
                await store.GetConversationsAsync(query, true),
                await store.GetPeopleAsync(query),
            };

            foreach (var section in sections)
            {
                Assert.IsTrue(section.Queries.Count > 0, $"{section.GetType().Name} ran no queries.");

                var failed = section.Queries.Where(q => q.Error != null).ToList();
                Assert.AreEqual(
                    0,
                    failed.Count,
                    string.Join(" | ", failed.Select(q => $"{q.Key}: {q.Error}")));

                foreach (var q in section.Queries)
                {
                    StringAssert.Contains(q.Sql, "DECLARE @usageFrom",
                        $"'{q.Key}' must publish the parameters it was run with, so the SQL can be pasted into SSMS.");
                }
            }
        }

        [TestMethod]
        public async Task EveryGroupingProducesAWorkingBreakdown()
        {
            var store = NewStore();

            foreach (var grouping in TeamsExplorerQuery.Groupings)
            {
                var adoption = await store.GetAdoptionAsync(
                    TeamsExplorerQuery.Create(28, DateTime.UtcNow, grouping));

                var breakdown = adoption.Queries.Single(q => q.Key == "adoption-breakdown");
                Assert.IsNull(breakdown.Error, $"Grouping '{grouping}' failed: {breakdown.Error}");
                Assert.AreEqual(grouping, adoption.GroupBy);
            }
        }

        /// <summary>
        /// The single most important definition on the page.
        /// </summary>
        [TestMethod]
        public async Task AUserWithAnAllZeroUsageRowIsNotCountedAsActive()
        {
            var overview = await NewStore().GetOverviewAsync(NewQuery(), new TeamsExplorerSources());

            // Three users have usage rows (heavy, light, idle); only two of them did anything.
            Assert.AreEqual(2, overview.Kpis.ActiveUsers,
                "A usage-report row with all-zero metrics means 'measured', not 'active'.");

            // Four users are in the directory, so reach is 2 of 4 rather than 3 of 4.
            Assert.AreEqual(4, overview.Kpis.KnownUsers);
            Assert.AreEqual(50, overview.Kpis.ReachPct, 0.001);
        }

        [TestMethod]
        public async Task PerDayMetricsAreSummedAcrossTheWindow()
        {
            var overview = await NewStore().GetOverviewAsync(NewQuery(), new TeamsExplorerSources());

            // The heavy user posted (10 team chat + 5 posts + 3 replies) = 18 channel messages on
            // each of two days. Reading one snapshot instead would report 18.
            Assert.AreEqual(36, overview.Kpis.ChannelMessages);

            // 2 private chats x 2 days for the heavy user, plus 1 for the light user.
            Assert.AreEqual(5, overview.Kpis.PrivateMessages);

            // 4 attended x 2 days.
            Assert.AreEqual(8, overview.Kpis.MeetingsAttended);

            // 3600 audio seconds x 2 days = 2 hours.
            Assert.AreEqual(2, overview.Kpis.AudioHours, 0.001);
        }

        [TestMethod]
        public async Task ModalityDurationsAreReportedAsSharesNotAddedTogether()
        {
            var overview = await NewStore().GetOverviewAsync(NewQuery(), new TeamsExplorerSources());

            // 1800s video against 3600s audio, and 900s of sharing against the same.
            Assert.AreEqual(50, overview.Kpis.VideoSharePct, 0.001);
            Assert.AreEqual(25, overview.Kpis.ScreenShareSharePct, 0.001);
        }

        [TestMethod]
        public async Task OpenCollaborationComparesChannelAgainstPrivateChat()
        {
            var overview = await NewStore().GetOverviewAsync(NewQuery(), new TeamsExplorerSources());

            // 36 channel messages against 5 private ones.
            Assert.AreEqual(36.0 / 41.0 * 100.0, overview.Kpis.OpenCollaborationPct, 0.001);
        }

        [TestMethod]
        public async Task CallFiguresSeparateGroupFromOneToOneAndSpotOutOfHours()
        {
            var meetings = await NewStore().GetMeetingsAsync(NewQuery());

            Assert.AreEqual(2, meetings.Kpis.Calls);
            Assert.AreEqual(1, meetings.Kpis.GroupCalls, "Call types must match case-insensitively ('groupCall').");
            Assert.AreEqual(1, meetings.Kpis.PeerToPeerCalls);

            // The 22:00 call is outside 08:00-18:00; the 10:00 one is not, and both are on a weekday.
            Assert.AreEqual(50, meetings.Kpis.AfterHoursPct, 0.001);
            Assert.AreEqual(0, meetings.Kpis.WeekendPct, 0.001);

            // Two distinct people joined a call.
            Assert.AreEqual(2, meetings.Kpis.Attendees);
        }

        [TestMethod]
        public async Task AttendeePresenceDetectsLateJoining()
        {
            var meetings = await NewStore().GetMeetingsAsync(NewQuery());

            // Sessions: 60/60, 30/60 and 15/15 -> mean 0.8333 -> 83.3%.
            Assert.AreEqual(83.333, meetings.Kpis.AttendeeEngagementPct, 0.01);
        }

        [TestMethod]
        public async Task CallQualityReportsWhatLittleTheSourceActuallyCarries()
        {
            var meetings = await NewStore().GetMeetingsAsync(NewQuery());

            Assert.AreEqual(1, meetings.Quality.FeedbackCount);
            Assert.AreEqual(1, meetings.Quality.FailureCount);
            Assert.AreEqual("poor", meetings.Quality.Ratings.Single().Label);
            Assert.AreEqual("MediaTimeout", meetings.Quality.FailureReasons.Single().Label);
        }

        [TestMethod]
        public async Task GovernanceFindingsSeparateUnmeasuredTeamsFromSilentOnes()
        {
            var collaboration = await NewStore().GetCollaborationAsync(NewQuery());

            Assert.AreEqual(3, collaboration.Kpis.TotalTeams);
            Assert.AreEqual(2, collaboration.Kpis.AuthorisedTeams);
            Assert.AreEqual(1, collaboration.Kpis.ActiveTeams);
            Assert.AreEqual(2, collaboration.Kpis.OwnerlessTeams);

            var dormant = collaboration.DormantTeams.Select(t => t.Name).ToList();
            CollectionAssert.Contains(dormant, "Contoso Dormant");
            CollectionAssert.DoesNotContain(
                dormant,
                "Contoso Unauthorised",
                "An unauthorised team is unmeasured, not dormant - listing it would send an admin to archive a busy team.");
        }

        [TestMethod]
        public async Task TeamMembershipUsesTheLatestSnapshotNotTheWholeHistory()
        {
            var collaboration = await NewStore().GetCollaborationAsync(NewQuery());

            var busy = collaboration.Teams.Single(t => t.Name == GreekTeamName);

            // Two members on the latest snapshot date; the older row for the same person must not add
            // a third.
            Assert.AreEqual(2, busy.Members);
            Assert.AreEqual(1, busy.Owners);
            Assert.IsTrue(busy.Authorised);
        }

        [TestMethod]
        public async Task NonLatinTeamAndKeywordNamesSurviveTheQueryPath()
        {
            var store = NewStore();

            var collaboration = await store.GetCollaborationAsync(NewQuery());
            Assert.IsTrue(
                collaboration.Teams.Any(t => t.Name == GreekTeamName),
                "A Greek team name must survive the nvarchar column and the query unchanged.");

            var conversations = await store.GetConversationsAsync(NewQuery(), true);
            Assert.IsTrue(
                conversations.Keywords.Any(k => k.Name == "\u03B1\u03BD\u03AC\u03BB\u03C5\u03C3\u03B7"),
                "A Greek key phrase must survive unchanged.");
        }

        [TestMethod]
        public async Task SentimentIsWeightedByMessageCountAndNeutralIsAHalf()
        {
            var conversations = await NewStore().GetConversationsAsync(NewQuery(), true);

            var team = conversations.SentimentByTeam.Single();

            // (0.0 * 10 + 1.0 * 30) / 40 = 0.75. A simple mean of the two days would be 0.5, which is
            // neutral - the opposite conclusion.
            Assert.AreEqual(0.75, team.Sentiment, 0.0001);
            Assert.AreEqual(40, team.Messages);
            Assert.AreEqual(2, conversations.ScoredChannelDays);
        }

        [TestMethod]
        public async Task PeopleAreRankedAndTheirSegmentIsResolved()
        {
            var people = await NewStore().GetPeopleAsync(NewQuery());

            Assert.AreEqual(2, people.Champions.Count, "Only users with at least one active day are ranked.");

            var top = people.Champions.First();
            Assert.AreEqual("ada@contoso.com", top.UserPrincipalName);
            Assert.AreEqual("Engineering", top.Department);
            Assert.AreEqual(2, top.ActiveDays);
            Assert.AreEqual(36, top.ChannelMessages);
            Assert.AreEqual(1, top.CallsHosted);
            Assert.IsFalse(string.IsNullOrWhiteSpace(top.Segment));

            Assert.AreEqual(1, people.Dormant.Count, "The all-zero user belongs in the dormant list.");
            Assert.AreEqual("alan@contoso.com", people.Dormant.Single().UserPrincipalName);
        }

        [TestMethod]
        public async Task ObfuscationIsNotClaimedWhenNamesLookLikeRealAddresses()
        {
            var people = await NewStore().GetPeopleAsync(NewQuery());

            Assert.IsFalse(people.NamesObfuscated,
                "Every seeded user has an address-shaped name, so the anonymisation probe must not fire.");
        }

        [TestMethod]
        public async Task DeviceMixCountsAUserOncePerPlatform()
        {
            var adoption = await NewStore().GetAdoptionAsync(NewQuery());

            var windows = adoption.Devices.Single(d => d.Platform == "Windows");
            var mobile = adoption.Devices.Single(d => d.Platform == "Mobile (any)");

            Assert.AreEqual(1, windows.Users);
            Assert.AreEqual(1, mobile.Users, "The iOS user must be counted once in the mobile roll-up.");
        }

        [TestMethod]
        public async Task LifecycleSplitsTheWindowInHalf()
        {
            var adoption = await NewStore().GetAdoptionAsync(NewQuery());

            // All the seeded activity is in the second half of a 28-day window, so both active users
            // read as newly active rather than returning.
            Assert.AreEqual(2, adoption.Lifecycle.NewUsers);
            Assert.AreEqual(0, adoption.Lifecycle.ReturningUsers);
            Assert.AreEqual(0, adoption.Lifecycle.LapsedUsers);
        }

        #endregion
    }
}
