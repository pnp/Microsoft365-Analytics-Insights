using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the <see cref="ShrinkUrlsFullUrlColumn"/> EF migration. The migration script is
    /// designed to run safely against a wide variety of customer Azure SQL DBs, so the tests
    /// exercise both the success path (column shrunk to nvarchar(850) + supporting index created,
    /// idempotently, preserving Unicode such as Greek) and the fail-fast path (a URL longer than
    /// 850 chars aborts the migration without changing anything) by re-running
    /// <see cref="ShrinkUrlsFullUrlColumn.Up_Sql"/> directly against the LocalDB test database.
    /// It also covers the State-C upgrade path: a database still on the superseded varchar(1700)
    /// form is converted to nvarchar(850) (what the later <see cref="UrlFullUrlNvarchar"/>
    /// migration replays) without losing data.
    /// </summary>
    [TestClass]
    public class ShrinkUrlsFullUrlColumnMigrationTests
    {
        private const string IndexName = "IX_urls_full_url";
        private const int MaxLen = 850;

        /// <summary>
        /// Forces EF initialisation (DEBUG build auto-applies pending migrations against LocalDB)
        /// so the urls table is guaranteed to exist before we start manipulating it.
        /// </summary>
        private static async Task EnsureSchemaAsync(AnalyticsEntitiesContext db)
        {
            await db.urls.Take(1).ToListAsync();
        }

        private static Task<int> ExecAsync(AnalyticsEntitiesContext db, string sql, params object[] p)
        {
            return db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, sql, p);
        }

        private static async Task<bool> IndexExistsAsync(AnalyticsEntitiesContext db)
        {
            var count = await db.Database.SqlQuery<int>(
                @"SELECT COUNT(*) FROM sys.indexes
                  WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @p0", IndexName).FirstAsync();
            return count > 0;
        }

        /// <summary>True when full_url is exactly nvarchar(850).</summary>
        private static async Task<bool> ColumnIsShrunkAsync(AnalyticsEntitiesContext db)
        {
            // nvarchar(850) stores 850 chars in 1700 bytes, so sys.columns.max_length = 1700.
            var count = await db.Database.SqlQuery<int>(
                @"SELECT COUNT(*)
                  FROM sys.columns c
                  INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
                  WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url'
                    AND t.name = N'nvarchar' AND c.max_length = 1700").FirstAsync();
            return count > 0;
        }

        /// <summary>
        /// Puts the column back into the legacy "pre-migration" shape (nvarchar(max), no index)
        /// so a test can exercise the migration from scratch.
        /// </summary>
        private static async Task SetLegacyStateAsync(AnalyticsEntitiesContext db)
        {
            await ExecAsync(db, $@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}')
    DROP INDEX [{IndexName}] ON [dbo].[urls];
ALTER TABLE [dbo].[urls] ALTER COLUMN [full_url] nvarchar(max) NOT NULL;");
        }

        /// <summary>
        /// Puts the column into the superseded "State C" shape (varchar(1700) + index) that
        /// customers who applied the original varchar form of the migration are on, so a test can
        /// exercise the varchar(1700) -> nvarchar(850) catch-up conversion.
        /// </summary>
        private static async Task SetVarchar1700StateAsync(AnalyticsEntitiesContext db)
        {
            await ExecAsync(db, $@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}')
    DROP INDEX [{IndexName}] ON [dbo].[urls];
ALTER TABLE [dbo].[urls] ALTER COLUMN [full_url] varchar(1700) NOT NULL;
CREATE NONCLUSTERED INDEX [{IndexName}] ON [dbo].[urls] ([full_url]);");
        }

        /// <summary>Insert a url row and return its id.</summary>
        private static async Task<int> InsertUrlAsync(AnalyticsEntitiesContext db, string url)
        {
            return await db.Database.SqlQuery<int>(
                @"INSERT INTO [dbo].[urls] (full_url) OUTPUT INSERTED.id VALUES (@p0)", url).FirstAsync();
        }

        private static Task DeleteUrlAsync(AnalyticsEntitiesContext db, int id)
        {
            return ExecAsync(db, "DELETE FROM [dbo].[urls] WHERE id = @p0", id);
        }

        private static async Task<bool> UrlExistsAsync(AnalyticsEntitiesContext db, int id)
        {
            var count = await db.Database.SqlQuery<int>(
                "SELECT COUNT(*) FROM [dbo].[urls] WHERE id = @p0", id).FirstAsync();
            return count > 0;
        }

        /// <summary>Read a url row's full_url back so a test can assert it round-tripped intact.</summary>
        private static async Task<string> GetUrlAsync(AnalyticsEntitiesContext db, int id)
        {
            return await db.Database.SqlQuery<string>(
                "SELECT full_url FROM [dbo].[urls] WHERE id = @p0", id).FirstAsync();
        }

        /// <summary>
        /// Return the database to the fully-migrated state (shrunk column + index) so other tests
        /// and the migration history aren't surprised by leftover legacy schema.
        /// </summary>
        private static async Task RestoreMigratedStateAsync(AnalyticsEntitiesContext db)
        {
            await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
        }

        [TestMethod]
        public async Task Migration_ShrinksColumn_AndCreatesIndex_OnCleanData()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await SetLegacyStateAsync(db);

                Assert.IsFalse(await ColumnIsShrunkAsync(db), "Pre-condition: column should be back to nvarchar(max).");
                Assert.IsFalse(await IndexExistsAsync(db), "Pre-condition: index should not exist yet.");

                var id = await InsertUrlAsync(db, "https://contoso.sharepoint.com/sites/x/Shared Documents/ok.docx");
                try
                {
                    await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);

                    Assert.IsTrue(await ColumnIsShrunkAsync(db), "full_url should now be nvarchar(850).");
                    Assert.IsTrue(await IndexExistsAsync(db), "IX_urls_full_url should have been created.");
                }
                finally
                {
                    await DeleteUrlAsync(db, id);
                    await RestoreMigratedStateAsync(db);
                }
            }
        }

        [TestMethod]
        public async Task Migration_IsIdempotent_WhenRunRepeatedly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await SetLegacyStateAsync(db);

                // First run does the work; the next two must be clean no-ops.
                await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
                await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
                await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);

                Assert.IsTrue(await ColumnIsShrunkAsync(db));
                Assert.IsTrue(await IndexExistsAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_Aborts_AndPreservesData_WhenUrlExceeds850Chars()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await SetLegacyStateAsync(db);

                // 851 characters - one over the 850 limit.
                const string prefix = "https://contoso.sharepoint.com/";
                var tooLong = prefix + new string('a', (MaxLen + 1) - prefix.Length);
                Assert.AreEqual(MaxLen + 1, tooLong.Length, "Test setup: URL must be exactly one character over the limit.");
                var id = await InsertUrlAsync(db, tooLong);

                try
                {
                    await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
                    Assert.Fail("Migration should have thrown because a URL exceeds 850 characters.");
                }
                catch (SqlException)
                {
                    // Expected: the migration aborts.
                }

                // Nothing must have changed: column is still the legacy type and the row survives intact.
                Assert.IsFalse(await ColumnIsShrunkAsync(db), "Column must NOT be shrunk when the guard fails.");
                Assert.IsFalse(await IndexExistsAsync(db), "Index must NOT be created when the guard fails.");
                Assert.IsTrue(await UrlExistsAsync(db, id), "The offending row must be preserved (no data loss).");

                // Fix the data and confirm the migration then succeeds.
                await DeleteUrlAsync(db, id);
                await RestoreMigratedStateAsync(db);
                Assert.IsTrue(await ColumnIsShrunkAsync(db), "After fixing the data the migration should succeed.");
                Assert.IsTrue(await IndexExistsAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_PreservesGreekUrl_WhenShrinkingFromNvarcharMax()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await SetLegacyStateAsync(db);

                // A synthetic Greek SharePoint-style URL (the classic "Καλημέρα κόσμε" charset
                // sample - no customer data) covering issue #122. nvarchar(max) -> nvarchar(850)
                // is a pure Unicode-preserving widen, so it must survive the shrink intact.
                // (The old varchar(1700) form corrupted every non-Latin character to '?'.)
                const string greekUrl =
                    "https://contoso.sharepoint.com/sites/example/Shared Documents/" +
                    "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5.pdf";
                Assert.IsTrue(greekUrl.Length <= MaxLen, "Test setup: sample URL must fit nvarchar(850).");

                var id = await InsertUrlAsync(db, greekUrl);
                try
                {
                    await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);

                    Assert.IsTrue(await ColumnIsShrunkAsync(db), "full_url should now be nvarchar(850).");
                    Assert.IsTrue(await IndexExistsAsync(db), "IX_urls_full_url should have been created.");
                    Assert.AreEqual(greekUrl, await GetUrlAsync(db, id),
                        "The Greek URL must round-trip intact through the nvarchar(850) conversion.");
                }
                finally
                {
                    await DeleteUrlAsync(db, id);
                    await RestoreMigratedStateAsync(db);
                }
            }
        }

        [TestMethod]
        public async Task Migration_ConvertsVarchar1700ToNvarchar850_PreservingData()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                // Simulate "State C": a customer who already applied the original varchar(1700)
                // form of this migration. Re-running Up_Sql (what UrlFullUrlNvarchar.Up replays)
                // must convert the column to nvarchar(850), keeping the index and the data. Greek
                // can't be seeded here because the source column is varchar (it would already be
                // corrupted), so use an ASCII URL to prove the conversion itself is lossless.
                await SetVarchar1700StateAsync(db);

                const string asciiUrl = "https://contoso.sharepoint.com/sites/x/Shared Documents/state-c.docx";
                var id = await InsertUrlAsync(db, asciiUrl);
                try
                {
                    Assert.IsFalse(await ColumnIsShrunkAsync(db), "Pre-condition: column should be varchar(1700), not nvarchar(850).");
                    Assert.IsTrue(await IndexExistsAsync(db), "Pre-condition: State C already has IX_urls_full_url.");

                    await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);

                    Assert.IsTrue(await ColumnIsShrunkAsync(db), "full_url should have been converted to nvarchar(850).");
                    Assert.IsTrue(await IndexExistsAsync(db), "IX_urls_full_url should still exist after the conversion.");
                    Assert.AreEqual(asciiUrl, await GetUrlAsync(db, id),
                        "The URL must survive the varchar(1700) -> nvarchar(850) conversion.");
                }
                finally
                {
                    await DeleteUrlAsync(db, id);
                    await RestoreMigratedStateAsync(db);
                }
            }
        }

        [TestMethod]
        public async Task Migration_DownThenUp_Roundtrips()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await RestoreMigratedStateAsync(db);

                await ExecAsync(db, ShrinkUrlsFullUrlColumn.Down_Sql);
                Assert.IsFalse(await IndexExistsAsync(db), "Down_Sql should drop the index.");
                Assert.IsFalse(await ColumnIsShrunkAsync(db), "Down_Sql should widen the column back to nvarchar(max).");

                await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
                Assert.IsTrue(await IndexExistsAsync(db), "Up_Sql should recreate the index.");
                Assert.IsTrue(await ColumnIsShrunkAsync(db), "Up_Sql should re-shrink the column.");
            }
        }
    }
}
