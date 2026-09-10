using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers <see cref="RetireUnusedAuditYammerStreamTables"/>, which removes the Exchange/Entra audit
    /// extended-property tables, the legacy Yammer message tables and the Microsoft Stream (Classic)
    /// tables - but ONLY when they are empty.
    ///
    /// The retention path is the one that matters. A tenant with historic rows in any group must come out
    /// of the upgrade with that group completely untouched: dropping a customer's fact table because an
    /// import was retired is not our call to make. The cross-group case is the subtle one -
    /// <c>yammer_msg_to_stream</c> has a foreign key to <c>stream_videos</c>, so keeping Yammer history
    /// must also keep the (empty) video lookup rather than stripping referential integrity off data we
    /// deliberately preserved.
    /// </summary>
    [TestClass]
    public class RetireUnusedAuditYammerStreamTablesMigrationTests
    {
        private const string MigrationId = "202609101000001_RetireUnusedAuditYammerStreamTables";

        /// <summary>
        /// Builds the pre-migration schema: the eight retired tables plus the parents they reference and
        /// the three reporting views that read them.
        /// </summary>
        private static ScratchDatabase CreatePreMigrationSchema()
        {
            var db = ScratchDatabase.Create("RetireTables");
            try
            {
                // Parent tables the retired schema points at. Only the columns the FKs and views need.
                db.Execute(
                    @"CREATE TABLE [dbo].[users] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [user_name] varchar(250) NOT NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[event_operations] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [operation_name] nvarchar(200) NOT NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[audit_events] (
                          [id] uniqueidentifier NOT NULL PRIMARY KEY,
                          [user_id] int NOT NULL,
                          [operation_id] int NOT NULL,
                          [time_stamp] datetime NOT NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[o365_client_applications] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [client_application_id] uniqueidentifier NOT NULL,
                          [name] nvarchar(100) NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[event_meta_exchange] (
                          [event_id] uniqueidentifier NOT NULL PRIMARY KEY,
                          [object_id] nvarchar(max) NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[event_meta_azure_ad] (
                          [event_id] uniqueidentifier NOT NULL PRIMARY KEY);");

                // The retired tables themselves, in the exact shape the migration's Down_Sql restores.
                db.Execute(RetireUnusedAuditYammerStreamTables.Down_Sql);

                // The three views the migration has to rewrite / drop, as created by the historic
                // "Audit Log Migration.sql" and the v1-0-10 migration.
                db.Execute(
                    @"CREATE VIEW [dbo].[events_view_azure_ad] AS
                          SELECT audit_events.id
                              ,[user_id]
                              ,[users].[user_name]
                              ,operation_id
                              ,event_operations.operation_name as operation
                              ,time_stamp
                              ,(select count(event_id) from audit_event_azure_ad_props where audit_event_azure_ad_props.event_id = audit_events.id) as extended_properties_count
                          FROM [dbo].audit_events
                          inner join users on audit_events.[user_id] = users.id
                          inner join event_meta_azure_ad on event_meta_azure_ad.event_id = audit_events.id
                          inner join event_operations on audit_events.operation_id = event_operations.id;");

                db.Execute(
                    @"CREATE VIEW [dbo].[events_view_exchange] AS
                          SELECT audit_events.id
                              ,[user_id]
                              ,[users].[user_name]
                              ,operation_id
                              ,event_operations.operation_name as operation
                              ,time_stamp
                              ,(select count(event_id) from audit_event_exchange_props where audit_event_exchange_props.event_id = audit_events.id) as extended_properties_count
                          FROM [dbo].audit_events
                          inner join users on audit_events.[user_id] = users.id
                          inner join event_meta_exchange on event_meta_exchange.event_id = audit_events.id
                          inner join event_operations on audit_events.operation_id = event_operations.id;");

                db.Execute(
                    @"CREATE VIEW [dbo].[events_view_stream] AS
                          SELECT audit_events.id
                              ,[user_id]
                              ,[users].[user_name]
                              ,event_operations.operation_name as operation
                              ,stream_videos.[name]
                              ,o365_client_applications.[name] as o365_client_application_name
                              ,time_stamp
                          FROM [dbo].audit_events
                          inner join users on audit_events.[user_id] = users.id
                          inner join event_meta_stream on event_meta_stream.event_id = audit_events.id
                          inner join event_operations on audit_events.operation_id = event_operations.id
                          inner join stream_videos on event_meta_stream.video_id = stream_videos.id
                          inner join o365_client_applications on event_meta_stream.o365_client_application_id = o365_client_applications.id;");

                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        private static bool TableExists(ScratchDatabase db, string table)
        {
            return db.Scalar($"SELECT CASE WHEN OBJECT_ID(N'dbo.{table}', N'U') IS NULL THEN 0 ELSE 1 END;")
                .Equals(1);
        }

        private static bool ViewExists(ScratchDatabase db, string view)
        {
            return db.Scalar($"SELECT CASE WHEN OBJECT_ID(N'dbo.{view}', N'V') IS NULL THEN 0 ELSE 1 END;")
                .Equals(1);
        }

        private static bool ViewHasColumn(ScratchDatabase db, string view, string column)
        {
            return db.Scalar(
                $@"SELECT COUNT(*) FROM sys.columns c
                   JOIN sys.views v ON v.object_id = c.object_id
                   WHERE v.name = N'{view}' AND c.name = N'{column}';").Equals(1);
        }

        private static void AssertAllRetiredTablesGone(ScratchDatabase db)
        {
            foreach (var table in new[]
            {
                "audit_event_exchange_props", "audit_event_azure_ad_props",
                "audit_event_prop_names", "audit_event_prop_vals",
                "yammer_msg_to_stream", "yammer_messages",
                "event_meta_stream", "stream_videos"
            })
            {
                Assert.IsFalse(TableExists(db, table), $"dbo.{table} should have been dropped.");
            }
        }

        [TestMethod]
        public void UpSql_AllTablesEmpty_DropsEverythingAndFixesTheViews()
        {
            using (var db = CreatePreMigrationSchema())
            {
                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);

                AssertAllRetiredTablesGone(db);

                Assert.IsFalse(ViewExists(db, "events_view_stream"),
                    "events_view_stream only reports Stream video events, so it goes with them.");

                // The other two views must survive with every column except the props count - dropping
                // them outright would break saved reports that select the remaining columns.
                Assert.IsTrue(ViewExists(db, "events_view_azure_ad"));
                Assert.IsTrue(ViewExists(db, "events_view_exchange"));
                Assert.IsFalse(ViewHasColumn(db, "events_view_azure_ad", "extended_properties_count"),
                    "extended_properties_count counted rows in a table that no longer exists.");
                Assert.IsFalse(ViewHasColumn(db, "events_view_exchange", "extended_properties_count"),
                    "extended_properties_count counted rows in a table that no longer exists.");
                Assert.IsTrue(ViewHasColumn(db, "events_view_azure_ad", "operation"),
                    "Every other column of the view must be preserved.");
                Assert.IsTrue(ViewHasColumn(db, "events_view_exchange", "user_name"),
                    "Every other column of the view must be preserved.");

                // The views must still be queryable - a view left referencing a dropped table only
                // fails when someone selects from it.
                Assert.AreEqual(0, db.Scalar("SELECT COUNT(*) FROM dbo.events_view_azure_ad;"));
                Assert.AreEqual(0, db.Scalar("SELECT COUNT(*) FROM dbo.events_view_exchange;"));
            }
        }

        [TestMethod]
        public void UpSql_IsIdempotent()
        {
            using (var db = CreatePreMigrationSchema())
            {
                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);
                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);
                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);

                AssertAllRetiredTablesGone(db);
            }
        }

        [TestMethod]
        public void UpSql_ExtendedPropertiesHoldData_RetainsThatGroupOnlyAndLeavesItsViewsAlone()
        {
            using (var db = CreatePreMigrationSchema())
            {
                db.Execute("INSERT INTO dbo.audit_event_prop_names ([name]) VALUES (N'ClientIP');");

                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);

                // Group 1 retained in full - one row anywhere in the group protects all four tables.
                Assert.IsTrue(TableExists(db, "audit_event_prop_names"), "The table holding data must survive.");
                Assert.IsTrue(TableExists(db, "audit_event_prop_vals"),
                    "The sibling lookup must survive too - the retained rows reference it.");
                Assert.IsTrue(TableExists(db, "audit_event_exchange_props"));
                Assert.IsTrue(TableExists(db, "audit_event_azure_ad_props"));
                Assert.AreEqual(1, db.Scalar("SELECT COUNT(*) FROM dbo.audit_event_prop_names;"),
                    "No DELETE may ever be issued against retained history.");

                // ...and its views left exactly as they were, so the history stays reportable.
                Assert.IsTrue(ViewHasColumn(db, "events_view_azure_ad", "extended_properties_count"),
                    "The view must not be rewritten while the table it reads is still there.");
                Assert.IsTrue(ViewHasColumn(db, "events_view_exchange", "extended_properties_count"),
                    "The view must not be rewritten while the table it reads is still there.");

                // The other two groups are independent and must still be cleaned up.
                Assert.IsFalse(TableExists(db, "yammer_msg_to_stream"));
                Assert.IsFalse(TableExists(db, "yammer_messages"));
                Assert.IsFalse(TableExists(db, "event_meta_stream"));
                Assert.IsFalse(TableExists(db, "stream_videos"));
                Assert.IsFalse(ViewExists(db, "events_view_stream"));
            }
        }

        [TestMethod]
        public void UpSql_YammerMessagesHoldData_RetainsStreamVideosBecauseItIsStillReferenced()
        {
            using (var db = CreatePreMigrationSchema())
            {
                db.Execute("INSERT INTO dbo.users ([user_name]) VALUES ('someone@contoso.onmicrosoft.com');");
                db.Execute(
                    @"INSERT INTO dbo.yammer_messages ([sender_id], [created], [yammer_msg_id], [likes_count], [followers_count])
                      SELECT TOP 1 id, '2020-01-01', 1, 0, 0 FROM dbo.users;");

                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);

                Assert.IsTrue(TableExists(db, "yammer_messages"), "The table holding data must survive.");
                Assert.IsTrue(TableExists(db, "yammer_msg_to_stream"),
                    "The link table is part of the same group, so it is retained with it.");

                // The cross-group rule: event_meta_stream still goes, but the video lookup cannot,
                // because yammer_msg_to_stream still has a foreign key to it.
                Assert.IsFalse(TableExists(db, "event_meta_stream"),
                    "Nothing references event_meta_stream, so an empty one is still dropped.");
                Assert.IsTrue(TableExists(db, "stream_videos"),
                    "stream_videos must be retained while yammer_msg_to_stream references it.");
                Assert.IsFalse(ViewExists(db, "events_view_stream"),
                    "The Stream view joins event_meta_stream, which has gone, so it must go too.");

                // Group 1 is unaffected by any of this.
                Assert.IsFalse(TableExists(db, "audit_event_prop_names"));

                // Once the Yammer history is cleared, a re-run finishes the job.
                db.Execute("DELETE FROM dbo.yammer_messages;");
                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);
                AssertAllRetiredTablesGone(db);
            }
        }

        [TestMethod]
        public void UpSql_NoTablesPresent_IsANoOp()
        {
            using (var db = ScratchDatabase.Create("RetireNothing"))
            {
                db.Execute(RetireUnusedAuditYammerStreamTables.Up_Sql);
                AssertAllRetiredTablesGone(db);
            }
        }

        [TestMethod]
        public void ManualScript_CarriesTheMigrationsUpSqlVerbatimAndStampsMigrationHistory()
        {
            var script = ManualScript();

            Assert.IsTrue(script.Contains(RetireUnusedAuditYammerStreamTables.Up_Sql.Trim()),
                "The manual script must run the migration's own Up_Sql verbatim - a hand-maintained copy "
                + "drifts, and a DBA who upgrades by hand would then get a different schema.");

            Assert.IsTrue(script.Contains("INSERT INTO dbo.__MigrationHistory"),
                "Without the stamp, EF re-applies the migration on the next installer run and the Health "
                + "page reports it as pending.");
            Assert.IsTrue(script.Contains(MigrationId),
                "The stamp must record this migration's own id.");
            Assert.IsTrue(script.Contains("202609100900001_DlpCopilotImpact"),
                "The stamp copies ContextKey/ProductVersion from the predecessor migration's row.");
            Assert.IsTrue(script.Contains("SET @model = 0x"),
                "This migration changes the EF model, so the Model blob must be embedded rather than "
                + "copied from the predecessor row.");
        }

        /// <summary>
        /// Executes the manual script exactly as a DBA running sqlcmd would - GO-separated batches on one
        /// connection with QUOTED_IDENTIFIER OFF - and proves it both completes and stamps.
        ///
        /// Every other test here runs the migration's <c>Up_Sql</c> through SqlClient, so the ~150 lines
        /// unique to the manual script (the completion guard, the __MigrationHistory stamp and the
        /// embedded model blob) were never executed by CI at all. That is precisely the surface the
        /// DenormaliseCopilotChatUserAndTime incident lived on, where a manual script refused to stamp
        /// after its schema work had succeeded and stranded the rest of the migration chain.
        /// </summary>
        [TestMethod]
        public void ManualScript_UnderSqlcmdDefaults_CompletesAndStamps()
        {
            using (var db = CreatePreMigrationSchema())
            {
                CreateMigrationHistoryWithPredecessor(db);

                db.ExecuteScript(ManualScript(), quotedIdentifierOn: false);

                AssertAllRetiredTablesGone(db);
                Assert.AreEqual(1, db.Scalar(
                    $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}';"),
                    "The schema work succeeded, so the migration must be stamped - an unstamped migration "
                    + "is reported as pending forever and blocks the next script in the chain.");
            }
        }

        /// <summary>
        /// A view permanently captures the session's QUOTED_IDENTIFIER when it is (re)created, so the
        /// migration must not leave a database in a different state depending on which client applied it.
        /// Without the explicit SET at the top of the manual script the installer path (SqlClient,
        /// QUOTED_IDENTIFIER ON) and the by-hand path (sqlcmd, OFF) produce different views.
        /// </summary>
        [TestMethod]
        public void ManualScript_RewritesViewsWithQuotedIdentifierOn_EvenUnderSqlcmdDefaults()
        {
            using (var db = CreatePreMigrationSchema())
            {
                CreateMigrationHistoryWithPredecessor(db);

                // The views start out ON, as the historic Audit Log Migration.sql creates them.
                Assert.AreEqual(1, ViewUsesQuotedIdentifier(db, "events_view_azure_ad"));
                Assert.AreEqual(1, ViewUsesQuotedIdentifier(db, "events_view_exchange"));

                db.ExecuteScript(ManualScript(), quotedIdentifierOn: false);

                Assert.AreEqual(1, ViewUsesQuotedIdentifier(db, "events_view_azure_ad"),
                    "The rewritten view must keep QUOTED_IDENTIFIER ON, or the by-hand upgrade silently "
                    + "produces a different database from the installer's.");
                Assert.AreEqual(1, ViewUsesQuotedIdentifier(db, "events_view_exchange"),
                    "The rewritten view must keep QUOTED_IDENTIFIER ON, or the by-hand upgrade silently "
                    + "produces a different database from the installer's.");
            }
        }

        /// <summary>
        /// A DBA who re-runs the script - because a batch failed, or because they are unsure whether it
        /// ran - must get a clean no-op, not a second stamp or an error.
        /// </summary>
        [TestMethod]
        public void ManualScript_IsIdempotent()
        {
            using (var db = CreatePreMigrationSchema())
            {
                CreateMigrationHistoryWithPredecessor(db);
                var script = ManualScript();

                db.ExecuteScript(script, quotedIdentifierOn: false);
                db.ExecuteScript(script, quotedIdentifierOn: false);

                AssertAllRetiredTablesGone(db);
                Assert.AreEqual(1, db.Scalar(
                    $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}';"),
                    "Re-running must not add a second history row.");
            }
        }

        /// <summary>
        /// The retention path through the manual script: a tenant with historic Yammer messages keeps that
        /// group, and must STILL be stamped. Refusing to stamp because the data is in an unexpected state
        /// is what stranded a customer upgrade before, so it is asserted explicitly here.
        /// </summary>
        [TestMethod]
        public void ManualScript_WithRetainedData_StillStamps()
        {
            using (var db = CreatePreMigrationSchema())
            {
                CreateMigrationHistoryWithPredecessor(db);
                db.Execute("INSERT INTO dbo.users ([user_name]) VALUES ('someone@contoso.onmicrosoft.com');");
                db.Execute(
                    @"INSERT INTO dbo.yammer_messages ([sender_id], [created], [yammer_msg_id], [likes_count], [followers_count])
                      SELECT TOP 1 id, '2020-01-01', 1, 0, 0 FROM dbo.users;");

                db.ExecuteScript(ManualScript(), quotedIdentifierOn: false);

                Assert.IsTrue(TableExists(db, "yammer_messages"),
                    "A group holding customer data must be retained untouched.");
                Assert.AreEqual(1, db.Scalar(
                    $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}';"),
                    "Retaining a populated group is a valid end state, so the migration must still be "
                    + "stamped - otherwise the whole manual upgrade chain stalls on the tenants the "
                    + "retention logic exists to protect.");
            }
        }

        private static object ViewUsesQuotedIdentifier(ScratchDatabase db, string view)
        {
            return db.Scalar(
                $@"SELECT CONVERT(int, uses_quoted_identifier) FROM sys.sql_modules
                   WHERE object_id = OBJECT_ID(N'dbo.{view}', N'V');");
        }

        /// <summary>
        /// EF's migration history table, holding the predecessor row the stamp copies ContextKey and
        /// ProductVersion from.
        /// </summary>
        private static void CreateMigrationHistoryWithPredecessor(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE [dbo].[__MigrationHistory] (
                      [MigrationId] nvarchar(150) NOT NULL,
                      [ContextKey] nvarchar(300) NOT NULL,
                      [Model] varbinary(max) NOT NULL,
                      [ProductVersion] nvarchar(32) NOT NULL,
                      CONSTRAINT [PK_dbo.__MigrationHistory] PRIMARY KEY ([MigrationId], [ContextKey]));");

            db.Execute(
                @"INSERT INTO [dbo].[__MigrationHistory] (MigrationId, ContextKey, Model, ProductVersion)
                  VALUES (N'202609100900001_DlpCopilotImpact',
                          N'Common.Entities.Migrations.Configuration',
                          0x00,
                          N'6.5.1');");
        }

        private static string ManualScript()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(
                    directory.FullName, "Common", "Entities", "Migrations", MigrationId + ".manual.sql");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not find the manual migration script under the test run directory.");
        }
    }
}
