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
            //
            // Asserted as the executable guard, not as a bare mention of the id: a comment or a stamp
            // source would otherwise keep this green after somebody deleted the guard itself.
            var manual = Normalise(ReadManualScript(ReclaimInputsId));

            StringAssert.Contains(
                manual,
                $"IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'{ExportAuditId}')",
                "The reclaim script must refuse to run until the export-audit migration is stamped.");

            StringAssert.Contains(
                manual,
                $"WHERE MigrationId = N'{ExportAuditId}';",
                "The stamp must copy the immediately preceding migration's snapshot row.");
        }

        [TestMethod]
        public void ExportAuditMigration_GuardsItsIndexSeparatelyFromItsTable()
        {
            // The migration runs with suppressTransaction, so SQL Server commits each statement
            // independently. Creating the index inside the "table does not exist" branch means a failure
            // between the two leaves the table present and the index missing - and a re-run keyed only
            // off the table skips the index and stamps the migration as complete.
            //
            // Asserted on the SQL rather than on behaviour because that is where the mistake is made,
            // and it is invisible in any test that only runs the happy path.
            var up = Normalise(AddCopilotAdoptionExportAudit.Up_Sql);

            StringAssert.Contains(
                up,
                "IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NOT NULL "
                + "AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_adoption_export_audit') "
                + "AND name = N'IX_copilot_adoption_export_audit_occurred_utc')",
                "The index must be guarded on its own existence, independently of the table's.");

            var tableGuard = up.IndexOf("IF OBJECT_ID(N'dbo.copilot_adoption_export_audit', N'U') IS NULL", StringComparison.Ordinal);
            var indexCreate = up.IndexOf("CREATE NONCLUSTERED INDEX", StringComparison.Ordinal);
            var indexGuard = up.IndexOf("AND name = N'IX_copilot_adoption_export_audit_occurred_utc')", StringComparison.Ordinal);

            Assert.IsTrue(tableGuard >= 0 && indexCreate >= 0 && indexGuard >= 0, "Expected guards not found in Up_Sql.");
            Assert.IsTrue(indexGuard < indexCreate,
                "The index guard must precede the CREATE INDEX, not sit inside the table's branch.");
        }

        [TestMethod]
        public void BothManualScripts_VerifyTheSchemaTheyCreatedBeforeStamping()
        {
            // A stamp reached without verification lets a partial apply be recorded as complete, and the
            // migration is then never retried. These are the objects each migration is responsible for.
            var exportAudit = Normalise(ReadManualScript(ExportAuditId));
            foreach (var required in new[]
            {
                "N'IX_copilot_adoption_export_audit_occurred_utc'",
                "N'PK_copilot_adoption_export_audit'",
                "name = N'actor'",
            })
            {
                StringAssert.Contains(exportAudit, required,
                    $"The export-audit script must verify {required} before stamping.");
            }

            var reclaim = Normalise(ReadManualScript(ReclaimInputsId));
            foreach (var required in new[]
            {
                "name = N'created_utc'",
                "N'IX_copilot_adoption_reclaim_exclusions_user_review'",
                "N'FK_copilot_adoption_reclaim_exclusions_users'",
            })
            {
                StringAssert.Contains(reclaim, required,
                    $"The reclaim script must verify {required} before stamping.");
            }
        }

        [TestMethod]
        public void BothManualScripts_GuardOnSchemaOnlyNotOnDataState()
        {
            // A stamp guard that refuses to stamp because rows are in an unexpected state has already
            // broken a customer upgrade in this repository: concurrency will always beat a data-state
            // check, and such a guard is stricter than EF itself, which stamps as soon as Up() returns.
            // Neither of these migrations backfills anything, so neither has any excuse to count rows.
            //
            // Checked over the WHOLE script, not just from the INSERT onwards - a data-state guard placed
            // immediately before the stamp is exactly the shape that caused the incident.
            foreach (var migrationId in new[] { ExportAuditId, ReclaimInputsId })
            {
                var manual = ReadManualScript(migrationId);

                foreach (var line in manual.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;

                    // Any aggregate or row-existence probe that is not against a system catalogue view
                    // or __MigrationHistory is a data-state check on customer rows.
                    var looksLikeRowProbe =
                        trimmed.IndexOf("COUNT(", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("COUNT_BIG(", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!looksLikeRowProbe) continue;

                    Assert.IsTrue(
                        trimmed.IndexOf("sys.", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("__MigrationHistory", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"{migrationId}: '{trimmed}' counts rows outside a system catalogue view. Stamp guards "
                        + "must check schema objects only - a data-state guard has already broken a customer upgrade here.");
                }
            }
        }

        /// <summary>Collapses whitespace so assertions are not hostage to SQL indentation or line breaks.</summary>
        private static string Normalise(string sql)
        {
            return System.Text.RegularExpressions.Regex.Replace(sql ?? string.Empty, @"\s+", " ");
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
