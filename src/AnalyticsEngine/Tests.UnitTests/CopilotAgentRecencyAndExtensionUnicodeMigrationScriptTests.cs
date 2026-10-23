using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// The two raw-SQL migrations of 22 October 2026, applied the way a DBA applies them by hand: each manual script
    /// applies from the prior state, stamps <c>__MigrationHistory</c> with its predecessor's model, is a no-op on
    /// re-run, and refuses to stamp without its predecessor. Neither changes the EF model, so each reuses its
    /// predecessor's snapshot byte for byte.
    /// </summary>
    [TestClass]
    public class CopilotAgentRecencyAndExtensionUnicodeMigrationScriptTests
    {
        private const string PromptCategoriesId = "202610080900001_AddPromptCategories";
        private const string RecencyId = "202610221500001_CopilotAgentNameAndFlagRecency";
        private const string UnicodeId = "202610221500002_EventFileExtensionUnicode";
        private const string PredecessorModel = "0x1F8B0800AABBCCDD";

        private static string MigrationsDirectory()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Common", "Entities", "Migrations");
                if (File.Exists(Path.Combine(candidate, RecencyId + ".manual.sql"))) return candidate;
                directory = directory.Parent;
            }

            Assert.Fail("Could not find the Migrations directory by walking up from the test assembly.");
            return null;
        }

        private static string Script(string migrationId) => File.ReadAllText(Path.Combine(MigrationsDirectory(), migrationId + ".manual.sql"));

        private static ScratchDatabase NewDatabase()
        {
            var db = ScratchDatabase.Create("recencyunicodemanual");
            db.Execute(@"
CREATE TABLE dbo.__MigrationHistory (
    MigrationId nvarchar(150) NOT NULL CONSTRAINT PK___MigrationHistory PRIMARY KEY,
    ContextKey nvarchar(300) NOT NULL,
    Model varbinary(max) NOT NULL,
    ProductVersion nvarchar(32) NOT NULL
);");
            return db;
        }

        private static void Stamp(ScratchDatabase db, string migrationId)
        {
            db.Execute("INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion) VALUES ("
                + $"N'{migrationId}', N'Common.Entities.Migrations.Configuration', {PredecessorModel}, N'6.5.2');");
        }

        private static int StampCount(ScratchDatabase db, string migrationId)
            => Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{migrationId}'"));

        private static string StampedModelMatches(ScratchDatabase db, string migrationId, string predecessorId)
            => (string)db.Scalar(
                $"SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{migrationId}') = " +
                $"(SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{predecessorId}') THEN 'yes' ELSE 'no' END");

        /// <summary>type:max_length:is_nullable:scale, or null when the column does not exist.</summary>
        private static string ColumnShape(ScratchDatabase db, string table, string column)
            => (string)db.Scalar(
                $@"SELECT t.name + ':' + CAST(c.max_length AS varchar(10)) + ':' + CAST(c.is_nullable AS varchar(1)) + ':' + CAST(c.scale AS varchar(3))
                   FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                   WHERE c.object_id = OBJECT_ID('dbo.{table}') AND c.name = '{column}'");

        [TestMethod]
        public void BothManualScriptsEmbedTheirUpSqlVerbatim_AndBothSnapshotsAreThePredecessors()
        {
            StringAssert.Contains(Script(RecencyId), CopilotAgentNameAndFlagRecency.Up_Sql.Trim());
            StringAssert.Contains(Script(UnicodeId), EventFileExtensionUnicode.Up_Sql.Trim());

            var predecessorSnapshot = File.ReadAllBytes(Path.Combine(MigrationsDirectory(), PromptCategoriesId + ".resx"));
            CollectionAssert.AreEqual(predecessorSnapshot, File.ReadAllBytes(Path.Combine(MigrationsDirectory(), RecencyId + ".resx")));
            CollectionAssert.AreEqual(predecessorSnapshot, File.ReadAllBytes(Path.Combine(MigrationsDirectory(), UnicodeId + ".resx")));
        }

        #region CopilotAgentNameAndFlagRecency

        /// <summary>copilot_agents as the CopilotAgents migration and its successors left it, with one row.</summary>
        private static void CreateAgentsTable(ScratchDatabase db)
        {
            db.Execute(@"
CREATE TABLE dbo.copilot_agents (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_dbo.copilot_agents] PRIMARY KEY,
    agent_id nvarchar(max) NULL,
    name nvarchar(100) NULL,
    is_custom_agent bit NULL
);
INSERT INTO dbo.copilot_agents (agent_id, name, is_custom_agent) VALUES (N'00000000-0000-0000-0000-000000000001', N'Contoso Helpdesk', 1);");
        }

        [TestMethod]
        public void Recency_AddsThreeNullableColumns_LeavesRowsUnknown_AndStampsWithThePredecessorsModel()
        {
            using (var db = NewDatabase())
            {
                CreateAgentsTable(db);
                Stamp(db, PromptCategoriesId);

                db.ExecuteScript(Script(RecencyId), quotedIdentifierOn: false);

                Assert.AreEqual("datetime2:7:1:3", ColumnShape(db, "copilot_agents", "name_seen_utc"));
                Assert.AreEqual("datetime2:7:1:3", ColumnShape(db, "copilot_agents", "flag_seen_utc"));
                Assert.AreEqual("bit:1:1:0", ColumnShape(db, "copilot_agents", "name_is_fallback"));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM dbo.copilot_agents WHERE name = N'Contoso Helpdesk' AND is_custom_agent = 1 " +
                    "AND name_seen_utc IS NULL AND flag_seen_utc IS NULL AND name_is_fallback IS NULL")),
                    "An existing row keeps its values, and its recency is unknown.");
                Assert.AreEqual(1, StampCount(db, RecencyId));
                Assert.AreEqual("yes", StampedModelMatches(db, RecencyId, PromptCategoriesId));
            }
        }

        [TestMethod]
        public void Recency_ReRunIsACleanNoOp_AndAnAlreadyAppliedSchemaStillStamps()
        {
            using (var db = NewDatabase())
            {
                CreateAgentsTable(db);
                Stamp(db, PromptCategoriesId);
                db.Execute(CopilotAgentNameAndFlagRecency.Up_Sql);
                db.Execute("UPDATE dbo.copilot_agents SET name_seen_utc = '2026-10-01T09:00:00', name_is_fallback = 0;");

                db.ExecuteScript(Script(RecencyId), quotedIdentifierOn: false);
                db.ExecuteScript(Script(RecencyId), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db, RecencyId), "The already-applied path stamps, once.");
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM dbo.copilot_agents WHERE name_seen_utc = '2026-10-01T09:00:00' AND name_is_fallback = 0")),
                    "A re-run keeps what the importer recorded.");
            }
        }

        [TestMethod]
        public void Recency_WithoutThePredecessor_RefusesToStamp()
        {
            using (var db = NewDatabase())
            {
                CreateAgentsTable(db);
                try
                {
                    db.ExecuteScript(Script(RecencyId), quotedIdentifierOn: false);
                    Assert.Fail("The script must refuse to stamp when the predecessor is missing.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, PromptCategoriesId);
                    StringAssert.Contains(ex.Message, "NOT stamped");
                }

                Assert.AreEqual(0, StampCount(db, RecencyId));
            }
        }

        [TestMethod]
        public void Recency_DownSql_RemovesTheColumns()
        {
            using (var db = NewDatabase())
            {
                CreateAgentsTable(db);
                db.Execute(CopilotAgentNameAndFlagRecency.Up_Sql);
                db.Execute(CopilotAgentNameAndFlagRecency.Down_Sql);

                Assert.IsNull(ColumnShape(db, "copilot_agents", "name_seen_utc"));
                Assert.IsNull(ColumnShape(db, "copilot_agents", "flag_seen_utc"));
                Assert.IsNull(ColumnShape(db, "copilot_agents", "name_is_fallback"));
            }
        }

        #endregion

        #region EventFileExtensionUnicode

        /// <summary>
        /// event_file_ext as Create DB.sql made it (varchar(250) NOT NULL and the unique IX_event_file_ext), with the two
        /// tables that point at it: copilot_event_files through a foreign key, event_meta_sharepoint without one. 'ss' and
        /// the German sharp s are different as varchar under a SQL collation such as the Azure SQL default, and equal as
        /// nvarchar. The collation is stated so the test does not depend on the server's.
        /// </summary>
        private static void CreateExtensionTables(ScratchDatabase db)
        {
            db.Execute(@"
CREATE TABLE dbo.event_file_ext (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_event_file_ext PRIMARY KEY,
    extension_name varchar(250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
);
CREATE UNIQUE NONCLUSTERED INDEX IX_event_file_ext ON dbo.event_file_ext (extension_name);
CREATE TABLE dbo.copilot_event_files (
    copilot_chat_id uniqueidentifier NOT NULL PRIMARY KEY,
    file_extension_id int NULL CONSTRAINT [FK_dbo.event_copilot_files_dbo.event_file_ext_file_extension_id]
        FOREIGN KEY REFERENCES dbo.event_file_ext (id)
);
CREATE TABLE dbo.event_meta_sharepoint (event_id uniqueidentifier NOT NULL PRIMARY KEY, file_extension_id int NULL);
SET IDENTITY_INSERT dbo.event_file_ext ON;
INSERT INTO dbo.event_file_ext (id, extension_name) VALUES (1, 'docx'), (2, 'ss'), (3, CHAR(223)), (4, 'pdf');
SET IDENTITY_INSERT dbo.event_file_ext OFF;
INSERT INTO dbo.copilot_event_files VALUES ('00000000-0000-0000-0000-000000000001', 3), ('00000000-0000-0000-0000-000000000002', 1);
INSERT INTO dbo.event_meta_sharepoint VALUES ('00000000-0000-0000-0000-000000000003', 3), ('00000000-0000-0000-0000-000000000004', 4);");
            db.Execute("CREATE VIEW dbo.events_view_sharepoint AS SELECT m.event_id, e.extension_name FROM dbo.event_meta_sharepoint AS m LEFT JOIN dbo.event_file_ext AS e ON e.id = m.file_extension_id;");
        }

        private static string ExtensionCollation(ScratchDatabase db)
            => (string)db.Scalar("SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.event_file_ext') AND name = 'extension_name'");

        [TestMethod]
        public void Unicode_ConvertsTheColumn_KeepsItsCollation_MergesRowsThatBecomeEqual_AndStamps()
        {
            using (var db = NewDatabase())
            {
                CreateExtensionTables(db);
                Stamp(db, RecencyId);
                var collation = ExtensionCollation(db);

                db.ExecuteScript(Script(UnicodeId), quotedIdentifierOn: false);

                Assert.AreEqual("nvarchar:500:0:0", ColumnShape(db, "event_file_ext", "extension_name"), "nvarchar(250), still NOT NULL.");
                Assert.AreEqual(collation, ExtensionCollation(db), "The column keeps its collation.");
                Assert.IsTrue(db.IndexHasColumn("event_file_ext", "IX_event_file_ext", "extension_name", 1, included: false));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.event_file_ext') AND name = 'IX_event_file_ext' AND is_unique = 1")));
                Assert.AreEqual("1,2,4", db.Scalar(
                    "SELECT STUFF((SELECT ',' + CAST(id AS varchar(10)) FROM dbo.event_file_ext ORDER BY id FOR XML PATH('')), 1, 1, '')"),
                    "The sharp s row is merged into the older 'ss' row; nothing else is touched.");
                Assert.AreEqual(2, Convert.ToInt32(db.Scalar("SELECT file_extension_id FROM dbo.copilot_event_files WHERE copilot_chat_id = '00000000-0000-0000-0000-000000000001'")),
                    "A foreign-key reference to the merged row is re-pointed.");
                Assert.AreEqual(2, Convert.ToInt32(db.Scalar("SELECT file_extension_id FROM dbo.event_meta_sharepoint WHERE event_id = '00000000-0000-0000-0000-000000000003'")),
                    "So is event_meta_sharepoint's, which has no foreign key.");
                Assert.AreEqual(4, Convert.ToInt32(db.Scalar("SELECT file_extension_id FROM dbo.event_meta_sharepoint WHERE event_id = '00000000-0000-0000-0000-000000000004'")));
                Assert.AreEqual("nvarchar", db.Scalar(
                    "SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.events_view_sharepoint') AND name = 'extension_name'"),
                    "A view over the table is refreshed.");

                // The point of it all: two Greek extensions the varchar column stored as the same '?????' are two rows.
                db.Execute("INSERT INTO dbo.event_file_ext (extension_name) VALUES (N'λόγος'), (N'χώρος');");
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.event_file_ext WHERE extension_name = N'λόγος'")));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.event_file_ext WHERE extension_name = N'χώρος'")));

                Assert.AreEqual(1, StampCount(db, UnicodeId));
                Assert.AreEqual("yes", StampedModelMatches(db, UnicodeId, RecencyId));
            }
        }

        [TestMethod]
        public void Unicode_ReRunIsACleanNoOp_AndAnAlreadyAppliedSchemaStillStamps()
        {
            using (var db = NewDatabase())
            {
                CreateExtensionTables(db);
                Stamp(db, RecencyId);
                db.Execute(EventFileExtensionUnicode.Up_Sql);
                db.Execute(EventFileExtensionUnicode.Up_Sql);
                db.Execute("INSERT INTO dbo.event_file_ext (extension_name) VALUES (N'λόγος');");
                const string indexStorage = "SELECT p.partition_id FROM sys.indexes AS i JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id " +
                    "WHERE i.object_id = OBJECT_ID('dbo.event_file_ext') AND i.name = 'IX_event_file_ext'";
                var storage = db.Scalar(indexStorage);

                db.ExecuteScript(Script(UnicodeId), quotedIdentifierOn: false);
                db.ExecuteScript(Script(UnicodeId), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db, UnicodeId), "The already-applied path stamps, once.");
                Assert.AreEqual(4, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.event_file_ext")));
                Assert.AreEqual(storage, db.Scalar(indexStorage), "The index is not rebuilt.");
            }
        }

        [TestMethod]
        public void Unicode_WithoutThePredecessor_RefusesToStamp()
        {
            using (var db = NewDatabase())
            {
                CreateExtensionTables(db);
                try
                {
                    db.ExecuteScript(Script(UnicodeId), quotedIdentifierOn: false);
                    Assert.Fail("The script must refuse to stamp when the predecessor is missing.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, RecencyId);
                    StringAssert.Contains(ex.Message, "NOT stamped");
                }

                Assert.AreEqual(0, StampCount(db, UnicodeId));
            }
        }

        /// <summary>
        /// Another index on the column would make ALTER COLUMN fail after IX_event_file_ext had been dropped, so the
        /// migration refuses before changing anything. User-created statistics do not stop it: they are dropped.
        /// </summary>
        [TestMethod]
        public void Unicode_AnotherIndexOnTheColumn_StopsItBeforeAnyChange_ButUserStatisticsDoNot()
        {
            using (var db = NewDatabase())
            {
                CreateExtensionTables(db);
                Stamp(db, RecencyId);
                db.Execute("CREATE STATISTICS ST_contoso_extension ON dbo.event_file_ext (extension_name);");
                db.Execute("CREATE NONCLUSTERED INDEX IX_contoso_extension ON dbo.event_file_ext (extension_name, id);");

                try
                {
                    db.ExecuteScript(Script(UnicodeId), quotedIdentifierOn: false);
                    Assert.Fail("The migration must refuse while another index depends on the column.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, "IX_contoso_extension");
                    StringAssert.Contains(ex.Message, "Nothing was changed");
                }
                Assert.AreEqual("varchar:250:0:0", ColumnShape(db, "event_file_ext", "extension_name"));
                Assert.IsTrue(db.IndexExists("event_file_ext", "IX_event_file_ext"), "The unique index must still be there.");
                Assert.AreEqual(0, StampCount(db, UnicodeId));

                db.Execute("DROP INDEX IX_contoso_extension ON dbo.event_file_ext;");
                db.ExecuteScript(Script(UnicodeId), quotedIdentifierOn: false);

                Assert.AreEqual("nvarchar:500:0:0", ColumnShape(db, "event_file_ext", "extension_name"));
                Assert.AreEqual(1, StampCount(db, UnicodeId));
            }
        }

        #endregion
    }
}
