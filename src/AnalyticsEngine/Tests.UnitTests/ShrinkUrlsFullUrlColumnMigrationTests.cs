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
    /// exercise both the success path (column shrunk to varchar(1700) + supporting index created,
    /// idempotently) and the fail-fast path (a URL longer than 1700 chars, or one that can't be
    /// represented as varchar, aborts the migration without changing anything) by re-running
    /// <see cref="ShrinkUrlsFullUrlColumn.Up_Sql"/> directly against the LocalDB test database.
    /// </summary>
    [TestClass]
    public class ShrinkUrlsFullUrlColumnMigrationTests
    {
        private const string IndexName = "IX_urls_full_url";
        private const int MaxLen = 1700;

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

        /// <summary>True when full_url is exactly varchar(1700).</summary>
        private static async Task<bool> ColumnIsShrunkAsync(AnalyticsEntitiesContext db)
        {
            var count = await db.Database.SqlQuery<int>(
                @"SELECT COUNT(*)
                  FROM sys.columns c
                  INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
                  WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url'
                    AND t.name = N'varchar' AND c.max_length = 1700").FirstAsync();
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

                    Assert.IsTrue(await ColumnIsShrunkAsync(db), "full_url should now be varchar(1700).");
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
        public async Task Migration_Aborts_AndPreservesData_WhenUrlExceeds1700Chars()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await SetLegacyStateAsync(db);

                // 1701 characters - one over the limit.
                var tooLong = "https://contoso.sharepoint.com/" + new string('a', 1701 - "https://contoso.sharepoint.com/".Length + 1);
                Assert.IsTrue(tooLong.Length > MaxLen, "Test setup: URL must exceed the limit.");
                var id = await InsertUrlAsync(db, tooLong);

                try
                {
                    await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
                    Assert.Fail("Migration should have thrown because a URL exceeds 1700 characters.");
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
        public async Task Migration_Aborts_WhenUrlNotRepresentableAsVarchar()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                // CJK characters used as the "not representable in a single-byte code page" sample.
                // \u4F60\u597D = 你好.
                const string cjk = "\u4F60\u597D";

                // Skip on UTF-8 / Unicode-capable collations where varchar can represent everything,
                // so this test is meaningful only where the conversion is actually lossy (e.g. CP1252).
                var lossy = await db.Database.SqlQuery<int>(
                    @"DECLARE @s nvarchar(10) = NCHAR(20320) + NCHAR(22909);
                      SELECT CASE WHEN @s = CONVERT(nvarchar(max), CONVERT(varchar(100), @s)) THEN 0 ELSE 1 END").FirstAsync();
                if (lossy == 0)
                {
                    Assert.Inconclusive("Database collation can represent the test characters as varchar; lossy-conversion guard not exercised here.");
                    return;
                }

                await SetLegacyStateAsync(db);

                // Short enough to pass the length guard, but contains CJK characters that don't fit a
                // single-byte code page.
                var id = await InsertUrlAsync(db, "https://contoso.sharepoint.com/sites/x/" + cjk + ".docx");
                try
                {
                    await ExecAsync(db, ShrinkUrlsFullUrlColumn.Up_Sql);
                    Assert.Fail("Migration should have thrown because a URL is not representable as varchar.");
                }
                catch (SqlException)
                {
                    // Expected.
                }

                Assert.IsFalse(await ColumnIsShrunkAsync(db), "Column must NOT be shrunk when the lossy-conversion guard fails.");
                Assert.IsTrue(await UrlExistsAsync(db, id), "The offending row must be preserved (no data loss).");

                await DeleteUrlAsync(db, id);
                await RestoreMigratedStateAsync(db);
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
