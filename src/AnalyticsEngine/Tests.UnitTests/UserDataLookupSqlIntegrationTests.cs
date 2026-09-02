extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Entities.Teams;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Common;
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
    /// What this proves: for every category, batched == per-category, for a user seeded with rows across
    /// all three ways a table links to a user (a direct FK column, indirectly via sessions, and via
    /// audit_events). What it does not prove: that every one of the ~30 tables is individually populated -
    /// the categories with no seeded rows only assert 0 == 0, which still catches an untranslatable query
    /// or a category missing from the batch.
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
                var seeded = await SeedUserWithDataAsync(db, upn);
                try
                {
                    var query = new SqlUserDataLookupQuery();

                    var batched = await query.GetCountsByCategoryAsync(seeded.UserId);

                    foreach (var meta in UserDataLookupRules.Categories)
                    {
                        var single = await query.GetCountForCategoryAsync(seeded.UserId, meta.Key);
                        Assert.IsTrue(batched.ContainsKey(meta.Key), $"the batched query answered nothing for '{meta.Key}'");
                        Assert.AreEqual(single, batched[meta.Key],
                            $"batched and per-category counts disagree for '{meta.Key}' - the single round trip is not equivalent");
                    }

                    // The seeded rows must actually show up, or every comparison above was 0 == 0.
                    Assert.AreEqual(2, batched[UserDataLookupRules.CatAuditEvents], "direct FK on audit_events");
                    Assert.AreEqual(1, batched[UserDataLookupRules.CatAuditSharePoint], "audit sub-type, joined via audit_events");
                    Assert.AreEqual(1, batched[UserDataLookupRules.CatPowerAppShares], "a different user column (shared_with_user_id)");
                    Assert.AreEqual(3, batched[UserDataLookupRules.CatWebHits], "web hits, linked indirectly through sessions");
                    Assert.AreEqual(1, batched[UserDataLookupRules.CatUsageOutlook], "direct FK on a daily usage-report table");
                }
                finally
                {
                    await CleanUpAsync(db, seeded);
                }
            }
        }

        [TestMethod]
        public async Task BatchedCounts_AnswerEveryKnownCategoryExactlyOnce()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var upn = $"batchkeys.{DateTime.UtcNow.Ticks}@contoso.com";
                var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
                db.users.Add(user);
                await db.SaveChangesAsync();

                try
                {
                    var batched = await new SqlUserDataLookupQuery().GetCountsByCategoryAsync(user.ID);

                    CollectionAssert.AreEquivalent(
                        UserDataLookupRules.Categories.Select(c => c.Key).ToList(),
                        batched.Keys.ToList(),
                        "the summary shows every catalogue category, so the batch must answer for every one of them and nothing else");
                    Assert.IsTrue(batched.Values.All(v => v == 0), "a brand-new user has no data anywhere");
                }
                finally
                {
                    db.users.Remove(user);
                    await db.SaveChangesAsync();
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
                var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
                db.users.Add(user);
                await db.SaveChangesAsync();

                var query = new SqlUserDataLookupQuery();

                // Warm up first: the very first context in a run can issue its own schema commands.
                await query.GetUserIdAsync(upn);

                var counter = new CommandCountingInterceptor();
                DbInterception.Add(counter);
                try
                {
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
                    DbInterception.Remove(counter);
                    db.users.Remove(user);
                    await db.SaveChangesAsync();
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
                var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
                db.users.Add(user);
                await db.SaveChangesAsync();

                try
                {
                    var query = new SqlUserDataLookupQuery();

                    var profile = await query.GetProfileAsync(upn);

                    Assert.IsNotNull(profile);
                    Assert.AreEqual(upn, profile.UserPrincipalName);
                    Assert.AreEqual(user.ID, profile.UserId, "the summary counts are run against the id this profile carries");
                    Assert.AreEqual(user.ID, await query.GetUserIdAsync(upn));
                }
                finally
                {
                    db.users.Remove(user);
                    await db.SaveChangesAsync();
                }
            }
        }

        #region Seeding

        private sealed class SeededUser
        {
            public int UserId { get; set; }
            public User User { get; set; }
            public List<CommonAuditEvent> AuditEvents { get; } = new List<CommonAuditEvent>();
            public SharePointEventMetadata SharePointEvent { get; set; }
            public PowerAppShareEventMetadata PowerAppShare { get; set; }
            public List<Hit> Hits { get; } = new List<Hit>();
            public UserSession Session { get; set; }
            public Url Url { get; set; }
            public OutlookUsageActivityLog OutlookLog { get; set; }
            public EventOperation Operation { get; set; }
        }

        /// <summary>
        /// Seeds one user with rows in each of the three link shapes the category catalogue uses, so the
        /// batched-vs-per-category comparison is exercising real joins rather than empty tables.
        /// </summary>
        private static async Task<SeededUser> SeedUserWithDataAsync(AnalyticsEntitiesContext db, string upn)
        {
            var ticks = DateTime.UtcNow.Ticks;
            var seeded = new SeededUser();

            var user = new User { UserPrincipalName = upn, AzureAdId = Guid.NewGuid().ToString() };
            db.users.Add(user);
            await db.SaveChangesAsync();
            seeded.User = user;
            seeded.UserId = user.ID;

            var operation = new EventOperation { Name = "UserDataLookupIntegrationTest " + ticks };
            db.event_operations.Add(operation);
            await db.SaveChangesAsync();
            seeded.Operation = operation;

            // Two audit events (the direct-FK shape) - one of them also gets workload metadata below.
            for (var i = 0; i < 2; i++)
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

            // Audit sub-type: linked to the user only through audit_events.event_id -> audit_events.user_id.
            seeded.SharePointEvent = new SharePointEventMetadata { AuditEvent = seeded.AuditEvents[0] };
            db.sharepoint_events.Add(seeded.SharePointEvent);

            // Same join, but counted through a different user column (shared_with_user_id).
            seeded.PowerAppShare = new PowerAppShareEventMetadata { AuditEvent = seeded.AuditEvents[1], SharedWithUser = user, RoleName = "CanView" };
            db.power_app_share_events.Add(seeded.PowerAppShare);

            // Web hits: linked to the user indirectly, hits -> sessions -> users.
            seeded.Url = new Url { FullUrl = $"https://contoso.sharepoint.com/sites/test/Καλημέρα-{ticks}.pdf" };
            seeded.Session = new UserSession { user = user, ai_session_id = "userdatalookup-" + ticks };
            db.sessions.Add(seeded.Session);
            for (var i = 0; i < 3; i++)
            {
                var hit = new Hit
                {
                    hit_timestamp = DateTime.UtcNow.AddMinutes(-i),
                    page_request_id = Guid.NewGuid(),
                    session = seeded.Session,
                    url = seeded.Url,
                };
                db.hits.Add(hit);
                seeded.Hits.Add(hit);
            }

            // A daily usage-report row: a direct FK on a completely different table.
            seeded.OutlookLog = new OutlookUsageActivityLog { User = user, Date = DateTime.UtcNow.Date };
            db.OutlookUsageActivityLogs.Add(seeded.OutlookLog);

            await db.SaveChangesAsync();
            return seeded;
        }

        /// <summary>
        /// Removes everything the test added. These tests share the unit-test database with the rest of
        /// the suite, so leaving audit events or hits behind would skew anything that counts them.
        /// </summary>
        private static async Task CleanUpAsync(AnalyticsEntitiesContext db, SeededUser seeded)
        {
            db.hits.RemoveRange(seeded.Hits);
            db.OutlookUsageActivityLogs.Remove(seeded.OutlookLog);
            db.sharepoint_events.Remove(seeded.SharePointEvent);
            db.power_app_share_events.Remove(seeded.PowerAppShare);
            await db.SaveChangesAsync();

            db.sessions.Remove(seeded.Session);
            db.urls.Remove(seeded.Url);
            db.AuditEventsCommon.RemoveRange(seeded.AuditEvents);
            await db.SaveChangesAsync();

            db.users.Remove(seeded.User);
            db.event_operations.Remove(seeded.Operation);
            await db.SaveChangesAsync();
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
