using Common.Entities;
using Common.Entities.LookupCaches;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure.Interception;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    [TestClass]
    public class UpnBatchLookupTests
    {
        [TestMethod]
        public async Task SharedResolver_UsesColumnCollationAndDatabaseScopedCache_WithPositionalAnswers()
        {
            // All databases have the same default collation. Only the COLUMN differs.
            foreach (var collation in new[] { "SQL_Latin1_General_CP1_CI_AS", "Latin1_General_CI_AS", "Latin1_General_CS_AS" })
            {
                using (var fixture = await Fixture.CreateAsync(collation))
                {
                    var recorder = new ParameterRecorder();
                    DbInterception.Add(recorder);
                    try
                    {
                        var names = new[] { "mixed@contoso.com", "MIXED@CONTOSO.COM", null, "", "trailing@contoso.com", "missing@contoso.com", "mixed@contoso.com", "strasse@contoso.com", new string('x', 5001) };
                        var insensitive = !collation.Contains("_CS_");
                        var expected = new int?[] { 1, insensitive ? (int?)1 : null, null, 3, 4, null, 1, collation.StartsWith("SQL_") ? (int?)6 : 5, null };
                        using (var db = new FixtureContext(fixture.ConnectionString))
                        {
                            CollectionAssert.AreEqual(expected, await ExistingUserIds.FindAsync(db, names));
                            var dictionary = await new UserCache(db).LoadExistingIdsAsync(names);
                            Assert.AreEqual(1, dictionary[names[0]]);
                            Assert.AreEqual(3, dictionary[""], "Empty is a real SQL value; null never matches.");
                            Assert.IsFalse(dictionary.ContainsKey(names[5]));
                            Assert.IsFalse(dictionary.ContainsKey(names[8]), "A long parameter must not be truncated to match another name.");
                            var users = await new SqlUserLookupStore(db).GetUsersByUpnAsync(new[] { names[0], names[0], null, names[5] });
                            Assert.AreEqual(1, users.Count, "Duplicate input slots become one tracked entity per chunk.");
                            Assert.AreEqual(1, users[0].LicenseLookups.Count, "Manager prefetch must keep its licence graph.");
                            Assert.AreEqual(EntityState.Unchanged, db.Entry(users[0]).State);
                            users[0].Mail = "updated@contoso.com";
                            await db.SaveChangesAsync();
                        }
                        var type = collation.StartsWith("SQL_") ? SqlDbType.VarChar : SqlDbType.NVarChar;
                        Assert.IsTrue(recorder.Types.Count > 0);
                        Assert.IsTrue(recorder.Types.All(t => t == type), "The column, not the database default or previous database, chooses the parameter type.");
                        Assert.AreEqual(1, recorder.Probes, "Both callers share one successful probe for this database.");

                        using (var connection = new SqlConnection(fixture.ConnectionString))
                        {
                            await connection.OpenAsync();
                            CollectionAssert.AreEqual(expected, await ExistingUserIds.FindAsync(connection, names));
                            var large = Enumerable.Repeat("mixed@contoso.com", 1001).ToList();
                            Assert.IsTrue((await ExistingUserIds.FindAsync(connection, large)).All(id => id == 1), "Slots survive chunk boundaries and duplicate inputs.");
                            using (var command = new SqlCommand("SELECT mail FROM dbo.users WHERE id = 1", connection))
                                Assert.AreEqual("updated@contoso.com", await command.ExecuteScalarAsync(), "Tracked changes persist, not just row counts.");
                        }
                    }
                    finally { DbInterception.Remove(recorder); }
                }
            }
        }

        [TestMethod]
        public void QueryBuilder_PreservesLongValuesAndRejectsUnknownCollation()
        {
            foreach (var type in new[] { SqlDbType.VarChar, SqlDbType.NVarChar })
            {
                var query = ExistingUserIds.BuildQuery(new[] { null, "", new string('x', 9000) }, 0, 3, type);
                Assert.AreEqual(2, query.Parameters.Length);
                Assert.AreEqual(-1, query.Parameters[1].Size);
                Assert.IsTrue(query.Parameters.All(p => p.SqlDbType == type));
                StringAssert.Contains(query.Sql, "(1,@n1),(2,@n2)");
            }
            Assert.ThrowsException<InvalidOperationException>(() => ExistingUserIds.TypeForCollation(null));
        }

        private sealed class ParameterRecorder : DbCommandInterceptor
        {
            public readonly List<SqlDbType> Types = new List<SqlDbType>();
            public int Probes;
            public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> context)
            {
                if (command.CommandText.Contains("SELECT collation_name FROM sys.columns")) Probes++;
                if (command.CommandText.Contains("GROUP BY k.slot"))
                    foreach (SqlParameter parameter in command.Parameters) Types.Add(parameter.SqlDbType);
            }
        }

        internal sealed class FixtureContext : AnalyticsEntitiesContext
        {
            static FixtureContext() { Database.SetInitializer<FixtureContext>(null); }
            public FixtureContext(string connectionString) : base(connectionString, true, false) { }
        }

        internal sealed class Fixture : IDisposable
        {
            private readonly string _master;
            private readonly string _catalog;
            public string ConnectionString { get; }
            private Fixture(string master, string catalog, string connectionString)
            {
                _master = master;
                _catalog = catalog;
                ConnectionString = connectionString;
            }
            public static async Task<Fixture> CreateAsync(string collation, bool benchmark = false)
            {
                var builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings["SPOInsightsEntities"].ConnectionString);
                var catalog = "SyntheticUpn713_" + Guid.NewGuid().ToString("N");
                builder.InitialCatalog = "master";
                var master = builder.ConnectionString;
                using (var connection = new SqlConnection(master))
                {
                    await connection.OpenAsync();
                    using (var command = new SqlCommand("CREATE DATABASE [" + catalog + "] COLLATE SQL_Latin1_General_CP1_CI_AS", connection))
                        await command.ExecuteNonQueryAsync();
                }
                builder.InitialCatalog = catalog;
                var fixture = new Fixture(master, catalog, builder.ConnectionString);
                try
                {
                    using (var connection = new SqlConnection(fixture.ConnectionString))
                    {
                        await connection.OpenAsync();
                        using (var command = new SqlCommand(
                            "CREATE TABLE dbo.users (id int IDENTITY PRIMARY KEY, user_name varchar(250) COLLATE " + collation + @" NOT NULL,
org_id int NULL, created_utc datetime2 NULL, mail nvarchar(max) NULL, last_updated datetime NULL, azure_ad_id nvarchar(max) NULL,
account_enabled bit NULL, postalcode nvarchar(50) NULL, company_name_id int NULL, state_or_province_id int NULL, manager_id int NULL,
country_or_region_id int NULL, office_location_id int NULL, usage_location_id int NULL, department_id int NULL, job_title_id int NULL);
CREATE TABLE dbo.user_license_type_lookups (id int IDENTITY PRIMARY KEY, user_id int NOT NULL, license_type_id int NOT NULL);
CREATE UNIQUE INDEX IX_user_license_type_lookups ON dbo.user_license_type_lookups(license_type_id, user_id);
" + (benchmark ? @"
WITH digits AS (SELECT v FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS t(v)),
numbers AS (SELECT TOP(200000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS n FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e CROSS JOIN digits f)
INSERT dbo.users(user_name,mail,azure_ad_id,account_enabled,postalcode,last_updated)
SELECT 'lookupbench' + RIGHT('0000000' + CAST(n AS varchar(7)),7) + '@contoso.com',
       'lookupbench' + RIGHT('0000000' + CAST(n AS varchar(7)),7) + '@contoso.com',
       '00000000-0000-0000-0000-000000000000', 1, N'00000', '20000101' FROM numbers;
CREATE UNIQUE INDEX IX_users ON dbo.users(user_name);
UPDATE STATISTICS dbo.users WITH FULLSCAN;
" : @"
CREATE INDEX IX_users ON dbo.users(user_name);
INSERT dbo.users(user_name) VALUES ('mixed@contoso.com'),('mixed@contoso.com'),(''),('trailing@contoso.com '),('straße@contoso.com'),('strasse@contoso.com');
INSERT dbo.user_license_type_lookups(user_id,license_type_id) VALUES(1,999);
"), connection))
                        {
                            command.CommandTimeout = 600;
                            await command.ExecuteNonQueryAsync();
                        }
                    }
                    return fixture;
                }
                catch { fixture.Dispose(); throw; }
            }
            public void Dispose()
            {
                using (var connection = new SqlConnection(_master))
                {
                    connection.Open();
                    using (var command = new SqlCommand("ALTER DATABASE [" + _catalog + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + _catalog + "]", connection))
                        command.ExecuteNonQuery();
                }
            }
        }
    }
}
