using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers <see cref="DenormaliseCopilotChatUserAndTime"/>, which copies the parent audit event's
    /// <c>user_id</c> and <c>time_stamp</c> onto <c>dbo.copilot_chats</c> so no Copilot report has to join
    /// <c>dbo.audit_events</c> any more (issue #360).
    ///
    /// The migration is the risky half of that change: it adds columns to what is a very large table on a
    /// Copilot-heavy tenant, backfills every row, and builds an index. These tests pin the three properties
    /// that make it safe to ship - it is correct, it is idempotent, and an interrupted run converges on
    /// re-run rather than leaving the reports quietly reading a half-filled column.
    /// </summary>
    [TestClass]
    public class DenormaliseCopilotChatUserAndTimeMigrationTests
    {
        /// <summary>
        /// The pre-migration shape: chats carry no user or timestamp of their own.
        /// <c>copilot_chats</c> is clustered on <c>event_id</c>, as in production - the backfill walks that
        /// clustered key, so the test would not exercise the real access pattern on a differently-keyed table.
        /// No FOREIGN KEY, so the orphan case below can be set up.
        /// </summary>
        private static ScratchDatabase CreateCopilotSchema()
        {
            var db = ScratchDatabase.Create("CopilotDenorm");
            try
            {
                db.Execute(
                    @"CREATE TABLE [dbo].[audit_events] (
                          [id] uniqueidentifier NOT NULL PRIMARY KEY,
                          [time_stamp] datetime NOT NULL,
                          [user_id] int NULL);

                      CREATE TABLE [dbo].[copilot_chats] (
                          [event_id] uniqueidentifier NOT NULL PRIMARY KEY CLUSTERED,
                          [app_host] nvarchar(200) NULL,
                          [agent_id] int NULL);");
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        private static void SeedChat(ScratchDatabase db, string guid, int userId, string whenUtc, string appHost)
        {
            db.Execute(
                $@"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                       VALUES ('{guid}', '{whenUtc}', {userId});
                   INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id)
                       VALUES ('{guid}', N'{appHost}', NULL);");
        }

        private static void RunMigration(ScratchDatabase db)
        {
            // Two batches on purpose: T-SQL resolves column names for a whole batch up front, so the
            // backfill cannot live in the same batch that adds the columns.
            db.Execute(DenormaliseCopilotChatUserAndTime.Up_Sql);
            db.Execute(DenormaliseCopilotChatUserAndTime.Up_Backfill_Sql);
        }

        [TestMethod]
        public void UpSql_BackfillsFromAuditEvents_AndIsIdempotent()
        {
            using (var db = CreateCopilotSchema())
            {
                SeedChat(db, "11111111-1111-1111-1111-111111111111", 7, "2026-06-01 09:15:00", "Teams");
                SeedChat(db, "22222222-2222-2222-2222-222222222222", 9, "2026-06-02 11:30:00", "Word");

                // Run twice: the second run must be a no-op, not a second backfill or a duplicate index.
                RunMigration(db);
                RunMigration(db);

                Assert.AreEqual(0, db.Scalar(
                    @"SELECT COUNT(*) FROM dbo.copilot_chats c
                      JOIN dbo.audit_events ae ON ae.id = c.event_id
                      WHERE c.user_id <> ae.user_id OR c.time_stamp <> ae.time_stamp
                         OR c.user_id IS NULL OR c.time_stamp IS NULL;"),
                    "Every chat must carry exactly its audit event's user and timestamp.");

                Assert.IsTrue(db.IndexHasColumn("copilot_chats", "IX_copilot_chats_time_stamp_user_id", "time_stamp", 1, false),
                    "[time_stamp] must lead: every Copilot query except LicensedUsers is window-scoped, and a "
                    + "leading time_stamp measured twice as fast there as a leading user_id.");
                Assert.IsTrue(db.IndexHasColumn("copilot_chats", "IX_copilot_chats_time_stamp_user_id", "user_id", 2, false),
                    "[user_id] must be the second KEY column - it takes part in the seat join, not just the output.");
                Assert.IsTrue(db.IndexHasColumn("copilot_chats", "IX_copilot_chats_time_stamp_user_id", "app_host", 0, true),
                    "[app_host] must be included so UsageByApp never touches the base row.");
                Assert.IsTrue(db.IndexHasColumn("copilot_chats", "IX_copilot_chats_time_stamp_user_id", "agent_id", 0, true),
                    "[agent_id] must be included so the agent estate query never touches the base row.");

                Assert.AreEqual(1, db.NonClusteredIndexCount("copilot_chats"),
                    "Re-running the migration must not build the index a second time.");
            }
        }

        [TestMethod]
        public void UpSql_LeavesChatsWithNoAuditEventNull()
        {
            using (var db = CreateCopilotSchema())
            {
                SeedChat(db, "11111111-1111-1111-1111-111111111111", 7, "2026-06-01 09:15:00", "Teams");

                // An orphan cannot occur while the foreign key is in place, but if one ever did it must stay
                // NULL rather than being invented. NULL fails "time_stamp >= @from", which is exactly the row
                // the old INNER JOIN dbo.audit_events dropped - so the reports see the same population.
                db.Execute(
                    @"INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id)
                          VALUES ('33333333-3333-3333-3333-333333333333', N'Orphan', NULL);");

                RunMigration(db);

                Assert.AreEqual(1, db.Scalar(
                    @"SELECT COUNT(*) FROM dbo.copilot_chats
                      WHERE event_id = '33333333-3333-3333-3333-333333333333'
                        AND user_id IS NULL AND time_stamp IS NULL;"),
                    "A chat with no audit event must keep NULL, matching the old INNER JOIN semantics.");
            }
        }

        [TestMethod]
        public void UpSql_ResumesAnInterruptedBackfill()
        {
            using (var db = CreateCopilotSchema())
            {
                SeedChat(db, "11111111-1111-1111-1111-111111111111", 7, "2026-06-01 09:15:00", "Teams");
                SeedChat(db, "22222222-2222-2222-2222-222222222222", 9, "2026-06-02 11:30:00", "Word");

                RunMigration(db);

                // Simulate an interrupted first run: the columns and index exist, but some rows were never
                // written. The backfill must find and finish them - if it short-circuited on "the columns
                // already exist" the reports would silently under-count for ever.
                db.Execute(
                    @"UPDATE dbo.copilot_chats SET user_id = NULL, time_stamp = NULL
                      WHERE event_id = '22222222-2222-2222-2222-222222222222';");

                db.Execute(DenormaliseCopilotChatUserAndTime.Up_Backfill_Sql);

                Assert.AreEqual(0, db.Scalar(
                    "SELECT COUNT(*) FROM dbo.copilot_chats WHERE user_id IS NULL OR time_stamp IS NULL;"),
                    "A partially-backfilled table must converge on re-run.");
            }
        }

        [TestMethod]
        public void ImporterMergeRepairsRowsLeftBehindByAnOlderImporter()
        {
            // The migration is stamped once and never runs again, but the columns stay NULLable. An OLD
            // importer binary still inserting during the upgrade window - or a row inserted into a key range
            // the backfill had already walked past - would otherwise keep NULL for ever and be SILENTLY
            // invisible to every Copilot report, because they all filter "c.time_stamp >= @from".
            //
            // The importer merge therefore carries a self-healing repair. This test executes the REAL
            // statement, extracted from the shipped embedded resource, so deleting or weakening it in
            // common_upsert_copilot_agents.sql fails here rather than silently losing customer data.
            var repairSql = ExtractRepairStatementFromShippedMerge();

            using (var db = CreateCopilotSchema())
            {
                SeedChat(db, "11111111-1111-1111-1111-111111111111", 7, "2026-06-01 09:15:00", "Teams");
                RunMigration(db);

                // The old importer inserts a chat with no denormalised columns, AFTER the backfill ran.
                db.Execute(
                    @"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                          VALUES ('44444444-4444-4444-4444-444444444444', '2026-06-03 10:00:00', 42);
                      INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id)
                          VALUES ('44444444-4444-4444-4444-444444444444', N'Word', NULL);");

                // An orphan, which can never be repaired and must not keep the repair busy for ever.
                db.Execute(
                    @"INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id)
                          VALUES ('55555555-5555-5555-5555-555555555555', N'Orphan', NULL);");

                db.Execute(repairSql);

                Assert.AreEqual(1, db.Scalar(
                    @"SELECT COUNT(*) FROM dbo.copilot_chats
                      WHERE event_id = '44444444-4444-4444-4444-444444444444'
                        AND user_id = 42 AND time_stamp = '2026-06-03 10:00:00';"),
                    "The importer merge must repair rows an older importer inserted after the backfill completed.");

                Assert.AreEqual(1, db.Scalar(
                    @"SELECT COUNT(*) FROM dbo.copilot_chats
                      WHERE event_id = '55555555-5555-5555-5555-555555555555'
                        AND user_id IS NULL AND time_stamp IS NULL;"),
                    "An orphan has no audit event to copy from and must be left alone.");

                // Idempotent: a second pass must find nothing left to do.
                db.Execute(repairSql);
                Assert.AreEqual(1, db.Scalar(
                    "SELECT COUNT(*) FROM dbo.copilot_chats WHERE time_stamp IS NULL;"),
                    "Only the unrepairable orphan should remain NULL.");
            }
        }

        /// <summary>
        /// Returns the shipped self-healing repair script, and fails if it no longer contains the repair -
        /// so this test cannot pass against an importer that has quietly dropped it.
        /// </summary>
        private static string ExtractRepairStatementFromShippedMerge()
        {
            var reader = new DataUtils.ProjectResourceReader(
                typeof(ActivityImporter.Engine.ActivityAPI.Copilot.CopilotAuditEventManager).Assembly);
            var script = reader.ReadResourceString(
                "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.repair_denormalised_copilot_columns.sql");

            StringAssert.Contains(script, "UPDATE TOP (@batch) c",
                "repair_denormalised_copilot_columns.sql no longer contains the self-healing repair of the "
                + "denormalised user_id / time_stamp columns. Removing it lets an upgrade leave Copilot "
                + "interactions permanently invisible to every report - see issue #360.");
            StringAssert.Contains(script, "WHERE c.time_stamp IS NULL",
                "The repair must find rows by time_stamp IS NULL - that is the SARGable 'not yet backfilled' test.");

            return script;
        }

        [TestMethod]
        public void DownSql_RemovesTheIndexAndColumns()
        {
            using (var db = CreateCopilotSchema())
            {
                SeedChat(db, "11111111-1111-1111-1111-111111111111", 7, "2026-06-01 09:15:00", "Teams");
                RunMigration(db);

                db.Execute(DenormaliseCopilotChatUserAndTime.Down_Sql);

                Assert.IsFalse(db.IndexExists("copilot_chats", "IX_copilot_chats_time_stamp_user_id"),
                    "Down must drop the index.");
                Assert.AreEqual(0, db.Scalar(
                    @"SELECT COUNT(*) FROM sys.columns
                      WHERE object_id = OBJECT_ID('dbo.copilot_chats') AND name IN ('user_id', 'time_stamp');"),
                    "Down must drop both denormalised columns.");
            }
        }
    }
}
