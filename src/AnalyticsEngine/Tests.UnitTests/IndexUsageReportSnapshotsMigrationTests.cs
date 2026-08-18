using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers <see cref="IndexUsageReportSnapshots"/>, which widens <c>IX_date</c> on the five
    /// per-user usage-report tables to <c>([date], [last_activity_date]) INCLUDE ([user_id])</c>.
    ///
    /// The important cases are the two states a customer database can be in: <c>IX_date</c> already
    /// present (created by the installer's profiling schema script) and so needing a rebuild, or
    /// absent (profiling never installed) and so needing a plain create. Both must end up with
    /// exactly ONE index - the whole point of re-using the <c>IX_date</c> name is to avoid leaving a
    /// second, overlapping index to maintain on these write-heavy tables.
    /// </summary>
    [TestClass]
    public class IndexUsageReportSnapshotsMigrationTests
    {
        // The profiling schema script only creates IX_date on some deployments, so the migration has
        // to handle both states. Teams/OneDrive start with the narrow index, the rest without one.
        private static readonly string[] TablesWithExistingIndex =
        {
            "teams_user_activity_log",
            "onedrive_user_activity_log",
        };

        private static readonly string[] TablesWithoutExistingIndex =
        {
            "outlook_user_activity_log",
            "sharepoint_user_activity_log",
            "yammer_user_activity_log",
        };

        private static ScratchDatabase CreateUsageSchema()
        {
            var db = ScratchDatabase.Create("UsageIx");
            try
            {
                foreach (var table in TablesWithExistingIndex)
                {
                    db.Execute(CreateTableSql(table));
                    // The narrow IX_date exactly as Profiling-03-CreateSchema.sql creates it.
                    db.Execute($"CREATE NONCLUSTERED INDEX [IX_date] ON [dbo].[{table}] ([date]);");
                }

                foreach (var table in TablesWithoutExistingIndex)
                {
                    db.Execute(CreateTableSql(table));
                }

                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        private static string CreateTableSql(string table) =>
            $@"CREATE TABLE [dbo].[{table}] (
                   [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                   [date] datetime NOT NULL,
                   [last_activity_date] datetime NULL,
                   [user_id] int NOT NULL);";

        private static void AssertWidened(ScratchDatabase db, string table)
        {
            Assert.IsTrue(db.IndexHasColumn(table, "IX_date", "date", 1, false),
                $"{table}: [date] should remain the leading key of IX_date so existing date predicates still seek.");
            Assert.IsTrue(db.IndexHasColumn(table, "IX_date", "last_activity_date", 2, false),
                $"{table}: [last_activity_date] should be the second key of IX_date.");
            Assert.IsTrue(db.IndexHasColumn(table, "IX_date", "user_id", 0, true),
                $"{table}: [user_id] should be an INCLUDE column of IX_date so the usage chart is index-only.");
            Assert.AreEqual(3, db.IndexColumnCount(table, "IX_date"),
                $"{table}: IX_date should carry exactly the three expected columns.");
            Assert.AreEqual(1, db.NonClusteredIndexCount(table),
                $"{table}: the migration must widen IX_date in place, not add a second overlapping index.");
        }

        [TestMethod]
        public void UpSql_WidensOrCreatesIxDate_AndIsIdempotent()
        {
            using (var db = CreateUsageSchema())
            {
                // Run twice: the second run must detect the widened definition and do nothing.
                db.Execute(IndexUsageReportSnapshots.Up_Sql);
                db.Execute(IndexUsageReportSnapshots.Up_Sql);

                foreach (var table in TablesWithExistingIndex)
                {
                    AssertWidened(db, table);
                }

                foreach (var table in TablesWithoutExistingIndex)
                {
                    AssertWidened(db, table);
                }
            }
        }

        [TestMethod]
        public void DownSql_RestoresTheNarrowIxDate()
        {
            using (var db = CreateUsageSchema())
            {
                db.Execute(IndexUsageReportSnapshots.Up_Sql);
                db.Execute(IndexUsageReportSnapshots.Down_Sql);

                foreach (var table in TablesWithExistingIndex)
                {
                    Assert.IsTrue(db.IndexHasColumn(table, "IX_date", "date", 1, false),
                        $"{table}: IX_date should still exist, keyed on [date].");
                    Assert.AreEqual(1, db.IndexColumnCount(table, "IX_date"),
                        $"{table}: IX_date should be back to its original single-column shape.");
                }
            }
        }

        [TestMethod]
        public void UpSql_SkipsMissingTables()
        {
            // A database that predates these tables (or has them partially) must be skipped rather
            // than failing the whole schema upgrade.
            using (var db = ScratchDatabase.Create("UsageIxBare"))
            {
                db.Execute(IndexUsageReportSnapshots.Up_Sql);
                Assert.IsFalse(db.IndexExists("teams_user_activity_log", "IX_date"),
                    "No usage tables exist, so nothing should have been indexed.");
            }
        }
    }
}
