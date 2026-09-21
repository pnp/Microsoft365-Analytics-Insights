using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers <see cref="IndexPlatformUserActivityLogDate"/>, which gives
    /// <c>dbo.platform_user_activity_log</c> the covering <c>IX_date</c> index the portal's Office
    /// apps report needs.
    /// </summary>
    /// <remarks>
    /// The two states a customer database can be in are what matter here, and they are the same two
    /// <see cref="IndexUsageReportSnapshotsMigrationTests"/> covers for the sibling tables:
    /// <c>IX_date</c> already present in its narrow, key-only form (created by the installer's
    /// profiling schema script) and so needing a rebuild, or absent (profiling never installed) and
    /// so needing a plain create. Both must end up with exactly ONE non-clustered index on
    /// <c>[date]</c> - re-using the name is the entire reason this migration does not simply add its
    /// own, because two overlapping indexes would both have to be maintained on a table that takes
    /// one row per user per day from the importer.
    /// </remarks>
    [TestClass]
    public class IndexPlatformUserActivityLogDateMigrationTests
    {
        private const string Table = "platform_user_activity_log";

        private static readonly string[] AppColumns =
            { "outlook", "word", "excel", "powerpoint", "onenote", "teams" };

        private static readonly string[] PlatformColumns = { "windows", "mac", "mobile", "web" };

        /// <summary>All 34 bit columns: four platforms, six apps and the 24 app-on-platform crosses.</summary>
        private static string[] BitColumns() =>
            PlatformColumns
                .Concat(AppColumns)
                .Concat(AppColumns.SelectMany(a => PlatformColumns.Select(p => a + "_" + p)))
                .ToArray();

        private static string CreateTableSql()
        {
            var bits = new StringBuilder();
            foreach (var column in BitColumns())
            {
                bits.Append("    [").Append(column).Append("] bit NOT NULL,\r\n");
            }

            return
                "CREATE TABLE [dbo].[" + Table + "] (\r\n" +
                "    [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,\r\n" +
                bits +
                "    [user_id] int NOT NULL,\r\n" +
                "    [date] datetime NOT NULL,\r\n" +
                "    [last_activity_date] datetime NULL);";
        }

        private static ScratchDatabase CreateSchema(bool withNarrowIndex)
        {
            var db = ScratchDatabase.Create("PlatformIx");
            try
            {
                db.Execute(CreateTableSql());
                if (withNarrowIndex)
                {
                    // Exactly as Profiling-03-CreateSchema.sql creates it.
                    db.Execute($"CREATE INDEX [IX_date] ON [dbo].[{Table}] ([date]);");
                }
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The index must be keyed on <c>[date]</c> alone and carry every column the report reads.
        /// </summary>
        /// <remarks>
        /// Asserting the full INCLUDE list rather than a sample is deliberate. A missing bit column
        /// would not fail anything - the query would still return the right answer, via a key lookup
        /// per matching row, which at tens of millions of rows is slower than the scan this index
        /// replaces. A silent performance regression is exactly what this migration exists to prevent.
        /// </remarks>
        private static void AssertCovering(ScratchDatabase db)
        {
            Assert.IsTrue(db.IndexHasColumn(Table, "IX_date", "date", 1, false),
                "[date] must be the leading - and only - key, so a window predicate seeks.");

            foreach (var column in new[] { "user_id" }.Concat(BitColumns()))
            {
                Assert.IsTrue(db.IndexHasColumn(Table, "IX_date", column, 0, true),
                    $"[{column}] must be an INCLUDE column, or the report falls back to key lookups.");
            }

            // 1 key + user_id + 34 bits.
            Assert.AreEqual(36, db.IndexColumnCount(Table, "IX_date"),
                "IX_date should carry exactly the key plus user_id and the 34 app/platform bits.");

            Assert.AreEqual(1, db.NonClusteredIndexCount(Table),
                "The migration must rebuild IX_date in place, not add a second overlapping index.");
        }

        [TestMethod]
        public void UpSql_CreatesTheCoveringIndexWhenNoneExists()
        {
            using (var db = CreateSchema(withNarrowIndex: false))
            {
                db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                AssertCovering(db);
            }
        }

        /// <summary>
        /// A deployment with the profiling extension already has a narrow <c>IX_date</c>; it must be
        /// upgraded in place rather than joined by a second index.
        /// </summary>
        [TestMethod]
        public void UpSql_WidensTheProfilingScriptIndexInPlace()
        {
            using (var db = CreateSchema(withNarrowIndex: true))
            {
                Assert.AreEqual(1, db.IndexColumnCount(Table, "IX_date"), "Precondition: the narrow index.");

                db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                AssertCovering(db);
            }
        }

        [TestMethod]
        public void UpSql_IsIdempotent()
        {
            foreach (var withNarrowIndex in new[] { true, false })
            {
                using (var db = CreateSchema(withNarrowIndex))
                {
                    // The second and third runs must detect the covering definition and do nothing.
                    db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                    db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                    db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);

                    AssertCovering(db);
                }
            }
        }

        /// <summary>
        /// Down returns the index to the profiling script's shape rather than dropping it.
        /// </summary>
        /// <remarks>
        /// Dropping it outright would silently remove an index the profiling extension created and
        /// expects to find, on a table it compiles from.
        /// </remarks>
        [TestMethod]
        public void DownSql_RestoresTheNarrowIxDateRatherThanRemovingIt()
        {
            using (var db = CreateSchema(withNarrowIndex: true))
            {
                db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                db.Execute(IndexPlatformUserActivityLogDate.Down_Sql);

                Assert.IsTrue(db.IndexHasColumn(Table, "IX_date", "date", 1, false),
                    "IX_date must still exist, keyed on [date].");
                Assert.AreEqual(1, db.IndexColumnCount(Table, "IX_date"),
                    "IX_date should be back to the profiling script's single-column shape.");
            }
        }

        [TestMethod]
        public void UpSql_RoundTripsAfterDown()
        {
            using (var db = CreateSchema(withNarrowIndex: false))
            {
                db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                db.Execute(IndexPlatformUserActivityLogDate.Down_Sql);
                db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);

                AssertCovering(db);
            }
        }

        /// <summary>
        /// A database that predates the table must be skipped, not fail the whole schema upgrade.
        /// </summary>
        [TestMethod]
        public void UpSql_SkipsAMissingTable()
        {
            using (var db = ScratchDatabase.Create("PlatformIxBare"))
            {
                db.Execute(IndexPlatformUserActivityLogDate.Up_Sql);
                Assert.IsFalse(db.IndexExists(Table, "IX_date"),
                    "The table does not exist, so nothing should have been indexed.");
            }
        }

        #region Manual upgrade script

        /// <summary>
        /// The by-hand script must run the migration's own SQL, not a hand-maintained copy that can
        /// drift from it.
        /// </summary>
        [TestMethod]
        public void ManualScript_EmbedsUpSqlVerbatim()
        {
            StringAssert.Contains(ManualScript(), IndexPlatformUserActivityLogDate.Up_Sql.Trim(),
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade "
                + "applies something different from what the installer applies.");
        }

        /// <summary>
        /// The __MigrationHistory stamp must sit behind the schema check and copy the immediate
        /// predecessor's row.
        /// </summary>
        /// <remarks>
        /// Guarding on SCHEMA and never on DATA STATE is the rule this repo learned the hard way: a
        /// stamp guard that demanded perfect data refused to record a completed migration on a tenant
        /// that upgraded with the importer running, which then blocked every later script in the
        /// chain. This migration creates an index and touches no rows, so there is no data state to
        /// be tempted by - the test pins that it stays that way.
        /// </remarks>
        [TestMethod]
        public void ManualScript_StampsFromTheImmediatePredecessorBehindASchemaCheck()
        {
            var script = ManualScript();

            var stampIndex = script.IndexOf("INSERT dbo.__MigrationHistory", StringComparison.OrdinalIgnoreCase);
            if (stampIndex < 0)
            {
                stampIndex = script.IndexOf("INSERT INTO dbo.__MigrationHistory", StringComparison.OrdinalIgnoreCase);
            }
            Assert.IsTrue(stampIndex > 0, "The script must stamp __MigrationHistory.");

            StringAssert.Contains(script, "202609171125117_DropUnreportedCopilotStudioCreditColumns",
                "The stamp must copy the IMMEDIATE predecessor's row; naming an earlier migration would "
                + "let a DBA skip one and leave a hole in __MigrationHistory.");

            StringAssert.Contains(script, "202609190900001_IndexPlatformUserActivityLogDate",
                "The stamp must record this migration's own id.");

            var preamble = script.Substring(0, stampIndex);
            StringAssert.Contains(preamble, "sys.indexes",
                "The stamp must sit behind a check that the index actually exists - a severity-16 "
                + "RAISERROR does not abort the batch, so an unguarded stamp records a failed apply "
                + "as complete and EF then never retries it.");

            Assert.IsFalse(script.IndexOf("COUNT(*)", StringComparison.OrdinalIgnoreCase) >= 0
                           && script.IndexOf("backfill", StringComparison.OrdinalIgnoreCase) >= 0,
                "This migration touches no rows; a data-state guard here would be both pointless and "
                + "the exact shape that has blocked a customer upgrade before.");
        }

        private static string ManualScript() =>
            File.ReadAllText(Path.Combine(MigrationsDirectory(),
                "202609190900001_IndexPlatformUserActivityLogDate.manual.sql"));

        private static string MigrationsDirectory()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null &&
                   !Directory.Exists(Path.Combine(directory.FullName, "Common", "Entities", "Migrations")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Could not locate the Migrations directory from the test output folder.");
            return Path.Combine(directory.FullName, "Common", "Entities", "Migrations");
        }

        #endregion
    }
}
