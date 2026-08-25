using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Data.SqlClient;
using System.Linq;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Upgrade rehearsal: build a database at the CURRENT STABLE schema (the newest migration on main),
    /// put data in it, then run the upgrade to the newest migration on dev - which is what an existing
    /// customer's database actually goes through.
    ///
    /// Migration tests that start from an empty database prove the migrations RUN. They do not prove the
    /// upgrade is safe on a populated database, and the two differ: 202608191533567_DeprecateTeamsAddons
    /// only drops the Teams add-on tables when they are EMPTY, so its behaviour on a real tenant is the
    /// path an empty-database test never reaches.
    ///
    /// NO CUSTOMER DATA - every value is generated (Contoso, zeroed GUIDs).
    /// </summary>
    [TestClass]
    public class UpgradeFromStableTests
    {
        // Newest migration on main at the time of writing.
        private const string StableMigrationId = "202608131055001_IndexReportDateQueries";

        private const string GreekUrl =
            "https://contoso.sharepoint.com/sites/example/Shared Documents/" +
            "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5.pdf";

        [TestMethod]
        public void Upgrade_FromStable_WithTeamsAddonData_RetainsTheTables()
        {
            RunUpgrade(populateTeamsAddons: true, dbName: "UpgradeFromStable_WithData", expectTablesRetained: true);
        }

        [TestMethod]
        public void Upgrade_FromStable_WithEmptyTeamsAddons_DropsTheTables()
        {
            RunUpgrade(populateTeamsAddons: false, dbName: "UpgradeFromStable_Empty", expectTablesRetained: false);
        }

        private static void RunUpgrade(bool populateTeamsAddons, string dbName, bool expectTablesRetained)
        {
            var connectionString = ScratchConnectionString(dbName);
            DropDatabase(connectionString, dbName);

            try
            {
                // 1. Stand up the database exactly as a customer on the current stable release has it.
                var migrator = NewMigrator(connectionString);
                migrator.Update(StableMigrationId);
                Assert.AreEqual(StableMigrationId, LatestApplied(connectionString),
                    "Database should be at the stable release's newest migration before the upgrade.");

                // 2. Put data in it. Row counts are checked again afterwards, so an upgrade that silently
                //    truncates a fact table fails here rather than on a customer tenant.
                var userId = SeedUser(connectionString);
                SeedUrl(connectionString);
                if (populateTeamsAddons) SeedTeamsAddons(connectionString, userId);

                var usersBefore = ScalarLong(connectionString, "SELECT COUNT_BIG(*) FROM dbo.users");
                var urlsBefore = ScalarLong(connectionString, "SELECT COUNT_BIG(*) FROM dbo.urls");
                var addonsBefore = populateTeamsAddons
                    ? ScalarLong(connectionString, "SELECT COUNT_BIG(*) FROM dbo.teams_addons")
                    : 0;

                // 3. The upgrade itself.
                NewMigrator(connectionString).Update();

                // 4. Everything that was there is still there.
                Assert.AreEqual(usersBefore, ScalarLong(connectionString, "SELECT COUNT_BIG(*) FROM dbo.users"),
                    "Upgrade changed the users row count.");
                Assert.AreEqual(urlsBefore, ScalarLong(connectionString, "SELECT COUNT_BIG(*) FROM dbo.urls"),
                    "Upgrade changed the urls row count.");
                Assert.AreEqual(GreekUrl, ScalarString(connectionString, "SELECT TOP 1 full_url FROM dbo.urls"),
                    "Non-ASCII URL did not survive the upgrade intact.");

                // 5. The upgrade actually reached the newest migration in the assembly.
                //
                //    Deliberately compared against the migrator's own list rather than a hard-coded id: a
                //    literal here would silently stop covering the newest migration the moment one is added,
                //    which is exactly when this test matters most.
                var newestAvailable = NewMigrator(connectionString).GetLocalMigrations().Last();
                Assert.AreEqual(newestAvailable, LatestApplied(connectionString),
                    "Upgrade did not reach the newest migration in the assembly.");

                // 6. The new tables this release adds exist - the usage-report tables, the Copilot audit
                //    tables that were previously parsed and dropped, and the interaction-history tables.
                var expectedTables = new[]
                {
                    // Graph Copilot usage reports
                    "copilot_usage_report_import_log", "copilot_usage_user_activity_log", "copilot_user_count_log",
                    // Copilot audit fields that used to be discarded
                    "copilot_event_accessed_resource_actions", "copilot_ai_system_plugins",
                    "copilot_event_context_types", "copilot_event_ai_system_plugins", "copilot_event_contexts",
                    // Copilot AI interaction history
                    "copilot_interactions", "copilot_interaction_sessions", "copilot_interaction_keywords",
                    "copilot_interaction_user_watermarks", "copilot_interaction_import_log",
                    "copilot_interaction_app_classes", "copilot_interaction_conversation_types",
                    "copilot_interaction_types", "copilot_interaction_locales", "copilot_interaction_devices",
                };

                foreach (var table in expectedTables)
                {
                    Assert.AreEqual(1L, ScalarLong(connectionString,
                        "SELECT COUNT_BIG(*) FROM sys.tables WHERE name = '" + table + "'"),
                        "Expected new table " + table + " after upgrade.");
                }

                // 7. Columns added to existing tables landed too. A migration that creates its new tables but
                //    silently skips an AddColumn leaves the importer writing to a column that isn't there.
                foreach (var column in new[]
                {
                    "copilot_chats.thread_id", "copilot_chats.client_region", "copilot_chats.copilot_log_version",
                    "copilot_event_messages.size", "copilot_event_messages.is_prompt",
                    "copilot_event_accessed_resources.action_id",
                    "copilot_event_accessed_resources.list_item_unique_id_id",
                    "copilot_ai_models.version",
                })
                {
                    var parts = column.Split('.');
                    Assert.AreEqual(1L, ScalarLong(connectionString,
                        "SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo." + parts[0] + "') AND name = '" + parts[1] + "'"),
                        "Expected new column " + column + " after upgrade.");
                }

                // 8. DeprecateTeamsAddons drops its tables only when they were empty.
                var addonTableCount = ScalarLong(connectionString,
                    "SELECT COUNT_BIG(*) FROM sys.tables WHERE name IN ('teams_addons','teams_addons_log','teams_addons_user_installed_log')");

                if (expectTablesRetained)
                {
                    Assert.AreEqual(3L, addonTableCount,
                        "Teams add-on tables held data and MUST be retained - dropping a populated customer fact table is not ours to decide.");
                    Assert.AreEqual(addonsBefore, ScalarLong(connectionString, "SELECT COUNT_BIG(*) FROM dbo.teams_addons"),
                        "Retained Teams add-on data was modified by the upgrade.");
                }
                else
                {
                    Assert.AreEqual(0L, addonTableCount,
                        "Teams add-on tables were empty and should have been dropped.");
                }
            }
            finally
            {
                DropDatabase(connectionString, dbName);
            }
        }

        private static DbMigrator NewMigrator(string connectionString)
        {
            var config = new Configuration
            {
                TargetDatabase = new DbConnectionInfo(connectionString, "System.Data.SqlClient")
            };
            return new DbMigrator(config);
        }

        private static string LatestApplied(string connectionString)
        {
            return ScalarString(connectionString,
                "SELECT TOP 1 MigrationId FROM __MigrationHistory ORDER BY LEFT(MigrationId, 15) DESC");
        }

        private static int SeedUser(string connectionString)
        {
            return (int)ScalarLong(connectionString,
                "INSERT INTO dbo.users (user_name) OUTPUT CAST(INSERTED.id AS bigint) " +
                "VALUES (N'analytics.user@contoso.com')");
        }

        private static void SeedUrl(string connectionString)
        {
            Execute(connectionString, "INSERT INTO dbo.urls (full_url) VALUES (N'" + GreekUrl.Replace("'", "''") + "')");
        }

        private static void SeedTeamsAddons(string connectionString, int userId)
        {
            Execute(connectionString,
                "INSERT INTO dbo.teams_addons (addon_type, published_state, graph_id, name) VALUES " +
                "(1, N'published', N'00000000-0000-0000-0000-000000000000', N'Contoso Approvals'), " +
                "(1, N'published', N'00000000-0000-0000-0000-000000000001', N'Contoso Polls');");
        }

        private static string ScratchConnectionString(string dbName)
        {
            var configured = System.Configuration.ConfigurationManager
                .ConnectionStrings["SPOInsightsEntities"].ConnectionString;
            return new SqlConnectionStringBuilder(configured) { InitialCatalog = dbName }.ConnectionString;
        }

        private static void DropDatabase(string connectionString, string dbName)
        {
            var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
            using (var c = new SqlConnection(master))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        "IF DB_ID('" + dbName + "') IS NOT NULL BEGIN " +
                        "ALTER DATABASE [" + dbName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                        "DROP DATABASE [" + dbName + "]; END";
                    cmd.CommandTimeout = 300;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();
                using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.CommandTimeout = 300; cmd.ExecuteNonQuery(); }
            }
        }

        private static long ScalarLong(string connectionString, string sql)
        {
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = sql; cmd.CommandTimeout = 300;
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
        }

        private static string ScalarString(string connectionString, string sql)
        {
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = sql; cmd.CommandTimeout = 300;
                    var v = cmd.ExecuteScalar();
                    return v == null || v == DBNull.Value ? null : v.ToString();
                }
            }
        }
    }
}
