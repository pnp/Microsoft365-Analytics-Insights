using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations;
using System.Data.Entity.Migrations.Infrastructure;
using System.Data.Entity.Migrations.Model;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Tests.UnitTests
{
    /// <summary>
    /// #855 R10-8: a DBA who upgrades by hand runs <c>&lt;migrationid&gt;.manual.sql</c>, and the installer
    /// runs the migration's <c>Up()</c>. Both must make the same change, so every manual script must contain,
    /// verbatim, every SQL statement its migration's <c>Up()</c> issues through <c>Sql(...)</c>
    /// (<c>Up_Sql</c>, and any further constant such as <c>Purge_Sql</c> or <c>Up_Backfill_Sql</c>).
    /// <para>
    /// The SQL is read from the migration itself rather than from constant names: <c>Up()</c> is called on a
    /// fresh instance, which only records operations and never touches a database, and its
    /// <see cref="SqlOperation"/>s are compared with the script. Only line endings are normalised, and
    /// leading and trailing whitespace trimmed, so any edit inside the SQL on one side only fails this test.
    /// Scaffolded migrations (<c>CreateTable</c>, <c>AddColumn</c>) issue no <see cref="SqlOperation"/>, so
    /// their hand-written scripts have nothing verbatim to compare and are not checked here.
    /// </para>
    /// </summary>
    [TestClass]
    public class ManualMigrationScriptVerbatimTests
    {
        /// <summary>
        /// Manual scripts that predate this test and are not verbatim copies of their migration's SQL. Each was
        /// reviewed when the test was added; the reason says why it differs. Do not add to this list: make the
        /// new script verbatim instead. An entry that becomes verbatim fails the test until it is removed.
        /// </summary>
        private static readonly Dictionary<string, string> NotVerbatim = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["202607130900001_DedupCopilotAccessedResourceSiteUrls"] =
                "The migration's early RETURN when there is nothing to de-duplicate is GOTO StampHistory in the script, so a no-op run still stamps.",
            ["202607141000001_IndexAuditEventsTimeStamp"] =
                "Same statements; the script leaves out and moves some of the migration's comments.",
            ["202607231000001_IndexSitesSiteId"] =
                "The migration's early RETURN when the index exists is an IF/ELSE in the script, so a no-op run still stamps.",
            ["202607231700001_CoverCopilotAccessedResourceDedup"] =
                "Early RETURN restructured as IF/ELSE so a no-op run stamps; one DECLARE moved and progress messages reworded.",
            ["202608250900001_IndexCopilotAccessedResourceFkColumns"] =
                "Same index DDL and ONLINE/offline fallback; the progress messages are reworded and timed in seconds.",
            ["202609170920001_CopilotSubscribedSkuCapacity"] =
                "Same statements as UpSql, split into batches with GO.",
        };

        [TestMethod]
        public void EveryManualScript_ContainsItsMigrationsUpSqlVerbatim()
        {
            var migrationsDir = MigrationsDirectory();
            var migrations = DiscoverMigrations();
            var scripts = Directory.GetFiles(migrationsDir, "*.manual.sql")
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var failures = new List<string>();
            var compared = 0;
            var allowListedDrift = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in scripts)
            {
                var id = Path.GetFileName(path).Substring(0, Path.GetFileName(path).Length - ".manual.sql".Length);
                if (!migrations.TryGetValue(id, out var migrationType))
                {
                    failures.Add($"{id}.manual.sql has no migration with that id in Common.Entities.");
                    continue;
                }

                var upSql = UpSqlOperations(migrationType);
                if (upSql.Count == 0) continue;

                compared++;
                var script = NormaliseLineEndings(File.ReadAllText(path));
                var drift = upSql
                    .Select((sql, index) => new { index, sql = NormaliseLineEndings(sql).Trim() })
                    .Where(op => !script.Contains(op.sql))
                    .Select(op => $"{id}: Sql() call #{op.index + 1} of {upSql.Count} in {migrationType.Name}.Up() is not in the manual script verbatim. {FirstDifference(script, op.sql)}")
                    .ToList();

                if (drift.Count == 0) continue;
                if (NotVerbatim.ContainsKey(id))
                {
                    allowListedDrift.Add(id);
                    continue;
                }
                failures.AddRange(drift);
            }

            foreach (var stale in NotVerbatim.Keys.Where(k => !allowListedDrift.Contains(k)))
            {
                failures.Add($"{stale} is allow-listed as not verbatim, but its manual script now contains all of its migration's SQL "
                             + "(or the script or its SQL is gone). Remove it from NotVerbatim.");
            }

            Assert.IsTrue(compared >= 30,
                $"Only {compared} manual scripts were compared with their migration's SQL; expected at least 30. "
                + "Discovery has stopped finding migrations or their Sql() operations, so this guard is no longer guarding anything.");
            Assert.AreEqual(0, failures.Count,
                "A manual upgrade script must apply the same SQL as the installer, so it must contain its migration's Up SQL "
                + "verbatim (rule 7 in src/AnalyticsEngine/.github/copilot-instructions.md). Copy the migration's SQL into the "
                + "script unchanged, or change both together:\r\n" + string.Join("\r\n", failures));
        }

        /// <summary>
        /// Every <see cref="DbMigration"/> in Common.Entities, by its <see cref="IMigrationMetadata.Id"/>.
        /// </summary>
        private static Dictionary<string, Type> DiscoverMigrations()
        {
            Type[] types;
            try
            {
                types = typeof(Configuration).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            return types
                .Where(t => typeof(DbMigration).IsAssignableFrom(t) && typeof(IMigrationMetadata).IsAssignableFrom(t) && !t.IsAbstract)
                .ToDictionary(t => ((IMigrationMetadata)Activator.CreateInstance(t)).Id, t => t, StringComparer.Ordinal);
        }

        /// <summary>
        /// The SQL that <c>Up()</c> issues through <c>Sql(...)</c>, in order. <see cref="DbMigration"/> keeps the
        /// operations it records in an internal list, which EF reads when it applies the migration.
        /// </summary>
        private static List<string> UpSqlOperations(Type migrationType)
        {
            var migration = (DbMigration)Activator.CreateInstance(migrationType);
            migration.Up();

            var operations = typeof(DbMigration).GetProperty("Operations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(operations,
                "EntityFramework's DbMigration no longer has the internal Operations property this test reads the Up() SQL from.");

            return ((IEnumerable<MigrationOperation>)operations.GetValue(migration))
                .OfType<SqlOperation>()
                .Select(o => o.Sql)
                .ToList();
        }

        /// <summary>
        /// Points at the first line of <paramref name="sql"/> that the script does not reproduce, so a failure says
        /// where the copy drifted instead of only that it did.
        /// </summary>
        private static string FirstDifference(string script, string sql)
        {
            var sqlLines = sql.Split('\n');
            var scriptLines = script.Split('\n');
            var start = Array.IndexOf(scriptLines, sqlLines[0]);
            if (start < 0)
                return $"Its first line is not in the script: \"{sqlLines[0].Trim()}\"";

            for (var i = 1; i < sqlLines.Length; i++)
            {
                var scriptLine = start + i < scriptLines.Length ? scriptLines[start + i] : "<end of script>";
                if (scriptLine != sqlLines[i])
                    return $"Line {i + 1} of the SQL differs. Migration: \"{sqlLines[i].Trim()}\" Script: \"{scriptLine.Trim()}\"";
            }

            return "Its lines are all present, but not as one contiguous block.";
        }

        private static string NormaliseLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        private static string MigrationsDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Common", "Entities", "Migrations");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            Assert.Fail("Could not locate Common\\Entities\\Migrations above the test assembly.");
            return null;
        }
    }
}
