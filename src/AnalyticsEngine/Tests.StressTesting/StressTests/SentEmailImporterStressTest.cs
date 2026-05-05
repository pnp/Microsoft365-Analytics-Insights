using Common.Entities;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tests.StressTesting.FakeLoaders;
using Tests.StressTesting.Infrastructure;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.StressTesting.StressTests
{
    /// <summary>
    /// End-to-end stress test for the sent-email import pipeline. Drives the real
    /// <see cref="SentEmailImporter"/> using a fake <see cref="ISentEmailSourceLoader"/>
    /// and a no-op <see cref="ISentEmailSentimentScorer"/>, so the pipeline (deduplication,
    /// existing-key lookups, address resolution, EF batching, SQL persistence and run
    /// statistics) is exercised at scale without calling Microsoft Graph or Azure AI.
    /// </summary>
    public class SentEmailImporterStressTest : BaseStressTest
    {
        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== Sent Email Importer Pipeline Stress Test ===\n");

            int userCount = GetIntegerInput("Number of fake users to seed", 50, 1, 100_000);
            int messagesPerUser = GetIntegerInput("Synthetic sent messages per user", 200, 0, 500_000);
            int maxRecipientsPerEmail = GetIntegerInput("Max recipients per message", 3, 1, 50);
            int distinctRecipientPool = GetIntegerInput("Distinct recipient address pool size", 500, 1, 1_000_000);
            bool deleteFirst = GetBooleanInput("Delete previously-generated stress data before run", false);
            bool runTwice = GetBooleanInput("Run import twice (second pass exercises duplicate detection)", false);

            string connectionString = ConnectionString;
            if (string.IsNullOrEmpty(connectionString))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nNo SQL connection string provided - this test must commit to a real database.");
                Console.WriteLine("Re-run the stress runner with a connection string argument.");
                Console.ResetColor();
                return new StressTestResult
                {
                    Success = false,
                    Message = "No DB connection string provided."
                };
            }

            long expectedRows = (long)userCount * messagesPerUser;
            Console.WriteLine($"\nPlanned load:");
            Console.WriteLine($"  Users:                       {userCount:N0}");
            Console.WriteLine($"  Synthetic messages per user: {messagesPerUser:N0}");
            Console.WriteLine($"  Total messages generated:    {expectedRows:N0}");
            Console.WriteLine($"  Recipient pool size:         {distinctRecipientPool:N0}");
            Console.WriteLine($"  Run twice (dedup test):      {(runTwice ? "yes" : "no")}");
            Console.WriteLine();
            Console.WriteLine("Press any key to start (Ctrl-C to abort)...");
            Console.ReadKey();
            Console.WriteLine();

            var result = new StressTestResult { Success = true };

            try
            {
                Func<AnalyticsEntitiesContext> dbFactory =
                    () => new AnalyticsEntitiesContext(connectionString, true, true);

                using (var db = dbFactory())
                {
                    Console.WriteLine("Initializing database...");
                    db.Database.Initialize(force: false);
                    Console.WriteLine("Database ready.");
                }

                if (deleteFirst)
                {
                    DeleteExistingStressData(dbFactory);
                }

                _memoryMonitor.Start();
                var totalSw = Stopwatch.StartNew();

                // 1) Seed users so SentEmailImporter.LoadUsersWithMailAsync finds mailboxes to scan.
                int seededUsers = SeedUsers(dbFactory, userCount);
                _memoryMonitor.UpdatePeak();

                // 2) Build importer wired up with the fake loader + null sentiment scorer.
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var appConfig = FakeAppConfigFactory.Create();
                var fakeLoader = new FakeSentEmailSourceLoader(
                    messagesPerUser: messagesPerUser,
                    maxRecipientsPerEmail: maxRecipientsPerEmail,
                    distinctRecipientPoolSize: distinctRecipientPool);
                var sentimentScorer = NullSentEmailSentimentScorer.Instance;

                var importer = new SentEmailImporter(
                    telemetry,
                    appConfig,
                    fakeLoader,
                    sentimentScorer,
                    dbFactory);

                Console.WriteLine($"\nRunning sent emails import pass #1 against {seededUsers:N0} seeded users...");
                var pass1Sw = Stopwatch.StartNew();
                RunImporter(importer);
                pass1Sw.Stop();
                Console.WriteLine($"Pass #1 finished in {pass1Sw.Elapsed:hh\\:mm\\:ss}.");
                _memoryMonitor.UpdatePeak();

                if (runTwice)
                {
                    Console.WriteLine($"\nRunning sent emails import pass #2 (expecting all rows to be detected as duplicates)...");
                    var pass2Sw = Stopwatch.StartNew();
                    RunImporter(importer);
                    pass2Sw.Stop();
                    Console.WriteLine($"Pass #2 finished in {pass2Sw.Elapsed:hh\\:mm\\:ss}.");
                    _memoryMonitor.UpdatePeak();
                }

                totalSw.Stop();
                _memoryMonitor.Stop();

                long actualMessages = CountStressRows(dbFactory);
                long actualRecipients = CountStressRecipients(dbFactory);

                result.ItemsProcessed = actualMessages;
                result.Duration = totalSw.Elapsed;
                result.InitialMemoryBytes = _memoryMonitor.InitialMemoryBytes;
                result.PeakMemoryBytes = _memoryMonitor.PeakMemoryBytes;
                result.FinalMemoryBytes = _memoryMonitor.CurrentMemoryBytes;
                result.Message =
                    $"Seeded {seededUsers:N0} users; importer persisted {actualMessages:N0} sent_email rows " +
                    $"(with {actualRecipients:N0} sent_email_recipients rows).";

                _memoryMonitor.PrintReport();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Exception = ex.GetBaseException();
                result.Message = $"Test failed: {result.Exception.Message}";
            }

            return result;
        }

        private static void RunImporter(SentEmailImporter importer)
        {
            // SentEmailImporter exposes an async ImportSentEmails() method - block here as
            // stress tests are synchronous console runs.
            importer.ImportSentEmails().GetAwaiter().GetResult();
        }

        #region DB helpers

        private static int SeedUsers(Func<AnalyticsEntitiesContext> dbFactory, int userCount)
        {
            Console.WriteLine($"Seeding {userCount:N0} fake users...");
            var sw = Stopwatch.StartNew();
            int inserted = 0;

            using (var db = dbFactory())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                db.Configuration.ValidateOnSaveEnabled = false;

                // Avoid re-inserting duplicates if the test is re-run without "delete first".
                var existing = db.users
                    .Where(u => u.UserPrincipalName.StartsWith("stress-user"))
                    .Select(u => u.UserPrincipalName)
                    .ToList();
                var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

                const int batch = 1000;
                var pending = new List<User>(batch);

                for (int i = 0; i < userCount; i++)
                {
                    var upn = $"stress-user{i:D6}@stress.local";
                    if (existingSet.Contains(upn))
                        continue;

                    pending.Add(new User
                    {
                        UserPrincipalName = upn,
                        Mail = upn,
                        AzureAdId = Guid.NewGuid().ToString()
                    });

                    if (pending.Count >= batch)
                    {
                        db.users.AddRange(pending);
                        db.ChangeTracker.DetectChanges();
                        db.SaveChanges();
                        foreach (var u in pending)
                            db.Entry(u).State = EntityState.Detached;
                        inserted += pending.Count;
                        pending.Clear();
                    }
                }

                if (pending.Count > 0)
                {
                    db.users.AddRange(pending);
                    db.ChangeTracker.DetectChanges();
                    db.SaveChanges();
                    inserted += pending.Count;
                }
            }

            sw.Stop();
            Console.WriteLine($"  Seeded {inserted:N0} new users in {sw.ElapsedMilliseconds:N0}ms " +
                              $"(existing stress users left untouched).");

            // Return total stress users in the DB (newly inserted + existing).
            using (var db = dbFactory())
            {
                return db.users.Count(u => u.UserPrincipalName.StartsWith("stress-user"));
            }
        }

        private static long CountStressRows(Func<AnalyticsEntitiesContext> dbFactory)
        {
            using (var db = dbFactory())
            {
                return db.SentEmails.LongCount(s => s.GraphMessageId.StartsWith("stress-msg-"));
            }
        }

        private static long CountStressRecipients(Func<AnalyticsEntitiesContext> dbFactory)
        {
            using (var db = dbFactory())
            {
                return db.SentEmailRecipients
                    .LongCount(r => r.SentEmail.GraphMessageId.StartsWith("stress-msg-"));
            }
        }

        private static void DeleteExistingStressData(Func<AnalyticsEntitiesContext> dbFactory)
        {
            Console.WriteLine("Deleting previously-generated stress data...");
            var sw = Stopwatch.StartNew();
            using (var db = dbFactory())
            {
                // Recipients first because of the FK to sent_emails.
                db.Database.ExecuteSqlCommand(
                    "DELETE r FROM sent_email_recipients r " +
                    "INNER JOIN sent_emails s ON s.id = r.sent_email_id " +
                    "WHERE s.graph_message_id LIKE 'stress-msg-%'");
                db.Database.ExecuteSqlCommand("DELETE FROM sent_emails WHERE graph_message_id LIKE 'stress-msg-%'");
                db.Database.ExecuteSqlCommand("DELETE FROM email_addresses WHERE address LIKE '%@stress.local' OR address LIKE 'recipient-%'");
                db.Database.ExecuteSqlCommand("DELETE FROM users WHERE user_name LIKE 'stress-user%@stress.local'");
            }
            sw.Stop();
            Console.WriteLine($"  Deleted in {sw.ElapsedMilliseconds:N0}ms.");
        }

        #endregion
    }
}
