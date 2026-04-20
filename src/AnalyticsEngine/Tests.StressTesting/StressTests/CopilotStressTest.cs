using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using Tests.StressTesting.Infrastructure;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.StressTesting.StressTests
{
    /// <summary>
    /// Stress test for the Copilot event staging and SQL commit pipeline.
    /// Exercises CopilotAuditEventManager at scale to detect memory leaks, measure throughput,
    /// and validate the accessed-resources SQL path under load.
    /// </summary>
    public class CopilotStressTest : BaseStressTest
    {
        private static readonly string[] AppHosts = { "Word", "Excel", "PowerPoint", "Outlook", "Teams", "OneNote", "Loop", "M365App" };
        private static readonly string[] ResourceTypes = { "Document", "Spreadsheet", "Presentation", "Link", "Email", "Meeting", "Chat" };
        private static readonly string[] SiteUrls =
        {
            "https://contoso.sharepoint.com/sites/engineering",
            "https://contoso.sharepoint.com/sites/marketing",
            "https://contoso.sharepoint.com/sites/hr",
            "https://contoso.sharepoint.com/sites/finance",
            "https://contoso.sharepoint.com/sites/legal"
        };

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== Copilot Event Import Stress Test Configuration ===\n");

            int totalEvents = GetIntegerInput("Total copilot events to generate", 5000, 1, 1000000);
            int batchSize = GetIntegerInput("Events per commit batch", 500, 1, 100000);
            int maxResourcesPerEvent = GetIntegerInput("Max accessed resources per event", 5, 0, 50);
            bool includeAgents = GetBooleanInput("Include agent data", true);
            bool collectGarbageEachBatch = GetBooleanInput("Force GC after each batch", false);
            bool verbose = GetBooleanInput("Verbose output", false);

            // Use connection string from command-line args (passed via BaseStressTest.ConnectionString)
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
            Console.WriteLine($"  Up to {(long)totalEvents * maxResourcesPerEvent:N0} accessed resource records");
            Console.WriteLine($"  Mode: {(commitToSql ? "Full pipeline (staging + SQL commit)" : "Staging only (in-memory)")}");
            Console.WriteLine();
            Console.WriteLine("Press any key to start test...");
            Console.ReadKey();
            Console.WriteLine();

            if (commitToSql)
            {
                // Initialize database via EF (creates DB + runs migrations if needed)
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

            var result = new StressTestResult { Success = true };
            var random = new Random(42); // Fixed seed for reproducibility

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
                        long batchEvents = RunBatch(eventsThisBatch, maxResourcesPerEvent, includeAgents, commitToSql, connectionString, random);

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
                    Console.WriteLine("This may indicate a memory leak in the copilot staging pipeline.");
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

        private long RunBatch(int eventCount, int maxResourcesPerEvent, bool includeAgents,
            bool commitToSql, string connectionString, Random random)
        {
            // Use a fake connection string for staging-only mode; real one for SQL commits
            var effectiveConnectionString = commitToSql ? connectionString : "Server=fake;Database=fake;";
            var quietLogger = new LoggerFactory().CreateLogger("CopilotStress");
            var manager = new CopilotAuditEventManager(effectiveConnectionString, new FakeCopilotMetadataLoader(), 
                quietLogger);

            // Build all events first, tracking their IDs for prerequisite inserts
            var eventIds = new List<Guid>(eventCount);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < eventCount; i++)
            {
                var eventId = Guid.NewGuid();
                eventIds.Add(eventId);

                var commonEvent = new CommonAuditEvent
                {
                    Id = eventId,
                    TimeStamp = DateTime.UtcNow,
                    Operation = new EventOperation { Name = "CopilotInteraction" },
                    User = new User
                    {
                        AzureAdId = Guid.NewGuid().ToString(),
                        UserPrincipalName = $"stressuser{random.Next(1, 1000)}@contoso.com"
                    }
                };

                var auditRecord = GenerateRandomCopilotEvent(random, maxResourcesPerEvent, includeAgents);
                manager.SaveSingleCopilotEventToSqlStaging(auditRecord, commonEvent).GetAwaiter().GetResult();
            }

            Console.WriteLine($"  Staging: {sw.ElapsedMilliseconds:N0}ms ({eventCount} events staged)");

            if (commitToSql)
            {
                // Pre-insert audit_events rows so the copilot_chats FK constraint is satisfied
                sw.Restart();
                Console.WriteLine($"  Inserting {eventIds.Count} prerequisite audit_events rows...");
                InsertPrerequisiteAuditEvents(connectionString, eventIds);
                Console.WriteLine($"  Prerequisite audit_events insert: {sw.ElapsedMilliseconds:N0}ms");

                sw.Restart();
                Console.WriteLine($"  Running CommitAllChanges (staging table insert + merge SQL)...");
                manager.CommitAllChanges().GetAwaiter().GetResult();
                Console.WriteLine($"  CommitAllChanges: {sw.ElapsedMilliseconds:N0}ms");
            }

            return eventCount;
        }

        /// <summary>
        /// Bulk-inserts minimal audit_events rows so the copilot_chats FK constraint is satisfied.
        /// Fully synchronous to avoid async deadlocks on .NET Framework 4.8.
        /// </summary>
        private void InsertPrerequisiteAuditEvents(string connectionString, List<Guid> eventIds)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO audit_events (id, time_stamp) VALUES (@id, @ts)";
                    var pId = cmd.Parameters.Add("@id", System.Data.DbType.Guid);
                    var pTs = cmd.Parameters.Add("@ts", System.Data.DbType.DateTime2);
                    pTs.Value = DateTime.UtcNow;

                    foreach (var id in eventIds)
                    {
                        pId.Value = id;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private CopilotAuditLogContent GenerateRandomCopilotEvent(Random random, int maxResourcesPerEvent, bool includeAgents)
        {
            var appHost = AppHosts[random.Next(AppHosts.Length)];
            var resourceCount = random.Next(0, maxResourcesPerEvent + 1);

            var accessedResources = new List<AccessedResource>();
            for (int r = 0; r < resourceCount; r++)
            {
                var resource = new AccessedResource
                {
                    Id = $"resource-{Guid.NewGuid():N}",
                    Name = $"StressDoc{random.Next(1, 10000)}.{GetRandomExtension(random)}",
                    Type = ResourceTypes[random.Next(ResourceTypes.Length)]
                };

                // ~60% of resources have a SiteUrl
                if (random.NextDouble() < 0.6)
                {
                    resource.SiteUrl = SiteUrls[random.Next(SiteUrls.Length)];
                }

                // ~40% of resources have a sensitivity label
                if (random.NextDouble() < 0.4)
                {
                    resource.SensitivityLabelId = $"label-{random.Next(1, 20)}";
                }

                accessedResources.Add(resource);
            }

            var content = new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    AppHost = appHost,
                    AccessedResources = accessedResources
                }
            };

            if (includeAgents && random.NextDouble() < 0.3)
            {
                // ~30% of events have an agent
                var agentIndex = random.Next(1, 10);
                content.AgentId = $"CopilotStudio.Declarative.Agent{agentIndex}";
                content.AgentName = $"StressAgent{agentIndex}";
                content.IsCustomAgent = random.NextDouble() < 0.5;
            }

            return content;
        }

        private static string GetRandomExtension(Random random)
        {
            var extensions = new[] { "docx", "xlsx", "pptx", "pdf", "msg", "txt", "csv" };
            return extensions[random.Next(extensions.Length)];
        }
    }
}
