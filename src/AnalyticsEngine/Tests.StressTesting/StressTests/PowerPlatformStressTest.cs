using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using Tests.StressTesting.Infrastructure;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.StressTesting.StressTests
{
    /// <summary>
    /// Stress test for the Power Platform (Power Apps + Power Automate) event staging and SQL commit pipeline.
    /// Mirrors CopilotStressTest: generates a configurable mix of Power Apps launches and Flow runs,
    /// exercising PowerPlatformAuditEventManager at scale to detect memory leaks, measure throughput
    /// and validate the lookup-deduplication SQL path under load.
    /// </summary>
    public class PowerPlatformStressTest : BaseStressTest
    {
        // Synthetic catalogue – mimics a tenant with a moderate number of repeat apps / flows.
        private static readonly string[] EnvironmentIds =
        {
            "Default-00000000-0000-0000-0000-000000000001",
            "Production-00000000-0000-0000-0000-000000000002",
            "Dev-00000000-0000-0000-0000-000000000003"
        };

        private static readonly string[] RecurrenceTypes = { "Manual", "Recurrence", "Automated", "Hybrid" };

        private static readonly string[] AppOperations = { "LaunchPowerApp", "EditPowerApp", "PublishPowerApp" };
        private static readonly string[] FlowOperations = { "FlowRunStarted", "FlowRunCompleted", "EditFlow", "CreateFlow" };

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== Power Platform Event Import Stress Test Configuration ===\n");

            int totalEvents = GetIntegerInput("Total Power Platform events to generate", 5000, 1, 1000000);
            int batchSize = GetIntegerInput("Events per commit batch", 500, 1, 100000);
            int distinctApps = GetIntegerInput("Distinct Power Apps to simulate", 25, 1, 100000);
            int distinctFlows = GetIntegerInput("Distinct Power Automate flows to simulate", 50, 1, 100000);
            int distinctUsers = GetIntegerInput("Distinct users to simulate", 200, 1, 100000);
            int appEventPercent = GetIntegerInput("Percent of events that are Power Apps (0-100)", 50, 0, 100);
            int adminEventPercent = GetIntegerInput("Percent of events that are admin events (0-100)", 5, 0, 100);
            bool collectGarbageEachBatch = GetBooleanInput("Force GC after each batch", false);
            bool verbose = GetBooleanInput("Verbose output", false);

            string connectionString = ConnectionString;
            bool commitToSql = false;

            if (!string.IsNullOrEmpty(connectionString))
            {
                commitToSql = GetBooleanInput("DB connection string provided. Commit batches to SQL", true);
            }
            else
            {
                Console.WriteLine("No DB connection string provided - running staging-only (in-memory) stress test.");
                Console.WriteLine("Pass a connection string as a command-line argument to enable SQL commits.");
            }

            int batchCount = (int)Math.Ceiling((double)totalEvents / batchSize);
            Console.WriteLine($"\nCalculated load:");
            Console.WriteLine($"  Total events: {totalEvents:N0}");
            Console.WriteLine($"  Batches: {batchCount:N0} x {batchSize:N0} events");
            Console.WriteLine($"  Apps catalogue: {distinctApps:N0}, Flows catalogue: {distinctFlows:N0}, Users: {distinctUsers:N0}");
            Console.WriteLine($"  Mix: ~{appEventPercent}% apps, ~{adminEventPercent}% admin, remainder flows");
            Console.WriteLine($"  Mode: {(commitToSql ? "Full pipeline (staging + SQL commit)" : "Staging only (in-memory)")}");
            Console.WriteLine();
            Console.WriteLine("Press any key to start test...");
            Console.ReadKey();
            Console.WriteLine();

            if (commitToSql)
            {
                Console.WriteLine("Initializing database...");
                try
                {
                    using (var db = new AnalyticsEntitiesContext(connectionString, true, true))
                    {
                        db.Database.Initialize(force: false);
                    }
                    Console.WriteLine("Database ready.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Database initialization FAILED: {ex.GetBaseException().Message}");
                    Console.ResetColor();
                    return new StressTestResult { Success = false, Message = $"Cannot initialize DB: {ex.GetBaseException().Message}", Exception = ex.GetBaseException() };
                }
            }

            var appCatalogue = BuildIdCatalogue("app", distinctApps);
            var flowCatalogue = BuildIdCatalogue("flow", distinctFlows);
            var userCatalogue = BuildUserCatalogue(distinctUsers);

            var result = new StressTestResult { Success = true };
            var random = new Random(42);

            try
            {
                _memoryMonitor.Start();
                var stopwatch = Stopwatch.StartNew();
                long totalEventsProcessed = 0;

                for (int batch = 0; batch < batchCount; batch++)
                {
                    int eventsThisBatch = Math.Min(batchSize, totalEvents - (batch * batchSize));

                    if (verbose || batch % 5 == 0)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Batch {batch + 1}/{batchCount} " +
                            $"({eventsThisBatch} events) - Memory: {_memoryMonitor.GetMemoryString(_memoryMonitor.CurrentMemoryBytes)}");
                    }

                    try
                    {
                        long batchEvents = RunBatch(eventsThisBatch, appEventPercent, adminEventPercent,
                            appCatalogue, flowCatalogue, userCatalogue,
                            commitToSql, connectionString, random);

                        totalEventsProcessed += batchEvents;
                        _memoryMonitor.UpdatePeak();

                        if (collectGarbageEachBatch)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"ERROR in batch {batch + 1}: {ex.GetBaseException().Message}");
                        Console.ResetColor();
                        result.Success = false;
                        result.Exception = ex.GetBaseException();
                        break;
                    }
                }

                stopwatch.Stop();
                _memoryMonitor.Stop();

                result.ItemsProcessed = totalEventsProcessed;
                result.Duration = stopwatch.Elapsed;
                result.InitialMemoryBytes = _memoryMonitor.InitialMemoryBytes;
                result.PeakMemoryBytes = _memoryMonitor.PeakMemoryBytes;
                result.FinalMemoryBytes = _memoryMonitor.CurrentMemoryBytes;

                if (result.Success)
                {
                    result.Message = $"Completed {batchCount} batch(es), {totalEventsProcessed:N0} events" +
                        $"{(commitToSql ? " committed to SQL" : " staged in-memory")}";
                }

                long memoryGrowth = result.FinalMemoryBytes - result.InitialMemoryBytes;
                double growthPercentage = result.InitialMemoryBytes > 0
                    ? (memoryGrowth / (double)result.InitialMemoryBytes) * 100
                    : 0;

                if (growthPercentage > 50)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\nWARNING: Memory grew by {growthPercentage:F1}% ({_memoryMonitor.GetMemoryString(memoryGrowth)})");
                    Console.WriteLine("This may indicate a memory leak in the power-platform staging pipeline.");
                    Console.ResetColor();
                    result.Message += $" | WARNING: {growthPercentage:F1}% memory growth";
                }

                _memoryMonitor.PrintReport();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Exception = ex;
                result.Message = $"Test failed: {ex.Message}";
            }

            return result;
        }

        private long RunBatch(int eventCount, int appEventPercent, int adminEventPercent,
            List<string> appCatalogue, List<string> flowCatalogue, List<(string Upn, string AadId)> userCatalogue,
            bool commitToSql, string connectionString, Random random)
        {
            var effectiveConnectionString = commitToSql ? connectionString : "Server=fake;Database=fake;";
            var quietLogger = new LoggerFactory().CreateLogger("PowerPlatformStress");
            var manager = new PowerPlatformAuditEventManager(effectiveConnectionString, quietLogger);

            var eventIds = new List<(Guid Id, string Upn, string AadId)>(eventCount);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < eventCount; i++)
            {
                var eventId = Guid.NewGuid();
                var user = userCatalogue[random.Next(userCatalogue.Count)];
                eventIds.Add((eventId, user.Upn, user.AadId));

                var common = new CommonAuditEvent
                {
                    Id = eventId,
                    TimeStamp = DateTime.UtcNow,
                    Operation = new EventOperation { Name = AppOperations[0] },
                    User = new User
                    {
                        AzureAdId = user.AadId,
                        UserPrincipalName = user.Upn
                    }
                };

                int roll = random.Next(0, 100);
                if (roll < appEventPercent)
                {
                    var appId = appCatalogue[random.Next(appCatalogue.Count)];
                    var content = new PowerAppsAuditLogContent
                    {
                        AppName = appId,
                        AppDisplayName = $"Display-{appId}",
                        EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
                        AppSessionId = Guid.NewGuid().ToString("N"),
                    };
                    common.Operation = new EventOperation { Name = AppOperations[random.Next(AppOperations.Length)] };
                    manager.SaveSinglePowerAppEventToSqlStaging(content, common).GetAwaiter().GetResult();
                }
                else if (roll < appEventPercent + adminEventPercent)
                {
                    var content = new PowerPlatformAdminAuditLogContent
                    {
                        EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
                        OriginalImportFileContents = "{\"PolicyChange\":\"stress\"}",
                    };
                    common.Operation = new EventOperation { Name = "AdminAction" };
                    manager.SaveSinglePowerPlatformAdminEventToSqlStaging(content, common).GetAwaiter().GetResult();
                }
                else
                {
                    var flowId = flowCatalogue[random.Next(flowCatalogue.Count)];
                    var content = new PowerAutomateAuditLogContent
                    {
                        FlowId = flowId,
                        FlowDisplayName = $"Display-{flowId}",
                        EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
                        RunId = Guid.NewGuid().ToString("N"),
                        RecurrenceType = RecurrenceTypes[random.Next(RecurrenceTypes.Length)],
                    };
                    common.Operation = new EventOperation { Name = FlowOperations[random.Next(FlowOperations.Length)] };
                    manager.SaveSinglePowerAutomateEventToSqlStaging(content, common).GetAwaiter().GetResult();
                }
            }

            Console.WriteLine($"  Staging: {sw.ElapsedMilliseconds:N0}ms ({eventCount} events staged)");

            if (commitToSql)
            {
                sw.Restart();
                Console.WriteLine($"  Inserting {eventIds.Count} prerequisite audit_events + users rows...");
                InsertPrerequisiteAuditEvents(connectionString, eventIds);
                Console.WriteLine($"  Prerequisite insert: {sw.ElapsedMilliseconds:N0}ms");

                sw.Restart();
                Console.WriteLine($"  Running CommitAllChanges (staging table insert + merge SQL)...");
                manager.CommitAllChanges().GetAwaiter().GetResult();
                Console.WriteLine($"  CommitAllChanges: {sw.ElapsedMilliseconds:N0}ms");
            }

            return eventCount;
        }

        /// <summary>
        /// Inserts minimal users + audit_events rows so the FK constraints on the metadata tables are satisfied.
        /// </summary>
        private void InsertPrerequisiteAuditEvents(string connectionString, List<(Guid Id, string Upn, string AadId)> eventIds)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // Make sure each distinct UPN exists in users.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM users WHERE user_name = @upn)
    INSERT INTO users (user_name) VALUES (@upn);";
                    var pUpn = cmd.Parameters.Add("@upn", System.Data.SqlDbType.NVarChar, 400);

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ev in eventIds)
                    {
                        if (seen.Add(ev.Upn))
                        {
                            pUpn.Value = ev.Upn;
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO audit_events (id, time_stamp) VALUES (@id, @ts)";
                    var pId = cmd.Parameters.AddWithValue("@id", DBNull.Value);
                    var pTs = cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow);

                    foreach (var ev in eventIds)
                    {
                        pId.Value = ev.Id;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static List<string> BuildIdCatalogue(string prefix, int count)
        {
            var list = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add($"{prefix}-{Guid.NewGuid():N}");
            }
            return list;
        }

        private static List<(string Upn, string AadId)> BuildUserCatalogue(int count)
        {
            var list = new List<(string, string)>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(($"stressuser{i}@contoso.com", Guid.NewGuid().ToString()));
            }
            return list;
        }
    }
}
