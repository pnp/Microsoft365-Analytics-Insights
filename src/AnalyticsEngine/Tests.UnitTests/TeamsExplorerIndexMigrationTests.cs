using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards the Teams Explorer index migration and the manual DBA script that replays it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failure modes this repository has actually hit are checked here. The first is a manual
    /// script that has drifted from the migration it replays, so a by-hand upgrade applies different
    /// SQL from the installer's. The second is a <c>__MigrationHistory</c> stamp guarded on DATA
    /// STATE rather than schema, which stranded a customer's whole upgrade chain when a handful of
    /// concurrently-inserted rows failed a perfection check.
    /// </para>
    /// <para>
    /// The measured evidence is also asserted. A performance-motivated migration whose doc comment
    /// cannot point at a before/after measurement is not approved for a stable release, and a doc
    /// comment is the easiest thing in a migration to quietly delete.
    /// </para>
    /// </remarks>
    [TestClass]
    public class TeamsExplorerIndexMigrationTests
    {
        private const string MigrationId = "202609161200001_IndexTeamsExplorerQueries";
        private const string PredecessorId = "202609151440027_CopilotPromptSafetyFields";

        [TestMethod]
        public void ManualScriptReplaysTheMigrationVerbatim()
        {
            var manual = ReadManualScript(MigrationId);

            StringAssert.Contains(manual, IndexTeamsExplorerQueries.Up_Sql,
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade "
                + "applies different SQL from the installer's.");

            StringAssert.Contains(manual, "INSERT INTO dbo.__MigrationHistory",
                "The script must stamp __MigrationHistory or EF will still consider the migration pending.");
        }

        [TestMethod]
        public void ManualScriptRefusesToRunOutOfOrder()
        {
            var manual = Normalise(ReadManualScript(MigrationId));

            StringAssert.Contains(
                manual,
                $"IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}')",
                "The script must check its predecessor is stamped before stamping itself.");

            StringAssert.Contains(
                manual,
                $"WHERE MigrationId = N'{PredecessorId}';",
                "The stamp must copy the predecessor's snapshot row - this migration reuses its model snapshot.");
        }

        /// <summary>
        /// The stamp must never be gated on the state of any data.
        /// </summary>
        /// <remarks>
        /// This migration writes no rows at all, so there is nothing to backfill and nothing to
        /// verify - but the rule is asserted anyway, because the natural way to "harden" a manual
        /// script later is to add a row check, and that is exactly what broke a customer upgrade in
        /// <c>DenormaliseCopilotChatUserAndTime</c>. A guard that would reject a state the installer
        /// path treats as success is always wrong.
        /// </remarks>
        [TestMethod]
        public void StampIsNotGatedOnDataState()
        {
            var manual = ReadManualScript(MigrationId);

            var stampAt = manual.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.Ordinal);
            Assert.IsTrue(stampAt > 0, "No __MigrationHistory stamp found.");

            var preStamp = Normalise(manual.Substring(0, stampAt));

            foreach (var forbidden in new[] { "COUNT(*) FROM dbo.teams", "COUNT(*) FROM dbo.call", "IS NULL) > 0" })
            {
                Assert.IsFalse(
                    preStamp.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0,
                    $"The pre-stamp section must not inspect row state ('{forbidden}'). Concurrency always "
                    + "beats a data-state guard, and refusing to stamp on one strands the migration chain.");
            }
        }

        [TestMethod]
        public void MigrationIsGuardedIdempotentAndCommitsPerStep()
        {
            var up = Normalise(IndexTeamsExplorerQueries.Up_Sql);

            StringAssert.Contains(up, "OBJECT_ID(N'dbo.' + @table, N'U')",
                "Each table must be guarded so a missing table is skipped rather than erroring.");
            StringAssert.Contains(up, "already has the required shape",
                "A re-run against an already-upgraded database must be a no-op, not a rebuild.");
            StringAssert.Contains(up, "RAISERROR(@msg, 0, 1) WITH NOWAIT",
                "A multi-minute index build must emit live progress.");
            StringAssert.Contains(up, "EXEC sp_executesql @sql",
                "ONLINE attempts must run through sp_executesql, or the edition rejection is not catchable.");
            StringAssert.Contains(up, "@edition IN (3, 5, 8)",
                "ONLINE must be gated on the editions that support it, with an offline fallback.");
        }

        [TestMethod]
        public void EveryIndexTheMigrationCreatesCanBeReverted()
        {
            var down = Normalise(IndexTeamsExplorerQueries.Down_Sql);

            foreach (var index in new[]
            {
                "IX_teams_channel_stats_log_date",
                "IX_teams_user_channel_reactions_date",
                "IX_team_membership_log_date",
            })
            {
                StringAssert.Contains(down, $"DROP INDEX [{index}]", $"Down must drop [{index}].");
            }

            // The device-usage index is WIDENED rather than added, so Down must restore its original
            // key-only shape rather than drop it - dropping it would remove an index the installer's
            // profiling schema script created and leave the table worse than before this migration.
            StringAssert.Contains(down, "CREATE NONCLUSTERED INDEX [IX_date] ON [dbo].[teams_user_device_usage_log] ([date]) WITH (DROP_EXISTING = ON);",
                "Down must restore IX_date to its key-only shape, not drop it.");
        }

        /// <summary>
        /// A performance migration must carry its measurement, and it must be a real one.
        /// </summary>
        [TestMethod]
        public void MigrationDocumentsItsMeasuredBeforeAndAfter()
        {
            var source = ReadMigrationSource(MigrationId);

            StringAssert.Contains(source, "logical reads",
                "A performance-motivated migration must record its measured logical reads.");
            StringAssert.Contains(source, "Invoke-TeamsExplorerIndexBenchmark.ps1",
                "The measurement must name the harness that produced it, so it can be reproduced.");
            StringAssert.Contains(source, "scan -> seek",
                "The plan operator either side must be recorded, so it is clear WHY it got faster.");

            // Both selectivities, per the repository rule - a single window hides regressions that only
            // appear at the other end.
            StringAssert.Contains(source, "28d", "The narrow-window measurement must be recorded.");
            StringAssert.Contains(source, "365d", "The wide-window measurement must be recorded.");

            StringAssert.Contains(source, "REJECTED",
                "The candidates that were measured and rejected must be recorded too - a negative result "
                + "is a result, and the next person to have the same idea needs to find it.");
        }

        /// <summary>Collapses whitespace so assertions are not hostage to SQL indentation or line breaks.</summary>
        private static string Normalise(string sql)
        {
            return Regex.Replace(sql ?? string.Empty, @"\s+", " ");
        }

        private static string ReadManualScript(string migrationId) =>
            ReadMigrationFile(migrationId + ".manual.sql",
                "Every schema migration must ship a manual upgrade script for operators who upgrade by "
                + "hand, and CI does not attach it - it has to be uploaded to the release by hand.");

        private static string ReadMigrationSource(string migrationId) =>
            ReadMigrationFile(migrationId + ".cs", "The migration source could not be found.");

        /// <summary>
        /// Reads a file from the migrations folder. Walks up from the test binaries rather than
        /// assuming a working directory, so it works under both vstest and the IDE.
        /// </summary>
        private static string ReadMigrationFile(string fileName, string failureHint)
        {
            var dir = new DirectoryInfo(
                Path.GetDirectoryName(typeof(TeamsExplorerIndexMigrationTests).Assembly.Location));

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Common", "Entities", "Migrations", fileName);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {fileName} anywhere above the test assembly. {failureHint}");
            return null;
        }
    }
}
