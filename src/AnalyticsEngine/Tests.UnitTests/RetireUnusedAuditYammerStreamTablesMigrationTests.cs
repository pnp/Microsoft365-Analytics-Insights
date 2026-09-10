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
