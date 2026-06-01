using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the AddAuditEventsOperationFK migration. Exercises the defensive
    /// guards (idempotency, dual-name detection, orphan cleanup, NOT NULL → NULL
    /// column relaxation) directly against the LocalDB test database by re-running
    /// <see cref="AddAuditEventsOperationFK.Up_Sql"/> in each scenario.
    /// </summary>
    [TestClass]
    public class AddAuditEventsOperationFKMigrationTests
    {
        private const string FkName = "FK_audit_events_event_operations";
        private const string LegacyFkName = "FK_events_event_operations";

        private static async Task EnsureSchemaAsync(AnalyticsEntitiesContext db)
        {
            await db.event_operations.Take(1).ToListAsync();
        }

        private static Task<int> ExecAsync(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, sql);
        }

        private static async Task DropFkIfExistsAsync(AnalyticsEntitiesContext db, string name)
        {
            await ExecAsync(db, $@"
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'{name}'
)
    ALTER TABLE [dbo].[audit_events] DROP CONSTRAINT [{name}];");
        }

        private static async Task<bool> FkExistsAsync(AnalyticsEntitiesContext db, string name)
        {
            var count = await db.Database.SqlQuery<int>(
                @"SELECT COUNT(*) FROM sys.foreign_keys
                  WHERE parent_object_id = OBJECT_ID(N'dbo.audit_events') AND name = @p0", name)
                .FirstAsync();
            return count > 0;
        }

        private static async Task<bool> FkIsTrustedAsync(AnalyticsEntitiesContext db, string name)
        {
            var notTrusted = await db.Database.SqlQuery<int>(
                @"SELECT ISNULL(MAX(CAST(is_not_trusted AS int)), 1)
                  FROM sys.foreign_keys
                  WHERE parent_object_id = OBJECT_ID(N'dbo.audit_events') AND name = @p0", name)
                .FirstAsync();
            return notTrusted == 0;
        }

        private static async Task ResetFkStateAsync(AnalyticsEntitiesContext db)
        {
            await DropFkIfExistsAsync(db, FkName);
            await DropFkIfExistsAsync(db, LegacyFkName);
        }

        /// <summary>
        /// Insert an audit_events row with the supplied operation_id (bypasses EF so we can
        /// write deliberately orphaned values) and return its primary key.
        /// </summary>
        private static async Task<Guid> InsertEventAsync(AnalyticsEntitiesContext db, int? operationId)
        {
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction,
                @"INSERT INTO dbo.audit_events (id, time_stamp, operation_id, user_id)
                  VALUES (@p0, SYSUTCDATETIME(), @p1, NULL)",
                id,
                (object)operationId ?? DBNull.Value);
            return id;
        }

        private static async Task DeleteEventAsync(AnalyticsEntitiesContext db, Guid id)
        {
            await db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction,
                "DELETE FROM dbo.audit_events WHERE id = @p0", id);
        }

        private static async Task<int?> GetEventOperationIdAsync(AnalyticsEntitiesContext db, Guid id)
        {
            return await db.Database.SqlQuery<int?>(
                "SELECT operation_id FROM dbo.audit_events WHERE id = @p0", id).SingleAsync();
        }

        [TestMethod]
        public async Task FK_Migration_CreatesTrustedFK_OnCleanData()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetFkStateAsync(db);

                await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);

                Assert.IsTrue(await FkExistsAsync(db, FkName), "FK should have been created.");
                Assert.IsTrue(await FkIsTrustedAsync(db, FkName),
                    "FK must be trusted (is_not_trusted = 0) after WITH CHECK CHECK CONSTRAINT.");

                await DropFkIfExistsAsync(db, FkName);
            }
        }

        [TestMethod]
        public async Task FK_Migration_NullsOutOrphans_AndCreatesTrustedFK()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetFkStateAsync(db);

                // Make sure no event_operations.id matches our orphan value.
                var orphanOpId = 2_000_000_001;
                Assert.AreEqual(0, await db.Database.SqlQuery<int>(
                    "SELECT COUNT(*) FROM dbo.event_operations WHERE id = @p0", orphanOpId).FirstAsync(),
                    "Test relies on the orphan operation_id not actually existing.");

                var orphanRowId = await InsertEventAsync(db, orphanOpId);

                try
                {
                    await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);

                    Assert.IsTrue(await FkExistsAsync(db, FkName));
                    Assert.IsTrue(await FkIsTrustedAsync(db, FkName),
                        "After orphan cleanup the FK validation must succeed and leave the FK trusted.");
                    Assert.IsNull(await GetEventOperationIdAsync(db, orphanRowId),
                        "Orphan operation_id must have been NULL-ed by the migration.");
                }
                finally
                {
                    await DropFkIfExistsAsync(db, FkName);
                    await DeleteEventAsync(db, orphanRowId);
                }
            }
        }

        [TestMethod]
        public async Task FK_Migration_IsIdempotent_WhenRunRepeatedly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetFkStateAsync(db);

                await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);
                await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);
                await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);

                Assert.IsTrue(await FkExistsAsync(db, FkName));

                await DropFkIfExistsAsync(db, FkName);
            }
        }

        [TestMethod]
        public async Task FK_Migration_SkipsCreate_WhenLegacyFkAlreadyPresent()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetFkStateAsync(db);

                // Simulate a fresh-install DB whose v1 Create DB.sql FK was preserved.
                await ExecAsync(db, $@"
ALTER TABLE [dbo].[audit_events] WITH CHECK
    ADD CONSTRAINT [{LegacyFkName}]
    FOREIGN KEY ([operation_id]) REFERENCES [dbo].[event_operations] ([id]);");

                await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);

                Assert.IsTrue(await FkExistsAsync(db, LegacyFkName),
                    "Legacy FK must be left untouched.");
                Assert.IsFalse(await FkExistsAsync(db, FkName),
                    "Migration must NOT create a duplicate FK under the new name.");

                await DropFkIfExistsAsync(db, LegacyFkName);
            }
        }

        [TestMethod]
        public async Task FK_Migration_RelaxesNotNullColumn_OnLegacySchema()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetFkStateAsync(db);

                // Simulate the original Create DB.sql state: NOT NULL column with the
                // legacy supporting index. Drop the modern index first because
                // ALTER COLUMN can't run while any index references the column.
                await ExecAsync(db, @"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'IX_operation_id')
    DROP INDEX [IX_operation_id] ON [dbo].[audit_events];
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'IX_FK_events_event_operations')
    DROP INDEX [IX_FK_events_event_operations] ON [dbo].[audit_events];");
                await ExecAsync(db,
                    "ALTER TABLE [dbo].[audit_events] ALTER COLUMN [operation_id] int NOT NULL;");
                await ExecAsync(db,
                    "CREATE NONCLUSTERED INDEX [IX_FK_events_event_operations] ON [dbo].[audit_events] ([operation_id]);");

                try
                {
                    await ExecAsync(db, AddAuditEventsOperationFK.Up_Sql);

                    var isNullable = await db.Database.SqlQuery<bool>(
                        @"SELECT is_nullable FROM sys.columns
                          WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'operation_id'")
                        .FirstAsync();
                    Assert.IsTrue(isNullable,
                        "Migration must relax operation_id to NULL so it matches the entity model.");

                    Assert.IsTrue(await FkExistsAsync(db, FkName));
                    Assert.IsTrue(await FkIsTrustedAsync(db, FkName));

                    // Canonical index should exist; the legacy duplicate should be gone.
                    var hasCanonical = await db.Database.SqlQuery<int>(
                        @"SELECT COUNT(*) FROM sys.indexes
                          WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'IX_operation_id'")
                        .FirstAsync();
                    Assert.AreEqual(1, hasCanonical, "IX_operation_id must be recreated after the column change.");
                }
                finally
                {
                    await DropFkIfExistsAsync(db, FkName);
                }
            }
        }
    }
}
