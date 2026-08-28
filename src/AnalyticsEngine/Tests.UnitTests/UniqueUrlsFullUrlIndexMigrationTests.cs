using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Exercises the raw SQL of <see cref="UniqueUrlsFullUrlIndex"/> (issue #167) against LocalDB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <see cref="UniqueUrlsFullUrlIndex.Up_Sql"/> - the exact constant the migration and the manual
    /// upgrade script both use - so the tests cannot drift from what actually ships.
    /// </para>
    /// <para>
    /// The interesting cases are not "does it delete duplicates". They are the ones that would break an
    /// upgrade for exactly the customers this migration exists for: child rows that collide on their OWN
    /// unique index once repointed, the legacy non-FK reference, non-ASCII URLs, and re-running an already
    /// applied migration.
    /// </para>
    /// </remarks>
    [TestClass]
    public class UniqueUrlsFullUrlIndexMigrationTests
    {
        private const string IndexName = "IX_urls_full_url";

        // "Καλημέρα κόσμε" - the classic Greek charset sample (synthetic; no customer data). A varchar
        // column would corrupt this to '?', and the de-duplication must not mangle it either.
        private const string GreekUrl =
            "https://contoso.sharepoint.com/sites/example/Shared Documents/" +
            "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5.pdf";

        private const string PlainUrl = "https://contoso.sharepoint.com/sites/example/Doc.docx";

        private static Task<int> ExecAsync(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, sql);
        }

        private static Task<T> ScalarAsync<T>(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.SqlQuery<T>(sql).FirstAsync();
        }

        /// <summary>Forces EF initialisation so the schema exists before the tests manipulate it.</summary>
        private static async Task EnsureSchemaAsync(AnalyticsEntitiesContext db)
        {
            await db.urls.Take(1).ToListAsync();
        }

        /// <summary>
        /// Puts <c>IX_urls_full_url</c> back into its pre-migration NON-unique shape so a test can run the
        /// migration from scratch, and clears any rows left by an earlier run.
        /// </summary>
        private static async Task ResetToPreMigrationStateAsync(AnalyticsEntitiesContext db)
        {
            await ExecAsync(db, $@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}')
    DROP INDEX [{IndexName}] ON [dbo].[urls];

DELETE FROM dbo.file_metadata_property_values WHERE url_id IN (SELECT id FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%');
DELETE FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%';

CREATE NONCLUSTERED INDEX [{IndexName}] ON [dbo].[urls] ([full_url]);");
        }

        private static Task<int> InsertUrlAsync(AnalyticsEntitiesContext db, string url)
        {
            return ScalarAsync<int>(db,
                $"INSERT INTO dbo.urls (full_url) OUTPUT INSERTED.id VALUES (N'{url.Replace("'", "''")}')");
        }

        private static Task<bool> IndexIsUniqueAsync(AnalyticsEntitiesContext db)
        {
            return ScalarAsync<int>(db,
                $@"SELECT COUNT(*) FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}' AND is_unique = 1")
                .ContinueWith(t => t.Result > 0);
        }

        private static Task<bool> IndexIgnoresDuplicateKeysAsync(AnalyticsEntitiesContext db)
        {
            return ScalarAsync<int>(db,
                $@"SELECT COUNT(*) FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}' AND ignore_dup_key = 1")
                .ContinueWith(t => t.Result > 0);
        }

        [TestMethod]
        public async Task Migration_RemovesDuplicates_AndMakesTheIndexUnique()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);

                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var remaining = await ScalarAsync<int>(db,
                    $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(1, remaining, "Exactly one row per distinct full_url must survive.");

                var survivorId = await ScalarAsync<int>(db,
                    $"SELECT id FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(keepId, survivorId, "The lowest id is the canonical survivor.");

                Assert.IsTrue(await IndexIsUniqueAsync(db), "IX_urls_full_url must be UNIQUE afterwards.");
                Assert.IsTrue(await IndexIgnoresDuplicateKeysAsync(db),
                    "IGNORE_DUP_KEY must be ON so a concurrent importer race skips the duplicate row instead of "
                    + "aborting the whole INSERT statement.");
            }
        }

        [TestMethod]
        public async Task Migration_RepointsReferences_ToTheCanonicalUrl()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                var dupId = await InsertUrlAsync(db, PlainUrl);

                // A reference pointing at the row that is about to be deleted.
                await ExecAsync(db,
                    $"INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) SELECT {dupId}, MIN(id), N'x', GETUTCDATE() FROM dbo.file_field_definitions");

                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var orphans = await ScalarAsync<int>(db,
                    @"SELECT COUNT(*) FROM dbo.file_metadata_property_values f
                      LEFT JOIN dbo.urls u ON u.id = f.url_id WHERE u.id IS NULL");
                Assert.AreEqual(0, orphans, "No reference may be left pointing at a deleted URL.");

                var repointed = await ScalarAsync<int>(db,
                    $"SELECT COUNT(*) FROM dbo.file_metadata_property_values WHERE url_id = {keepId}");
                Assert.IsTrue(repointed > 0, "The reference must now point at the canonical URL.");
            }
        }

        [TestMethod]
        public async Task Migration_PrunesRowsThatWouldCollideOnTheirOwnUniqueIndex()
        {
            // The case that would otherwise break the upgrade for everyone who has duplicates:
            // file_metadata_property_values has UNIQUE (url_id, field_id), so repointing two duplicate
            // url_ids onto one canonical id creates a duplicate key and the UPDATE fails.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                var dupId = await InsertUrlAsync(db, PlainUrl);

                // Same field_id under BOTH urls - these collide once dupId becomes keepId.
                await ExecAsync(db,
                    $@"DECLARE @f int = (SELECT MIN(id) FROM dbo.file_field_definitions);
                       INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) VALUES ({keepId}, @f, N'keep-me', GETUTCDATE());
                       INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) VALUES ({dupId}, @f, N'collides', GETUTCDATE());");

                // Must not throw.
                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var rows = await db.Database.SqlQuery<string>(
                    $"SELECT field_value FROM dbo.file_metadata_property_values WHERE url_id = {keepId}").ToListAsync();

                Assert.AreEqual(1, rows.Count, "Exactly one of the colliding rows may survive.");
                Assert.AreEqual("keep-me", rows.Single(),
                    "The row that ALREADY pointed at the canonical URL is the one to keep.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_PreservesNonAsciiUrlsExactly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, GreekUrl);
                await InsertUrlAsync(db, GreekUrl);

                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var survivors = await db.Database.SqlQuery<string>(
                    $"SELECT full_url FROM dbo.urls WHERE full_url = N'{GreekUrl.Replace("'", "''")}'").ToListAsync();

                Assert.AreEqual(1, survivors.Count);
                Assert.AreEqual(GreekUrl, survivors.Single(),
                    "A Greek URL must survive de-duplication byte-for-byte - no '?' substitution, no truncation.");
            }
        }

        [TestMethod]
        public async Task Migration_TreatsCaseDifferencesAsDuplicates_MatchingTheIndexItCreates()
        {
            // The database collation is case-insensitive, so the unique index would reject '.../Foo' once
            // '.../foo' exists. The de-duplication must group the same way, or the index creation would fail
            // on rows the grouping considered distinct.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl.ToUpperInvariant().Replace("HTTPS://CONTOSO.SHAREPOINT.COM/SITES/EXAMPLE/", "https://contoso.sharepoint.com/sites/example/"));

                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var remaining = await ScalarAsync<int>(db,
                    $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(1, remaining, "Case-only variants are the same URL under this collation.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_IsANoOpOnASecondRun()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);

                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);
                var afterFirst = await ScalarAsync<int>(db, "SELECT COUNT(*) FROM dbo.urls");

                // Must not throw and must change nothing.
                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);
                var afterSecond = await ScalarAsync<int>(db, "SELECT COUNT(*) FROM dbo.urls");

                Assert.AreEqual(afterFirst, afterSecond, "Re-running an applied migration must be a no-op.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_IsSafeOnADatabaseWithNoDuplicates()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, GreekUrl);

                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var count = await ScalarAsync<int>(db,
                    "SELECT COUNT(*) FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%'");
                Assert.AreEqual(2, count, "Nothing may be deleted when there is nothing duplicated.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task AfterMigration_ADuplicateInsertIsSkippedWithoutAbortingTheStatement()
        {
            // The importer-hardening half of #167. Without IGNORE_DUP_KEY a concurrent check-then-insert
            // race would abort the whole INSERT and lose the other new URLs in the same statement.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await ExecAsync(db, UniqueUrlsFullUrlIndex.Up_Sql);

                var brandNew = "https://contoso.sharepoint.com/sites/example/BrandNew.docx";

                // One duplicate and one genuinely new URL in a single statement.
                await ExecAsync(db,
                    $@"INSERT INTO dbo.urls (full_url)
                       SELECT N'{PlainUrl}' UNION ALL SELECT N'{brandNew}';");

                Assert.AreEqual(1, await ScalarAsync<int>(db, $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'"),
                    "The duplicate must have been skipped, not inserted.");
                Assert.AreEqual(1, await ScalarAsync<int>(db, $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{brandNew}'"),
                    "The other row in the same statement must still have been inserted.");
            }
        }

        [TestMethod]
        public void ManualScriptUsesTheSameSqlAsTheMigration()
        {
            // Rule 7 of the migration conventions: the manual upgrade script must contain the migration's
            // Up SQL verbatim, so a by-hand upgrade cannot diverge from the installer's.
            var manual = ReadManualScript("202609010900001_UniqueUrlsFullUrlIndex");

            StringAssert.Contains(manual, UniqueUrlsFullUrlIndex.Up_Sql,
                "The manual script must embed the migration's Up_Sql verbatim.");

            StringAssert.Contains(manual, "202608310800001_ColumnstoreUsageReportMetrics",
                "The stamp must be conditional on the predecessor, so the scripts cannot be applied out of order.");

            StringAssert.Contains(manual, "INSERT INTO dbo.__MigrationHistory",
                "The script must stamp __MigrationHistory or EF will still consider the migration pending.");
        }

        /// <summary>
        /// Reads a <c>&lt;migrationid&gt;.manual.sql</c> from the repository. Walks up from the test binaries
        /// rather than assuming a working directory, so it works under both vstest and the IDE.
        /// </summary>
        private static string ReadManualScript(string migrationId)
        {
            var dir = new System.IO.DirectoryInfo(
                System.IO.Path.GetDirectoryName(typeof(UniqueUrlsFullUrlIndexMigrationTests).Assembly.Location));

            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(
                    dir.FullName, "Common", "Entities", "Migrations", migrationId + ".manual.sql");

                if (System.IO.File.Exists(candidate))
                {
                    return System.IO.File.ReadAllText(candidate);
                }

                dir = dir.Parent;
            }

            Assert.Fail(
                $"Could not find {migrationId}.manual.sql anywhere above the test assembly. Every schema "
                + "migration must ship a manual upgrade script for operators who upgrade by hand.");
            return null;
        }
    }
}
