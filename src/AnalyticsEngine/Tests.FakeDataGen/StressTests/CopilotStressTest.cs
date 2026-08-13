using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using Tests.FakeDataGen.Seeding;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.FakeDataGen.StressTests
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

        // A response can be Classic (1 credit), Generative (2) or TenantGraph (10). Mirrors the audit schema.
        private static readonly string[] ResponseTypes = { "Classic", "Generative", "TenantGraph" };

        // Model names surfaced in ModelTransparencyDetails. DEEP_LEO drives premium (deep-reasoning) billing.
        private static readonly string[] ModelNames = { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "DEEP_LEO" };

        // Synthetic document-name fragments. Deliberately includes non-Latin (Greek) text so round-trip /
        // truncation bugs surface in the nvarchar staging + merge path rather than in a customer tenant
        // (SharePoint/OneDrive names routinely contain scripts like Greek). See repo character-set convention.
        private static readonly string[] DocNameFragments =
        {
            "QuarterlyReport", "Budget", "Roadmap", "Καλημέρα κόσμε", "Σχέδιο", "Ετήσια Ανασκόπηση",
            "Proposal", "MeetingNotes", "Análisis", "Presupuesto", "戦略計画", "プレゼン"
        };

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== Copilot Event Import Stress Test Configuration ===\n");

            int totalEvents = GetIntegerInput("Total copilot events to generate", 5000, 1, 1000000, "STRESS_TOTAL_EVENTS");
            int batchSize = GetIntegerInput("Events per commit batch", 500, 1, 100000, "STRESS_BATCH_SIZE");
            int maxResourcesPerEvent = GetIntegerInput("Max accessed resources per event", 5, 0, 50, "STRESS_MAX_RESOURCES");
            int maxMessagesPerEvent = GetIntegerInput("Max response messages per event", 6, 0, 500, "STRESS_MAX_MESSAGES");
            int distinctUsers = GetIntegerInput("Distinct users to seed (with departments, job titles, licenses)", 1000, 1, 200000, "STRESS_DISTINCT_USERS");
            bool includeAgents = GetBooleanInput("Include agent data", true, "STRESS_INCLUDE_AGENTS");
            bool collectGarbageEachBatch = GetBooleanInput("Force GC after each batch", false, "STRESS_GC_EACH_BATCH");
            bool verbose = GetBooleanInput("Verbose output", false, "STRESS_VERBOSE");

            // Use connection string from command-line args (passed via BaseStressTest.ConnectionString)
            string connectionString = ConnectionString;
            bool commitToSql = false;

            if (!string.IsNullOrEmpty(connectionString))
            {
                commitToSql = GetBooleanInput("DB connection string provided. Commit batches to SQL", true, "STRESS_COMMIT_SQL");
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
            Console.WriteLine($"  Up to {(long)totalEvents * maxMessagesPerEvent:N0} response-message records");
            Console.WriteLine($"  Distinct users: {distinctUsers:N0} (seeded with departments, job titles, company, location + licenses)");
            Console.WriteLine($"  Mode: {(commitToSql ? "Full pipeline (staging + SQL commit)" : "Staging only (in-memory)")}");
            Console.WriteLine();
            PauseIfInteractive("Press any key to start test...");
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

                    // Seed a pool of users with realistic metadata (departments, job titles,
                    // company, locations) + licenses so the audit_events committed below link to
                    // real users and copilot reports sliced by department / job title have data.
                    Console.WriteLine($"Seeding {distinctUsers:N0} users with metadata...");
                    SeedUsersWithMetadata(connectionString, distinctUsers);
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

            // UPN pool the events draw from. Matches the users seeded above (when committing to
            // SQL) so every event links to a metadata-rich user; harmless in staging-only mode.
            var userCatalogue = BuildUserCatalogue(distinctUsers);

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
                        long batchEvents = RunBatch(eventsThisBatch, maxResourcesPerEvent, maxMessagesPerEvent, includeAgents, commitToSql, connectionString, userCatalogue, random);

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

        private long RunBatch(int eventCount, int maxResourcesPerEvent, int maxMessagesPerEvent, bool includeAgents,
            bool commitToSql, string connectionString, IReadOnlyList<string> userCatalogue, Random random)
        {
            // Use a fake connection string for staging-only mode; real one for SQL commits
            var effectiveConnectionString = commitToSql ? connectionString : "Server=fake;Database=fake;";
            var quietLogger = new LoggerFactory().CreateLogger("CopilotStress");
            var manager = new CopilotAuditEventManager(effectiveConnectionString, new FakeCopilotMetadataLoader(),
                quietLogger);

            // Build all events first, tracking their IDs + owning user UPN for prerequisite inserts
            var eventIds = new List<(Guid Id, string Upn)>(eventCount);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < eventCount; i++)
            {
                var eventId = Guid.NewGuid();
                var upn = userCatalogue[random.Next(userCatalogue.Count)];
                eventIds.Add((eventId, upn));

                var commonEvent = new CommonAuditEvent
                {
                    Id = eventId,
                    TimeStamp = DateTime.UtcNow,
                    Operation = new EventOperation { Name = "CopilotInteraction" },
                    User = new User
                    {
                        AzureAdId = Guid.NewGuid().ToString(),
                        UserPrincipalName = upn
                    }
                };

                var auditRecord = GenerateRandomCopilotEvent(random, maxResourcesPerEvent, maxMessagesPerEvent, includeAgents);
                manager.SaveSingleCopilotEventToSqlStaging(auditRecord, commonEvent).GetAwaiter().GetResult();
            }

            Console.WriteLine($"  Staging: {sw.ElapsedMilliseconds:N0}ms ({eventCount} events staged)");

            if (commitToSql)
            {
                // Pre-insert audit_events rows so the copilot_chats FK constraint is satisfied
                sw.Restart();
                Console.WriteLine($"  Inserting {eventIds.Count} prerequisite audit_events rows (linked to seeded users)...");
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
        /// Inserts audit_events rows (each linked to its seeded, metadata-rich user) so the
        /// copilot_chats FK constraint is satisfied and copilot data is attributed to real users.
        /// Fully synchronous to avoid async deadlocks on .NET Framework 4.8.
        /// </summary>
        private void InsertPrerequisiteAuditEvents(string connectionString, List<(Guid Id, string Upn)> eventIds)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    // Link each audit_event to its seeded user so copilot reports sliced by
                    // department / job title / license have real data. Users are pre-seeded in
                    // SeedUsersWithMetadata, so the join always resolves.
                    cmd.CommandText = @"
INSERT INTO audit_events (id, time_stamp, user_id)
SELECT @id, @ts, u.id
FROM users u
WHERE u.user_name = @upn;";
                    var pId = cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier);
                    var pTs = cmd.Parameters.Add("@ts", System.Data.SqlDbType.DateTime);
                    var pUpn = cmd.Parameters.Add("@upn", System.Data.SqlDbType.NVarChar, 400);

                    foreach (var ev in eventIds)
                    {
                        pId.Value = ev.Id;
                        pTs.Value = DateTime.UtcNow;
                        pUpn.Value = ev.Upn;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Builds the UPN pool the stress events draw from. Matches the format used by
        /// <see cref="UserMetadataSeeder.SeedUsers"/> so events line up with the seeded users.
        /// </summary>
        private static List<string> BuildUserCatalogue(int count)
        {
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add(SeedDataCatalogue.BuildUpn("stressuser", i));
            return list;
        }

        /// <summary>
        /// Seeds the shared metadata lookups + license catalogue, then a pool of users carrying
        /// department, job title, company, location (and other) metadata plus random licenses, via
        /// the same idempotent helper used by the other stress tests. Re-runnable: existing stress
        /// users are left untouched.
        /// </summary>
        private static void SeedUsersWithMetadata(string connectionString, int userCount)
        {
            var random = new Random(123);
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                UserMetadataSeeder.EnsureMetadataLookups(conn);
                UserMetadataSeeder.EnsureLicenseTypes(conn);
                var licenseIds = UserMetadataSeeder.LoadLicenseTypeIds(conn);

                var seeded = UserMetadataSeeder.SeedUsers(conn, userCount, random);
                Console.WriteLine($"  Seeded {seeded.Count:N0} new users with department/job-title/company/location " +
                                  $"metadata (existing stress users left untouched).");

                if (seeded.Count > 0 && licenseIds.Count > 0)
                {
                    var newUserIds = new List<int>(seeded.Count);
                    foreach (var u in seeded) newUserIds.Add(u.Id);
                    int assigned = UserMetadataSeeder.AssignRandomLicenses(conn, newUserIds, licenseIds, random, maxLicensesPerUser: 2);
                    Console.WriteLine($"  Assigned {assigned:N0} license(s) across newly-created users.");
                }
            }
        }

        private CopilotAuditLogContent GenerateRandomCopilotEvent(Random random, int maxResourcesPerEvent, int maxMessagesPerEvent, bool includeAgents)
        {
            var appHost = AppHosts[random.Next(AppHosts.Length)];
            var resourceCount = random.Next(0, maxResourcesPerEvent + 1);

            var accessedResources = new List<AccessedResource>();
            for (int r = 0; r < resourceCount; r++)
            {
                var resource = new AccessedResource
                {
                    Id = $"resource-{Guid.NewGuid():N}",
                    Name = $"{DocNameFragments[random.Next(DocNameFragments.Length)]} {random.Next(1, 10000)}.{GetRandomExtension(random)}",
                    Type = ResourceTypes[random.Next(ResourceTypes.Length)],
                    Action = random.NextDouble() < 0.8 ? "Read" : "Edit",
                    ListItemUniqueId = Guid.NewGuid().ToString()
                };

                // ~60% of resources have a SiteUrl (incl. a non-Latin path to exercise Unicode round-trips)
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
                },
                // Populate the parsed event so messages_json + model_transparency_json are exercised - these
                // are the blobs the chat-only merge (common_upsert_copilot_agents.sql) re-parses with OPENJSON,
                // so a realistic before/after must include them (the previous generator left them null).
                ParsedAuditEvent = BuildParsedEvent(random, accessedResources, maxMessagesPerEvent)
            };

            if (includeAgents && random.NextDouble() < 0.3)
            {
                // ~30% of events have an agent
                var agentIndex = random.Next(1, 10);
                content.AgentId = $"CopilotStudio.Declarative.Agent{agentIndex}";
                content.AgentName = $"StressAgent{agentIndex}";
                content.IsCustomAgent = random.NextDouble() < 0.5;
            }

            content.Cost = BuildCost(content.ParsedAuditEvent);
            return content;
        }

        /// <summary>
        /// Builds a realistic parsed Copilot event: a multi-turn conversation (prompt + response per turn,
        /// only responses are billable / serialized) plus 1-2 transparency models. Mirrors the shape of real
        /// BizChat chat-only events, which carry the largest messages_json blobs.
        /// </summary>
        private static CopilotAuditEvent BuildParsedEvent(Random random, List<AccessedResource> accessedResources, int maxMessagesPerEvent)
        {
            var responseCount = maxMessagesPerEvent <= 0 ? 0 : random.Next(1, maxMessagesPerEvent + 1);

            var messages = new List<Message>(responseCount * 2);
            for (int m = 0; m < responseCount; m++)
            {
                // User prompt (not billable, filtered out before SQL) ...
                messages.Add(new Message { Id = Guid.NewGuid().ToString(), IsPrompt = true });
                // ... followed by the Copilot response (billable, serialized to messages_json).
                messages.Add(new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    IsPrompt = false,
                    Type = ResponseTypes[random.Next(ResponseTypes.Length)]
                });
            }

            var models = new List<ModelTransparencyDetail>
            {
                new ModelTransparencyDetail { ModelName = ModelNames[random.Next(ModelNames.Length)] }
            };
            if (random.NextDouble() < 0.25)
            {
                models.Add(new ModelTransparencyDetail { ModelName = ModelNames[random.Next(ModelNames.Length)] });
            }

            return new CopilotAuditEvent
            {
                AccessedResources = accessedResources,
                Messages = messages,
                ModelTransparencyDetails = models,
                AnswerType = ResponseTypes[random.Next(ResponseTypes.Length)]
            };
        }

        /// <summary>
        /// Builds a non-trivial cost estimate so copilot_credit_estimate_json is exercised. New instance every
        /// call - never the shared <see cref="CopilotCreditEstimation.NoCost"/> singleton.
        /// </summary>
        private static CopilotCreditEstimation BuildCost(CopilotAuditEvent parsed)
        {
            int responses = 0;
            if (parsed?.Messages != null)
            {
                foreach (var msg in parsed.Messages)
                {
                    if (!msg.IsPrompt) responses++;
                }
            }

            return new CopilotCreditEstimation
            {
                GenerativeAnswers = responses,
                TotalCredits = responses * 2,
                CreditBreakdown = new Dictionary<string, int> { { "GenerativeAnswers", responses * 2 } },
                ModelsUsed = parsed?.ModelTransparencyDetails?.ConvertAll(m => m.ModelName) ?? new List<string>()
            };
        }

        private static string GetRandomExtension(Random random)
        {
            var extensions = new[] { "docx", "xlsx", "pptx", "pdf", "msg", "txt", "csv" };
            return extensions[random.Next(extensions.Length)];
        }
    }
}
