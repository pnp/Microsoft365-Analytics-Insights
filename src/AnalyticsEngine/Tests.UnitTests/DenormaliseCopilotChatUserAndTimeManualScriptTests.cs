using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    [TestClass]
    public class DenormaliseCopilotChatUserAndTimeManualScriptTests
    {
        private const string MigrationId = "202608310747353_DenormaliseCopilotChatUserAndTime";
        private const string PredecessorId = "202608250900001_IndexCopilotAccessedResourceFkColumns";
        private const string IndexName = "IX_copilot_chats_time_stamp_user_id";

        [TestMethod]
        public void StampGuard_MissingSchemaHardFails_AndDoesNotStamp()
        {
            using (var db = CreatePostMigrationSchema(createUserIdColumn: true, createTimeStampColumn: false, createIndex: false))
            {
                AddMigrationHistory(db);

                var ex = AssertSqlException(() => ExecuteSqlBatch(db.ConnectionString, StampBatch()));

                StringAssert.Contains(ex.Message, "NOT stamped - the schema work did not complete");
                StringAssert.Contains(ex.Message, "column copilot_chats.time_stamp");
                StringAssert.Contains(ex.Message, "index IX_copilot_chats_time_stamp_user_id");
                Assert.AreEqual(0, MigrationStampCount(db), "A failed schema guard must not stamp the migration.");
            }
        }

        [TestMethod]
        public void StampGuard_MissingUserIdHardFails_AndDoesNotStamp()
        {
            using (var db = CreatePostMigrationSchema(createUserIdColumn: false, createTimeStampColumn: true, createIndex: false))
            {
                AddMigrationHistory(db);

                var ex = AssertSqlException(() => ExecuteSqlBatch(db.ConnectionString, StampBatch()));

                StringAssert.Contains(ex.Message, "NOT stamped - the schema work did not complete");
                StringAssert.Contains(ex.Message, "column copilot_chats.user_id");
                Assert.AreEqual(0, MigrationStampCount(db), "The schema guard must still check the denormalised user column.");
            }
        }

        [TestMethod]
        public void StampGuard_RepairableResidualRowsWarn_AndStillStamp()
        {
            using (var db = CreatePostMigrationSchema(createUserIdColumn: true, createTimeStampColumn: true, createIndex: true))
            {
                AddMigrationHistory(db);
                db.Execute(
                    @"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                          VALUES ('11111111-1111-1111-1111-111111111111', '2026-06-01 09:15:00', 7);
                      INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                          VALUES ('11111111-1111-1111-1111-111111111111', N'Teams', NULL, NULL, NULL);");

                var stampBatch = StampBatch();
                ExecuteSqlBatch(db.ConnectionString, stampBatch);

                Assert.AreEqual(1, MigrationStampCount(db), "Repairable residual rows are data state, not schema failure.");
                Assert.IsTrue(
                    Regex.IsMatch(
                        stampBatch,
                        @"IF\s+@unbackfilled\s*>\s*0\s+RAISERROR\('DenormaliseCopilotChatUserAndTime:\s+WARNING\b.*The importer repairs these rows automatically on its next cycle",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline),
                    "Residual rows must warn from the executable @unbackfilled branch and name the importer self-healing repair that clears them.");
            }
        }

        [TestMethod]
        public void ManualScript_MopUpPassRepairsResidualRows_AfterIndexCreation_AndIsBounded()
        {
            var script = ManualScript();
            var indexCreate = script.IndexOf("creating IX_copilot_chats_time_stamp_user_id", StringComparison.Ordinal);
            var mopUp = script.IndexOf("-- 4. Mop-up pass", StringComparison.Ordinal);
            var stamp = script.IndexOf("Record the migration", StringComparison.Ordinal);

            Assert.IsTrue(indexCreate > 0, "The script must create the timestamp/user index before mop-up can seek NULL timestamps.");
            Assert.IsTrue(mopUp > indexCreate, "The mop-up must run after the index exists.");
            Assert.IsTrue(stamp > mopUp, "The mop-up must run before the migration stamp.");

            var mopUpSection = script.Substring(mopUp, stamp - mopUp);
            StringAssert.Contains(mopUpSection, "DECLARE @mopBatch  int    = 50000");
            StringAssert.Contains(mopUpSection, "UPDATE TOP (@mopBatch) c");
            StringAssert.Contains(mopUpSection, "WHERE c.time_stamp IS NULL");
            Assert.IsTrue(
                Regex.IsMatch(mopUpSection, @"WHILE\s+@mopThis\s*>\s*0\s+AND\s+@mopPasses\s*<\s*20", RegexOptions.IgnoreCase),
                "The mop-up must be bounded; it is not required to win a race against a still-running old importer.");

            using (var db = CreatePostMigrationSchema(createUserIdColumn: true, createTimeStampColumn: true, createIndex: true))
            {
                db.Execute(
                    @"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                          VALUES ('44444444-4444-4444-4444-444444444444', '2026-06-04 12:00:00', 11);
                      INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                          VALUES ('44444444-4444-4444-4444-444444444444', N'PowerPoint', NULL, NULL, NULL);");

                ExecuteSqlBatch(db.ConnectionString, MopUpBatch());

                Assert.AreEqual(1, db.Scalar(
                    @"SELECT COUNT(*) FROM dbo.copilot_chats
                      WHERE event_id = '44444444-4444-4444-4444-444444444444'
                        AND user_id = 11 AND time_stamp = '2026-06-04 12:00:00';"),
                    "The mop-up must repair rows left behind after the main watermark backfill.");
            }
        }

        [TestMethod]
        public void StampGuard_MissingPredecessorHardFails_AndDoesNotStamp()
        {
            using (var db = CreatePostMigrationSchema(createUserIdColumn: true, createTimeStampColumn: true, createIndex: true))
            {
                CreateMigrationHistoryTable(db);

                var ex = AssertSqlException(() => ExecuteSqlBatch(db.ConnectionString, StampBatch()));

                StringAssert.Contains(ex.Message, "prerequisite migration 202608250900001_IndexCopilotAccessedResourceFkColumns is missing");
                Assert.AreEqual(0, MigrationStampCount(db), "The manual scripts must still be chained in migration-id order.");
            }
        }

        [TestMethod]
        public void ManualScript_AlreadyAppliedSchemaFallsThroughToStamp()
        {
            using (var db = CreatePostMigrationSchema(createUserIdColumn: true, createTimeStampColumn: true, createIndex: true))
            {
                AddMigrationHistory(db);
                db.Execute(
                    @"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                          VALUES ('22222222-2222-2222-2222-222222222222', '2026-06-02 11:30:00', 9);
                      INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                          VALUES ('22222222-2222-2222-2222-222222222222', N'Word', NULL, 9, '2026-06-02 11:30:00');");

                ExecuteManualScript(db);

                Assert.AreEqual(1, MigrationStampCount(db),
                    "An already-applied schema must skip work and still reach the stamp rather than returning early.");
            }
        }

        [TestMethod]
        public void ManualScript_RerunIsNoOp()
        {
            using (var db = CreatePreMigrationSchema())
            {
                AddMigrationHistory(db);
                db.Execute(
                    @"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                          VALUES ('33333333-3333-3333-3333-333333333333', '2026-06-03 10:00:00', 42);
                      INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id)
                          VALUES ('33333333-3333-3333-3333-333333333333', N'Excel', NULL);");

                ExecuteManualScript(db);
                ExecuteManualScript(db);

                Assert.AreEqual(1, MigrationStampCount(db), "Re-running must not duplicate the migration-history row.");
                Assert.AreEqual(1, db.NonClusteredIndexCount("copilot_chats"), "Re-running must not create duplicate indexes.");
                Assert.AreEqual(1, db.Scalar(
                    @"SELECT COUNT(*) FROM dbo.copilot_chats
                      WHERE event_id = '33333333-3333-3333-3333-333333333333'
                        AND user_id = 42 AND time_stamp = '2026-06-03 10:00:00';"),
                    "Re-running must leave the completed backfill intact.");
            }
        }

        private static ScratchDatabase CreatePreMigrationSchema()
        {
            var db = ScratchDatabase.Create("CopilotDenormManual");
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

        private static ScratchDatabase CreatePostMigrationSchema(bool createUserIdColumn, bool createTimeStampColumn, bool createIndex)
        {
            var db = CreatePreMigrationSchema();
            if (createUserIdColumn)
            {
                db.Execute("ALTER TABLE dbo.copilot_chats ADD user_id int NULL;");
            }

            if (createTimeStampColumn)
            {
                db.Execute("ALTER TABLE dbo.copilot_chats ADD time_stamp datetime NULL;");
            }

            if (createIndex)
            {
                db.Execute(
                    $@"CREATE NONCLUSTERED INDEX [{IndexName}]
                       ON [dbo].[copilot_chats] ([time_stamp], [user_id])
                       INCLUDE ([app_host], [agent_id]);");
            }

            return db;
        }

        private static void AddMigrationHistory(ScratchDatabase db)
        {
            CreateMigrationHistoryTable(db);
            db.Execute(
                $@"INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
                   VALUES (N'{PredecessorId}', N'Common.Entities.Migrations.Configuration', 0x00, N'6.5.2');");
        }

        private static void CreateMigrationHistoryTable(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.__MigrationHistory (
                      MigrationId nvarchar(150) NOT NULL,
                      ContextKey nvarchar(300) NOT NULL,
                      Model varbinary(max) NOT NULL,
                      ProductVersion nvarchar(32) NOT NULL,
                      CONSTRAINT [PK_dbo.__MigrationHistory] PRIMARY KEY (MigrationId, ContextKey));");
        }

        private static int MigrationStampCount(ScratchDatabase db)
        {
            return Convert.ToInt32(db.Scalar(
                $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}';"));
        }

        private static void ExecuteManualScript(ScratchDatabase db)
        {
            foreach (var batch in ManualScriptBatches())
            {
                ExecuteSqlBatch(db.ConnectionString, batch);
            }
        }

        private static string StampBatch()
        {
            return ManualScriptBatches().Single(b => b.Contains("Record the migration"));
        }

        private static string MopUpBatch()
        {
            var script = ManualScript();
            var mopUp = script.IndexOf("-- 4. Mop-up pass", StringComparison.Ordinal);
            var afterMopUp = Regex.Match(script.Substring(mopUp), @"(?im)^\s*GO\s*$").Index;
            Assert.IsTrue(mopUp > 0 && afterMopUp > 0, "Could not isolate the manual script's mop-up section.");

            return @"SET NOCOUNT ON;
DECLARE @migration nvarchar(100) = N'DenormaliseCopilotChatUserAndTime';
DECLARE @stepStart datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
" + script.Substring(mopUp, afterMopUp);
        }

        private static IReadOnlyList<string> ManualScriptBatches()
        {
            return Regex.Split(ManualScript(), @"(?im)^\s*GO\s*$")
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .ToList();
        }

        private static string ManualScript()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(
                    directory.FullName,
                    "Common",
                    "Entities",
                    "Migrations",
                    MigrationId + ".manual.sql");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not find the manual migration script under the test run directory.");
        }

        private static SqlException AssertSqlException(Action action)
        {
            try
            {
                action();
            }
            catch (SqlException ex)
            {
                return ex;
            }

            Assert.Fail("Expected a SqlException.");
            return null;
        }

        private static IReadOnlyList<string> ExecuteSqlBatch(string connectionString, string sql)
        {
            var messages = new List<string>();
            using (var connection = new SqlConnection(connectionString))
            {
                connection.InfoMessage += (sender, args) => messages.Add(args.Message);
                connection.Open();
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
                {
                    command.ExecuteNonQuery();
                }
            }

            return messages;
        }
    }
}
