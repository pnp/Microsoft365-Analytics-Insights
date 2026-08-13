using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data;
using System.Data.Entity.Migrations;
using System.Data.SqlClient;

namespace Tests.UnitTests
{
    /// <summary>
    /// End-to-end tests of the actual EF6 migration pipeline (<see cref="DbMigrator"/>, the same
    /// engine <c>DatabaseUpgrader.CheckDbUpgraded</c> / the installer use) for the two published
    /// upgrade-from states, proving each reaches <c>nvarchar(850)</c> without losing data:
    ///
    ///   * State B - previous stable release (last migration
    ///     <c>202606011010001_RemoveDataverseTables</c>, <c>full_url</c> still <c>(n)varchar(max)</c>):
    ///     a Greek URL seeded *before* the upgrade must survive intact. This is the scenario the
    ///     Greek bank in issue #122 hit; they were previously blocked by the old varchar lossy check.
    ///   * State C - current stable release (last migration
    ///     <c>202606011739254_UrlFullUrlVarcharMapping</c>, <c>full_url</c> = <c>varchar(1700)</c>):
    ///     only the new <see cref="UrlFullUrlNvarchar"/> migration runs, converting
    ///     <c>varchar(1700) -> nvarchar(850)</c> losslessly.
    ///
    /// Unlike <see cref="ShrinkUrlsFullUrlColumnMigrationTests"/> (which runs the raw SQL script),
    /// these drive <see cref="DbMigrator"/> so the model-snapshot validation runs for real - i.e.
    /// reaching the latest migration must NOT throw <c>AutomaticDataLossException</c>.
    ///
    /// Seeding / verification use a raw <see cref="SqlConnection"/> rather than
    /// <see cref="AnalyticsEntitiesContext"/> so the DEBUG auto-migrate initializer
    /// (<c>MigrateDatabaseToLatestVersion</c>) does not fire and silently jump the DB to latest
    /// before we have asserted the intermediate state.
    /// </summary>
    [TestClass]
    public class UrlFullUrlMigrationPipelineTests
    {
        private const string RemoveDataverseTablesId = "202606011010001_RemoveDataverseTables";
        private const string VarcharMappingId = "202606011739254_UrlFullUrlVarcharMapping";
        // The most recent migration - updated whenever a newer one is added, since these pipeline tests
        // assert the DB reaches the true latest migration after MigrateToLatest(). UrlFullUrlNvarchar (which
        // performs the urls.full_url nvarchar(850) conversion asserted below) still runs as part of the path;
        // IndexAuditEventsTimeStamp, IndexSitesSiteId, CoverCopilotAccessedResourceDedup and
        // IndexUsageReportSnapshots are later, unrelated schema-only index migrations.
        private const string LatestId = "202608131055001_IndexReportDateQueries";
        private const string IndexName = "IX_urls_full_url";

        // "Καλημέρα κόσμε" - the classic Greek charset sample (synthetic; no customer data).
        private const string GreekUrl =
            "https://contoso.sharepoint.com/sites/example/Shared Documents/" +
            "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5.pdf";

        private static string _connStr;
        private static string ConnStr()
        {
            if (_connStr == null)
            {
                // Construct-only (no query) so the auto-migrate initializer is not triggered.
                using (var db = new AnalyticsEntitiesContext())
                    _connStr = db.Database.Connection.ConnectionString;
            }
            return _connStr;
        }

        private static void ExecRaw(string sql)
        {
            using (var c = new SqlConnection(ConnStr()))
            {
                c.Open();
                using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 })
                    cmd.ExecuteNonQuery();
            }
        }

        private static T ScalarRaw<T>(string sql)
        {
            using (var c = new SqlConnection(ConnStr()))
            {
                c.Open();
                using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 })
                {
                    var o = cmd.ExecuteScalar();
                    return (o == null || o == DBNull.Value) ? default(T) : (T)o;
                }
            }
        }

        private static int InsertUrlRaw(string url)
        {
            using (var c = new SqlConnection(ConnStr()))
            {
                c.Open();
                using (var cmd = new SqlCommand(
                    "INSERT INTO dbo.urls (full_url) OUTPUT INSERTED.id VALUES (@u)", c))
                {
                    // Explicit NVarChar so the value is sent as Unicode (preserves Greek when the
                    // target column is (n)varchar(max)).
                    cmd.Parameters.Add(new SqlParameter("@u", SqlDbType.NVarChar, -1) { Value = url });
                    return (int)cmd.ExecuteScalar();
                }
            }
        }

        private static string GetUrlRaw(int id)
        {
            using (var c = new SqlConnection(ConnStr()))
            {
                c.Open();
                using (var cmd = new SqlCommand("SELECT full_url FROM dbo.urls WHERE id = @id", c))
                {
                    cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                    return (string)cmd.ExecuteScalar();
                }
            }
        }

        private static void DeleteUrlRaw(int id)
        {
            using (var c = new SqlConnection(ConnStr()))
            {
                c.Open();
                using (var cmd = new SqlCommand("DELETE FROM dbo.urls WHERE id = @id", c))
                {
                    cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static string LastMigrationId()
            => ScalarRaw<string>("SELECT TOP 1 MigrationId FROM dbo.__MigrationHistory ORDER BY LEFT(MigrationId, 15) DESC");

        /// <summary>True when full_url is exactly nvarchar(850) (= 1700 bytes).</summary>
        private static bool ColumnIsNvarchar850()
            => ScalarRaw<int>(
                @"SELECT COUNT(*) FROM sys.columns c
                  INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
                  WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url'
                    AND t.name = N'nvarchar' AND c.max_length = 1700") > 0;

        /// <summary>Human-readable current full_url type, for assertion messages.</summary>
        private static string ColumnType()
            => ScalarRaw<string>(
                @"SELECT t.name + N'(' +
                    CASE WHEN c.max_length = -1 THEN N'max'
                         WHEN t.name LIKE N'n%' THEN CAST(c.max_length / 2 AS nvarchar(10))
                         ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
                  FROM sys.columns c
                  INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
                  WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url'");

        private static bool IndexExists()
            => ScalarRaw<int>(
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'" + IndexName + "'") > 0;

        private static void MigrateTo(string migrationId) => new DbMigrator(new Configuration()).Update(migrationId);
        private static void MigrateToLatest() => new DbMigrator(new Configuration()).Update();

        /// <summary>
        /// State B: a database whose last migration is <c>RemoveDataverseTables</c> (the previous
        /// stable release), with a Greek URL already stored, upgrades all the way to the latest fix
        /// without corrupting it. This is the exact scenario from issue #122.
        /// </summary>
        [TestMethod]
        public void StateB_RemoveDataverseTables_UpgradesToLatest_PreservingGreekUrl()
        {
            int seededId = -1;
            try
            {
                // 1. Put the DB in State B: history at RemoveDataverseTables, full_url (n)varchar(max).
                MigrateTo(RemoveDataverseTablesId);
                Assert.AreEqual(RemoveDataverseTablesId, LastMigrationId(),
                    "Pre-condition: DB should be sitting at RemoveDataverseTables (State B).");
                Assert.IsFalse(ColumnIsNvarchar850(),
                    "Pre-condition: full_url should still be (n)varchar(max), not nvarchar(850). Actual: " + ColumnType());
                Assert.IsFalse(IndexExists(), "Pre-condition: IX_urls_full_url should not exist yet.");

                // 2. Seed a Greek URL while still on the old schema, like a real customer's data.
                seededId = InsertUrlRaw(GreekUrl);
                Assert.AreEqual(GreekUrl, GetUrlRaw(seededId),
                    "Sanity: the Greek URL is stored intact in the (n)varchar(max) column.");

                // 3. Upgrade to latest - exactly what DatabaseUpgrader.CheckDbUpgraded / the installer runs.
                //    Must NOT throw AutomaticDataLossException (the model snapshot must match).
                MigrateToLatest();

                // 4. Verify the upgrade completed and is lossless.
                Assert.AreEqual(LatestId, LastMigrationId(),
                    "DB should now be at the latest migration.");
                Assert.IsTrue(ColumnIsNvarchar850(),
                    "full_url should now be nvarchar(850). Actual: " + ColumnType());
                Assert.IsTrue(IndexExists(), "IX_urls_full_url should exist after the upgrade.");
                Assert.AreEqual(GreekUrl, GetUrlRaw(seededId),
                    "The Greek URL must survive the RemoveDataverseTables -> latest upgrade byte-for-byte.");
            }
            finally
            {
                if (seededId > 0) { try { DeleteUrlRaw(seededId); } catch { /* best effort */ } }
                MigrateToLatest(); // leave the shared DB fully migrated for other tests
            }
        }

        /// <summary>
        /// State C: a database on the current stable release (<c>full_url</c> already
        /// <c>varchar(1700)</c>, history at <c>UrlFullUrlVarcharMapping</c>) upgrades via the new
        /// <see cref="UrlFullUrlNvarchar"/> migration, converting <c>varchar(1700) -> nvarchar(850)</c>
        /// without losing data. (An ASCII URL is used: on a Latin test collation varchar can't hold
        /// Greek anyway, and any DB that legitimately reached State C only contains representable
        /// data - see the PR description.)
        /// </summary>
        [TestMethod]
        public void StateC_VarcharMapping_UpgradesToLatest_PreservingData()
        {
            const string asciiUrl = "https://contoso.sharepoint.com/sites/example/Shared Documents/state-c.docx";
            int seededId = -1;
            try
            {
                // 1. History at VarcharMapping...
                MigrateTo(VarcharMappingId);
                Assert.AreEqual(VarcharMappingId, LastMigrationId(),
                    "Pre-condition: DB should be at UrlFullUrlVarcharMapping.");

                // ...then force the column into the *old published* shape (varchar(1700) + index),
                // which this release actually produced but the corrected ShrinkUrlsFullUrlColumn no
                // longer creates. This faithfully reproduces a State C customer's on-disk schema.
                ExecRaw(
                    @"IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'" + IndexName + @"')
                          DROP INDEX [" + IndexName + @"] ON [dbo].[urls];
                      ALTER TABLE [dbo].[urls] ALTER COLUMN [full_url] varchar(1700) NOT NULL;
                      CREATE NONCLUSTERED INDEX [" + IndexName + @"] ON [dbo].[urls] ([full_url]);");
                Assert.IsFalse(ColumnIsNvarchar850(),
                    "Pre-condition: full_url should be varchar(1700) (State C), not nvarchar(850). Actual: " + ColumnType());
                Assert.IsTrue(IndexExists(), "Pre-condition: State C already has IX_urls_full_url.");

                // 2. Seed a URL while on the varchar(1700) schema.
                seededId = InsertUrlRaw(asciiUrl);

                // 3. Upgrade to latest - only UrlFullUrlNvarchar is pending; it converts varchar(1700) -> nvarchar(850).
                MigrateToLatest();

                // 4. Verify.
                Assert.AreEqual(LatestId, LastMigrationId(), "DB should now be at the latest migration.");
                Assert.IsTrue(ColumnIsNvarchar850(),
                    "full_url should now be nvarchar(850). Actual: " + ColumnType());
                Assert.IsTrue(IndexExists(), "IX_urls_full_url should still exist after the conversion.");
                Assert.AreEqual(asciiUrl, GetUrlRaw(seededId),
                    "The URL must survive the varchar(1700) -> nvarchar(850) upgrade.");
            }
            finally
            {
                if (seededId > 0) { try { DeleteUrlRaw(seededId); } catch { /* best effort */ } }
                MigrateToLatest(); // leave the shared DB fully migrated for other tests
            }
        }

        /// <summary>
        /// State C, Greek-collation case: a Greek customer who reached the current stable release
        /// stored Greek in <c>varchar(1700)</c> faithfully (their DB's Greek code page, CP1253).
        /// Converting that column to <c>nvarchar(850)</c> - exactly what <see cref="UrlFullUrlNvarchar"/>
        /// does - must keep the Greek byte-for-byte. Uses a self-contained temp table with an
        /// explicit <c>Greek_CI_AS</c> collation so the result is independent of the test database's
        /// own (Latin) collation. This is the permanent CI proof that the State C upgrade cannot
        /// corrupt Greek that was representable to begin with.
        /// </summary>
        [TestMethod]
        public void StateC_VarcharGreekCollation_ConvertsToNvarchar850_PreservingGreekUrl()
        {
            using (var c = new SqlConnection(ConnStr()))
            {
                c.Open();

                void Exec(string sql, string val = null)
                {
                    using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 })
                    {
                        if (val != null)
                            cmd.Parameters.Add(new SqlParameter("@u", SqlDbType.NVarChar, -1) { Value = val });
                        cmd.ExecuteNonQuery();
                    }
                }
                string Read()
                {
                    using (var cmd = new SqlCommand("SELECT full_url FROM #c", c))
                        return (string)cmd.ExecuteScalar();
                }

                // varchar(1700) under a Greek collation = how a Greek-collation State C DB holds full_url.
                Exec(@"IF OBJECT_ID('tempdb..#c') IS NOT NULL DROP TABLE #c;
                       CREATE TABLE #c (full_url varchar(1700) COLLATE Greek_CI_AS NOT NULL);");
                Exec("INSERT INTO #c (full_url) VALUES (@u);", GreekUrl);

                Assert.AreEqual(GreekUrl, Read(),
                    "Sanity: Greek is stored intact in varchar(1700) under a Greek collation (CP1253).");

                // The conversion UrlFullUrlNvarchar performs.
                Exec("ALTER TABLE #c ALTER COLUMN full_url nvarchar(850) NOT NULL;");

                Assert.AreEqual(GreekUrl, Read(),
                    "The Greek URL must survive varchar(1700) [Greek_CI_AS] -> nvarchar(850) byte-for-byte.");
            }
        }
    }
}
