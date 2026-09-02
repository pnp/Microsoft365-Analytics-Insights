extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Entities.Email;
using Common.Entities.Entities.Teams;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure.Interception;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Proves the admin user-data lookup's <b>single-round-trip</b> category counts return exactly what
    /// the per-category counts they replaced return, against a real database.
    ///
    /// <see cref="UserDataLookupServiceTests"/> covers the logic with an in-memory store, but it cannot
    /// see the one thing that actually changed in SQL: <see cref="IUserDataLookupQuery.GetCountsByCategoryAsync"/>
    /// projects ~30 counts into one statement instead of issuing ~30 separate <c>COUNT</c> queries. Only
    /// a real database can show that EF translates that projection to the same numbers - and that the
    /// key-to-count mapping lines each count up with the right category.
    ///
    /// The seeded user is given a <b>distinct</b> row count per category, so two categories whose
    /// subqueries or dictionary entries were crossed produce different numbers and fail. Equal counts
    /// (or zeroes everywhere) would let a swap pass, which is the whole risk being guarded here.
    ///
    /// 21 of the 30 categories are seeded. The other nine are left at zero because they need an object
    /// graph this test does not build: the six Teams/calls categories (memberships, ownerships,
    /// reactions, calls organised, call sessions, call feedback) need a Team or CallRecord,
    /// copilot-interactions needs a Copilot chat, and page-likes / page-comments need per-row URLs. A
    /// swap purely between two of those nine would not be caught.
    /// </summary>
    [TestClass]
    public class UserDataLookupSqlIntegrationTests
    {
        [TestMethod]
        public async Task BatchedCounts_MatchThePerCategoryCounts_ForEveryCategory()
        {
            // Deliberately ASCII: dbo.users.user_name is still varchar(250), so a non-Latin UPN is
            // stored mangled (issue #402). The seeded URL below is Unicode, because urls.full_url IS
            // nvarchar and must round-trip.
            var upn = $"userdatalookup.{DateTime.UtcNow.Ticks}@contoso.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                // The seed state is created BEFORE the first write and cleanup is armed immediately, so a
                // failure part-way through seeding still removes whatever it managed to commit - this
                // database is shared with the rest of the suite.
                var seeded = new SeededUser();
                try
                {
                    await SeedUserWithDataAsync(db, upn, seeded);
                    var query = new SqlUserDataLookupQuery();

                    var batched = await query.GetCountsByCategoryAsync(seeded.UserId);

                    foreach (var meta in UserDataLookupRules.Categories)
                    {
                        var single = await query.GetCountForCategoryAsync(seeded.UserId, meta.Key);
                        Assert.IsTrue(batched.ContainsKey(meta.Key), $"the batched query answered nothing for '{meta.Key}'");
                        Assert.AreEqual(single, batched[meta.Key],
                            $"batched and per-category counts disagree for '{meta.Key}' - the single round trip is not equivalent");
                    }

                    // Every seeded category must report its own, unique count. Without this the loop
                    // above would be satisfied by 0 == 0, and a crossed mapping would pass.
                    foreach (var expected in seeded.ExpectedCounts)
                    {
                        Assert.AreEqual(expected.Value, batched[expected.Key],
                            $"'{expected.Key}' reported the wrong number - its subquery or dictionary entry is counting something else");
                    }

                    CollectionAssert.AllItemsAreUnique(seeded.ExpectedCounts.Values.ToList(),
                        "the seeded counts must all differ, or a crossed mapping could still pass");

                    // Assert against what the DATABASE actually returned, not the fixture's own
                    // bookkeeping: exactly the seeded categories carry data and the rest really are zero.
                    // This is what makes the class comment's "21 of 30" claim checkable.
                    var nonZero = batched.Where(c => c.Value != 0).Select(c => c.Key).OrderBy(k => k).ToList();
                    CollectionAssert.AreEqual(
                        seeded.ExpectedCounts.Keys.OrderBy(k => k).ToList(),
                        nonZero,
                        "the categories reporting data are not the ones that were seeded");
                    Assert.AreEqual(21, nonZero.Count,
                        "the class comment says 21 of the 30 categories carry data; keep it honest if that changes");
                }
                finally
                {
                    await CleanUpAsync(seeded);
                }
            }
        }

        /// <summary>
        /// The page tells an admin that the SQL shown next to each count reproduces that count. That is
        /// a promise to someone who will paste it into SSMS, and nothing verified it against a real
        /// database - it is also an independent implementation (it names the table and user column
        /// explicitly from the catalogue) so it cross-checks the EF projection.
        /// </summary>
        [TestMethod]
        public async Task DisplaySql_ReproducesTheCountItIsShownNextTo()
        {
            var upn = $"displaysql.{DateTime.UtcNow.Ticks}@contoso.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                var seeded = new SeededUser();
                try
                {
                    await SeedUserWithDataAsync(db, upn, seeded);
                    var batched = await new SqlUserDataLookupQuery().GetCountsByCategoryAsync(seeded.UserId);

                    foreach (var meta in UserDataLookupRules.Categories)
                    {
                        var sql = UserDataLookupRules.BuildCountSql(meta, upn);
                        var fromDisplaySql = (await db.Database.SqlQuery<int>(sql).ToListAsync()).Single();

                        Assert.AreEqual(batched[meta.Key], fromDisplaySql,
                            $"the SQL shown for '{meta.Key}' does not reproduce the count shown beside it{Environment.NewLine}{sql}");
                    }
                }
                finally
                {
                    await CleanUpAsync(seeded);
                }
            }
        }

        [TestMethod]
        public async Task BatchedCounts_AnswerEveryKnownCategoryExactlyOnce()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var upn = $"batchkeys.{DateTime.UtcNow.Ticks}@contoso.com";
                try
                {
                    var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
                    db.users.Add(user);
                    await db.SaveChangesAsync();

                    var batched = await new SqlUserDataLookupQuery().GetCountsByCategoryAsync(user.ID);

                    CollectionAssert.AreEquivalent(
                        UserDataLookupRules.Categories.Select(c => c.Key).ToList(),
                        batched.Keys.ToList(),
                        "the summary shows every catalogue category, so the batch must answer for every one of them and nothing else");
                    Assert.IsTrue(batched.Values.All(v => v == 0), "a brand-new user has no data anywhere");
                }
                finally
                {
                    await DeleteUserByUpnAsync(db, upn);
                }
            }
        }

        /// <summary>
        /// The actual point of the change, measured rather than asserted in a PR description: the
        /// batched projection is <b>one</b> database command, where counting the categories one at a
        /// time is one command each. EF6 has no query batching, so without this the summary endpoint
        /// issues a round trip per category on every admin lookup.
        /// </summary>
        [TestMethod]
        public async Task BatchedCounts_AreOneCommand_WherePerCategoryCountsAreOneEach()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var upn = $"roundtrips.{DateTime.UtcNow.Ticks}@contoso.com";
                CommandCountingInterceptor counter = null;
                try
                {
                    var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
                    db.users.Add(user);
                    await db.SaveChangesAsync();

                    var query = new SqlUserDataLookupQuery();

                    // Warm up first: the very first context in a run can issue its own schema commands.
                    await query.GetUserIdAsync(upn);

                    counter = new CommandCountingInterceptor();
                    DbInterception.Add(counter);

                    counter.Reset();
                    await query.GetCountsByCategoryAsync(user.ID);
                    var batchedCommands = counter.Count;

                    counter.Reset();
                    foreach (var meta in UserDataLookupRules.Categories)
                    {
                        await query.GetCountForCategoryAsync(user.ID, meta.Key);
                    }
                    var perCategoryCommands = counter.Count;

                    Assert.AreEqual(1, batchedCommands, "the whole point is that every category count arrives in one round trip");
                    Assert.AreEqual(UserDataLookupRules.Categories.Count, perCategoryCommands,
                        "the per-category path is one command each - that is what the summary endpoint used to do");
                }
                finally
                {
                    // The interceptor is process-wide, and the user row is in a database shared with the
                    // rest of the suite: both must go even if the warm-up or an assertion threw. The user
                    // is removed by UPN, not by a captured id, so a save that committed without handing
                    // the id back is still undone.
                    if (counter != null) DbInterception.Remove(counter);
                    await DeleteUserByUpnAsync(db, upn);
                }
            }
        }

        [TestMethod]
        public async Task UnknownUpn_ResolvesToNoProfileAndNoUserId()
        {
            var query = new SqlUserDataLookupQuery();
            var upn = $"definitely-not-a-user.{DateTime.UtcNow.Ticks}@contoso.com";

            Assert.IsNull(await query.GetProfileAsync(upn));
            Assert.IsNull(await query.GetUserIdAsync(upn));
        }

        /// <summary>
        /// The summary and the drill-down resolve the user by two different queries (profile vs id
        /// only); both must land on the same row, or a drill-down would silently report another user's
        /// data.
        /// </summary>
        /// <remarks>
        /// This deliberately uses an ASCII UPN. A non-Latin UPN cannot be asserted here yet:
        /// <c>dbo.users.user_name</c> is still <c>varchar(250)</c> on the default CP1 collation, so a
        /// Greek UPN is written to the database as <c>?a??µ??a...</c> and never matches on read. That is
        /// a pre-existing schema bug, filed as issue #402 - fixing it needs a migration, which #381
        /// rules out. The service-level Unicode guarantee is covered without a database in
        /// <c>UserDataLookupServiceTests.Summary_UpnWithNonAsciiCharacters_IsMatchedAndEmittedVerbatim</c>.
        /// </remarks>
        [TestMethod]
        public async Task Profile_AndUserIdLookup_ResolveTheSameUser()
        {
            var upn = $"profilelookup.{DateTime.UtcNow.Ticks}@contoso.com";
            using (var db = new AnalyticsEntitiesContext())
            {
                try
                {
                    var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
                    db.users.Add(user);
                    await db.SaveChangesAsync();

                    var query = new SqlUserDataLookupQuery();

                    var profile = await query.GetProfileAsync(upn);

                    Assert.IsNotNull(profile);
                    Assert.AreEqual(upn, profile.UserPrincipalName);
                    Assert.AreEqual(user.ID, profile.UserId, "the summary counts are run against the id this profile carries");
                    Assert.AreEqual(user.ID, await query.GetUserIdAsync(upn));
                }
                finally
                {
                    await DeleteUserByUpnAsync(db, upn);
                }
            }
        }

        #region Seeding

        private sealed class SeededUser
        {
            /// <summary>
            /// A value unique to this test run, embedded in every row's natural key. Cleanup is driven
            /// off these rather than off generated ids, so it still works if a save committed but the
            /// identity value never made it back (EF commits before <c>AcceptAllChanges</c>).
            /// </summary>
            public string Tag { get; set; }

            public string Upn { get; set; }
            public int UserId { get; set; }
            public User User { get; set; }
            public EventOperation Operation { get; set; }
            public O365ClientApplication ClientApp { get; set; }
            public StreamVideo Video { get; set; }
            public EmailAddress FromAddress { get; set; }
            public List<CommonAuditEvent> AuditEvents { get; } = new List<CommonAuditEvent>();

            /// <summary>The row count deliberately given to each category, all different from each other.</summary>
            public Dictionary<string, int> ExpectedCounts { get; } = new Dictionary<string, int>();
        }

        /// <summary>
        /// Seeds one user with a <b>distinct</b> number of rows per category, covering all three ways a
        /// table links to a user: a direct FK column, indirectly via sessions (web hits), and via
        /// audit_events (the event_meta_* sub-types). Distinct counts are the point: if two categories'
        /// subqueries or dictionary entries were crossed, they would report each other's number and the
        /// test fails.
        ///
        /// See the class comment for which nine categories are deliberately left at zero.
        /// </summary>
        private static async Task SeedUserWithDataAsync(AnalyticsEntitiesContext db, string upn, SeededUser seeded)
        {
            var ticks = DateTime.UtcNow.Ticks;

            // Recorded BEFORE the first write: cleanup keys off these, so a save that commits without
            // handing back its identity value still gets undone.
            seeded.Tag = "userdatalookup-" + ticks;
            seeded.Upn = upn;

            var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
            db.users.Add(user);
            await db.SaveChangesAsync();
            seeded.User = user;
            seeded.UserId = user.ID;

            var operation = new EventOperation { Name = "UserDataLookupIntegrationTest " + seeded.Tag };
            db.event_operations.Add(operation);
            await db.SaveChangesAsync();
            seeded.Operation = operation;

            // --- audit_events (direct FK) and its sub-types (joined through event_id) ---
            // Each sub-type is keyed by event_id, so a sub-type with N rows needs N distinct audit
            // events. 14 events is enough for the largest sub-type below and gives audit-events its own
            // count that no sub-type shares.
            const int auditEventCount = 14;
            for (var i = 0; i < auditEventCount; i++)
            {
                var auditEvent = new CommonAuditEvent
                {
                    Id = Guid.NewGuid(),
                    TimeStamp = DateTime.UtcNow.AddMinutes(-i),
                    Operation = operation,
                    User = user,
                    EventData = "{}",
                };
                db.AuditEventsCommon.Add(auditEvent);
                seeded.AuditEvents.Add(auditEvent);
            }
            await db.SaveChangesAsync();
            seeded.ExpectedCounts[UserDataLookupRules.CatAuditEvents] = auditEventCount;

            AddAuditChildren(db, seeded, UserDataLookupRules.CatAuditSharePoint, 1, e => db.sharepoint_events.Add(new SharePointEventMetadata { AuditEvent = e }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatAuditExchange, 2, e => db.exchange_events.Add(new ExchangeEventMetadata { AuditEvent = e }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatAuditEntra, 3, e => db.azure_ad_events.Add(new AzureADEventMetadata { AuditEvent = e }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatAuditGeneral, 4, e => db.general_audit_events.Add(new GeneralEventMetada { AuditEvent = e }));

            // Stream events need a client application and a video: both FK columns are non-nullable.
            var clientApp = new O365ClientApplication { Name = "UserDataLookupTest " + seeded.Tag, ClientApplicationId = Guid.NewGuid() };
            var video = new StreamVideo { Name = "UserDataLookupTest " + seeded.Tag, StreamID = Guid.NewGuid() };
            db.O365ClientApplications.Add(clientApp);
            db.Streams.Add(video);
            await db.SaveChangesAsync();
            seeded.ClientApp = clientApp;
            seeded.Video = video;
            AddAuditChildren(db, seeded, UserDataLookupRules.CatAuditStream, 5, e => db.StreamEvents.Add(new StreamEventMetada { AuditEvent = e, ClientApplication = clientApp, Video = video }));

            AddAuditChildren(db, seeded, UserDataLookupRules.CatPowerAppEvents, 6, e => db.power_app_events.Add(new PowerAppEventMetadata { AuditEvent = e }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatFlowEvents, 7, e => db.power_automate_flow_events.Add(new PowerAutomateFlowEventMetadata { AuditEvent = e }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatPowerBiEvents, 8, e => db.power_bi_events.Add(new PowerBIEventMetadata { AuditEvent = e }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatCopilotStudioEvents, 9, e => db.copilot_studio_events.Add(new CopilotStudioEventMetadata { AuditEvent = e }));

            // The two "shared with me" tables join through audit_events too, but are counted on their
            // own user column (shared_with_user_id). They have a unique index on
            // (event_id, shared_with_user_id), so each row still needs its own audit event.
            AddAuditChildren(db, seeded, UserDataLookupRules.CatPowerAppShares, 10,
                e => db.power_app_share_events.Add(new PowerAppShareEventMetadata { AuditEvent = e, SharedWithUser = user, RoleName = "CanView" }));
            AddAuditChildren(db, seeded, UserDataLookupRules.CatFlowShares, 11,
                e => db.power_automate_flow_share_events.Add(new PowerAutomateFlowShareEventMetadata { AuditEvent = e, SharedWithUser = user, RoleName = "CanView" }));

            await db.SaveChangesAsync();

            // --- web hits: linked to the user indirectly, hits -> sessions -> users ---
            const int hitCount = 12;
            var url = new Url { FullUrl = $"https://contoso.sharepoint.com/sites/test/Καλημέρα-{seeded.Tag}.pdf" };
            var session = new UserSession { user = user, ai_session_id = seeded.Tag };
            db.sessions.Add(session);
            for (var i = 0; i < hitCount; i++)
            {
                db.hits.Add(new Hit
                {
                    hit_timestamp = DateTime.UtcNow.AddMinutes(-i),
                    page_request_id = Guid.NewGuid(),
                    session = session,
                    url = url,
                });
            }
            seeded.ExpectedCounts[UserDataLookupRules.CatWebHits] = hitCount;
            await db.SaveChangesAsync();

            // --- the daily usage-report logs: direct FK, one distinct count each ---
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageOutlook, 15, (u, d) => db.OutlookUsageActivityLogs.Add(new OutlookUsageActivityLog { User = u, Date = d }));
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageOneDrive, 16, (u, d) => db.OneDriveUserActivityLogs.Add(new OneDriveUserActivityLog { User = u, Date = d }));
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageSharePoint, 17, (u, d) => db.SharePointUserActivityLogs.Add(new SharePointUserActivityLog { User = u, Date = d }));
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageYammer, 18, (u, d) => db.YammerUserActivityLogs.Add(new YammerUserActivityLog { User = u, Date = d }));
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageTeams, 19, (u, d) => db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog { User = u, Date = d }));
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageTeamsDevice, 20, (u, d) => db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog { User = u, Date = d }));
            AddUsageDays(db, seeded, UserDataLookupRules.CatUsageAppPlatform, 21, (u, d) => db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog { User = u, Date = d }));

            // --- sent emails: another direct FK on a different table ---
            const int sentEmailCount = 22;
            var fromAddress = new EmailAddress { Address = upn };
            db.EmailAddresses.Add(fromAddress);
            await db.SaveChangesAsync();
            seeded.FromAddress = fromAddress;
            for (var i = 0; i < sentEmailCount; i++)
            {
                db.SentEmails.Add(new SentEmail
                {
                    User = user,
                    FromAddress = fromAddress,
                    SentDate = DateTime.UtcNow.AddMinutes(-i),
                    Subject = $"Καλημέρα {i}",
                    GraphMessageId = $"{seeded.Tag}-{i}",
                });
            }
            seeded.ExpectedCounts[UserDataLookupRules.CatSentEmails] = sentEmailCount;

            await db.SaveChangesAsync();
        }

        /// <summary>Adds <paramref name="count"/> rows hanging off distinct audit events (they are keyed or uniquely indexed by event_id).</summary>
        private static void AddAuditChildren(AnalyticsEntitiesContext db, SeededUser seeded, string categoryKey, int count, Action<CommonAuditEvent> add)
        {
            for (var i = 0; i < count; i++)
            {
                add(seeded.AuditEvents[i]);
            }
            seeded.ExpectedCounts[categoryKey] = count;
        }

        /// <summary>Adds <paramref name="count"/> daily activity-report rows, one per distinct day.</summary>
        private static void AddUsageDays(AnalyticsEntitiesContext db, SeededUser seeded, string categoryKey, int count, Action<User, DateTime> add)
        {
            for (var i = 0; i < count; i++)
            {
                add(seeded.User, DateTime.UtcNow.Date.AddDays(-i));
            }
            seeded.ExpectedCounts[categoryKey] = count;
        }

        /// <summary>
        /// Removes everything the test added. These tests share the unit-test database with the rest of
        /// the suite, so leaving audit events or hits behind would skew anything that counts them.
        ///
        /// Tolerates partial state: it is armed before the first write, so it may be called when seeding
        /// failed half way and only some of the ids were assigned.
        ///
        /// Runs entirely as raw SQL on a <b>fresh</b> context: the seeding context still tracks all the
        /// rows, and deleting a parent out from under a tracked child makes EF try to null a
        /// non-nullable foreign key on the next <c>SaveChanges</c>.
        /// </summary>
        private static async Task CleanUpAsync(SeededUser seeded)
        {
            if (seeded?.Tag == null)
            {
                // Seeding never started, so there is nothing to undo.
                return;
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                // Resolve the user from its UPN rather than trusting the captured identity value: EF
                // commits before it hands the id back, so a failure in between would leave UserId at 0
                // with the row already in the database.
                var userId = (await db.Database.SqlQuery<int>(
                    "SELECT ISNULL((SELECT TOP 1 id FROM dbo.users WHERE user_name = @p0), 0)", seeded.Upn).ToListAsync()).Single();

                // Children of audit_events and of the user, before their parents.
                await db.Database.ExecuteSqlCommandAsync(
                    @"DELETE h FROM dbo.hits h INNER JOIN dbo.sessions s ON s.id = h.session_id WHERE s.user_id = @p0;
                      DELETE FROM dbo.sessions WHERE user_id = @p0;
                      DELETE FROM dbo.sent_emails WHERE user_id = @p0;
                      DELETE FROM dbo.outlook_user_activity_log WHERE user_id = @p0;
                      DELETE FROM dbo.onedrive_user_activity_log WHERE user_id = @p0;
                      DELETE FROM dbo.sharepoint_user_activity_log WHERE user_id = @p0;
                      DELETE FROM dbo.yammer_user_activity_log WHERE user_id = @p0;
                      DELETE FROM dbo.teams_user_activity_log WHERE user_id = @p0;
                      DELETE FROM dbo.teams_user_device_usage_log WHERE user_id = @p0;
                      DELETE FROM dbo.platform_user_activity_log WHERE user_id = @p0;
                      DELETE c FROM dbo.event_meta_sharepoint c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_exchange c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_azure_ad c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_general c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_stream c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_power_app c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_power_automate_flow c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_power_bi c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE c FROM dbo.event_meta_copilot_studio c INNER JOIN dbo.audit_events e ON e.id = c.event_id WHERE e.user_id = @p0;
                      DELETE FROM dbo.event_meta_power_app_share WHERE shared_with_user_id = @p0;
                      DELETE FROM dbo.event_meta_power_automate_flow_share WHERE shared_with_user_id = @p0;
                      DELETE FROM dbo.audit_events WHERE user_id = @p0;
                      DELETE FROM dbo.users WHERE id = @p0;",
                    userId);

                // The lookup rows the deleted children pointed at, now nothing references them. Matched on
                // this run's tag rather than on captured ids, for the same reason as the user above.
                await db.Database.ExecuteSqlCommandAsync(
                    @"DELETE FROM dbo.urls WHERE full_url LIKE N'%' + @p0 + N'%';
                      DELETE FROM dbo.event_operations WHERE operation_name LIKE N'%' + @p0 + N'%';
                      DELETE FROM dbo.o365_client_applications WHERE name LIKE N'%' + @p0 + N'%';
                      DELETE FROM dbo.stream_videos WHERE name LIKE N'%' + @p0 + N'%';
                      DELETE FROM dbo.email_addresses WHERE address = @p1;",
                    seeded.Tag, seeded.Upn ?? string.Empty);

                var leftOver = (await db.Database.SqlQuery<int>(
                    "SELECT COUNT(*) FROM dbo.users WHERE user_name = @p0", seeded.Upn ?? string.Empty).ToListAsync()).Single();
                Assert.AreEqual(0, leftOver, "the test user should have been removed - later tests share this database");
            }
        }

        /// <summary>
        /// Removes a user by its UPN, which is unique per test run. Deliberately not by a captured
        /// identity value: EF commits before it returns the generated id, so a failure in between would
        /// leave the row behind in a database the whole suite shares.
        /// </summary>
        private static Task DeleteUserByUpnAsync(AnalyticsEntitiesContext db, string upn)
        {
            return db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.users WHERE user_name = @p0", upn ?? string.Empty);
        }

        #endregion

        #region Command counting

        /// <summary>
        /// Counts the database commands EF actually executes, so a "one round trip" claim is measured
        /// rather than assumed. Registered globally by <see cref="DbInterception"/>, so it must only be
        /// added around the call being measured (the test project does not run tests in parallel).
        /// </summary>
        private sealed class CommandCountingInterceptor : IDbCommandInterceptor
        {
            public int Count { get; private set; }

            public void Reset() => Count = 0;

            public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) => Count++;
            public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) { }
            public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) => Count++;
            public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) { }
            public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) => Count++;
            public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) { }
        }

        #endregion
    }
}
