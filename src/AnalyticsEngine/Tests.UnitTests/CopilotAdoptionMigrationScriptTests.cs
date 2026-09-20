using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards the Copilot Adoption schema migration against the failure modes the repository has
    /// actually hit before: a manual DBA script that has drifted from the migration it is supposed to
    /// replay, and a stamp guard that checks data state rather than schema.
    /// </summary>
    /// <remarks>
    /// The verbatim assertion is not ceremony. Reviewing an earlier revision of this branch found a
    /// migration corrected in its manual DBA script but not in the migration itself, which would have
    /// meant a by-hand upgrade and an installer upgrade applying different SQL. Nothing else would have
    /// caught it.
    /// </remarks>
    [TestClass]
    public class CopilotAdoptionMigrationScriptTests
    {
        private const string ReclaimInputsId = "202609131940001_CopilotReclaimEligibilityInputs";
        private const string PredecessorId = "202609101200001_RetireImportDbHacks";
        private const string TargetsId = "202609171000001_CopilotAdoptionTargets";
        // GraphCopilotUsageApiV2 landed between this migration and the period-fact tables, so the
        // targets script now chains from it: that is the row whose Model blob carries report_version,
        // and stamping anything older would leave EF seeing the schema as behind the build.
        private const string DigestId = "202609171030001_CopilotAdoptionDigest";
        private const string DigestPredecessorId = "202609171020002_CopilotAdoptionInterventions";
        private const string TargetsPredecessorId = "202609170940001_CoworkUsageReportTables";
        private const string DropPeriodTablesId = "202609201430001_DropCopilotAdoptionPeriodTables";
        private const string DropPeriodTablesPredecessorId = "202609190900001_IndexPlatformUserActivityLogDate";

        [TestMethod]
        public void DropPeriodTablesManualScript_ReplaysTheMigrationVerbatimAndCopiesPredecessorSnapshot()
        {
            var manual = ReadManualScript(DropPeriodTablesId);

            StringAssert.Contains(manual, DropCopilotAdoptionPeriodTables.Up_Sql);
            StringAssert.Contains(manual, $"IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory\r\n                        WHERE MigrationId = N'{DropPeriodTablesPredecessorId}')");
            StringAssert.Contains(manual, $"SELECT N'{DropPeriodTablesId}', ContextKey, Model, ProductVersion");
            StringAssert.Contains(manual, $"WHERE MigrationId = N'{DropPeriodTablesPredecessorId}';");
        }

        /// <summary>
        /// The drop is destructive, so the script has to warn plainly, and it must drop children before
        /// parents or the cascading foreign keys to <c>copilot_adoption_cohort</c> and <c>dbo.users</c>
        /// block the drop half way through and leave the chain unstamped.
        /// </summary>
        [TestMethod]
        public void DropPeriodTablesManualScript_WarnsItIsDestructiveAndDropsChildrenFirst()
        {
            var manual = ReadManualScript(DropPeriodTablesId);

            StringAssert.Contains(manual, "DESTRUCTIVE AND IRREVERSIBLE",
                "A script that deletes customer history has to say so where a DBA will read it.");

            var order = new[]
            {
                "copilot_adoption_intervention",
                "copilot_adoption_cohort_member",
                "copilot_adoption_cohort",
            };

            var positions = new int[order.Length];
            for (var i = 0; i < order.Length; i++)
            {
                positions[i] = manual.IndexOf("(N'" + order[i] + "')", StringComparison.Ordinal);
                Assert.IsTrue(positions[i] > 0, $"{order[i]} is not in the drop list.");
            }

            Assert.IsTrue(positions[0] < positions[2] && positions[1] < positions[2],
                "Both children of copilot_adoption_cohort must be dropped before the parent.");
        }

        /// <summary>
        /// Same rule as every other manual script: the stamp guard may check SCHEMA, never DATA STATE.
        /// Here "schema" means the tables are gone - a guard that refused to stamp because some row
        /// count was unexpected is the shape that has previously stranded a customer's upgrade chain.
        /// </summary>
        [TestMethod]
        public void DropPeriodTablesManualScript_VerifiesSchemaOnlyBeforeStamping()
        {
            var manual = ReadManualScript(DropPeriodTablesId);
            var stampAt = manual.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.Ordinal);
            Assert.IsTrue(stampAt > 0, "No __MigrationHistory stamp found.");
            var preStamp = Normalise(manual.Substring(0, stampAt));

            foreach (var table in new[]
            {
                "copilot_adoption_period_run",
                "copilot_adoption_user_period",
                "copilot_adoption_targets",
                "copilot_adoption_cohort",
                "copilot_adoption_cohort_member",
                "copilot_adoption_intervention",
                "copilot_adoption_digest_run",
            })
            {
                StringAssert.Contains(preStamp, $"OBJECT_ID(N'dbo.{table}', N'U') IS NOT NULL",
                    $"The stamp guard must confirm dbo.{table} is actually gone before recording the migration.");
            }
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
        public void ReclaimInputsManualScript_RefusesToRunOutOfOrder()
        {
            // Asserted as the executable guard, not as a bare mention of the predecessor id: a comment
            // or the stamp source would otherwise keep this green after somebody deleted the guard.
            var manual = Normalise(ReadManualScript(ReclaimInputsId));

            StringAssert.Contains(
                manual,
                $"IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}')",
                "The script must refuse to run until its predecessor is stamped.");

            StringAssert.Contains(
                manual,
                $"WHERE MigrationId = N'{PredecessorId}';",
                "The stamp must copy the predecessor's snapshot row - this migration reuses its model snapshot.");
        }

        [TestMethod]
        public void ReclaimInputsManualScript_VerifiesTheSchemaItCreatedBeforeStamping()
        {
            // A stamp reached without verification lets a partial apply be recorded as complete, and the
            // migration is then never retried.
            //
            // Asserted over the PRE-STAMP SEGMENT only - the text between the end of the schema work and
            // the __MigrationHistory insert. Every object name below also appears in the CREATE statements
            // further up, so searching the whole file would stay green after the entire verification block
            // was deleted.
            var manual = ReadManualScript(ReclaimInputsId);

            var stampAt = manual.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.Ordinal);
            Assert.IsTrue(stampAt > 0, "No __MigrationHistory stamp found.");

            var lastCreate = manual.Substring(0, stampAt).LastIndexOf("CREATE ", StringComparison.Ordinal);
            Assert.IsTrue(lastCreate > 0, "No CREATE statement found before the stamp.");

            var preStamp = Normalise(manual.Substring(lastCreate, stampAt - lastCreate));

            foreach (var required in new[]
            {
                "name = N'created_utc'",
                "N'IX_copilot_adoption_reclaim_exclusions_user_review'",
                "N'FK_copilot_adoption_reclaim_exclusions_users'",
            })
            {
                StringAssert.Contains(preStamp, required,
                    $"The section guarding the stamp must verify {required}. Found only in the creation SQL, "
                    + "which proves nothing about whether the creation succeeded.");
            }

            var whole = Normalise(manual);
            Assert.IsTrue(
                whole.IndexOf("not be stamped", StringComparison.OrdinalIgnoreCase) >= 0
                || whole.IndexOf("NOT stamped", StringComparison.OrdinalIgnoreCase) >= 0,
                "A failed schema verification must fail loudly and must not stamp __MigrationHistory, or a "
                + "partial apply is recorded as complete and never retried.");

            StringAssert.Contains(whole, ", 16, 1) WITH NOWAIT",
                "The verification failure must be raised at severity 16 so an operator sees it.");
        }

        [TestMethod]
        public void ReclaimInputsManualScript_GuardsOnSchemaOnlyNotOnDataState()
        {
            // A stamp guard that refuses to stamp because rows are in an unexpected state has already
            // broken a customer upgrade in this repository: concurrency will always beat a data-state
            // check, and such a guard is stricter than EF itself, which stamps as soon as Up() returns.
            // This migration backfills nothing, so it has no excuse to count rows.
            //
            // Checked over the WHOLE script, not just from the INSERT onwards - a data-state guard placed
            // immediately before the stamp is exactly the shape that caused the incident.
            var manual = ReadManualScript(ReclaimInputsId);

            foreach (var line in manual.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;

                var looksLikeRowProbe =
                    trimmed.IndexOf("COUNT(", StringComparison.OrdinalIgnoreCase) >= 0
                    || trimmed.IndexOf("COUNT_BIG(", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!looksLikeRowProbe) continue;

                Assert.IsTrue(
                    trimmed.IndexOf("sys.", StringComparison.OrdinalIgnoreCase) >= 0
                    || trimmed.IndexOf("__MigrationHistory", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"'{trimmed}' counts rows outside a system catalogue view. Stamp guards must check schema "
                    + "objects only - a data-state guard has already broken a customer upgrade here.");
            }
        }


        [TestMethod]
        public void TargetsManualScript_ReplaysTheMigrationVerbatimAndCopiesPredecessorSnapshot()
        {
            var manual = ReadManualScript(TargetsId);

            StringAssert.Contains(manual, CopilotAdoptionTargets.Up_Sql);
            StringAssert.Contains(manual, $"IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'{TargetsPredecessorId}')");
            StringAssert.Contains(manual, $"SELECT N'{TargetsId}', ContextKey, Model, ProductVersion");
            StringAssert.Contains(manual, $"WHERE MigrationId = N'{TargetsPredecessorId}';");
        }

        [TestMethod]
        public void TargetsManualScript_VerifiesSchemaOnlyBeforeStamping()
        {
            var manual = ReadManualScript(TargetsId);
            var stampAt = manual.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.Ordinal);
            Assert.IsTrue(stampAt > 0, "No __MigrationHistory stamp found.");
            var preStamp = Normalise(manual.Substring(0, stampAt));

            foreach (var required in new[]
            {
                "name = N'baseline_value'",
                "name = N'baseline_scoring_options_hash'",
                "N'IX_copilot_adoption_targets_active_scope'",
            })
            {
                StringAssert.Contains(preStamp, required);
            }

            foreach (var line in manual.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
                var looksLikeRowProbe = trimmed.IndexOf("COUNT(", StringComparison.OrdinalIgnoreCase) >= 0
                    || trimmed.IndexOf("COUNT_BIG(", StringComparison.OrdinalIgnoreCase) >= 0;
                Assert.IsFalse(looksLikeRowProbe && trimmed.IndexOf("sys.", StringComparison.OrdinalIgnoreCase) < 0
                    && trimmed.IndexOf("__MigrationHistory", StringComparison.OrdinalIgnoreCase) < 0,
                    $"'{trimmed}' counts data rows. Target stamp guards must check schema only.");
            }
        }

        [TestMethod]
        public void DigestManualScript_ReplaysTheMigrationVerbatimAndCopiesPredecessorSnapshot()
        {
            var manual = ReadManualScript(DigestId);

            StringAssert.Contains(manual, CopilotAdoptionDigest.Up_Sql);
            StringAssert.Contains(manual, $"IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'{DigestPredecessorId}')");
            StringAssert.Contains(manual, $"SELECT N'{DigestId}', ContextKey, Model, ProductVersion");
            StringAssert.Contains(manual, $"WHERE MigrationId = N'{DigestPredecessorId}';");
        }

        [TestMethod]
        public void DigestManualScript_VerifiesSchemaOnlyBeforeStamping()
        {
            var manual = ReadManualScript(DigestId);
            var stampAt = manual.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.Ordinal);
            Assert.IsTrue(stampAt > 0, "No __MigrationHistory stamp found.");
            var preStamp = Normalise(manual.Substring(0, stampAt));

            foreach (var required in new[]
            {
                "name = N'period_end'",
                "name = N'recipients_hash'",
                "name = N'error'",
                "N'UX_copilot_adoption_digest_run_period_recipients'",
                "N'IX_copilot_adoption_digest_run_updated'",
            })
            {
                StringAssert.Contains(preStamp, required);
            }

            foreach (var line in manual.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
                var looksLikeRowProbe = trimmed.IndexOf("COUNT(", StringComparison.OrdinalIgnoreCase) >= 0
                    || trimmed.IndexOf("COUNT_BIG(", StringComparison.OrdinalIgnoreCase) >= 0;
                Assert.IsFalse(looksLikeRowProbe && trimmed.IndexOf("sys.", StringComparison.OrdinalIgnoreCase) < 0
                    && trimmed.IndexOf("__MigrationHistory", StringComparison.OrdinalIgnoreCase) < 0,
                    $"'{trimmed}' counts data rows. Digest stamp guards must check schema only.");
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
