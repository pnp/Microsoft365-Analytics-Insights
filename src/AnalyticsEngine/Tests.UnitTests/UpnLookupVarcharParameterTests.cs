extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using Common.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure.Interception;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.AgentCosts;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Every per-user lookup by UPN must send its UPN as a <c>varchar</c> parameter (#713).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>dbo.users.user_name</c> is <c>varchar(250)</c> by design, but the EF model leaves
    /// <c>User.UserPrincipalName</c> as a Unicode string, so a plain <c>u.UserPrincipalName == upn</c> sends an
    /// <c>nvarchar(4000)</c> parameter. <c>nvarchar</c> has the higher precedence, so SQL Server converts the
    /// COLUMN, and under a SQL collation (the Azure SQL Database default) that turns every lookup into a scan of
    /// <c>IX_users</c>. The lookups wrap the value in <c>DbFunctions.AsNonUnicode</c>, and these tests read the
    /// parameter EF actually hands the provider, so dropping the wrapper fails here rather than in a
    /// customer's CPU graph.
    /// </para>
    /// <para>
    /// The UPNs are ASCII because Entra restricts <c>userPrincipalName</c> to ASCII; what a non-ASCII input does is
    /// a storage question, recorded in <c>UpnLookupBenchmarkTests</c> and in the PR, not asserted here.
    /// </para>
    /// </remarks>
    [TestClass]
    public class UpnLookupVarcharParameterTests
    {
        [TestMethod]
        public async Task UserCacheLoad_SendsTheUpnAsAVarcharParameter()
        {
            var upn = $"varchar.{Guid.NewGuid():N}@contoso.com";
            var recorder = new UserNameComparisonRecorder();
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                DbInterception.Add(recorder);
                try
                {
                    Assert.IsNull(await new UserCache(db).Load(upn), "Nobody has this UPN.");
                }
                finally
                {
                    DbInterception.Remove(recorder);
                }
            }

            AssertEveryComparisonIsVarchar(recorder, upn, expectedCount: 1);
        }

        /// <summary>
        /// Changing the parameter type must not change who is found. Every key is looked up twice on the same
        /// rows - through <see cref="UserCache.Load"/>, and through the query it used to be (a plain
        /// <c>nvarchar</c> parameter) - and both must land on the same user: different case, a trailing space on
        /// either side, duplicates (lowest id wins) and a UPN nobody has.
        /// </summary>
        /// <remarks>
        /// Production has a unique index on <c>user_name</c>, so the duplicates are inserted inside a transaction
        /// that drops it and is rolled back: nothing is left behind in the shared test database.
        /// </remarks>
        [TestMethod]
        public async Task UserCacheLoad_FindsTheSameUserAsTheNvarcharQueryDid()
        {
            var recorder = new UserNameComparisonRecorder();
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                await db.Database.Connection.OpenAsync();
                using (var transaction = db.Database.BeginTransaction())
                {
                    await DropUniqueUserNameIndexAsync(db);

                    var token = Guid.NewGuid().ToString("N").Substring(0, 12);
                    var mixedId = await InsertUserAsync(db, $"Mixed.{token}@Contoso.com");
                    var lowestDuplicateId = await InsertUserAsync(db, $"DUPE.{token}@CONTOSO.COM");
                    await InsertUserAsync(db, $"dupe.{token}@contoso.com");
                    await InsertUserAsync(db, $"Dupe.{token}@Contoso.com");
                    var storedWithTrailingSpaceId = await InsertUserAsync(db, $"trailing.{token}@contoso.com ");

                    var expected = new Dictionary<string, int?>
                    {
                        [$"Mixed.{token}@Contoso.com"] = mixedId,
                        [$"mixed.{token}@contoso.com"] = mixedId,
                        [$"MIXED.{token}@CONTOSO.COM"] = mixedId,
                        [$"mixed.{token}@contoso.com  "] = mixedId,
                        [$"dupe.{token}@contoso.com"] = lowestDuplicateId,
                        [$"trailing.{token}@contoso.com"] = storedWithTrailingSpaceId,
                        [$"missing.{token}@contoso.com"] = null,
                    };

                    DbInterception.Add(recorder);
                    try
                    {
                        foreach (var key in expected.Keys)
                        {
                            var viaLoad = await new UserCache(db).Load(key);
                            var viaNvarcharParameter = await db.users.Where(u => u.UserPrincipalName == key).OrderBy(u => u.ID).FirstOrDefaultAsync();

                            Assert.AreEqual(viaNvarcharParameter?.ID, viaLoad?.ID, $"'{key}' must resolve to the same user as the nvarchar query did.");
                            Assert.AreEqual(expected[key], viaLoad?.ID, $"'{key}' resolved to the wrong user.");
                        }
                    }
                    finally
                    {
                        DbInterception.Remove(recorder);
                        transaction.Rollback();
                    }

                    foreach (var key in expected.Keys)
                    {
                        var sent = recorder.For(key);
                        Assert.AreEqual(2, sent.Count, $"'{key}' should have been compared with user_name twice: once by Load, once by the nvarchar query.");
                        Assert.AreEqual(1, sent.Count(c => c.DbType == DbType.AnsiString), $"Load must send '{key}' as varchar: {string.Join(", ", sent)}");
                        Assert.AreEqual(1, sent.Count(c => c.DbType == DbType.String), $"The comparison query is the nvarchar one Load used to send: {string.Join(", ", sent)}");
                    }
                }
            }
        }

        /// <summary>
        /// The manager fallback in <c>UserDataMapper</c> - reached when the manager is not found by object id -
        /// and <c>EnsureUserIsTrackedAsync</c>, which resolves an unsaved template entity by its UPN.
        /// </summary>
        [TestMethod]
        public async Task UserDataMapper_ManagerResolvedByUpn_SendsTheUpnAsVarchar()
        {
            var token = Guid.NewGuid().ToString("N");
            var managerUpn = $"mapper.manager.{token}@contoso.com";
            var managerAadId = Guid.NewGuid().ToString();
            var managerId = await SeedUserAsync(managerUpn, azureAdId: managerAadId);
            try
            {
                var managerGraph = new GraphUser { Id = managerAadId, UserPrincipalName = managerUpn, AccountEnabled = true };
                var reportGraph = new GraphUser
                {
                    Id = Guid.NewGuid().ToString(),
                    UserPrincipalName = $"mapper.report.{token}@contoso.com",
                    AccountEnabled = true,
                    ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerAadId } },
                };
                var graphUsers = new List<GraphUser> { managerGraph, reportGraph };

                // 1) Not in the object-id dictionary: the per-user fallback query by UPN.
                var fallback = new UserNameComparisonRecorder();
                using (var db = new AnalyticsEntitiesContext())
                {
                    var report = new User { UserPrincipalName = reportGraph.UserPrincipalName };
                    var mapper = new UserDataMapper(AnalyticsLogger.ConsoleOnlyTracer(), new UserMetadataCache(db));
                    mapper.SetGraphUserLookup(graphUsers);

                    DbInterception.Add(fallback);
                    try
                    {
                        await mapper.UpdateUserMetadata(db, reportGraph, graphUsers, report,
                            new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase), new List<User>());
                    }
                    finally
                    {
                        DbInterception.Remove(fallback);
                    }

                    Assert.AreEqual(managerId, report.Manager?.ID, "The manager must still be found by UPN.");
                }
                AssertEveryComparisonIsVarchar(fallback, managerUpn, expectedCount: 1);

                // 2) In the dictionary, but as an unsaved template (ID 0): EnsureUserIsTrackedAsync looks it up by UPN.
                var tracked = new UserNameComparisonRecorder();
                using (var db = new AnalyticsEntitiesContext())
                {
                    var report = new User { UserPrincipalName = reportGraph.UserPrincipalName };
                    var mapper = new UserDataMapper(AnalyticsLogger.ConsoleOnlyTracer(), new UserMetadataCache(db));
                    mapper.SetGraphUserLookup(graphUsers);
                    var byAadId = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase)
                    {
                        [managerAadId] = new User { UserPrincipalName = managerUpn.ToUpperInvariant() },
                    };

                    DbInterception.Add(tracked);
                    try
                    {
                        await mapper.UpdateUserMetadata(db, reportGraph, graphUsers, report, byAadId, new List<User>());
                    }
                    finally
                    {
                        DbInterception.Remove(tracked);
                    }

                    Assert.AreEqual(managerId, report.Manager?.ID, "The template must resolve to the stored manager, whatever the case of its UPN.");
                }
                AssertEveryComparisonIsVarchar(tracked, managerUpn.ToUpperInvariant(), expectedCount: 1);
            }
            finally
            {
                await DeleteUserAsync(managerId);
            }
        }

        /// <summary>The admin user-data lookup resolves the user twice: the profile, and the id for a drill-down.</summary>
        [TestMethod]
        public async Task UserDataLookup_ProfileAndUserId_SendTheUpnAsVarchar()
        {
            var upn = $"userdatalookup.varchar.{Guid.NewGuid():N}@contoso.com";
            var userId = await SeedUserAsync(upn);
            var recorder = new UserNameComparisonRecorder();
            try
            {
                var query = new SqlUserDataLookupQuery();
                DbInterception.Add(recorder);
                try
                {
                    Assert.AreEqual(userId, await query.GetUserIdAsync(upn));
                    Assert.IsNotNull(await query.GetProfileAsync(upn), "The profile must still be found.");
                    Assert.AreEqual(userId, await query.GetUserIdAsync(upn.ToUpperInvariant()), "Matching stays case-insensitive.");
                }
                finally
                {
                    DbInterception.Remove(recorder);
                }
            }
            finally
            {
                await DeleteUserAsync(userId);
            }

            AssertEveryComparisonIsVarchar(recorder, upn, expectedCount: 2);
            AssertEveryComparisonIsVarchar(recorder, upn.ToUpperInvariant(), expectedCount: 1);
        }

        /// <summary>Agent costs add a billed user who is not in the database yet, matching on UPN first.</summary>
        [TestMethod]
        public async Task AgentCostUserLinkStore_EnsureUser_SendsTheUpnAsVarchar()
        {
            var upn = $"agentcost.varchar.{Guid.NewGuid():N}@contoso.com";
            var userId = await SeedUserAsync(upn);
            var recorder = new UserNameComparisonRecorder();
            try
            {
                var store = new SqlAgentCostUserLinkStore(DefaultAnalyticsDbContextFactory.Instance, NullLogger.Instance);
                int ensured;
                DbInterception.Add(recorder);
                try
                {
                    ensured = await store.EnsureUserAsync(new EntraUserRef { ObjectId = Guid.NewGuid().ToString(), UserPrincipalName = upn.ToUpperInvariant() });
                }
                finally
                {
                    DbInterception.Remove(recorder);
                }

                Assert.AreEqual(userId, ensured, "An existing user must be matched on UPN, case-insensitively, not added again.");
            }
            finally
            {
                await DeleteUserAsync(userId);
            }

            AssertEveryComparisonIsVarchar(recorder, upn.ToUpperInvariant(), expectedCount: 1);
        }

        #region Helpers

        private static void AssertEveryComparisonIsVarchar(UserNameComparisonRecorder recorder, string upn, int expectedCount)
        {
            var sent = recorder.For(upn);
            Assert.AreEqual(expectedCount, sent.Count, $"Expected {expectedCount} comparison(s) of user_name with '{upn}', saw: {string.Join(", ", sent)}");
            foreach (var comparison in sent)
            {
                Assert.AreEqual(DbType.AnsiString, comparison.DbType,
                    $"'{upn}' went to SQL Server as {comparison}: an nvarchar parameter makes SQL Server convert the varchar user_name column, "
                    + "which cannot seek IX_users under a SQL collation. Wrap the value in DbFunctions.AsNonUnicode (#713).");
            }
        }

        /// <summary>
        /// Drops whatever unique index or constraint is on <c>user_name</c>, so duplicates can be inserted. Only ever
        /// called inside a transaction that is rolled back.
        /// </summary>
        internal static Task DropUniqueUserNameIndexAsync(AnalyticsEntitiesContext db)
            => db.Database.ExecuteSqlCommandAsync(@"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += CASE WHEN i.is_unique_constraint = 1
        THEN N'ALTER TABLE dbo.users DROP CONSTRAINT ' + QUOTENAME(i.name) + N';'
        ELSE N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.users;' END
FROM sys.indexes AS i
WHERE i.object_id = OBJECT_ID('dbo.users') AND i.is_unique = 1 AND i.is_primary_key = 0
  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = 'user_name');
EXEC sp_executesql @sql;");

        private static async Task<int> InsertUserAsync(AnalyticsEntitiesContext db, string upn)
            => await db.Database.SqlQuery<int>("INSERT INTO dbo.users (user_name) OUTPUT INSERTED.id VALUES (@p0)", upn).SingleAsync();

        private static async Task<int> SeedUserAsync(string upn, string azureAdId = "")
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { UserPrincipalName = upn, AzureAdId = azureAdId };
                db.users.Add(user);
                await db.SaveChangesAsync();
                return user.ID;
            }
        }

        private static async Task DeleteUserAsync(int id)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.users WHERE id = @p0", id);
            }
        }

        #endregion

        /// <summary>
        /// Records every parameter EF compares with <c>user_name</c>, with the type it was actually handed to the
        /// provider as. Registered process-wide by <see cref="DbInterception"/>, so add it only around the calls
        /// being checked (this project does not run tests in parallel) and look results up by the UPN sent.
        /// </summary>
        internal sealed class UserNameComparisonRecorder : IDbCommandInterceptor
        {
            private static readonly Regex ComparedWithUserName = new Regex(
                @"\[user_name\]\s*=\s*@(?<name>\w+)|@(?<name>\w+)\s*=\s*(?:\[\w+\]\.)?\[user_name\]",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            private readonly object _lock = new object();
            private readonly List<UserNameComparison> _comparisons = new List<UserNameComparison>();

            public IReadOnlyList<UserNameComparison> For(string upn)
            {
                lock (_lock)
                {
                    return _comparisons.Where(c => string.Equals(c.Value as string, upn, StringComparison.Ordinal)).ToList();
                }
            }

            public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) => Record(command);
            public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) => Record(command);
            public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) => Record(command);
            public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) { }
            public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) { }
            public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) { }

            private void Record(DbCommand command)
            {
                var names = ComparedWithUserName.Matches(command.CommandText ?? string.Empty).Cast<Match>()
                    .Select(m => m.Groups["name"].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var parameter = command.Parameters.Cast<DbParameter>()
                        .FirstOrDefault(p => string.Equals(p.ParameterName.TrimStart('@'), name, StringComparison.OrdinalIgnoreCase));
                    if (parameter == null)
                    {
                        continue;
                    }

                    lock (_lock)
                    {
                        _comparisons.Add(new UserNameComparison(name, parameter.DbType, parameter.Size, parameter.Value));
                    }
                }
            }
        }

        internal sealed class UserNameComparison
        {
            public UserNameComparison(string parameterName, DbType dbType, int size, object value)
            {
                ParameterName = parameterName;
                DbType = dbType;
                Size = size;
                Value = value;
            }

            public string ParameterName { get; }
            public DbType DbType { get; }
            public int Size { get; }
            public object Value { get; }

            public override string ToString() => $"@{ParameterName} {DbType}({Size})";
        }
    }
}
