using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers <see cref="IndexReportDateQueries"/>, which gives the in-app Reports date-range queries
    /// covering indexes.
    ///
    /// Two paths matter. <c>audit_events</c> already has a key-only
    /// <c>IX_audit_events_time_stamp</c> on databases that applied
    /// <see cref="IndexAuditEventsTimeStamp"/>, so it must be REBUILT to add the INCLUDE columns
    /// (SQL Server cannot add includes to an existing index). The other three tables have no date
    /// index at all, so they take the plain create path.
    /// </summary>
    [TestClass]
    public class IndexReportDateQueriesMigrationTests
    {
        private static ScratchDatabase CreateReportSchema()
        {
            var db = ScratchDatabase.Create("ReportIx");
            try
            {
                db.Execute(
                    @"CREATE TABLE [dbo].[audit_events] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [time_stamp] datetime NOT NULL,
                          [operation_id] int NULL,
                          [user_id] int NULL);");

                // The key-only index left behind by IndexAuditEventsTimeStamp: the rebuild path.
                db.Execute(
                    "CREATE NONCLUSTERED INDEX [IX_audit_events_time_stamp] ON [dbo].[audit_events] ([time_stamp]);");

                db.Execute(
                    @"CREATE TABLE [dbo].[hits] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [hit_timestamp] datetime NOT NULL,
                          [session_id] int NOT NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[call_records] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [start] datetime NOT NULL,
                          [end] datetime NULL);");

                db.Execute(
                    @"CREATE TABLE [dbo].[sent_emails] (
                          [id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                          [sent_date] datetime NOT NULL);");

                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        [TestMethod]
        public void UpSql_RebuildsAuditIndexWithIncludes_AndIsIdempotent()
        {
            using (var db = CreateReportSchema())
            {
                // Run twice: the second run must recognise the covering definition and skip it,
                // rather than rebuilding a very large index again.
                db.Execute(IndexReportDateQueries.Up_Sql);
                db.Execute(IndexReportDateQueries.Up_Sql);

                Assert.IsTrue(db.IndexHasColumn("audit_events", "IX_audit_events_time_stamp", "time_stamp", 1, false),
                    "The importer's date-range seek relies on [time_stamp] staying the leading key.");
                Assert.IsTrue(db.IndexHasColumn("audit_events", "IX_audit_events_time_stamp", "operation_id", 0, true),
                    "[operation_id] must be included so the activity-by-operation chart avoids a lookup per event.");
                Assert.IsTrue(db.IndexHasColumn("audit_events", "IX_audit_events_time_stamp", "user_id", 0, true),
                    "[user_id] must be included so the Copilot active-users chart is index-only.");
                Assert.AreEqual(1, db.NonClusteredIndexCount("audit_events"),
                    "The existing index should be widened in place, not duplicated.");
            }
        }

        [TestMethod]
        public void UpSql_CreatesTheMissingReportIndexes()
        {
            using (var db = CreateReportSchema())
            {
                db.Execute(IndexReportDateQueries.Up_Sql);

                Assert.IsTrue(db.IndexHasColumn("hits", "IX_hits_hit_timestamp", "hit_timestamp", 1, false));
                Assert.IsTrue(db.IndexHasColumn("hits", "IX_hits_hit_timestamp", "session_id", 0, true),
                    "[session_id] must be included so the unique-visitors chart does not touch the base row.");

                Assert.IsTrue(db.IndexHasColumn("call_records", "IX_call_records_start", "start", 1, false));
                Assert.IsTrue(db.IndexHasColumn("call_records", "IX_call_records_start", "end", 0, true),
                    "[end] must be included so the call-minutes chart can compute durations from the index.");

                Assert.IsTrue(db.IndexHasColumn("sent_emails", "IX_sent_emails_sent_date", "sent_date", 1, false));
            }
        }

        [TestMethod]
        public void DownSql_RestoresTheOriginalIndexes()
        {
            using (var db = CreateReportSchema())
            {
                db.Execute(IndexReportDateQueries.Up_Sql);
                db.Execute(IndexReportDateQueries.Down_Sql);

                // The audit index predates this migration, so Down narrows it rather than dropping
                // it - the importer still needs a seek on [time_stamp].
                Assert.AreEqual(1, db.IndexColumnCount("audit_events", "IX_audit_events_time_stamp"),
                    "The audit index should be back to its key-only shape, not removed.");
                Assert.IsTrue(db.IndexHasColumn("audit_events", "IX_audit_events_time_stamp", "time_stamp", 1, false));

                Assert.IsFalse(db.IndexExists("hits", "IX_hits_hit_timestamp"));
                Assert.IsFalse(db.IndexExists("call_records", "IX_call_records_start"));
                Assert.IsFalse(db.IndexExists("sent_emails", "IX_sent_emails_sent_date"));
            }
        }

        [TestMethod]
        public void UpSql_SkipsMissingTables()
        {
            using (var db = ScratchDatabase.Create("ReportIxBare"))
            {
                db.Execute(IndexReportDateQueries.Up_Sql);
                Assert.IsFalse(db.IndexExists("hits", "IX_hits_hit_timestamp"),
                    "No report tables exist, so nothing should have been indexed.");
            }
        }
    }
}
