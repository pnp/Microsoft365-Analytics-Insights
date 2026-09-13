using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    /// <summary>
    /// The demo exists to be looked at in the portal, so "the rows are present" is not the property that
    /// matters - "the report can measure them" is. This runs the Licence assignments report's own coverage
    /// query against a generated database and asserts Copilot is measured, not merely stored.
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    [TestCategory("Integration")]
    [DoNotParallelize]
    public class DemoLicenceActivityCoverageTests
    {
        // Fixed so the reporting window is deterministic: the default period is 28 days ending on the
        // Sunday on or before (as-of - 3), which for 2026-09-01 is 2026-07-27..2026-08-23.
        private static readonly DateTime AsOf = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void ShortHistory_StillLetsTheLicenceReportMeasureCopilot()
        {
            // 35 days is the case that used to break. The official Copilot rows began 27 days into the
            // window (2026-08-24), one day AFTER the reporting window ends, so the report had no
            // fully-contained snapshot and banded every user "unknown" - while the M365 workloads, whose
            // rows are daily rather than rolling, stayed fully measured. That asymmetry is the bug.
            string name = "ContosoDemo_Coverage_" + Guid.NewGuid().ToString("N");
            var options = DemoOptions.Parse(new[] { "--database", name, "--users", "30", "--days", "35",
                "--as-of", "2026-09-01", "--batch-size", "1000", "--no-profiles" }, DateTime.UtcNow);
            try
            {
                using (var database = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    database.Open(null);
                    var summary = DemoCommand.NewSummary(options);
                    using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                        new DemoGenerator(options).Generate(sink, summary, null);
                    database.ValidateAndComplete(summary, null);
                }

                var query = LicenceActivityQuery.Create(null, null, AsOf);
                Assert.AreEqual("2026-07-27", query.From);
                Assert.AreEqual("2026-08-23", query.To);

                var overview = new SqlLicenceActivityStore(SqlDemoDatabase.LocalConnection(name))
                    .LoadOverviewAsync(query, AllSources(), null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                var copilot = overview.Coverage.Single(c => c.Workload == "copilot");
                Assert.AreEqual("available", copilot.Status,
                    "Copilot must be measurable on the default reporting period: " + copilot.Message);
                Assert.AreEqual("microsoftGraphCopilotUsageReport", copilot.Source,
                    "The audit and interaction sources are positive evidence only and never produce bands.");

                var licence = overview.Licences.OrderByDescending(l => l.AssignedUsers).First();
                var bands = licence.Workloads.Single(w => w.Workload == "copilot");
                int measured = bands.High + bands.Moderate + bands.Low + bands.Zero;
                Assert.IsTrue(measured > 0,
                    $"Every holder of {licence.SkuId} was unknown for Copilot; the portal renders that as \"Not measured\".");

                // Only Copilot-licensed users appear in the official report, so some unknowns are correct -
                // but they must not be the whole population, which is what the defect looked like.
                Assert.IsTrue(bands.Unknown < licence.AssignedUsers);
            }
            finally { Drop(name); }
        }

        private static LicenceActivitySources AllSources() => new LicenceActivitySources
        {
            UserMetadata = true,
            UsageReports = true,
            CopilotUsageReports = true,
            CopilotAudit = true,
            CopilotInteractions = true,
            NowUtc = AsOf
        };

        private static void Drop(string name)
        {
            using (var master = new SqlConnection(SqlDemoDatabase.LocalConnection("master")))
            {
                master.Open();
                using (var command = master.CreateCommand())
                {
                    command.CommandText =
                        "IF DB_ID(@name) IS NOT NULL EXEC('ALTER DATABASE [' + @name + '] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [' + @name + ']');";
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
