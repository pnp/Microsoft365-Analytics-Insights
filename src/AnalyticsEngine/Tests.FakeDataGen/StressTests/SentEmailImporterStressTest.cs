using Common.Entities;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tests.FakeDataGen.Seeding;
using Tests.FakeDataGen.StressTests.FakeLoaders;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// End-to-end stress test for the sent-email import pipeline. Drives the real
    /// <see cref="SentEmailImporter"/> using a fake <see cref="ISentEmailSourceLoader"/>
    /// and a fake <see cref="ISentEmailSentimentScorer"/> (synthetic 0-1 cognitive scores,
    /// or a no-op when scoring is turned off), so the pipeline (deduplication, existing-key
    /// lookups, address resolution, sentiment scoring, EF batching, SQL persistence and run
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
            int internalEmailPercent = GetIntegerInput("Percent of emails that are internal (recipients on sender's domain)", 70, 0, 100);
            bool deleteFirst = GetBooleanInput("Delete previously-generated stress data before run", false);
            bool runTwice = GetBooleanInput("Run import twice (second pass exercises duplicate detection)", false);
            bool generateScores = GetBooleanInput("Generate cognitive/sentiment scores (0-1, mostly happy)", true);

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
            Console.WriteLine($"  Internal email mix:          {internalEmailPercent}% internal / {100 - internalEmailPercent}% external");
            Console.WriteLine($"  Run twice (dedup test):      {(runTwice ? "yes" : "no")}");
            Console.WriteLine($"  Cognitive scoring:           {(generateScores ? "on (0-1, mostly happy)" : "off")}");
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
                int seededUsers = SeedUsers(dbFactory, connectionString, userCount);
                _memoryMonitor.UpdatePeak();

                // 2) Build importer wired up with the fake loader + sentiment scorer.
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var appConfig = FakeAppConfigFactory.Create();
                var fakeLoader = new FakeSentEmailSourceLoader(
                    messagesPerUser: messagesPerUser,
                    maxRecipientsPerEmail: maxRecipientsPerEmail,
                    distinctRecipientPoolSize: distinctRecipientPool,
                    internalEmailPercent: internalEmailPercent);
                // Use the fake scorer to populate sent_emails.cognitive_score with synthetic
                // 0-1 sentiment values (0 = very unhappy, 1 = very happy). Skewed happy with
                // ups and downs so the data isn't a single constant. The null scorer leaves
                // every cognitive_score NULL, so only switch to it when scoring is turned off.
                ISentEmailSentimentScorer sentimentScorer = generateScores
                    ? (ISentEmailSentimentScorer)new FakeSentEmailSentimentScorer()
                    : NullSentEmailSentimentScorer.Instance;

                var importer = new SentEmailImporter(
                    logger,
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
                string scoreSummary = DescribeScoreCoverage(dbFactory);
                string mixSummary = DescribeInternalExternalMix(dbFactory);

                result.ItemsProcessed = actualMessages;
                result.Duration = totalSw.Elapsed;
                result.InitialMemoryBytes = _memoryMonitor.InitialMemoryBytes;
                result.PeakMemoryBytes = _memoryMonitor.PeakMemoryBytes;
                result.FinalMemoryBytes = _memoryMonitor.CurrentMemoryBytes;
                result.Message =
                    $"Seeded {seededUsers:N0} users; importer persisted {actualMessages:N0} sent_email rows " +
                    $"(with {actualRecipients:N0} sent_email_recipients rows). Cognitive scores: {scoreSummary}. " +
                    $"Internal/external: {mixSummary}.";

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

        private static int SeedUsers(Func<AnalyticsEntitiesContext> dbFactory, string connectionString, int userCount)
        {
            Console.WriteLine($"Seeding {userCount:N0} fake users...");
            var sw = Stopwatch.StartNew();
            int inserted = 0;

            // Seed the shared metadata lookup tables (departments, job titles, companies, states,
            // countries, office + usage locations) up front and load their ids, so every seeded
            // user can be given realistic metadata FKs. Without this the importer's users have a
            // null department/job-title (etc.) and any report that slices sent email by department
            // or job title is empty. Uses the same idempotent helper and catalogue as the other
            // stress tests so all of them populate identical metadata.
            var random = new Random(123);
            List<int> departmentIds, jobTitleIds, companyIds, stateIds, countryIds, officeIds, usageIds;
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                UserMetadataSeeder.EnsureMetadataLookups(conn);
                departmentIds = UserMetadataSeeder.LoadLookupIds(conn, "user_departments");
                jobTitleIds = UserMetadataSeeder.LoadLookupIds(conn, "user_job_titles");
                companyIds = UserMetadataSeeder.LoadLookupIds(conn, "user_company_name");
                stateIds = UserMetadataSeeder.LoadLookupIds(conn, "user_state_or_province");
                countryIds = UserMetadataSeeder.LoadLookupIds(conn, "user_country_or_region");
                officeIds = UserMetadataSeeder.LoadLookupIds(conn, "user_office_locations");
                usageIds = UserMetadataSeeder.LoadLookupIds(conn, "user_usage_locations");
            }
            Console.WriteLine($"  Metadata lookups ready ({departmentIds.Count} departments, " +
                              $"{jobTitleIds.Count} job titles) - assigning random metadata to seeded users.");

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
                        AzureAdId = Guid.NewGuid().ToString(),
                        DepartmentId = PickOrNull(departmentIds, random),
                        JobTitleId = PickOrNull(jobTitleIds, random),
                        CompanyNameId = PickOrNull(companyIds, random),
                        StateOrProvinceId = PickOrNull(stateIds, random),
                        UserCountryId = PickOrNull(countryIds, random),
                        OfficeLocationId = PickOrNull(officeIds, random),
                        UsageLocationId = PickOrNull(usageIds, random)
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

        /// <summary>
        /// Picks a random id from a lookup id list, or null when the list is empty so the user's
        /// FK column is left NULL rather than pointing at a non-existent row.
        /// </summary>
        private static int? PickOrNull(IList<int> ids, Random random)
        {
            if (ids == null || ids.Count == 0) return null;
            return ids[random.Next(ids.Count)];
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

        /// <summary>
        /// Summarises how many stress sent_email rows received a cognitive score and the
        /// distribution (avg/min/max), so a run confirms scores were actually written.
        /// </summary>
        private static string DescribeScoreCoverage(Func<AnalyticsEntitiesContext> dbFactory)
        {
            using (var db = dbFactory())
            {
                var stressRows = db.SentEmails.Where(s => s.GraphMessageId.StartsWith("stress-msg-"));
                long total = stressRows.LongCount();
                if (total == 0)
                    return "no stress rows present";

                var scoredRows = stressRows.Where(s => s.CognitiveScore != null);
                long scored = scoredRows.LongCount();
                if (scored == 0)
                    return $"0/{total:N0} rows scored";

                double avg = scoredRows.Average(s => s.CognitiveScore).Value;
                double min = scoredRows.Min(s => s.CognitiveScore).Value;
                double max = scoredRows.Max(s => s.CognitiveScore).Value;
                return $"{scored:N0}/{total:N0} rows scored (avg {avg:F2}, range {min:F2}-{max:F2})";
            }
        }

        /// <summary>
        /// Classifies each stress sent_email as internal (no recipient on a different domain to the
        /// sender) or external, so a run confirms the configured internal/external mix landed.
        /// Done in raw SQL because the classification needs the address domain (the part after '@'),
        /// which EF can't translate cleanly.
        /// </summary>
        private static string DescribeInternalExternalMix(Func<AnalyticsEntitiesContext> dbFactory)
        {
            const string sql =
                "SELECT ISNULL(SUM(CAST(x.is_internal AS bigint)), 0) AS InternalCount, COUNT_BIG(*) AS TotalCount " +
                "FROM ( " +
                "  SELECT CASE WHEN EXISTS ( " +
                "      SELECT 1 FROM sent_email_recipients r " +
                "      INNER JOIN email_addresses ra ON ra.id = r.recipient_address_id " +
                "      WHERE r.sent_email_id = s.id " +
                "        AND SUBSTRING(ra.address, CHARINDEX('@', ra.address) + 1, 4000) " +
                "            <> SUBSTRING(fa.address, CHARINDEX('@', fa.address) + 1, 4000) " +
                "    ) THEN 0 ELSE 1 END AS is_internal " +
                "  FROM sent_emails s " +
                "  INNER JOIN email_addresses fa ON fa.id = s.from_address_id " +
                "  WHERE s.graph_message_id LIKE 'stress-msg-%' " +
                ") x;";

            using (var db = dbFactory())
            {
                var row = db.Database.SqlQuery<EmailMixRow>(sql).FirstOrDefault();
                if (row == null || row.TotalCount == 0)
                    return "no stress rows present";

                long external = row.TotalCount - row.InternalCount;
                double pct = row.InternalCount * 100.0 / row.TotalCount;
                return $"{row.InternalCount:N0} internal / {external:N0} external ({pct:F0}% internal)";
            }
        }

        private class EmailMixRow
        {
            public long InternalCount { get; set; }
            public long TotalCount { get; set; }
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
