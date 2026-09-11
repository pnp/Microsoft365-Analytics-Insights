using Common.Entities.LicenceActivity;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// Turns "the portal shows nothing" into an answer the operator gets while they are still looking at
    /// the generator.
    ///
    /// The portal decides which workloads it can measure from the WEB APPLICATION's ImportJobSettings
    /// app setting, not from the rows in the database. Every one of those flags is opt-in and defaults to
    /// false, so a demo database full of Copilot activity still renders the Licence assignments report's
    /// Copilot column as "Not measured" until the app is told the source exists. Nothing the generator
    /// writes can change that, so it prints the setting instead - and then runs the report's own coverage
    /// query so the operator can see what the portal will show before they wire anything up.
    /// </summary>
    internal static class DemoPortalReadiness
    {
        /// <summary>
        /// The import toggles whose tables this generator actually fills. Deliberately not "everything":
        /// a flag listed here but not generated would send an operator looking for data that is absent.
        /// </summary>
        internal const string RequiredImportJobSettings =
            "GraphUsersMetadata=True;GraphUsageReports=True;GraphCopilotUsageReports=True;"
            + "Copilot=True;CopilotInteractionHistory=True;ActivityLog=True;WebTraffic=True;ImportDlp=True;"
            + "CopilotStudioCredits=True;AzureCostManagement=True";

        internal static void PrintPortalSetup(string database, Action<string> write)
        {
            if (write == null) return;
            write(string.Empty);
            write("To view this data, point a web application at it with:");
            write($"  connectionStrings   SPOInsightsEntities = Server=(localdb)\\MSSQLLocalDB;Database={database};Integrated Security=True");
            write("  appSettings         ImportJobSettings = " + RequiredImportJobSettings);
            write(string.Empty);
            write("Those toggles are how the portal decides which sources exist; they are all opt-in and");
            write("default to false. In particular, WITHOUT GraphCopilotUsageReports=True the Licence");
            write("assignments report shows Copilot as \"Not measured\" for every user even though this");
            write("database is full of Copilot activity: the audit and interaction sources are positive");
            write("evidence only and never produce activity bands.");
            write("Likewise the Agent costs report leads with \"Neither agent cost import is switched on\"");
            write("until CopilotStudioCredits and AzureCostManagement are set, no matter how much billing");
            write("data the database holds - that banner is read from this setting, never from the rows.");
        }

        /// <summary>
        /// Runs the Licence assignments report's own coverage query against the finished database and
        /// prints, per workload, what the portal will be able to measure. Diagnostic only: any failure is
        /// reported and swallowed, because the data is already generated and verified by this point.
        /// </summary>
        internal static void PrintMeasuredCoverage(string connectionString, DateTime nowUtc, Action<string> write)
        {
            if (write == null) return;
            write(string.Empty);
            write("Checking what the Licence assignments report can measure (its own query, all sources enabled)...");
            var watch = Stopwatch.StartNew();
            try
            {
                var query = LicenceActivityQuery.Create(null, null, nowUtc);
                var sources = new LicenceActivitySources
                {
                    UserMetadata = true,
                    UsageReports = true,
                    CopilotUsageReports = true,
                    CopilotAudit = true,
                    CopilotInteractions = true,
                    NowUtc = nowUtc
                };
                var overview = new SqlLicenceActivityStore(connectionString)
                    .LoadOverviewAsync(query, sources, null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                var largest = overview.Licences.OrderByDescending(l => l.AssignedUsers).FirstOrDefault();
                write($"Default reporting period {query.From} to {query.To}"
                    + (largest == null ? ":" : $", licence \"{largest.Name}\" ({largest.AssignedUsers:N0} users):"));
                foreach (var coverage in overview.Coverage)
                {
                    var bands = largest?.Workloads.FirstOrDefault(w => w.Workload == coverage.Workload);
                    int measured = bands == null ? 0 : bands.High + bands.Moderate + bands.Low + bands.Zero;
                    write($"  {coverage.Workload,-10} {coverage.Status,-16} "
                        + (bands == null ? string.Empty : $"{measured:N0} measured, {bands.Unknown:N0} unknown"));
                }
                if (largest != null)
                {
                    var unmeasured = overview.Coverage
                        .Where(c => (largest.Workloads.FirstOrDefault(w => w.Workload == c.Workload)?.Unknown ?? 0)
                                    == largest.AssignedUsers)
                        .Select(c => c.Workload)
                        .ToList();
                    if (unmeasured.Count > 0)
                    {
                        write("Unmeasured for every holder of that licence: " + string.Join(", ", unmeasured)
                            + ". The portal renders these as \"Not measured\", not as zero activity.");
                    }
                }
                write($"Coverage check took {watch.Elapsed.TotalSeconds:F1}s. The portal applies the same rules,");
                write("but only to the sources its own ImportJobSettings enables.");
            }
            catch (Exception ex)
            {
                write("Coverage check could not be completed: " + ex.Message);
                write("The generated data is unaffected; this step only reports what the portal would show.");
            }
        }
    }
}
