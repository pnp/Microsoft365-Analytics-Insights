using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards the two Copilot Adoption schema migrations against the failure modes the repository has
    /// actually hit before: a manual DBA script that has drifted from the migration it is supposed to
    /// replay, a prerequisite chain that lets a by-hand upgrade stamp a migration out of order, and a
    /// stamp guard that checks data state rather than schema.
    /// </summary>
    /// <remarks>
    /// The verbatim assertion is not ceremony. Reviewing the merged branch found the export-audit
    /// migration creating its table and its index inside one "does the table exist" guard: because the
    /// migration runs with <c>suppressTransaction</c>, a failure between the two would commit the table,
    /// leave the index missing, and a re-run keyed only off the table would skip the index and stamp the
    /// migration as complete. The manual script was corrected and the migration was not, which is
    /// precisely the divergence this test exists to catch.
    /// </remarks>
    [TestClass]
    public class CopilotAdoptionMigrationScriptTests
    {
        private const string ExportAuditId = "202609131930001_AddCopilotAdoptionExportAudit";
        private const string ReclaimInputsId = "202609131940001_CopilotReclaimEligibilityInputs";

        [TestMethod]
        public void ExportAuditManualScript_ReplaysTheMigrationVerbatim()
        {
            var manual = ReadManualScript(ExportAuditId);

            StringAssert.Contains(manual, AddCopilotAdoptionExportAudit.Up_Sql,
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade applies "
                + "different SQL from the installer's.");

            StringAssert.Contains(manual, "INSERT INTO dbo.__MigrationHistory",
                "The script must stamp __MigrationHistory or EF will still consider the migration pending.");

            StringAssert.Contains(manual, "202609101200001_RetireImportDbHacks",
                "The stamp must be conditional on the predecessor, so the scripts cannot be applied out of order.");
        }

        [TestMethod]
        public void ReclaimInputsManualScript_ReplaysTheMigrationVerbatim()
        {
            var manual = ReadManualScript(ReclaimInputsId);

            StringAssert.Contains(manual, CopilotReclaimEligibilityInputs.Up_Sql,
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade applies "
                + "different SQL from the installer's.");

            StringAssert.Contains(manual, "INSERT INTO dbo.__MigrationHistory",
                "The script must stamp __MigrationHistory or EF will still consider the migration pending.");
        }

        [TestMethod]
        public void ReclaimInputsManualScript_RequiresTheExportAuditMigrationFirst()
        {
            // The two scripts ship in the same release and the reclaim one has the later id. Naming a
            // more distant ancestor as the prerequisite would let a by-hand deployment stamp the latest
            // migration while dbo.copilot_adoption_export_audit was absent - and every Copilot Adoption
            // export would then be refused, because an export that cannot write its audit row returns no
            // data.
            var manual = ReadManualScript(ReclaimInputsId);

            StringAssert.Contains(manual, ExportAuditId,
                "The reclaim script must name the export-audit migration as its prerequisite and stamp source.");
        }

        [TestMethod]
        public void BothManualScripts_GuardOnSchemaOnlyNotOnDataState()
        {
            // A stamp guard that refuses to stamp because rows are in an unexpected state has already
            // broken a customer upgrade in this repository: concurrency will always beat a data-state
            // check, and such a guard is stricter than EF itself, which stamps as soon as Up() returns.
            // Neither of these migrations backfills anything, so neither has any excuse to count rows.
            foreach (var migrationId in new[] { ExportAuditId, ReclaimInputsId })
            {
                var manual = ReadManualScript(migrationId);
                var stamp = manual.Substring(manual.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.Ordinal));

                foreach (var dataStateSmell in new[] { "COUNT(", "SELECT TOP", "IS NULL)" })
                {
                    Assert.IsFalse(
                        stamp.IndexOf(dataStateSmell, StringComparison.OrdinalIgnoreCase) >= 0
                            && stamp.IndexOf("sys.", StringComparison.OrdinalIgnoreCase) < 0,
                        $"{migrationId}: the stamp section looks like it inspects row state ('{dataStateSmell}'). "
                        + "Stamp guards must check schema objects only.");
                }
            }
        }

        /// <summary>
        /// Reads a <c>&lt;migrationid&gt;.manual.sql</c> from the repository. Walks up from the test binaries
        /// rather than assuming a working directory, so it works under both vstest and the IDE.
        /// </summary>
        private static string ReadManualScript(string migrationId)
        {
            var dir = new DirectoryInfo(
                Path.GetDirectoryName(typeof(CopilotAdoptionMigrationScriptTests).Assembly.Location));

            while (dir != null)
            {
                var candidate = Path.Combine(
                    dir.FullName, "Common", "Entities", "Migrations", migrationId + ".manual.sql");

                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                dir = dir.Parent;
            }

            Assert.Fail(
                $"Could not find {migrationId}.manual.sql anywhere above the test assembly. Every schema "
                + "migration must ship a manual upgrade script for operators who upgrade by hand, and CI "
                + "does not attach it - it has to be uploaded to the release by hand.");
            return null;
        }
    }
}
