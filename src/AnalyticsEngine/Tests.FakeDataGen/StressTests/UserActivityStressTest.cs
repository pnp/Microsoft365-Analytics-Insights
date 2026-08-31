using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// Seeds users + metadata + licenses + per-workload user-activity-log rows + copilot
    /// chat events so the profiling SQL in
    /// App.ControlPanel.Engine/SqlExtentions/Profiling-03-CreateSchema.sql can be
    /// exercised against realistic data volumes.
    ///
    /// Defaults to 56 weeks of activity (the default maximum retention window of the
    /// profiling solution). Activity is generated for every workload referenced by the
    /// profiling SQL: Teams, OneDrive, SharePoint, Outlook, Yammer, Teams device usage,
    /// M365 app platform usage, Yammer device usage, and Copilot chats (audit_events +
    /// copilot_chats). Counts are weighted high on weekdays (Mon-Fri) and low on
    /// weekends (Sat-Sun) so the resulting profiling reports reflect a realistic
    /// working-week shape.
    ///
    /// User + license + lookup-metadata seeding is delegated to
    /// <see cref="UserMetadataSeeder"/> so every stress test and data generator
    /// shares the same shape.
    /// </summary>
    public class UserActivityStressTest : BaseStressTest
    {
        private const int DefaultWeeks = 56;
        private const int DefaultUsers = 200;
        private const int DefaultActivityDaysPerWeek = 1;
        private const int DefaultMaxLicensesPerUser = 2;
        private const int DefaultMaxCopilotChatsPerWeekday = 8;

        // Weekend activity is scaled down to this fraction of the weekday baseline so
        // every activity row shows a clear weekday/weekend split in the profiling output.
        private const double WeekendActivityScale = 0.15;
        private const double WeekendBoolProbabilityScale = 0.25;

        // Copilot host names referenced by the PIVOT in Profiling-03-CreateSchema.sql.
        // Keep this in sync with the host list there or new hosts will be PIVOTed as 0.
        private static readonly string[] CopilotAppHosts =
        {
            "bizchat", "appchat", "Assist365", "Bing", "BashTool", "DevUI", "Excel",
            "Loop", "M365AdminCenter", "M365App", "Office", "OneNote", "Outlook",
            "Planner", "PowerPoint", "SharePoint", "Stream", "Teams",
            "VivaCopilot", "VivaEngage", "VivaGoals", "Whiteboard", "Word"
        };

        // Workloads matching the activity tables referenced from Profiling-03-CreateSchema.sql.
        private enum Workload
        {
            Teams,
            OneDrive,
            SharePoint,
            Outlook,
            Yammer,
            TeamsDeviceUsage,
            AppPlatform,
            YammerDevice
        }

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== User Activity Stress Test Configuration ===\n");

            int userCount = GetIntegerInput("Number of distinct users to seed", DefaultUsers, 1, 1_000_000);
            int weeks = GetIntegerInput("Weeks of activity to generate (default = profiling retention)", DefaultWeeks, 1, 520);
            int daysPerWeek = GetIntegerInput("Activity rows per user per week", DefaultActivityDaysPerWeek, 1, 7);
            int maxLicensesPerUser = GetIntegerInput("Max licenses per user", DefaultMaxLicensesPerUser, 0, SeedDataCatalogue.LicenseCatalogue.Length);
            int maxCopilotChatsPerWeekday = GetIntegerInput("Max copilot chats per user per active weekday", DefaultMaxCopilotChatsPerWeekday, 0, 1000);
            int bulkBatchSize = GetIntegerInput("SqlBulkCopy batch size", 10_000, 100, 1_000_000);
            bool verbose = GetBooleanInput("Verbose output", false);

            string connectionString = ConnectionString;
            if (string.IsNullOrEmpty(connectionString))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("This stress test writes directly to SQL and needs a connection string on the command line.");
                Console.ResetColor();
                return new StressTestResult
                {
                    Success = false,
                    Message = "No connection string provided - UserActivityStressTest requires SQL.",
                };
            }

            int workloadCount = Enum.GetNames(typeof(Workload)).Length;
            long rowsPerWorkload = (long)userCount * weeks * daysPerWeek;
            long totalActivityRows = rowsPerWorkload * workloadCount;

            // Expected copilot rows: avg = (5 weekdays * weekdayMax/2) + (2 weekends * weekdayMax/2 * scale).
            long expectedCopilotPerUserWeek = (long)Math.Round(
                5 * (maxCopilotChatsPerWeekday / 2.0) +
                2 * (maxCopilotChatsPerWeekday / 2.0) * WeekendActivityScale);
            long expectedCopilotRows = (long)userCount * weeks * expectedCopilotPerUserWeek;

            Console.WriteLine($"\nCalculated load:");
            Console.WriteLine($"  Users:                 {userCount:N0}");
            Console.WriteLine($"  Weeks of activity:     {weeks:N0} (default = {DefaultWeeks})");
            Console.WriteLine($"  Activity rows / week:  {daysPerWeek}");
            Console.WriteLine($"  Rows per workload:     {rowsPerWorkload:N0}");
            Console.WriteLine($"  Workloads:             {workloadCount}");
            Console.WriteLine($"  Total activity rows:   {totalActivityRows:N0}");
            Console.WriteLine($"  Copilot chats (est):   ~{expectedCopilotRows:N0} (and matching audit_events rows)");
            Console.WriteLine($"  Weekend scale:         counts x {WeekendActivityScale:P0}, bools x {WeekendBoolProbabilityScale:P0}");
            Console.WriteLine();
            Console.WriteLine("Press any key to start test...");
            Console.ReadKey();
            Console.WriteLine();

            Console.WriteLine("Initializing database (EF migrations)...");
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

            var result = new StressTestResult { Success = true };
            var random = new Random(42);

            try
            {
                _memoryMonitor.Start();
                var stopwatch = Stopwatch.StartNew();
                long totalRowsInserted = 0;

                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();

                    Console.WriteLine("Seeding lookup tables...");
                    UserMetadataSeeder.EnsureMetadataLookups(conn);

                    Console.WriteLine("Seeding license_types...");
                    UserMetadataSeeder.EnsureLicenseTypes(conn);
                    var licenseIds = UserMetadataSeeder.LoadLicenseTypeIds(conn);

                    Console.WriteLine($"Seeding {userCount:N0} users with random metadata...");
                    var sw = Stopwatch.StartNew();
                    var insertedUsers = UserMetadataSeeder.SeedUsers(conn, userCount, random,
                        upnPrefix: "useractivitystress");
                    Console.WriteLine($"  Inserted {insertedUsers.Count:N0} new user(s) in {sw.ElapsedMilliseconds:N0}ms.");

                    if (insertedUsers.Count == 0)
                    {
                        Console.WriteLine("All requested users already existed - re-loading their ids for license + activity seeding...");
                        insertedUsers = LoadExistingStressUsers(conn, "useractivitystress", userCount);
                    }

                    if (maxLicensesPerUser > 0)
                    {
                        sw.Restart();
                        var userIds = new List<int>(insertedUsers.Count);
                        foreach (var u in insertedUsers) userIds.Add(u.Id);
                        int licensesAssigned = UserMetadataSeeder.AssignRandomLicenses(conn, userIds, licenseIds, random, maxLicensesPerUser);
                        Console.WriteLine($"  Assigned {licensesAssigned:N0} licenses across {userIds.Count:N0} user(s) in {sw.ElapsedMilliseconds:N0}ms.");
                    }

                    _memoryMonitor.UpdatePeak();

                    Console.WriteLine($"\nGenerating activity rows for {insertedUsers.Count:N0} user(s) x {weeks} week(s) x {daysPerWeek} day(s)/week across {workloadCount} workload(s)...");

                    // Activity dates run from "weeks*7 - 1" days ago up to today, picking
                    // the configured number of distinct days per week per user.
                    DateTime endDate = DateTime.UtcNow.Date;
                    foreach (Workload workload in Enum.GetValues(typeof(Workload)))
                    {
                        sw.Restart();
                        long rowsForWorkload = BulkInsertWorkload(conn, workload, insertedUsers, weeks, daysPerWeek, endDate, bulkBatchSize, random, verbose);
                        totalRowsInserted += rowsForWorkload;
                        Console.WriteLine($"  {workload,-18} -> {rowsForWorkload:N0} rows in {sw.ElapsedMilliseconds:N0}ms");
                        _memoryMonitor.UpdatePeak();
                    }

                    if (maxCopilotChatsPerWeekday > 0)
                    {
                        Console.WriteLine($"\nGenerating copilot audit_events + copilot_chats (weekdays high, weekends low)...");
                        sw.Restart();
                        long copilotRows = BulkInsertCopilotEvents(conn, insertedUsers, weeks, endDate, maxCopilotChatsPerWeekday, bulkBatchSize, random, verbose);
                        Console.WriteLine($"  Copilot            -> {copilotRows:N0} chat events (+ matching audit_events) in {sw.ElapsedMilliseconds:N0}ms");
                        totalRowsInserted += copilotRows * 2; // one audit_events row per copilot_chats row.
                        _memoryMonitor.UpdatePeak();
                    }
                }

                stopwatch.Stop();
                _memoryMonitor.Stop();

                result.ItemsProcessed = totalRowsInserted;
                result.Duration = stopwatch.Elapsed;
                result.InitialMemoryBytes = _memoryMonitor.InitialMemoryBytes;
                result.PeakMemoryBytes = _memoryMonitor.PeakMemoryBytes;
                result.FinalMemoryBytes = _memoryMonitor.CurrentMemoryBytes;
                result.Message = $"Seeded {userCount:N0} user(s) + {totalRowsInserted:N0} activity rows across {workloadCount} workload(s) over {weeks} week(s).";

                _memoryMonitor.PrintReport();

                // Optional follow-up: roll up the freshly-seeded daily rows into the
                // weekly profiling tables so the data is immediately consumable by the
                // profiling reports without waiting for the scheduled Automation job.
                var compileStatus = RunCompileWeeklyIfRequested(connectionString, weeks);
                if (!string.IsNullOrEmpty(compileStatus))
                {
                    result.Message += " " + compileStatus;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Exception = ex.GetBaseException();
                result.Message = $"Test failed: {ex.GetBaseException().Message}";
            }

            return result;
        }

        /// <summary>
        /// Prompts the user (post-seed) whether to execute <c>[profiling].[usp_CompileWeekly]</c>
        /// against the same database, then runs it with a configurable <c>@WeeksToKeep</c>.
        /// Returns null if the user declined, or a short status string suitable for inclusion
        /// in the stress-test result message.
        /// </summary>
        private string RunCompileWeeklyIfRequested(string connectionString, int defaultWeeksToKeep)
        {
            Console.WriteLine();
            Console.WriteLine("Optional: run [profiling].[usp_CompileWeekly] to refresh weekly profiling stats");
            Console.WriteLine("from the rows that were just seeded (matches what the scheduled Automation job does).");
            bool runIt = GetBooleanInput("Run usp_CompileWeekly now", false);
            if (!runIt)
            {
                Console.WriteLine("Skipped usp_CompileWeekly.");
                return null;
            }

            int weeksToKeep = GetIntegerInput("@WeeksToKeep (retention window passed to the proc)", Math.Max(defaultWeeksToKeep, 1), 1, 520);

            Console.WriteLine($"Running [profiling].[usp_CompileWeekly] @WeeksToKeep = {weeksToKeep}...");
            var sw = Stopwatch.StartNew();
            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.CommandText = "[profiling].[usp_CompileWeekly]";
                        cmd.CommandTimeout = 10500; // 3 hours, same as Weekly.ps1
                        cmd.Parameters.AddWithValue("@WeeksToKeep", weeksToKeep);

                        var returnParam = new SqlParameter("@returnValue", SqlDbType.Int)
                        {
                            Direction = ParameterDirection.ReturnValue
                        };
                        cmd.Parameters.Add(returnParam);

                        cmd.ExecuteNonQuery();
                        sw.Stop();

                        int returned = returnParam.Value is int i ? i : Convert.ToInt32(returnParam.Value);
                        if (returned == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"usp_CompileWeekly completed OK in {sw.Elapsed}.");
                            Console.ResetColor();
                            return $"usp_CompileWeekly OK ({sw.Elapsed:hh\\:mm\\:ss}).";
                        }

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"usp_CompileWeekly returned non-zero ({returned}) after {sw.Elapsed}.");
                        Console.ResetColor();
                        return $"usp_CompileWeekly returned {returned} ({sw.Elapsed:hh\\:mm\\:ss}).";
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"usp_CompileWeekly FAILED after {sw.Elapsed}: {ex.GetBaseException().Message}");
                Console.ResetColor();
                return $"usp_CompileWeekly FAILED: {ex.GetBaseException().Message}";
            }
        }

        /// <summary>
        /// Bulk-inserts activity rows for a single workload table using <see cref="SqlBulkCopy"/>.
        /// Each user gets <paramref name="daysPerWeek"/> distinct dates per week for
        /// <paramref name="weeks"/> consecutive weeks ending on <paramref name="endDate"/>.
        /// </summary>
        private static long BulkInsertWorkload(SqlConnection conn, Workload workload,
            IReadOnlyList<SeededUser> users, int weeks, int daysPerWeek, DateTime endDate,
            int bulkBatchSize, Random random, bool verbose)
        {
            string table = TableNameFor(workload);
            var schema = BuildSchema(workload);
            var dt = new DataTable();
            foreach (var col in schema) dt.Columns.Add(col.Name, col.Type);

            long total = 0;
            long batchRows = 0;

            using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = table, BatchSize = bulkBatchSize, BulkCopyTimeout = 0 })
            {
                foreach (DataColumn col in dt.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                foreach (var user in users)
                {
                    for (int w = 0; w < weeks; w++)
                    {
                        // Each week occupies a 7-day window ending on (endDate - 7*(weeks-1-w)).
                        // For w = weeks-1 the window is the most recent 7 days ending on endDate;
                        // for w = 0 it is the oldest week in the retention range.
                        DateTime weekEnd = endDate.AddDays(-7 * (weeks - 1 - w));
                        var pickedDays = PickDistinctDayOffsets(daysPerWeek, random);
                        foreach (var dayOffset in pickedDays)
                        {
                            DateTime activityDate = weekEnd.AddDays(-(6 - dayOffset));

                            var row = dt.NewRow();
                            PopulateRow(workload, row, user.Id, activityDate, random);
                            dt.Rows.Add(row);
                            batchRows++;
                            total++;

                            if (batchRows >= bulkBatchSize)
                            {
                                bulk.WriteToServer(dt);
                                dt.Clear();
                                batchRows = 0;
                                if (verbose) Console.WriteLine($"    {table}: {total:N0} rows so far...");
                            }
                        }
                    }
                }

                if (batchRows > 0)
                {
                    bulk.WriteToServer(dt);
                    dt.Clear();
                }
            }

            return total;
        }

        private static int[] PickDistinctDayOffsets(int count, Random random)
        {
            if (count >= 7) return new[] { 0, 1, 2, 3, 4, 5, 6 };
            var set = new HashSet<int>();
            while (set.Count < count) set.Add(random.Next(7));
            var arr = new int[set.Count];
            set.CopyTo(arr);
            return arr;
        }

        private static string TableNameFor(Workload workload)
        {
            switch (workload)
            {
                case Workload.Teams: return "teams_user_activity_log";
                case Workload.OneDrive: return "onedrive_user_activity_log";
                case Workload.SharePoint: return "sharepoint_user_activity_log";
                case Workload.Outlook: return "outlook_user_activity_log";
                case Workload.Yammer: return "yammer_user_activity_log";
                case Workload.TeamsDeviceUsage: return "teams_user_device_usage_log";
                case Workload.AppPlatform: return "platform_user_activity_log";
                case Workload.YammerDevice: return "yammer_device_activity_log";
                default: throw new ArgumentOutOfRangeException(nameof(workload));
            }
        }

        /// <summary>
        /// DataTable schema (excluding the IDENTITY id column) for a given workload's activity log.
        /// Mirrors the EF entity definitions in Common.Entities/Entities/UsageReports/.
        /// </summary>
        private static (string Name, Type Type)[] BuildSchema(Workload workload)
        {
            // Base columns shared by every UserRelatedAbstractUsageActivity table.
            var common = new List<(string, Type)>
            {
                ("date", typeof(DateTime)),
                ("last_activity_date", typeof(DateTime)),
                ("user_id", typeof(int)),
            };

            switch (workload)
            {
                case Workload.Teams:
                    common.AddRange(new (string, Type)[]
                    {
                        ("private_chat_count", typeof(long)),
                        ("team_chat_count", typeof(long)),
                        ("calls_count", typeof(long)),
                        ("meetings_count", typeof(long)),
                        ("adhoc_meetings_attended_count", typeof(long)),
                        ("adhoc_meetings_organized_count", typeof(long)),
                        ("meetings_attended_count", typeof(long)),
                        ("meetings_organized_count", typeof(long)),
                        ("scheduled_onetime_meetings_attended_count", typeof(long)),
                        ("scheduled_onetime_meetings_organized_count", typeof(long)),
                        ("scheduled_recurring_meetings_attended_count", typeof(long)),
                        ("scheduled_recurring_meetings_organized_count", typeof(long)),
                        ("audio_duration_seconds", typeof(int)),
                        ("video_duration_seconds", typeof(int)),
                        ("screenshare_duration_seconds", typeof(int)),
                        ("post_messages", typeof(long)),
                        ("reply_messages", typeof(long)),
                        ("urgent_messages", typeof(long)),
                    });
                    break;
                case Workload.OneDrive:
                case Workload.SharePoint:
                    common.AddRange(new (string, Type)[]
                    {
                        ("viewed_or_edited", typeof(long)),
                        ("synced", typeof(long)),
                        ("shared_internally", typeof(long)),
                        ("shared_externally", typeof(long)),
                    });
                    break;
                case Workload.Outlook:
                    common.AddRange(new (string, Type)[]
                    {
                        ("email_send_count", typeof(long)),
                        ("email_receive_count", typeof(long)),
                        ("email_read_count", typeof(long)),
                        ("meeting_created_count", typeof(long)),
                        ("meeting_interacted_count", typeof(long)),
                    });
                    break;
                case Workload.Yammer:
                    common.AddRange(new (string, Type)[]
                    {
                        ("posted_count", typeof(int)),
                        ("read_count", typeof(int)),
                        ("liked_count", typeof(int)),
                    });
                    break;
                case Workload.TeamsDeviceUsage:
                    common.AddRange(new (string, Type)[]
                    {
                        ("used_web", typeof(bool)),
                        ("used_win_phone", typeof(bool)),
                        ("used_linux", typeof(bool)),
                        ("used_chrome_os", typeof(bool)),
                        ("used_ios", typeof(bool)),
                        ("used_android", typeof(bool)),
                        ("used_mac", typeof(bool)),
                        ("used_windows", typeof(bool)),
                    });
                    break;
                case Workload.YammerDevice:
                    common.AddRange(new (string, Type)[]
                    {
                        ("used_web", typeof(bool)),
                        ("used_win_phone", typeof(bool)),
                        ("used_android", typeof(bool)),
                        ("used_ipad", typeof(bool)),
                        ("used_iphone", typeof(bool)),
                        ("used_others", typeof(bool)),
                    });
                    break;
                case Workload.AppPlatform:
                    foreach (var col in AppPlatformBoolColumns)
                    {
                        common.Add((col, typeof(bool)));
                    }
                    break;
            }
            return common.ToArray();
        }

        private static readonly string[] AppPlatformBoolColumns =
        {
            "windows","mac","mobile","web",
            "outlook","word","excel","powerpoint","onenote","teams",
            "outlook_windows","word_windows","excel_windows","powerpoint_windows","onenote_windows","teams_windows",
            "outlook_mac","word_mac","excel_mac","powerpoint_mac","onenote_mac","teams_mac",
            "outlook_mobile","word_mobile","excel_mobile","powerpoint_mobile","onenote_mobile","teams_mobile",
            "outlook_web","word_web","excel_web","powerpoint_web","onenote_web","teams_web",
        };

        private static void PopulateRow(Workload workload, DataRow row, int userId, DateTime date, Random random)
        {
            row["date"] = date;
            row["last_activity_date"] = date;
            row["user_id"] = userId;

            bool weekend = IsWeekend(date);

            switch (workload)
            {
                case Workload.Teams:
                    row["private_chat_count"] = ScaledLong(random, 50, weekend);
                    row["team_chat_count"] = ScaledLong(random, 200, weekend);
                    row["calls_count"] = ScaledLong(random, 10, weekend);
                    row["meetings_count"] = ScaledLong(random, 10, weekend);
                    row["adhoc_meetings_attended_count"] = ScaledLong(random, 5, weekend);
                    row["adhoc_meetings_organized_count"] = ScaledLong(random, 3, weekend);
                    row["meetings_attended_count"] = ScaledLong(random, 8, weekend);
                    row["meetings_organized_count"] = ScaledLong(random, 4, weekend);
                    row["scheduled_onetime_meetings_attended_count"] = ScaledLong(random, 5, weekend);
                    row["scheduled_onetime_meetings_organized_count"] = ScaledLong(random, 3, weekend);
                    row["scheduled_recurring_meetings_attended_count"] = ScaledLong(random, 5, weekend);
                    row["scheduled_recurring_meetings_organized_count"] = ScaledLong(random, 3, weekend);
                    row["audio_duration_seconds"] = ScaledInt(random, 3600 * 4, weekend);
                    row["video_duration_seconds"] = ScaledInt(random, 3600 * 2, weekend);
                    row["screenshare_duration_seconds"] = ScaledInt(random, 3600, weekend);
                    row["post_messages"] = ScaledLong(random, 30, weekend);
                    row["reply_messages"] = ScaledLong(random, 60, weekend);
                    row["urgent_messages"] = ScaledLong(random, 3, weekend);
                    break;
                case Workload.OneDrive:
                case Workload.SharePoint:
                    row["viewed_or_edited"] = ScaledLong(random, 100, weekend);
                    row["synced"] = ScaledLong(random, 50, weekend);
                    row["shared_internally"] = ScaledLong(random, 20, weekend);
                    row["shared_externally"] = ScaledLong(random, 5, weekend);
                    break;
                case Workload.Outlook:
                    row["email_send_count"] = ScaledLong(random, 80, weekend);
                    row["email_receive_count"] = ScaledLong(random, 200, weekend);
                    row["email_read_count"] = ScaledLong(random, 150, weekend);
                    row["meeting_created_count"] = ScaledLong(random, 10, weekend);
                    row["meeting_interacted_count"] = ScaledLong(random, 15, weekend);
                    break;
                case Workload.Yammer:
                    row["posted_count"] = (int)ScaledLong(random, 10, weekend);
                    row["read_count"] = (int)ScaledLong(random, 50, weekend);
                    row["liked_count"] = (int)ScaledLong(random, 15, weekend);
                    break;
                case Workload.TeamsDeviceUsage:
                    row["used_web"] = ScaledBool(random, 0.3, weekend);
                    row["used_win_phone"] = false;
                    row["used_linux"] = ScaledBool(random, 0.05, weekend);
                    row["used_chrome_os"] = ScaledBool(random, 0.05, weekend);
                    row["used_ios"] = ScaledBool(random, 0.4, weekend);
                    row["used_android"] = ScaledBool(random, 0.3, weekend);
                    row["used_mac"] = ScaledBool(random, 0.2, weekend);
                    row["used_windows"] = ScaledBool(random, 0.8, weekend);
                    break;
                case Workload.YammerDevice:
                    row["used_web"] = ScaledBool(random, 0.4, weekend);
                    row["used_win_phone"] = false;
                    row["used_android"] = ScaledBool(random, 0.25, weekend);
                    row["used_ipad"] = ScaledBool(random, 0.1, weekend);
                    row["used_iphone"] = ScaledBool(random, 0.3, weekend);
                    row["used_others"] = ScaledBool(random, 0.05, weekend);
                    break;
                case Workload.AppPlatform:
                    foreach (var col in AppPlatformBoolColumns)
                    {
                        row[col] = ScaledBool(random, 0.4, weekend);
                    }
                    break;
            }
        }

        private static bool IsWeekend(DateTime date)
            => date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

        /// <summary>
        /// Returns a random non-negative count in [0, weekdayMax] for weekday rows, and a
        /// scaled-down range for weekend rows so reports show a clear high-on-weekday /
        /// low-on-weekend shape.
        /// </summary>
        private static long ScaledLong(Random random, int weekdayMax, bool weekend)
        {
            int max = weekend ? Math.Max(1, (int)Math.Round(weekdayMax * WeekendActivityScale)) : weekdayMax;
            return random.Next(0, max + 1);
        }

        private static int ScaledInt(Random random, int weekdayMax, bool weekend)
        {
            int max = weekend ? Math.Max(1, (int)Math.Round(weekdayMax * WeekendActivityScale)) : weekdayMax;
            return random.Next(0, max + 1);
        }

        /// <summary>
        /// Returns true with <paramref name="weekdayProbability"/> on weekdays, or that
        /// probability scaled by <see cref="WeekendBoolProbabilityScale"/> on weekends.
        /// </summary>
        private static bool ScaledBool(Random random, double weekdayProbability, bool weekend)
        {
            double p = weekend ? weekdayProbability * WeekendBoolProbabilityScale : weekdayProbability;
            return random.NextDouble() < p;
        }

        /// <summary>
        /// Generates copilot chat events (audit_events + copilot_chats) for every user
        /// across the requested window. Number of chats per user-day is high on weekdays
        /// (uniform 0..weekdayMax) and low on weekends (scaled by <see cref="WeekendActivityScale"/>).
        ///
        /// Returns the number of copilot_chats rows inserted. An identical number of
        /// audit_events rows is inserted (one per chat) since copilot_chats.event_id has
        /// a FK to audit_events.id.
        /// </summary>
        private static long BulkInsertCopilotEvents(SqlConnection conn, IReadOnlyList<SeededUser> users,
            int weeks, DateTime endDate, int weekdayMax, int bulkBatchSize, Random random, bool verbose)
        {
            // Two DataTables share rows by event_id: we add to both, flush both together
            // so the FK constraint is satisfied at every flush boundary.
            var audit = new DataTable();
            audit.Columns.Add("id", typeof(Guid));
            audit.Columns.Add("time_stamp", typeof(DateTime));
            audit.Columns.Add("user_id", typeof(int));

            var chats = new DataTable();
            chats.Columns.Add("event_id", typeof(Guid));
            chats.Columns.Add("app_host", typeof(string));
            // Denormalised copies of the audit event's user and timestamp. The real importer merge writes
            // these (common_upsert_copilot_agents.sql); generated data must too, or everything this harness
            // produces is invisible to the Copilot Adoption page, which filters on copilot_chats.time_stamp
            // rather than joining dbo.audit_events.
            chats.Columns.Add("user_id", typeof(int));
            chats.Columns.Add("time_stamp", typeof(DateTime));

            long total = 0;
            long pending = 0;

            using (var auditBulk = new SqlBulkCopy(conn) { DestinationTableName = "audit_events", BatchSize = bulkBatchSize, BulkCopyTimeout = 0 })
            using (var chatBulk = new SqlBulkCopy(conn) { DestinationTableName = "copilot_chats", BatchSize = bulkBatchSize, BulkCopyTimeout = 0 })
            {
                foreach (DataColumn col in audit.Columns) auditBulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                foreach (DataColumn col in chats.Columns) chatBulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                DateTime windowStart = endDate.AddDays(-(7 * weeks - 1));
                int totalDays = 7 * weeks;

                foreach (var user in users)
                {
                    for (int d = 0; d < totalDays; d++)
                    {
                        DateTime day = windowStart.AddDays(d);
                        bool weekend = IsWeekend(day);
                        int max = weekend
                            ? Math.Max(1, (int)Math.Round(weekdayMax * WeekendActivityScale))
                            : weekdayMax;
                        int chatCount = random.Next(0, max + 1);

                        for (int c = 0; c < chatCount; c++)
                        {
                            // Spread chats across the working day so the same user can have
                            // many distinct timestamps within one calendar day.
                            int hour = weekend ? random.Next(10, 20) : random.Next(8, 19);
                            int minute = random.Next(0, 60);
                            DateTime ts = day.AddHours(hour).AddMinutes(minute);

                            var eventId = Guid.NewGuid();
                            audit.Rows.Add(eventId, ts, user.Id);
                            chats.Rows.Add(eventId, CopilotAppHosts[random.Next(CopilotAppHosts.Length)], user.Id, ts);
                            pending++;
                            total++;

                            if (pending >= bulkBatchSize)
                            {
                                auditBulk.WriteToServer(audit);
                                chatBulk.WriteToServer(chats);
                                audit.Clear();
                                chats.Clear();
                                pending = 0;
                                if (verbose) Console.WriteLine($"    copilot_chats: {total:N0} rows so far...");
                            }
                        }
                    }
                }

                if (pending > 0)
                {
                    auditBulk.WriteToServer(audit);
                    chatBulk.WriteToServer(chats);
                    audit.Clear();
                    chats.Clear();
                }
            }

            return total;
        }

        /// <summary>
        /// On re-runs where every UPN already existed we reload the matching users from
        /// the database so we can still assign licenses + activity to them.
        /// </summary>
        private static List<SeededUser> LoadExistingStressUsers(SqlConnection conn, string upnPrefix, int max)
        {
            var users = new List<SeededUser>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP (@max) id, user_name FROM users WHERE user_name LIKE @pattern ORDER BY id;";
                cmd.Parameters.Add("@max", SqlDbType.Int).Value = max;
                cmd.Parameters.Add("@pattern", SqlDbType.NVarChar, 400).Value = upnPrefix + "%";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        users.Add(new SeededUser(reader.GetInt32(0), reader.GetString(1)));
                    }
                }
            }
            return users;
        }
    }
}
