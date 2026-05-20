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
    /// Stress test for the Power Platform event-staging + SQL-commit pipeline.
    /// Covers the five adoption workloads served by PowerPlatformAuditEventManager:
    /// Power Apps (with share + connector events), Power Automate (with share + connector events),
    /// Power BI report/dashboard activity, Copilot Studio bots, and Dataverse record operations.
    /// </summary>
    public class PowerPlatformStressTest : BaseStressTest
    {
        private static readonly string[] EnvironmentIds =
        {
            "Default-00000000-0000-0000-0000-000000000001",
            "Production-00000000-0000-0000-0000-000000000002",
            "Dev-00000000-0000-0000-0000-000000000003"
        };

        private static readonly string[] ClientTypes = { "Web", "Mobile", "Desktop", "Teams" };
        private static readonly string[] ShareRoles = { "CanView", "CanEdit", "Owner" };
        private static readonly string[] Connectors = { "shared_sharepointonline", "shared_office365", "shared_teams", "shared_onedriveforbusiness", "shared_sql", "shared_outlookmessage" };
        private static readonly string[] ReportTypes = { "PowerBIReport", "PaginatedReport" };
        private static readonly string[] DataverseEntities = { "account", "contact", "opportunity", "incident", "custom_widget" };

        private static readonly string[] Departments = { "Engineering", "Marketing", "Sales", "Finance", "Human Resources", "Legal", "Operations", "Product", "Design", "Customer Support", "IT", "Research & Development" };
        private static readonly string[] Companies = { "Contoso Ltd", "Fabrikam Inc", "Northwind Traders", "Adventure Works", "Woodgrove Bank", "Tailspin Toys", "Litware Inc", "Proseware" };
        private static readonly string[] JobTitles = { "Software Engineer", "Senior Developer", "Product Manager", "Data Analyst", "UX Designer", "DevOps Engineer", "Solutions Architect", "Business Analyst", "Project Manager", "QA Engineer", "Technical Lead", "VP of Engineering", "Marketing Specialist", "Account Executive", "Support Engineer" };

        private static readonly string[] AppOperations = { "LaunchPowerApp", "EditPowerApp", "PublishPowerApp", "CreatePowerApp" };
        private static readonly string[] FlowOperations = { "FlowRunStarted", "FlowRunCompleted", "EditFlow", "CreateFlow" };

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== Power Platform Event Import Stress Test ===\n");

            int totalEvents = GetIntegerInput("Total Power Platform events to generate", 5000, 1, 1000000);
            int batchSize = GetIntegerInput("Events per commit batch", 500, 1, 100000);
            int distinctApps = GetIntegerInput("Distinct Power Apps", 25, 1, 100000);
            int distinctFlows = GetIntegerInput("Distinct Power Automate flows", 50, 1, 100000);
            int distinctReports = GetIntegerInput("Distinct Power BI reports", 20, 1, 100000);
            int distinctBots = GetIntegerInput("Distinct Copilot Studio bots", 5, 1, 100000);
            int distinctUsers = GetIntegerInput("Distinct users", 200, 1, 100000);

            // Adoption mix (percentages must sum to <= 100; remainder rolls into Dataverse)
            int appPercent = GetIntegerInput("% Power Apps events", 30, 0, 100);
            int sharePercent = GetIntegerInput("% of app/flow events that also include a share", 5, 0, 100);
            int flowPercent = GetIntegerInput("% Power Automate events", 30, 0, 100);
            int powerBiPercent = GetIntegerInput("% Power BI events", 25, 0, 100);
            int copilotStudioPercent = GetIntegerInput("% Copilot Studio events", 5, 0, 100);

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
                Console.WriteLine("No DB connection string - running staging-only (in-memory) stress test.");
            }

            int batchCount = (int)Math.Ceiling((double)totalEvents / batchSize);
            Console.WriteLine($"\nCalculated load:");
            Console.WriteLine($"  Total events: {totalEvents:N0}");
            Console.WriteLine($"  Batches: {batchCount:N0} x {batchSize:N0} events");
            Console.WriteLine($"  Catalogue: {distinctApps:N0} apps, {distinctFlows:N0} flows, {distinctReports:N0} BI reports, {distinctBots:N0} bots, {distinctUsers:N0} users");
            Console.WriteLine($"  Mix: ~{appPercent}% apps, ~{flowPercent}% flows, ~{powerBiPercent}% Power BI, ~{copilotStudioPercent}% Copilot Studio, rest Dataverse. ~{sharePercent}% of app/flow events also include a share recipient.");
            Console.WriteLine($"  Mode: {(commitToSql ? "Full pipeline (staging + SQL commit)" : "Staging only")}\n");
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
            var workspaceCatalogue = BuildIdCatalogue("workspace", Math.Max(1, distinctReports / 5));
            var reportCatalogue = BuildIdCatalogue("report", distinctReports);
            var dashboardCatalogue = BuildIdCatalogue("dashboard", Math.Max(1, distinctReports / 4));
            var botCatalogue = BuildIdCatalogue("bot", distinctBots);
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
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Batch {batch + 1}/{batchCount} ({eventsThisBatch} events) - Memory: {_memoryMonitor.GetMemoryString(_memoryMonitor.CurrentMemoryBytes)}");
                    }

                    try
                    {
                        long batchEvents = RunBatch(eventsThisBatch,
                            appPercent, flowPercent, powerBiPercent, copilotStudioPercent, sharePercent,
                            appCatalogue, flowCatalogue, workspaceCatalogue, reportCatalogue, dashboardCatalogue, botCatalogue,
                            userCatalogue, commitToSql, connectionString, random);

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

        private long RunBatch(int eventCount,
            int appPercent, int flowPercent, int powerBiPercent, int copilotStudioPercent, int sharePercent,
            List<string> apps, List<string> flows, List<string> workspaces, List<string> reports, List<string> dashboards, List<string> bots,
            List<(string Upn, string AadId)> users, bool commitToSql, string connectionString, Random random)
        {
            var effectiveConnectionString = commitToSql ? connectionString : "Server=fake;Database=fake;";
            var quietLogger = new LoggerFactory().CreateLogger("PowerPlatformStress");
            var manager = new PowerPlatformAuditEventManager(effectiveConnectionString, quietLogger);

            var eventIds = new List<(Guid Id, string Upn, string Operation, DateTime TimeStamp)>(eventCount);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < eventCount; i++)
            {
                var eventId = Guid.NewGuid();
                var user = users[random.Next(users.Count)];

                var common = new CommonAuditEvent
                {
                    Id = eventId,
                    TimeStamp = DateTime.UtcNow.AddSeconds(-random.Next(0, 86400)),
                    Operation = new EventOperation { Name = AppOperations[0] },
                    User = new User { AzureAdId = user.AadId, UserPrincipalName = user.Upn }
                };

                int roll = random.Next(0, 100);
                int cumulative = 0;

                if (roll < (cumulative += appPercent))
                {
                    GenerateAppEvent(manager, common, apps, random, sharePercent, users);
                }
                else if (roll < (cumulative += flowPercent))
                {
                    GenerateFlowEvent(manager, common, flows, random, sharePercent, users);
                }
                else if (roll < (cumulative += powerBiPercent))
                {
                    GeneratePowerBIEvent(manager, common, workspaces, reports, dashboards, random);
                }
                else if (roll < (cumulative += copilotStudioPercent))
                {
                    GenerateCopilotStudioEvent(manager, common, bots, random);
                }
                else
                {
                    GenerateDataverseEvent(manager, common, random);
                }

                eventIds.Add((eventId, user.Upn, common.Operation.Name, common.TimeStamp));
            }

            Console.WriteLine($"  Staging: {sw.ElapsedMilliseconds:N0}ms ({eventCount} events: {manager.StagedAppCount} apps + {manager.StagedAppShareCount} app-shares, {manager.StagedFlowCount} flows + {manager.StagedFlowShareCount} flow-shares, {manager.StagedPowerBiCount} BI, {manager.StagedCopilotStudioCount} bots, {manager.StagedDataverseCount} dv)");

            if (commitToSql)
            {
                sw.Restart();
                Console.WriteLine($"  Inserting {eventIds.Count} prerequisite audit_events + users rows...");
                InsertPrerequisiteAuditEvents(connectionString, eventIds);
                Console.WriteLine($"  Prerequisite insert: {sw.ElapsedMilliseconds:N0}ms");

                sw.Restart();
                Console.WriteLine($"  Running CommitAllChanges (staging table insert + 7 merge scripts)...");
                manager.CommitAllChanges().GetAwaiter().GetResult();
                Console.WriteLine($"  CommitAllChanges: {sw.ElapsedMilliseconds:N0}ms");
            }

            return eventCount;
        }

        private void GenerateAppEvent(PowerPlatformAuditEventManager manager, CommonAuditEvent common,
            List<string> apps, Random random, int sharePercent, List<(string Upn, string AadId)> users)
        {
            var appId = apps[random.Next(apps.Count)];
            var operation = AppOperations[random.Next(AppOperations.Length)];
            common.Operation = new EventOperation { Name = operation };

            var content = new PowerAppsAuditLogContent
            {
                AppName = appId,
                AppDisplayName = $"Display-{appId}",
                EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
                AppSessionId = Guid.NewGuid().ToString("N"),
                ClientType = ClientTypes[random.Next(ClientTypes.Length)],
                UserAgent = "Mozilla/5.0 stress-test",
            };

            // Publish events typically carry connector bindings
            if (operation == "PublishPowerApp" || operation == "CreatePowerApp")
            {
                content.ConnectionReferences = new List<PowerPlatformConnectionRef>();
                int connectorCount = random.Next(1, 4);
                for (int c = 0; c < connectorCount; c++)
                {
                    content.ConnectionReferences.Add(new PowerPlatformConnectionRef { ConnectorName = Connectors[random.Next(Connectors.Length)] });
                }
            }

            // Optional share recipients
            if (random.Next(100) < sharePercent)
            {
                content.Permissions = new List<PowerPlatformPermissionEntry>();
                int recipients = random.Next(1, 3);
                for (int r = 0; r < recipients; r++)
                {
                    var recipient = users[random.Next(users.Count)];
                    content.Permissions.Add(new PowerPlatformPermissionEntry
                    {
                        PrincipalName = recipient.Upn,
                        RoleName = ShareRoles[random.Next(ShareRoles.Length)]
                    });
                }
            }

            manager.SaveSinglePowerAppEventToSqlStaging(content, common).GetAwaiter().GetResult();
        }

        private void GenerateFlowEvent(PowerPlatformAuditEventManager manager, CommonAuditEvent common,
            List<string> flows, Random random, int sharePercent, List<(string Upn, string AadId)> users)
        {
            var flowId = flows[random.Next(flows.Count)];
            var operation = FlowOperations[random.Next(FlowOperations.Length)];
            common.Operation = new EventOperation { Name = operation };

            var content = new PowerAutomateAuditLogContent
            {
                FlowId = flowId,
                FlowDisplayName = $"Display-{flowId}",
                EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
                RunId = Guid.NewGuid().ToString("N"),
            };

            if (operation == "CreateFlow" || operation == "EditFlow")
            {
                content.ConnectionReferences = new List<PowerPlatformConnectionRef>();
                int connectorCount = random.Next(1, 4);
                for (int c = 0; c < connectorCount; c++)
                {
                    content.ConnectionReferences.Add(new PowerPlatformConnectionRef { ConnectorName = Connectors[random.Next(Connectors.Length)] });
                }
            }

            if (random.Next(100) < sharePercent)
            {
                content.Permissions = new List<PowerPlatformPermissionEntry>();
                int recipients = random.Next(1, 3);
                for (int r = 0; r < recipients; r++)
                {
                    var recipient = users[random.Next(users.Count)];
                    content.Permissions.Add(new PowerPlatformPermissionEntry
                    {
                        PrincipalName = recipient.Upn,
                        RoleName = ShareRoles[random.Next(ShareRoles.Length)]
                    });
                }
            }

            manager.SaveSinglePowerAutomateEventToSqlStaging(content, common).GetAwaiter().GetResult();
        }

        private void GeneratePowerBIEvent(PowerPlatformAuditEventManager manager, CommonAuditEvent common,
            List<string> workspaces, List<string> reports, List<string> dashboards, Random random)
        {
            // Production only persists ViewReport events (see ActivityReportLoader gate).
            var reportId = reports[random.Next(reports.Count)];
            var content = new PowerBIAuditLogContent
            {
                WorkspaceId = workspaces[random.Next(workspaces.Count)],
                WorkspaceName = $"WS-{random.Next(1, 100)}",
                ReportId = reportId,
                ReportName = $"Report-{reportId}",
                ReportType = ReportTypes[random.Next(ReportTypes.Length)],
            };
            common.Operation = new EventOperation { Name = "ViewReport" };

            manager.SaveSinglePowerBIEventToSqlStaging(content, common).GetAwaiter().GetResult();
        }

        private void GenerateCopilotStudioEvent(PowerPlatformAuditEventManager manager, CommonAuditEvent common,
            List<string> bots, Random random)
        {
            var botId = bots[random.Next(bots.Count)];
            var content = new CopilotStudioAuditLogContent
            {
                BotId = botId,
                BotName = $"Bot-{botId}",
                EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
            };
            common.Operation = new EventOperation { Name = random.Next(0, 10) < 7 ? "MessageSent" : "BotPublished" };
            manager.SaveSingleCopilotStudioEventToSqlStaging(content, common).GetAwaiter().GetResult();
        }

        private void GenerateDataverseEvent(PowerPlatformAuditEventManager manager, CommonAuditEvent common, Random random)
        {
            var content = new DataverseAuditLogContent
            {
                EnvironmentName = EnvironmentIds[random.Next(EnvironmentIds.Length)],
                EntityName = DataverseEntities[random.Next(DataverseEntities.Length)],
                RecordId = Guid.NewGuid().ToString(),
            };
            common.Operation = new EventOperation { Name = new[] { "CreateRecord", "UpdateRecord", "DeleteRecord" }[random.Next(3)] };
            manager.SaveSingleDataverseEventToSqlStaging(content, common).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Inserts the prerequisite users (with departments, companies, job titles) + audit_events rows
        /// so FKs are satisfied when CommitAllChanges runs.
        /// </summary>
        private void InsertPrerequisiteAuditEvents(string connectionString, List<(Guid Id, string Upn, string Operation, DateTime TimeStamp)> eventIds)
        {
            var random = new Random(123);

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                // Insert lookup data: departments, companies, job titles
                InsertLookupValues(conn, "user_departments", Departments);
                InsertLookupValues(conn, "user_company_name", Companies);
                InsertLookupValues(conn, "user_job_titles", JobTitles);

                // Insert distinct users with department, company, and job title
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM users WHERE user_name = @upn)
    INSERT INTO users (user_name, department_id, company_name_id, job_title_id)
    SELECT @upn, d.id, c.id, j.id
    FROM user_departments d, user_company_name c, user_job_titles j
    WHERE d.name = @dept AND c.name = @company AND j.name = @job;";
                    var pUpn = cmd.Parameters.Add("@upn", System.Data.SqlDbType.NVarChar, 400);
                    var pDept = cmd.Parameters.Add("@dept", System.Data.SqlDbType.NVarChar, 100);
                    var pCompany = cmd.Parameters.Add("@company", System.Data.SqlDbType.NVarChar, 100);
                    var pJob = cmd.Parameters.Add("@job", System.Data.SqlDbType.NVarChar, 100);
                    var seenUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ev in eventIds)
                    {
                        if (seenUsers.Add(ev.Upn))
                        {
                            pUpn.Value = ev.Upn;
                            pDept.Value = Departments[random.Next(Departments.Length)];
                            pCompany.Value = Companies[random.Next(Companies.Length)];
                            pJob.Value = JobTitles[random.Next(JobTitles.Length)];
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // Insert distinct operations
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM event_operations WHERE operation_name = @op)
    INSERT INTO event_operations (operation_name) VALUES (@op);";
                    var pOp = cmd.Parameters.Add("@op", System.Data.SqlDbType.NVarChar, 400);
                    var seenOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ev in eventIds)
                    {
                        if (seenOps.Add(ev.Operation))
                        {
                            pOp.Value = ev.Operation;
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // Insert audit_events with user_id and operation_id linked
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO audit_events (id, time_stamp, user_id, operation_id)
SELECT @id, @ts, u.id, o.id
FROM users u
INNER JOIN event_operations o ON o.operation_name = @op
WHERE u.user_name = @upn;";
                    var pId = cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier);
                    var pTs = cmd.Parameters.Add("@ts", System.Data.SqlDbType.DateTime);
                    var pUpn = cmd.Parameters.Add("@upn", System.Data.SqlDbType.NVarChar, 400);
                    var pOp = cmd.Parameters.Add("@op", System.Data.SqlDbType.NVarChar, 400);
                    foreach (var ev in eventIds)
                    {
                        pId.Value = ev.Id;
                        pTs.Value = ev.TimeStamp;
                        pUpn.Value = ev.Upn;
                        pOp.Value = ev.Operation;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void InsertLookupValues(SqlConnection conn, string tableName, string[] values)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
IF NOT EXISTS (SELECT 1 FROM [{tableName}] WHERE name = @name)
    INSERT INTO [{tableName}] (name) VALUES (@name);";
                var pName = cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 100);
                foreach (var val in values)
                {
                    pName.Value = val;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static List<string> BuildIdCatalogue(string prefix, int count)
        {
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add($"{prefix}-{Guid.NewGuid():N}");
            return list;
        }

        private static List<(string Upn, string AadId)> BuildUserCatalogue(int count)
        {
            var list = new List<(string, string)>(count);
            for (int i = 0; i < count; i++) list.Add(($"stressuser{i}@contoso.com", Guid.NewGuid().ToString()));
            return list;
        }
    }
}
