using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("DemoGenerator")]
    [TestCategory("Integration")]
    [DoNotParallelize]
    public class DemoGeneratorSqlTests
    {
        [TestMethod]
        public void RealSchema_GenerationReportingRerunsAndRefusalsAreSafe()
        {
            string name = "ContosoDemo_Test_" + Guid.NewGuid().ToString("N");
            var options = DemoOptions.Parse(new[] { "--database", name, "--users", "30", "--days", "35",
                "--as-of", "2026-09-01", "--batch-size", "1000" }, DateTime.UtcNow);
            try
            {
                using (var database = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    database.Open(null);
                    var summary = DemoCommand.NewSummary(options);
                    using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                        new DemoGenerator(options).Generate(sink, summary, null);
                    database.ValidateAndComplete(summary, null);
                    Assert.IsTrue(summary.CompletedProfileWeeks > 0);
                    Assert.ThrowsException<InvalidOperationException>(() => database.CreateSink());
                }
                using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection(name)))
                {
                    connection.Open();
                    Assert.AreEqual(30L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                    Assert.AreEqual(30L * 33, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.teams_user_activity_log;"));
                    Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.copilot_chats c
JOIN dbo.audit_events a ON a.id=c.event_id WHERE a.user_id<>c.user_id OR a.time_stamp<>c.time_stamp;"));
                    Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.audit_events
WHERE DATEDIFF(day,'19000101',time_stamp)%7 IN (5,6);"));
                    Assert.AreEqual(1L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_state_or_province WHERE name=N'Αττική';"));
                    Assert.AreEqual(13L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.urls WHERE full_url LIKE N'%Καλημέρα%';"));
                    Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM (
SELECT user_id,license_type_id FROM dbo.user_license_type_lookups GROUP BY user_id,license_type_id HAVING COUNT_BIG(*)>1) d;"));
                }
                using (var raw = new RawContext(SqlDemoDatabase.LocalConnection(name)))
                {
                    var rows = raw.Database.SqlQuery<LicensedUserUsageRow>(
                        CopilotAdoptionSql.LicensedUsersSql(new[] { 2 }, new[] { 5 }, includeCopilotReport: true),
                        new SqlParameter("@from", options.AsOf.AddDays(-28)),
                        new SqlParameter("@historyFrom", options.AsOf.AddDays(-365)),
                        new SqlParameter("@maxRows", 1000),
                        new SqlParameter("@copilotReportDate", options.ReportEnd),
                        new SqlParameter("@copilotReportPeriodDays", 28)).ToList();
                    Assert.AreEqual(18, rows.Count);
                    Assert.AreEqual(rows.Count, rows.Select(r => r.UserId).Distinct().Count());
                }
                using (var rerun = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    rerun.Open(null);
                    Assert.IsTrue(rerun.AlreadyComplete);
                    Assert.ThrowsException<InvalidOperationException>(() => rerun.CreateSink());
                }
                var changed = DemoOptions.Parse(new[] { "--database", name, "--users", "30", "--days", "35",
                    "--as-of", "2026-09-01", "--seed", "43" }, DateTime.UtcNow);
                using (var refused = new SqlDemoDatabase(changed, CancellationToken.None))
                {
                    Assert.ThrowsException<InvalidOperationException>(() => refused.Open(null));
                    Assert.ThrowsException<InvalidOperationException>(() => refused.CreateSink());
                }
                using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection(name)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "EXEC sys.sp_updateextendedproperty @name=N'M365AnalyticsSyntheticDemoState', @value=N'building';";
                        command.ExecuteNonQuery();
                    }
                }
                using (var refused = new SqlDemoDatabase(options, CancellationToken.None))
                    Assert.ThrowsException<InvalidOperationException>(() => refused.Open(null));
                using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection(name)))
                {
                    connection.Open();
                    Assert.AreEqual(30L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                }
            }
            finally { DropOwnedScratch(name); }
        }

        [TestMethod]
        public void ExistingUnmarkedLocalTarget_IsNeverClaimedOrUpgraded()
        {
            string name = "ContosoDemo_Refusal_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection("master")))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE DATABASE [" + name + "];";
                        command.ExecuteNonQuery();
                    }
                }
                using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection(name)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE TABLE dbo.ContosoSentinel (id int NOT NULL); INSERT dbo.ContosoSentinel VALUES (1);";
                        command.ExecuteNonQuery();
                    }
                }
                var options = DemoOptions.Parse(new[] { "--database", name }, new DateTime(2026, 9, 1));
                using (var refused = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    Assert.ThrowsException<InvalidOperationException>(() => refused.Open(null));
                    Assert.ThrowsException<InvalidOperationException>(() => refused.CreateSink());
                }
                using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection(name)))
                {
                    connection.Open();
                    Assert.AreEqual(1L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.ContosoSentinel;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.tables WHERE name='users';"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.extended_properties WHERE class=0;"));
                }
            }
            finally { DropOwnedScratch(name); }
        }

        private static long Scalar(SqlConnection connection, string sql)
        {
            using (var command = connection.CreateCommand()) { command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        }

        private static void DropOwnedScratch(string name)
        {
            using (var connection = new SqlConnection(SqlDemoDatabase.LocalConnection("master")))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [" + name
                        + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + name + "]; END;";
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
            }
        }

        private sealed class RawContext : DbContext
        {
            static RawContext() { Database.SetInitializer<RawContext>(null); }
            public RawContext(string connection) : base(connection) { }
        }
    }
}
