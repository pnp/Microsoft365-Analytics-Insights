using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations.Design;
using System.IO;
using System.Linq;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards against the class of breakage described in issue #271: two pull requests each add an EF6
    /// migration that is valid in isolation, and merging both silently breaks the target branch.
    ///
    /// Every migration's <c>.resx</c> carries a gzipped EDMX snapshot of the entity model as it stood
    /// when the migration was scaffolded. If PR B is scaffolded before PR A merges, and B's migration id
    /// sorts AFTER A's, then once both land the newest snapshot in the chain knows nothing about A's
    /// entities. <c>Migrations/Configuration.cs</c> sets <c>AutomaticMigrationsEnabled = true</c>, so EF
    /// reconciles the difference with an on-the-fly automatic migration - which re-creates A's objects
    /// ("There is already an object named 'x' in the database") or, for removals, throws
    /// <see cref="System.Data.Entity.Migrations.Infrastructure.AutomaticDataLossException"/>.
    ///
    /// Neither branch fails on its own, so nothing catches it until the merge reaches the target branch.
    ///
    /// This test asks EF the question directly: scaffold against the current entity model and assert
    /// that nothing is pending. That is exactly the condition that triggers the automatic migration, so
    /// it needs no heuristics about snapshot sizes or table names, and it fails on the PULL REQUEST
    /// because CI builds the merge of the PR into its target branch.
    ///
    /// REPAIRING A STALE SNAPSHOT
    /// The canonical fix is <c>Add-Migration -Force &lt;Name&gt;</c> in the Visual Studio Package Manager
    /// Console. When that is not available, set the environment variable
    /// <c>MIGRATION_SNAPSHOT_DUMP_PATH</c> to a file path and run this test: it writes the correct
    /// base64 <c>Target</c> for the current model to that file, which can be pasted over the
    /// <c>&lt;data name="Target"&gt;</c> value in the newest migration's <c>.resx</c>.
    /// </summary>
    [TestClass]
    public class MigrationSnapshotTests
    {
        /// <summary>
        /// Calls EF generates into a migration's Up() when it has real work to do. If the model and the
        /// newest snapshot agree, Scaffold() produces an empty Up() and none of these appear.
        /// </summary>
        private static readonly string[] MigrationOperations =
        {
            "CreateTable(", "DropTable(", "AddColumn(", "DropColumn(", "AlterColumn(",
            "CreateIndex(", "DropIndex(", "AddForeignKey(", "DropForeignKey(",
            "AddPrimaryKey(", "DropPrimaryKey(", "RenameTable(", "RenameColumn(",
            "RenameIndex(", "MoveTable(", "CreateStoredProcedure(", "AlterStoredProcedure(",
            "DropStoredProcedure("
        };

        [TestMethod]
        public void LatestMigrationSnapshot_IsUpToDateWithTheEntityModel()
        {
            var scaffolded = new MigrationScaffolder(new Configuration()).Scaffold("PendingModelChangeProbe");

            DumpTargetSnapshotIfRequested(scaffolded);

            var userCode = scaffolded.UserCode ?? string.Empty;
            var pending = FindPendingOperations(userCode);
            if (pending.Count == 0)
            {
                return;
            }

            Assert.Fail(
                "The newest EF migration's model snapshot does not match the current entity model, so EF " +
                "would generate an automatic migration at runtime (see issue #271).\r\n\r\n" +
                "This normally means a migration was scaffolded before another migration - one that sorts " +
                "EARLIER but was merged LATER - reached this branch. The snapshot in the newest .resx " +
                "therefore predates it.\r\n\r\n" +
                "Pending operations EF wants to apply:\r\n" +
                string.Join("\r\n", pending.Select(p => "    " + p)) +
                "\r\n\r\nFix: re-scaffold the newest migration's snapshot with `Add-Migration -Force " +
                "<Name>` (Package Manager Console, project Entities, startup project " +
                "WebJob.Office365ActivityImporter), keeping its existing Up()/Down() DDL. Headless " +
                "alternative: set MIGRATION_SNAPSHOT_DUMP_PATH and re-run this test to emit the correct " +
                "Target value.");
        }

        /// <summary>
        /// Returns the trimmed source lines of the scaffolded Up() that represent real schema operations.
        ///
        /// Only Up() is considered. Down() is the inverse of Up(), so a scaffolded CreateTable in Up()
        /// always has a matching DropTable in Down() - scanning the whole file would report every change
        /// twice and in both directions, which reads as though the model wanted to drop the very tables
        /// it is actually missing.
        ///
        /// Deliberately line-based rather than a full parse: the point is a readable diagnostic, and any
        /// operation at all is a failure regardless of its arguments.
        /// </summary>
        private static List<string> FindPendingOperations(string userCode)
        {
            var found = new List<string>();

            foreach (var rawLine in ExtractUpBody(userCode).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//"))
                {
                    continue;
                }

                if (MigrationOperations.Any(op => line.IndexOf(op, StringComparison.Ordinal) >= 0))
                {
                    // Long CreateTable() calls span many lines; the first is enough to identify it.
                    found.Add(line.Length > 200 ? line.Substring(0, 200) + " ..." : line);
                }
            }

            return found;
        }

        /// <summary>
        /// Text between the scaffolded Up() and Down() methods. EF always emits Up() first, so this is a
        /// stable split without needing to brace-match.
        /// </summary>
        private static string ExtractUpBody(string userCode)
        {
            const string upMarker = "public override void Up()";
            const string downMarker = "public override void Down()";

            var start = userCode.IndexOf(upMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return userCode;
            }

            start += upMarker.Length;

            var end = userCode.IndexOf(downMarker, start, StringComparison.Ordinal);
            return end < 0 ? userCode.Substring(start) : userCode.Substring(start, end - start);
        }

        private static void DumpTargetSnapshotIfRequested(ScaffoldedMigration scaffolded)
        {
            var path = Environment.GetEnvironmentVariable("MIGRATION_SNAPSHOT_DUMP_PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            object target;
            if (scaffolded.Resources.TryGetValue("Target", out target) && target is string)
            {
                File.WriteAllText(path, (string)target);
                Console.WriteLine("Wrote current-model Target snapshot to " + path);
            }

            File.WriteAllText(path + ".usercode.txt", scaffolded.UserCode ?? string.Empty);
        }
    }
}
