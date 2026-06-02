using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the AddAuditEventsOperationIndex EF migration. The migration script is
    /// designed to run safely against a wide variety of customer Azure SQL DBs, so the
    /// tests exercise the defensive guards (idempotency + legacy index detection) directly
    /// against the LocalDB test database by re-running <see cref="AddAuditEventsOperationIndex.Up_Sql"/>.
    /// </summary>
    [TestClass]
    public class AddAuditEventsOperationIndexMigrationTests
    {
        private const string IndexName = "IX_operation_id";
        private const string LegacyIndexName = "IX_FK_events_event_operations";

        /// <summary>
        /// Forces EF initialisation (DEBUG build auto-applies pending migrations against
        /// LocalDB) so the audit_events table is guaranteed to exist before we start
        /// fiddling with its indexes.
        /// </summary>
        private static async Task EnsureSchemaAsync(AnalyticsEntitiesContext db)
        {
            // Touch any DbSet to trigger EF's database initializer.
            await db.event_operations.Take(1).ToListAsync();
        }

        private static Task<int> ExecAsync(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, sql);
        }

        private static async Task DropIndexIfExistsAsync(AnalyticsEntitiesContext db, string name)
        {
            await ExecAsync(db, $@"
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'{name}'
)
    DROP INDEX [{name}] ON [dbo].[audit_events];");
        }

        private static async Task<bool> IndexExistsAsync(AnalyticsEntitiesContext db, string name)
        {
            var count = await db.Database.SqlQuery<int>(
                @"SELECT COUNT(*) FROM sys.indexes
                  WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = @p0", name)
                .FirstAsync();
            return count > 0;
        }

        private static async Task<int> IndexedColumnCountAsync(AnalyticsEntitiesContext db, string name, string column)
        {
            return await db.Database.SqlQuery<int>(
                @"SELECT COUNT(*) FROM sys.indexes i
                  INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                  INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                  WHERE i.object_id = OBJECT_ID(N'dbo.audit_events')
                    AND i.name = @p0
                    AND c.name = @p1", name, column).FirstAsync();
        }

        /// <summary>
        /// Restore the default state (only IX_operation_id exists) so other tests / the
        /// migration history aren't surprised by leftover indexes.
        /// </summary>
        private static async Task RestoreDefaultStateAsync(AnalyticsEntitiesContext db)
        {
            await DropIndexIfExistsAsync(db, LegacyIndexName);
            if (!await IndexExistsAsync(db, IndexName))
            {
                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);
            }
        }

        [TestMethod]
        public async Task Migration_CreatesIndex_WhenNoSupportingIndexExists()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                // Simulate a customer DB that went through the audit_events rebuild path
                // and has neither the new nor the legacy index.
                await DropIndexIfExistsAsync(db, IndexName);
                await DropIndexIfExistsAsync(db, LegacyIndexName);

                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);

                Assert.IsTrue(await IndexExistsAsync(db, IndexName),
                    "Migration should have created IX_operation_id when no supporting index existed.");
                Assert.AreEqual(1, await IndexedColumnCountAsync(db, IndexName, "operation_id"),
                    "IX_operation_id must index exactly the operation_id column.");

                await RestoreDefaultStateAsync(db);
            }
        }

        [TestMethod]
        public async Task Migration_IsIdempotent_WhenRunRepeatedly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                await DropIndexIfExistsAsync(db, IndexName);
                await DropIndexIfExistsAsync(db, LegacyIndexName);

                // Three back-to-back runs must succeed: create, then two no-ops.
                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);
                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);
                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);

                Assert.IsTrue(await IndexExistsAsync(db, IndexName));

                await RestoreDefaultStateAsync(db);
            }
        }

        [TestMethod]
        public async Task Migration_SkipsCreate_WhenLegacyIndexAlreadyPresent()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                // Simulate a fresh-install DB that already has the v1 / Create DB.sql
                // index name and shouldn't get a duplicate.
                await DropIndexIfExistsAsync(db, IndexName);
                await DropIndexIfExistsAsync(db, LegacyIndexName);
                await ExecAsync(db,
                    $"CREATE NONCLUSTERED INDEX [{LegacyIndexName}] ON [dbo].[audit_events] ([operation_id]);");

                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);

                Assert.IsTrue(await IndexExistsAsync(db, LegacyIndexName),
                    "Legacy index must be left in place.");
                Assert.IsFalse(await IndexExistsAsync(db, IndexName),
                    "Migration must NOT create IX_operation_id when the legacy index already exists.");

                await RestoreDefaultStateAsync(db);
            }
        }

        [TestMethod]
        public async Task Migration_DownThenUp_RecreatesIndex()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                // Start from the post-migration state.
                await RestoreDefaultStateAsync(db);

                await ExecAsync(db, AddAuditEventsOperationIndex.Down_Sql);
                Assert.IsFalse(await IndexExistsAsync(db, IndexName),
                    "Down_Sql should remove IX_operation_id.");

                await ExecAsync(db, AddAuditEventsOperationIndex.Up_Sql);
                Assert.IsTrue(await IndexExistsAsync(db, IndexName),
                    "Up_Sql should recreate IX_operation_id after Down_Sql.");

                await RestoreDefaultStateAsync(db);
            }
        }
    }
}
