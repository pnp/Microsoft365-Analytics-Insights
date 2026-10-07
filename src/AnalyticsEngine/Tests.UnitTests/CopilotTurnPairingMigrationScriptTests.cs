using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// The by-hand upgrade path for <c>202610071400001_CopilotTurnPairing</c> (issue #699): the script applies
    /// from the prior state, stamps <c>__MigrationHistory</c> with the predecessor's model, is a no-op on re-run,
    /// and refuses to stamp without its predecessor.
    /// </summary>
    [TestClass]
    public class CopilotTurnPairingMigrationScriptTests
    {
        private const string MigrationId = "202610071400001_CopilotTurnPairing";
        private const string PredecessorId = "202610021200001_LicenceHistory";
        private const string PredecessorModel = "0x1F8B0800AABBCCDD";

        private static string Script()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Common", "Entities", "Migrations", MigrationId + ".manual.sql");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {MigrationId}.manual.sql by walking up from the test assembly.");
            return null;
        }

        /// <summary>The prior state: copilot_chats as the previous migration left it, with one row.</summary>
        private static ScratchDatabase NewDatabase()
        {
            var db = ScratchDatabase.Create("turnpairingmanual");
            db.Execute(@"
CREATE TABLE dbo.__MigrationHistory (
    MigrationId nvarchar(150) NOT NULL CONSTRAINT PK___MigrationHistory PRIMARY KEY,
    ContextKey nvarchar(300) NOT NULL,
    Model varbinary(max) NOT NULL,
    ProductVersion nvarchar(32) NOT NULL
);
CREATE TABLE dbo.copilot_chats (
    event_id uniqueidentifier NOT NULL CONSTRAINT [PK_dbo.copilot_chats] PRIMARY KEY,
    app_host nvarchar(max) NULL,
    agent_id int NULL,
    thread_id nvarchar(450) NULL,
    user_id int NULL,
    time_stamp datetime NULL
);
INSERT INTO dbo.copilot_chats (event_id, app_host, user_id, time_stamp)
VALUES ('00000000-0000-0000-0000-000000000001', N'Office', 1, '2026-10-01T09:00:00');");
            return db;
        }

        private static void StampPredecessor(ScratchDatabase db)
        {
            db.Execute("INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion) VALUES ("
                + $"N'{PredecessorId}', N'Common.Entities.Migrations.Configuration', {PredecessorModel}, N'6.5.2');");
        }

        private static int StampCount(ScratchDatabase db)
            => Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'"));

        [TestMethod]
        public void TheScriptEmbedsTheMigrationsOwnUpSqlVerbatim()
        {
            StringAssert.Contains(Script(), CopilotTurnPairing.Up_Sql.Trim(),
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade applies something different from the installer.");
        }

        [TestMethod]
        public void RunningItAddsTheSchemaAndStampsTheMigrationWithThePredecessorsModel()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual("nvarchar:900:1", db.Scalar(
                    @"SELECT t.name + ':' + CAST(c.max_length AS varchar(10)) + ':' + CAST(c.is_nullable AS varchar(1))
                      FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                      WHERE c.object_id = OBJECT_ID('dbo.copilot_chats') AND c.name = 'conversation_id'"),
                    "conversation_id is a NULLable, Unicode-safe nvarchar(450).");
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'copilot_chat_duplicates'")));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_copilot_chat_duplicates_copilot_chats' AND delete_referential_action_desc = 'CASCADE'")));
                Assert.IsTrue(db.IndexHasColumn("copilot_chat_duplicates", "IX_copilot_chat_duplicates_counted_event_id", "counted_event_id", 1, included: false));
                Assert.IsTrue(db.IndexHasColumn("copilot_chat_duplicates", "IX_copilot_chat_duplicates_time_stamp", "time_stamp", 1, included: false));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.copilot_chats")), "No row is touched.");
                Assert.AreEqual(0, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.copilot_chats') AND index_id > 1")),
                    "No index is built on copilot_chats.");

                Assert.AreEqual(1, StampCount(db));
                Assert.AreEqual("yes", db.Scalar(
                    $"SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}') = " +
                    $"(SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}') THEN 'yes' ELSE 'no' END"),
                    "The migration does not change the EF model, so the stamp carries the predecessor's model.");
            }
        }

        [TestMethod]
        public void ReRunningItIsACleanNoOp()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                db.Execute(@"INSERT INTO dbo.copilot_chats (event_id, app_host, conversation_id)
                             VALUES ('00000000-0000-0000-0000-000000000002', N'm365copilot', N'44444444-4444-4444-4444-444444444444');
                             INSERT INTO dbo.copilot_chat_duplicates (event_id, time_stamp, counted_event_id, reason)
                             VALUES ('00000000-0000-0000-0000-000000000002', '2026-10-01T08:59:55', '00000000-0000-0000-0000-000000000001', 1);");

                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db), "The stamp must not be duplicated.");
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.copilot_chat_duplicates")), "A re-run keeps the pairs.");
                Assert.AreEqual("44444444-4444-4444-4444-444444444444", db.Scalar(
                    "SELECT conversation_id FROM dbo.copilot_chats WHERE event_id = '00000000-0000-0000-0000-000000000002'"));
            }
        }

        [TestMethod]
        public void TheSchemaIsAlreadyThere_StillStamps()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.Execute(CopilotTurnPairing.Up_Sql);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db), "The already-applied path must still reach the stamp.");
            }
        }

        [TestMethod]
        public void WithoutThePredecessorItRefusesToStamp()
        {
            using (var db = NewDatabase())
            {
                try
                {
                    db.ExecuteScript(Script(), quotedIdentifierOn: false);
                    Assert.Fail("The script must refuse to stamp when the predecessor is missing.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, PredecessorId);
                    StringAssert.Contains(ex.Message, "NOT stamped");
                }

                Assert.AreEqual(0, StampCount(db));
            }
        }

        [TestMethod]
        public void DeletingAnInteraction_CascadesToItsDuplicateRow()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                db.Execute(@"INSERT INTO dbo.copilot_chats (event_id, app_host) VALUES ('00000000-0000-0000-0000-000000000002', N'm365copilot');
                             INSERT INTO dbo.copilot_chat_duplicates (event_id, time_stamp, counted_event_id, reason)
                             VALUES ('00000000-0000-0000-0000-000000000002', '2026-10-01T08:59:55', '00000000-0000-0000-0000-000000000001', 1);
                             DELETE FROM dbo.copilot_chats WHERE event_id = '00000000-0000-0000-0000-000000000002';");

                Assert.AreEqual(0, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.copilot_chat_duplicates")));
            }
        }

        [TestMethod]
        public void DownSql_RemovesTheSchema()
        {
            using (var db = NewDatabase())
            {
                db.Execute(CopilotTurnPairing.Up_Sql);
                db.Execute(CopilotTurnPairing.Down_Sql);

                Assert.AreEqual(0, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'copilot_chat_duplicates'")));
                Assert.IsNull(db.Scalar("SELECT COL_LENGTH('dbo.copilot_chats', 'conversation_id')") as int?);
            }
        }
    }
}
