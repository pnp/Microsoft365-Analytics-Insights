using App.ControlPanel.Engine;
using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins the manifest names of every embedded resource the product loads at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several features find their SQL by asking the assembly for an embedded resource, either by exact
    /// name or by prefix. A resource's manifest name is derived from the project's root namespace and the
    /// file's folder path, so it is <b>silently</b> sensitive to project-file changes - moving a file,
    /// renaming a folder, changing <c>RootNamespace</c>, or converting a project to the SDK style, which
    /// globs resources rather than listing them.
    /// </para>
    /// <para>
    /// Nothing else catches that. The code compiles, the build succeeds, and the failure appears only when
    /// the feature runs. The worst case is <see cref="DatabaseUpgrader"/>: it discovers its SQL upgrade
    /// scripts by prefix, so a changed prefix does not throw - it simply finds <b>zero</b> scripts and the
    /// database upgrade quietly does nothing.
    /// </para>
    /// <para>
    /// These tests exist mainly to make the planned SDK-style project conversion (issue #510) safe, but
    /// they guard any accidental rename just as well. If one fails, do not update the expected value
    /// without first checking what loads that resource.
    /// </para>
    /// </remarks>
    [TestClass]
    public class EmbeddedResourceNameTests
    {
        /// <summary>
        /// The prefix <see cref="DatabaseUpgrader"/> uses to discover SQL upgrade scripts. Kept as a
        /// literal rather than referencing the private constant, so the test fails if the constant moves
        /// away from the resources it is meant to match.
        /// </summary>
        const string SqlExtensionsPrefix = "App.ControlPanel.Engine.SqlExtentions";

        static readonly string[] ExpectedControlPanelEngineResources =
        {
            "App.ControlPanel.Engine.Properties.Resources.resources",
            "App.ControlPanel.Engine.SqlExtentions.Profiling-01-CommandExecute.sql",
            "App.ControlPanel.Engine.SqlExtentions.Profiling-02-IndexOptimize.sql",
            "App.ControlPanel.Engine.SqlExtentions.Profiling-03-CreateSchema.sql",
        };

        static readonly string[] ExpectedImporterEngineResources =
        {
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.common_upsert_copilot_agents.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_activity_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_sp_copilot_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.repair_denormalised_copilot_columns.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp.SQL.insert_copilot_dlp_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp.SQL.insert_dlp_rule_matches_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_copilot_studio_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_share_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_share_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_bi_events_from_staging_table.sql",
            "WebJob.Office365ActivityImporter.Engine.Properties.Resources.resources",
            "WebJob.Office365ActivityImporter.Engine.Resources.Product_names_and_service_plan_identifiers_for_licensing.csv",
        };

        /// <summary>
        /// Lives outside the project directory (<c>..\..\..\Clean Old Data Data.sql</c>), which makes its
        /// manifest name particularly fragile - and the spaces in it are part of the name.
        /// </summary>
        const string CleanOldDataResource = "Common.Entities.Clean Old Data Data.sql";

        static Assembly ControlPanelEngine => typeof(DatabaseUpgrader).Assembly;
        static Assembly ImporterEngine => typeof(WebJob.Office365ActivityImporter.Engine.ActivityImportCache).Assembly;
        static Assembly Entities => typeof(AnalyticsEntitiesContext).Assembly;

        [TestMethod]
        public void ControlPanelEngine_EmbeddedResourceNames_AreUnchanged()
        {
            AssertResourceNames(ControlPanelEngine, ExpectedControlPanelEngineResources);
        }

        [TestMethod]
        public void ImporterEngine_EmbeddedResourceNames_AreUnchanged()
        {
            AssertResourceNames(ImporterEngine, ExpectedImporterEngineResources);
        }

        /// <summary>
        /// The upgrade path that fails silently rather than loudly.
        /// </summary>
        [TestMethod]
        public void DatabaseUpgrader_StillFindsItsSqlScriptsByPrefix()
        {
            var found = ControlPanelEngine.GetManifestResourceNames()
                .Where(n => n.StartsWith(SqlExtensionsPrefix))
                .OrderBy(n => n)
                .ToList();

            Assert.AreEqual(3, found.Count,
                $"DatabaseUpgrader discovers SQL upgrade scripts with the prefix '{SqlExtensionsPrefix}'. " +
                $"It found {found.Count} instead of 3. This does NOT throw at runtime - the upgrade simply " +
                "applies no scripts, so a customer database is silently left un-upgraded. Found: " +
                string.Join(", ", found));
        }

        /// <summary>
        /// Loaded by exact name from outside the project directory.
        /// </summary>
        [TestMethod]
        public void Entities_StillEmbedsTheDataCleanupScript()
        {
            using (var stream = Entities.GetManifestResourceStream(CleanOldDataResource))
            {
                Assert.IsNotNull(stream,
                    $"'{CleanOldDataResource}' is no longer embedded under that name. It is included from " +
                    @"outside the project directory (..\..\..\Clean Old Data Data.sql), so its manifest " +
                    "name is especially sensitive to project-file changes.");
            }
        }

        /// <summary>
        /// Every EF migration must keep its compiled <c>.resources</c> entry, because those carry the
        /// gzipped model snapshots EF compares the live model against.
        /// </summary>
        /// <remarks>
        /// If a project change alters these names, EF cannot find the snapshot for the latest migration,
        /// decides the model has changed, and - with <c>AutomaticMigrationsEnabled</c> - either
        /// auto-migrates or throws <c>AutomaticDataLossException</c> on every context construction.
        /// </remarks>
        [TestMethod]
        public void Entities_EveryMigrationKeepsItsModelSnapshotResource()
        {
            var migrationTypes = Entities.GetTypes()
                .Where(t => t.Namespace == "Common.Entities.Migrations"
                            && typeof(System.Data.Entity.Migrations.Infrastructure.IMigrationMetadata).IsAssignableFrom(t))
                .ToList();

            Assert.IsTrue(migrationTypes.Count > 0, "Found no EF migrations at all - the test is not looking in the right place.");

            var resourceNames = new HashSet<string>(Entities.GetManifestResourceNames());

            var missing = migrationTypes
                .Select(t => t.FullName + ".resources")
                .Where(expected => !resourceNames.Contains(expected))
                .OrderBy(n => n)
                .ToList();

            Assert.AreEqual(0, missing.Count,
                $"{missing.Count} of {migrationTypes.Count} migrations have no matching model-snapshot " +
                "resource. EF resolves the snapshot by the migration's type name, so these migrations " +
                "would be treated as having no snapshot. Missing: " + string.Join(", ", missing.Take(5)));
        }

        static void AssertResourceNames(Assembly assembly, string[] expected)
        {
            var actual = assembly.GetManifestResourceNames().OrderBy(n => n).ToArray();

            CollectionAssert.AreEqual(expected.OrderBy(n => n).ToArray(), actual,
                $"The embedded resource names in {assembly.GetName().Name} have changed. These are loaded " +
                "by exact name or by prefix at runtime, so a rename breaks the feature silently rather " +
                "than at build time. Expected:\n  " + string.Join("\n  ", expected.OrderBy(n => n)) +
                "\nActual:\n  " + string.Join("\n  ", actual));
        }
    }
}
