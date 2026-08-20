using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for profiling stored procedures that aggregate weekly activity data.
    /// Tests the usp_CompileActivityWeek stored procedure and its dependencies.
    /// </summary>
    [TestClass]
    public class ProfilingStoredProcedureTests
    {
        private const int TEST_USER_ID = 99999;
        private static readonly DateTime TEST_MONDAY = new DateTime(2024, 1, 1); // January 1, 2024 is a Monday

        /// <summary>
        /// Runs once before all tests in this class. Ensures database schema and SQL scripts are deployed.
        /// </summary>
        [ClassInitialize]
        public static void InitializeDatabase(TestContext context)
        {
            var config = new AppConfig();
            var connectionString = config.ConnectionStrings.DatabaseConnectionString;

            var initInfo = new DatabaseUpgradeInfo { ConnectionString = connectionString };

            // Run database upgrade to ensure all schemas, stored procedures, and tables exist
            DatabaseUpgrader.CheckDbUpgraded(initInfo, (s) =>
            {
                Console.WriteLine($"[DatabaseUpgrader] {s}");
                context.WriteLine($"[DatabaseUpgrader] {s}");
            });

            Console.WriteLine("Database initialization complete. All SQL scripts have been executed.");

            // Clean up any test users from previous test runs
            CleanupTestUsers();
        }

        /// <summary>
        /// Runs once after all tests in this class. Cleans up test users.
        /// </summary>
        [ClassCleanup]
        public static void CleanupDatabase()
        {
            CleanupTestUsers();
        }

        /// <summary>
        /// Helper method to clean up test users
        /// </summary>
        private static void CleanupTestUsers()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Remove all test users created during tests
                var testUserIds = new[] { TEST_USER_ID, TEST_USER_ID + 1, TEST_USER_ID + 2, TEST_USER_ID + 3 };
                var userIdList = string.Join(",", testUserIds);

                var conn = db.Database.Connection;
                try
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        // First, clean up any data that references these users to avoid FK constraint violations
                        cmd.CommandText = $@"
                            -- Clean up activity logs
                            DELETE FROM dbo.teams_user_activity_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.onedrive_user_activity_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.sharepoint_user_activity_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.outlook_user_activity_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.yammer_user_activity_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.teams_user_device_usage_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.platform_user_activity_log WHERE user_id IN ({userIdList});
                            DELETE FROM dbo.yammer_device_activity_log WHERE user_id IN ({userIdList});
                            
                            -- Clean up copilot data
                            DELETE FROM dbo.copilot_chats 
                            WHERE event_id IN (
                                SELECT id FROM dbo.audit_events WHERE user_id IN ({userIdList})
                            );
                            DELETE FROM dbo.audit_events WHERE user_id IN ({userIdList});
                            
                            -- Clean up profiling tables
                            DELETE FROM profiling.ActivitiesWeekly WHERE user_id IN ({userIdList});
                            DELETE FROM profiling.ActivitiesWeeklyColumns WHERE user_id IN ({userIdList});
                            DELETE FROM profiling.UsageWeekly WHERE user_id IN ({userIdList});
                            
                            -- Finally, remove the test users
                            DELETE FROM dbo.users WHERE id IN ({userIdList});";
                        cmd.CommandTimeout = 300;
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Error cleaning up test users: {ex.Message}");
                }
                finally
                {
                    if (conn.State == System.Data.ConnectionState.Open)
                    {
                        conn.Close();
                    }
                }
            }
            Console.WriteLine("Test users cleaned up.");
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithNoData_RunsWithoutError()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY;

                // Clean up any existing test data
                await CleanupTestData(db, monday);

                // Run the stored procedure with no source data
                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                // Verify no errors and no data was inserted
                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                var columnCount = await GetActivitiesWeeklyColumnsCount(db, monday);

                Assert.AreEqual(0, rowCount, "Should have no rows when no source data exists");
                Assert.AreEqual(0, columnCount, "Should have no columns when no source data exists");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithTeamsData_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY;
                var sunday = monday.AddDays(6);

                // Ensure test user exists
                await EnsureTestUserExists(db, TEST_USER_ID);

                // Clean up any existing test data
                await CleanupTestData(db, monday);

                // Insert test Teams activity data for the week
                await InsertTeamsActivityTestData(db, TEST_USER_ID, monday, sunday);

                // Run the stored procedure
                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                // Verify data was aggregated
                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                var columnCount = await GetActivitiesWeeklyColumnsCount(db, monday);

                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for Teams data");
                Assert.IsTrue(columnCount > 0, "Should have aggregated columns for Teams data");

                // Verify specific Teams metrics exist
                var teamsMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(teamsMetrics.Contains("Teams Private Chats"), "Should contain Teams Private Chats metric");
                Assert.IsTrue(teamsMetrics.Contains("Teams Team Chats"), "Should contain Teams Team Chats metric");
                Assert.IsTrue(teamsMetrics.Contains("Teams Calls"), "Should contain Teams Calls metric");
                Assert.IsTrue(teamsMetrics.Contains("Teams Meetings"), "Should preserve the Teams Meetings metric for existing reports");
                Assert.IsTrue(teamsMetrics.Contains("Teams Meetings Organized"), "Should contain Teams Meetings Organized metric");
                Assert.IsTrue(teamsMetrics.Contains("Teams Meetings Attended"), "Should contain Teams Meetings Attended metric");

                var meetingsTotal = await GetActivityMetricSum(db, monday, TEST_USER_ID, "Teams Meetings");
                var meetingsColumn = await GetActivityColumnValue(db, monday, TEST_USER_ID, "Teams Meetings");
                Assert.AreEqual(7, meetingsTotal, "Teams Meetings should aggregate the legacy meetings_count values");
                Assert.AreEqual(meetingsTotal, meetingsColumn, "Long and wide report forms should agree for Teams Meetings");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithOneDriveData_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(7); // Use different week to avoid conflicts
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertOneDriveActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                var columnCount = await GetActivitiesWeeklyColumnsCount(db, monday);

                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for OneDrive data");
                Assert.IsTrue(columnCount > 0, "Should have aggregated columns for OneDrive data");

                var oneDriveMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(oneDriveMetrics.Contains("OneDrive Viewed/Edited"), "Should contain OneDrive Viewed/Edited metric");
                Assert.IsTrue(oneDriveMetrics.Contains("OneDrive Synced"), "Should contain OneDrive Synced metric");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithSharePointData_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(14); // Use different week
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertSharePointActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for SharePoint data");

                var spoMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(spoMetrics.Contains("SPO Viewed/Edited"), "Should contain SPO Viewed/Edited metric");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithOutlookData_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(21); // Use different week
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertOutlookActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for Outlook data");

                var outlookMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(outlookMetrics.Contains("Emails Sent"), "Should contain Emails Sent metric");
                Assert.IsTrue(outlookMetrics.Contains("Emails Received"), "Should contain Emails Received metric");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithYammerData_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(28); // Use different week
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertYammerActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for Yammer data");

                var yammerMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(yammerMetrics.Contains("Yammer Posted"), "Should contain Yammer Posted metric");
                Assert.IsTrue(yammerMetrics.Contains("Yammer Read"), "Should contain Yammer Read metric");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithCopilotData_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(35); // Use different week
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertCopilotActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for Copilot data");

                var copilotMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(copilotMetrics.Contains("Copilot Chats"), "Should contain Copilot Chats metric");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithAllDataTypes_AggregatesCorrectly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(42); // Use different week
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);

                // Insert all types of test data
                await InsertTeamsActivityTestData(db, TEST_USER_ID, monday, sunday);
                await InsertOneDriveActivityTestData(db, TEST_USER_ID, monday, sunday);
                await InsertSharePointActivityTestData(db, TEST_USER_ID, monday, sunday);
                await InsertOutlookActivityTestData(db, TEST_USER_ID, monday, sunday);
                await InsertYammerActivityTestData(db, TEST_USER_ID, monday, sunday);
                await InsertCopilotActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                var rowCount = await GetActivitiesWeeklyRowCount(db, monday);
                var columnCount = await GetActivitiesWeeklyColumnsCount(db, monday);

                Assert.IsTrue(rowCount > 0, "Should have aggregated rows for all data types");
                Assert.IsTrue(columnCount > 0, "Should have aggregated columns for all data types");

                // Verify we have metrics from all sources
                var allMetrics = await GetActivitiesWeeklyMetrics(db, monday, TEST_USER_ID);
                Assert.IsTrue(allMetrics.Count >= 6, "Should have metrics from all 6 data sources");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_RunTwice_DoesNotDuplicate()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(49); // Use different week
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertTeamsActivityTestData(db, TEST_USER_ID, monday, sunday);

                // Run the first time
                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);
                var firstRowCount = await GetActivitiesWeeklyRowCount(db, monday);
                var firstColumnCount = await GetActivitiesWeeklyColumnsCount(db, monday);

                // Run the second time (should skip because already aggregated)
                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);
                var secondRowCount = await GetActivitiesWeeklyRowCount(db, monday);
                var secondColumnCount = await GetActivitiesWeeklyColumnsCount(db, monday);

                Assert.AreEqual(firstRowCount, secondRowCount, "Row count should not change on second run");
                Assert.AreEqual(firstColumnCount, secondColumnCount, "Column count should not change on second run");
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_WithMultipleUsers_AggregatesForEach()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(56); // Use different week
                var sunday = monday.AddDays(6);

                // Ensure all test users exist
                var userId1 = TEST_USER_ID + 1;
                var userId2 = TEST_USER_ID + 2;
                var userId3 = TEST_USER_ID + 3;

                await EnsureTestUserExists(db, userId1);
                await EnsureTestUserExists(db, userId2);
                await EnsureTestUserExists(db, userId3);

                await CleanupTestData(db, monday);

                // Insert data for multiple users
                await InsertTeamsActivityTestData(db, userId1, monday, sunday);
                await InsertTeamsActivityTestData(db, userId2, monday, sunday);
                await InsertTeamsActivityTestData(db, userId3, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                // Verify data for all users
                var columnCount = await GetActivitiesWeeklyColumnsCount(db, monday);
                Assert.AreEqual(3, columnCount, "Should have aggregated data for 3 users");
            }
        }

        // =====================================================================
        // Value-correctness tests. The tests above only assert that rows exist
        // and that a metric NAME is present - but usp_CompileWeekActivityRows
        // UNPIVOTs every column (including zeros), so every metric name is
        // always present regardless of the data. These assert the actual
        // aggregated numbers so a wrong SUM / date filter / column mapping fails.
        // =====================================================================

        [TestMethod]
        public async Task UspCompileActivityWeek_TeamsData_ComputesExactWeeklySums()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(63);
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                // Seeds 5,3,2,1,1,0,...,600,300,120,4,6,1 per day, for 7 days.
                await InsertTeamsActivityTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                // Long form (ActivitiesWeekly): value x 7 days.
                Assert.AreEqual(35, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Teams Private Chats"));
                Assert.AreEqual(21, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Teams Team Chats"));
                Assert.AreEqual(14, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Teams Calls"));
                Assert.AreEqual(4200, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Teams Audio Duration Seconds"));

                // Wide form (ActivitiesWeeklyColumns) must equal the long form for the same metric.
                Assert.AreEqual(35, await GetActivityColumnValue(db, monday, TEST_USER_ID, "Teams Private Chats"));
                Assert.AreEqual(42, await GetActivityColumnValue(db, monday, TEST_USER_ID, "Teams Reply Messages"));
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_OtherWorkloads_ComputeExactWeeklySums()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(70);
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertOneDriveActivityTestData(db, TEST_USER_ID, monday, sunday);   // 10,5,2,1
                await InsertSharePointActivityTestData(db, TEST_USER_ID, monday, sunday);  // 15,8,3,2
                await InsertOutlookActivityTestData(db, TEST_USER_ID, monday, sunday);     // 20,50,40,2,3
                await InsertYammerActivityTestData(db, TEST_USER_ID, monday, sunday);      // 3,10,5

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                Assert.AreEqual(70, await GetActivityMetricSum(db, monday, TEST_USER_ID, "OneDrive Viewed/Edited"));
                Assert.AreEqual(35, await GetActivityMetricSum(db, monday, TEST_USER_ID, "OneDrive Synced"));
                Assert.AreEqual(105, await GetActivityMetricSum(db, monday, TEST_USER_ID, "SPO Viewed/Edited"));
                Assert.AreEqual(140, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Emails Sent"));
                Assert.AreEqual(350, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Emails Received"));
                Assert.AreEqual(280, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Emails Read"));
                Assert.AreEqual(21, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Yammer Posted"));
                Assert.AreEqual(70, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Yammer Read"));
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_ExcludesActivityOutsideTheWeek()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(77);
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await CleanupTestData(db, monday.AddDays(7));

                await InsertTeamsActivityTestData(db, TEST_USER_ID, monday, sunday);            // 7 in-week days
                // One extra day in the FOLLOWING week - must NOT be counted in this week's compile.
                await InsertTeamsActivityTestData(db, TEST_USER_ID, monday.AddDays(7), monday.AddDays(7));

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                // 5 private chats x 7 in-week days = 35 (NOT 40).
                Assert.AreEqual(35, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Teams Private Chats"),
                    "Activity dated outside the Mon-Sun window must be excluded from the week");

                await CleanupTestData(db, monday.AddDays(7));
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_CopilotAppHosts_AreMappedAndFolded()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(84);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);

                // 2 Word, 1 Excel, and the two fold-pairs:
                //   bizchat + M365App -> "Copilot App M365App";  appchat + Office -> "Copilot App Office".
                await InsertCopilotChat(db, TEST_USER_ID, monday, "Word");
                await InsertCopilotChat(db, TEST_USER_ID, monday.AddDays(1), "Word");
                await InsertCopilotChat(db, TEST_USER_ID, monday.AddDays(1), "Excel");
                await InsertCopilotChat(db, TEST_USER_ID, monday.AddDays(2), "bizchat");
                await InsertCopilotChat(db, TEST_USER_ID, monday.AddDays(2), "M365App");
                await InsertCopilotChat(db, TEST_USER_ID, monday.AddDays(3), "appchat");
                await InsertCopilotChat(db, TEST_USER_ID, monday.AddDays(3), "Office");

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                Assert.AreEqual(7, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot Chats"), "Total Copilot chats");
                Assert.AreEqual(2, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot App Word"));
                Assert.AreEqual(1, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot App Excel"));
                Assert.AreEqual(2, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot App M365App"), "bizchat + M365App fold together");
                Assert.AreEqual(2, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot App Office"), "appchat + Office fold together");
                // No files/meetings were seeded.
                Assert.AreEqual(0, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot Files"));
                Assert.AreEqual(0, await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot Meetings"));
            }
        }

        [TestMethod]
        public async Task UspCompileActivityWeek_CopilotSundayActivity_IsIncluded()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(98);
                var sundayLate = monday.AddDays(6).AddHours(23).AddMinutes(59);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupTestData(db, monday);
                await InsertCopilotChat(db, TEST_USER_ID, sundayLate, "Teams");

                await ExecuteStoredProcedure(db, "profiling.usp_CompileActivityWeek", monday);

                Assert.AreEqual(
                    1,
                    await GetActivityMetricSum(db, monday, TEST_USER_ID, "Copilot Chats"),
                    "Copilot activity from the full Sunday must be included in the Mon-Sun week");
            }
        }

        // ---- usp_CompileUsageWeek / UsageWeekly - previously untested entirely ----

        [TestMethod]
        public async Task UspCompileUsageWeek_TeamsDeviceUsage_SetsWeeklyFlags()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var monday = TEST_MONDAY.AddDays(91);
                var sunday = monday.AddDays(6);

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupUsageTestData(db, monday);
                // Each day: used_web=1, used_windows=1, used_ios=1 (mac/others 0).
                await InsertTeamsDeviceUsageTestData(db, TEST_USER_ID, monday, sunday);

                await ExecuteStoredProcedure(db, "profiling.usp_CompileUsageWeek", monday);

                Assert.AreEqual(1, await GetUsageFlag(db, monday, TEST_USER_ID, "Teams Used Web"));
                Assert.AreEqual(1, await GetUsageFlag(db, monday, TEST_USER_ID, "Teams Used Windows"));
                Assert.AreEqual(1, await GetUsageFlag(db, monday, TEST_USER_ID, "Teams Used iOS"));
                // "Teams Used Mobile" = win_phone OR ios OR android -> 1 because ios was used.
                Assert.AreEqual(1, await GetUsageFlag(db, monday, TEST_USER_ID, "Teams Used Mobile"));
                // Not used this week.
                Assert.AreEqual(0, await GetUsageFlag(db, monday, TEST_USER_ID, "Teams Used Mac"));
                Assert.AreEqual(0, await GetUsageFlag(db, monday, TEST_USER_ID, "Teams Used Android"));

                await CleanupUsageTestData(db, monday);
            }
        }

        // ---- usp_CompileWeekly orchestrator - retention / cleanup, previously untested ----

        [TestMethod]
        public async Task UspCompileWeekly_DeletesWeeksOlderThanRetention()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // usp_CompileWeekly bases everything on the Monday of (today - 4 days).
                var thisWeeksMonday = MondayOf(DateTime.Today.AddDays(-4));
                var recentMonday = thisWeeksMonday.AddDays(-7);        // last completed week - inside retention
                var oldMonday = thisWeeksMonday.AddDays(-7 * 20);      // 20 weeks back - outside a 4-week window
                const int weeksToKeep = 4;

                await EnsureTestUserExists(db, TEST_USER_ID);
                await CleanupWeeklyRows(db, TEST_USER_ID);

                // Seed a recent and an old week directly into all three weekly tables. Seeding the
                // recent week into all three makes usp_CompileWeekly's resume point = recentMonday,
                // so its compile loop does no work and we test the retention DELETE in isolation.
                await SeedWeeklyRows(db, TEST_USER_ID, recentMonday);
                await SeedWeeklyRows(db, TEST_USER_ID, oldMonday);

                await ExecuteSql(db, $"EXEC profiling.usp_CompileWeekly @WeeksToKeep = {weeksToKeep}");

                Assert.AreEqual(0, await CountWeeklyRows(db, TEST_USER_ID, oldMonday),
                    "Weeks older than the retention window must be deleted");
                Assert.IsTrue(await CountWeeklyRows(db, TEST_USER_ID, recentMonday) > 0,
                    "Weeks inside the retention window must be kept");

                await CleanupWeeklyRows(db, TEST_USER_ID);
            }
        }

        #region Helper Methods

        private async Task EnsureTestUserExists(AnalyticsEntitiesContext db, int userId)
        {
            // First, delete any existing user with this email to avoid unique constraint violations
            // This can happen if a previous test run failed and didn't clean up properly
            var testEmail = $"testuser{userId}@unittest.local";

            // Delete related records first to avoid FK constraint violations
            await ExecuteSql(db, $@"
                -- Clean up activity logs that reference this user
                DELETE FROM dbo.teams_user_activity_log WHERE user_id = {userId};
                DELETE FROM dbo.onedrive_user_activity_log WHERE user_id = {userId};
                DELETE FROM dbo.sharepoint_user_activity_log WHERE user_id = {userId};
                DELETE FROM dbo.outlook_user_activity_log WHERE user_id = {userId};
                DELETE FROM dbo.yammer_user_activity_log WHERE user_id = {userId};
                
                -- Clean up Copilot data
                DELETE FROM dbo.copilot_chats 
                WHERE event_id IN (SELECT id FROM dbo.audit_events WHERE user_id = {userId});
                
                DELETE FROM dbo.audit_events WHERE user_id = {userId};
                
                -- Clean up profiling tables
                DELETE FROM profiling.ActivitiesWeekly WHERE user_id = {userId};
                DELETE FROM profiling.ActivitiesWeeklyColumns WHERE user_id = {userId};
            ");

            // Now delete the user
            await ExecuteSql(db, $@"
                DELETE FROM dbo.users WHERE mail = '{testEmail}';");

            // Now insert test user with explicit ID using IDENTITY_INSERT
            await ExecuteSql(db, $@"
                SET IDENTITY_INSERT dbo.users ON;
                
                INSERT INTO dbo.users (id, user_name, mail, azure_ad_id, account_enabled)
                VALUES ({userId}, '{testEmail}', '{testEmail}', 
                        '{Guid.NewGuid()}', 1);
                
                SET IDENTITY_INSERT dbo.users OFF;");
        }

        private async Task CleanupTestData(AnalyticsEntitiesContext db, DateTime monday)
        {
            var sunday = monday.AddDays(6);

            // Get all test user IDs that might be used (base + up to 3 additional users for multiple user tests)
            var testUserIds = new[] { TEST_USER_ID, TEST_USER_ID + 1, TEST_USER_ID + 2, TEST_USER_ID + 3 };
            var userIdList = string.Join(",", testUserIds);

            // Clean up source tables for all potential test users
            await ExecuteSql(db, $@"
                DELETE FROM dbo.teams_user_activity_log 
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");

            await ExecuteSql(db, $@"
                DELETE FROM dbo.onedrive_user_activity_log 
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");

            await ExecuteSql(db, $@"
                DELETE FROM dbo.sharepoint_user_activity_log 
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");

            await ExecuteSql(db, $@"
                DELETE FROM dbo.outlook_user_activity_log 
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");

            await ExecuteSql(db, $@"
                DELETE FROM dbo.yammer_user_activity_log 
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");

            // Clean up Copilot test data
            await ExecuteSql(db, $@"
                DELETE FROM dbo.copilot_chats 
                WHERE event_id IN (
                    SELECT id FROM dbo.audit_events 
                    WHERE user_id IN ({userIdList}) AND time_stamp >= '{monday:yyyy-MM-dd}' AND time_stamp <= '{sunday:yyyy-MM-dd}'
                )");

            await ExecuteSql(db, $@"
                DELETE FROM dbo.audit_events 
                WHERE user_id IN ({userIdList}) AND time_stamp >= '{monday:yyyy-MM-dd}' AND time_stamp <= '{sunday:yyyy-MM-dd}'");

            // Clean up target tables
            await ExecuteSql(db, $@"
                DELETE FROM profiling.ActivitiesWeekly 
                WHERE MetricDate = '{monday:yyyy-MM-dd}' AND user_id IN ({userIdList})");

            await ExecuteSql(db, $@"
                DELETE FROM profiling.ActivitiesWeeklyColumns 
                WHERE date = '{monday:yyyy-MM-dd}' AND user_id IN ({userIdList})");
        }

        private async Task InsertTeamsActivityTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            // Insert test data for each day of the week
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.teams_user_activity_log 
                    (user_id, date, private_chat_count, team_chat_count, calls_count, meetings_count, 
                     meetings_attended_count, meetings_organized_count, 
                     adhoc_meetings_attended_count, adhoc_meetings_organized_count,
                     scheduled_onetime_meetings_attended_count, scheduled_onetime_meetings_organized_count,
                     scheduled_recurring_meetings_attended_count, scheduled_recurring_meetings_organized_count,
                     audio_duration_seconds, video_duration_seconds, screenshare_duration_seconds,
                     post_messages, reply_messages, urgent_messages)
                    VALUES 
                    ({userId}, '{date:yyyy-MM-dd}', 5, 3, 2, 1, 1, 0, 0, 0, 1, 0, 0, 0, 600, 300, 120, 4, 6, 1)");
            }
        }

        private async Task InsertOneDriveActivityTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.onedrive_user_activity_log 
                    (user_id, date, viewed_or_edited, synced, shared_internally, shared_externally)
                    VALUES 
                    ({userId}, '{date:yyyy-MM-dd}', 10, 5, 2, 1)");
            }
        }

        private async Task InsertSharePointActivityTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.sharepoint_user_activity_log 
                    (user_id, date, viewed_or_edited, synced, shared_internally, shared_externally)
                    VALUES 
                    ({userId}, '{date:yyyy-MM-dd}', 15, 8, 3, 2)");
            }
        }

        private async Task InsertOutlookActivityTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.outlook_user_activity_log 
                    (user_id, date, email_send_count, email_receive_count, email_read_count, 
                     meeting_created_count, meeting_interacted_count)
                    VALUES 
                    ({userId}, '{date:yyyy-MM-dd}', 20, 50, 40, 2, 3)");
            }
        }

        private async Task InsertYammerActivityTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.yammer_user_activity_log 
                    (user_id, date, posted_count, read_count, liked_count)
                    VALUES 
                    ({userId}, '{date:yyyy-MM-dd}', 3, 10, 5)");
            }
        }

        private async Task InsertCopilotActivityTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                // First insert audit event
                var eventId = await ExecuteScalar<Guid>(db, $@"
                    INSERT INTO dbo.audit_events 
                    (id, user_id, time_stamp, operation_id)
                    OUTPUT INSERTED.id
                    VALUES 
                    (NEWID(), {userId}, '{date:yyyy-MM-dd HH:mm:ss}', 1)");

                // Then insert copilot chat
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.copilot_chats 
                    (event_id, app_host)
                    VALUES 
                    ('{eventId}', 'Teams')");
            }
        }

        private async Task ExecuteStoredProcedure(AnalyticsEntitiesContext db, string procedureName, DateTime monday)
        {
            await ExecuteSql(db, $"EXEC {procedureName} @Monday = '{monday:yyyy-MM-dd}'");
        }

        private async Task<int> GetActivitiesWeeklyRowCount(AnalyticsEntitiesContext db, DateTime monday)
        {
            return await ExecuteScalar<int>(db, $@"
                SELECT COUNT(*) 
                FROM profiling.ActivitiesWeekly 
                WHERE MetricDate = '{monday:yyyy-MM-dd}' AND user_id = {TEST_USER_ID}");
        }

        private async Task<int> GetActivitiesWeeklyColumnsCount(AnalyticsEntitiesContext db, DateTime monday)
        {
            return await ExecuteScalar<int>(db, $@"
                SELECT COUNT(*) 
                FROM profiling.ActivitiesWeeklyColumns 
                WHERE date = '{monday:yyyy-MM-dd}'");
        }

        private async Task<System.Collections.Generic.List<string>> GetActivitiesWeeklyMetrics(AnalyticsEntitiesContext db, DateTime monday, int userId)
        {
            var metrics = new System.Collections.Generic.List<string>();
            var conn = db.Database.Connection;
            var wasOpen = conn.State == System.Data.ConnectionState.Open;

            try
            {
                if (!wasOpen)
                {
                    await conn.OpenAsync();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
                        SELECT DISTINCT Metric 
                        FROM profiling.ActivitiesWeekly 
                        WHERE MetricDate = '{monday:yyyy-MM-dd}' AND user_id = {userId}";

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            metrics.Add(reader.GetString(0));
                        }
                    }
                }
            }
            finally
            {
                if (!wasOpen && conn.State == System.Data.ConnectionState.Open)
                {
                    conn.Close();
                }
            }

            return metrics;
        }

        private async Task ExecuteSql(AnalyticsEntitiesContext db, string sql)
        {
            var conn = db.Database.Connection;
            var wasOpen = conn.State == System.Data.ConnectionState.Open;

            try
            {
                if (!wasOpen)
                {
                    await conn.OpenAsync();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 300; // 5 minutes timeout for complex operations
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                if (!wasOpen && conn.State == System.Data.ConnectionState.Open)
                {
                    conn.Close();
                }
            }
        }

        private async Task<T> ExecuteScalar<T>(AnalyticsEntitiesContext db, string sql)
        {
            var conn = db.Database.Connection;
            var wasOpen = conn.State == System.Data.ConnectionState.Open;

            try
            {
                if (!wasOpen)
                {
                    await conn.OpenAsync();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 300;
                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null || result == DBNull.Value)
                    {
                        return default(T);
                    }
                    return (T)Convert.ChangeType(result, typeof(T));
                }
            }
            finally
            {
                if (!wasOpen && conn.State == System.Data.ConnectionState.Open)
                {
                    conn.Close();
                }
            }
        }

        private async Task<long> GetActivityMetricSum(AnalyticsEntitiesContext db, DateTime monday, int userId, string metric)
        {
            return await ExecuteScalar<long>(db, $@"
                SELECT ISNULL(SUM([Sum]), 0)
                FROM profiling.ActivitiesWeekly
                WHERE MetricDate = '{monday:yyyy-MM-dd}' AND user_id = {userId} AND Metric = '{metric}'");
        }

        private async Task<long> GetActivityColumnValue(AnalyticsEntitiesContext db, DateTime monday, int userId, string column)
        {
            return await ExecuteScalar<long>(db, $@"
                SELECT ISNULL([{column}], 0)
                FROM profiling.ActivitiesWeeklyColumns
                WHERE date = '{monday:yyyy-MM-dd}' AND user_id = {userId}");
        }

        private async Task<int> GetUsageFlag(AnalyticsEntitiesContext db, DateTime monday, int userId, string column)
        {
            return await ExecuteScalar<int>(db, $@"
                SELECT ISNULL(CAST([{column}] AS INT), 0)
                FROM profiling.UsageWeekly
                WHERE date = '{monday:yyyy-MM-dd}' AND user_id = {userId}");
        }

        private async Task InsertCopilotChat(AnalyticsEntitiesContext db, int userId, DateTime date, string appHost)
        {
            var eventId = await ExecuteScalar<Guid>(db, $@"
                INSERT INTO dbo.audit_events (id, user_id, time_stamp, operation_id)
                OUTPUT INSERTED.id
                VALUES (NEWID(), {userId}, '{date:yyyy-MM-dd HH:mm:ss}', 1)");

            await ExecuteSql(db, $@"
                INSERT INTO dbo.copilot_chats (event_id, app_host)
                VALUES ('{eventId}', '{appHost}')");
        }

        private async Task InsertTeamsDeviceUsageTestData(AnalyticsEntitiesContext db, int userId, DateTime startDate, DateTime endDate)
        {
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                await ExecuteSql(db, $@"
                    INSERT INTO dbo.teams_user_device_usage_log
                    (user_id, date, used_web, used_mac, used_windows, used_linux, used_chrome_os,
                     used_win_phone, used_ios, used_android)
                    VALUES
                    ({userId}, '{date:yyyy-MM-dd}', 1, 0, 1, 0, 0, 0, 1, 0)");
            }
        }

        private async Task CleanupUsageTestData(AnalyticsEntitiesContext db, DateTime monday)
        {
            var sunday = monday.AddDays(6);
            var userIdList = string.Join(",", new[] { TEST_USER_ID, TEST_USER_ID + 1, TEST_USER_ID + 2, TEST_USER_ID + 3 });

            await ExecuteSql(db, $@"
                DELETE FROM dbo.teams_user_device_usage_log
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");
            await ExecuteSql(db, $@"
                DELETE FROM dbo.platform_user_activity_log
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");
            await ExecuteSql(db, $@"
                DELETE FROM dbo.yammer_device_activity_log
                WHERE user_id IN ({userIdList}) AND date >= '{monday:yyyy-MM-dd}' AND date <= '{sunday:yyyy-MM-dd}'");
            await ExecuteSql(db, $@"
                DELETE FROM profiling.UsageWeekly
                WHERE date = '{monday:yyyy-MM-dd}' AND user_id IN ({userIdList})");
        }

        /// <summary>Monday (on or before) of the given date's week - mirrors profiling.udf_GetMonday.</summary>
        private static DateTime MondayOf(DateTime d)
        {
            var daysSinceMonday = ((int)d.DayOfWeek + 6) % 7;
            return d.Date.AddDays(-daysSinceMonday);
        }

        /// <summary>Seeds one row per weekly table (Activities long + columns + Usage) for a given week.</summary>
        private async Task SeedWeeklyRows(AnalyticsEntitiesContext db, int userId, DateTime monday)
        {
            await ExecuteSql(db, $@"
                INSERT INTO profiling.ActivitiesWeekly (user_id, MetricDate, Metric, [Sum])
                VALUES ({userId}, '{monday:yyyy-MM-dd}', 'Teams Calls', 1)");
            await ExecuteSql(db, $@"
                INSERT INTO profiling.ActivitiesWeeklyColumns (user_id, date)
                VALUES ({userId}, '{monday:yyyy-MM-dd}')");
            await ExecuteSql(db, $@"
                INSERT INTO profiling.UsageWeekly (user_id, date)
                VALUES ({userId}, '{monday:yyyy-MM-dd}')");
        }

        /// <summary>Total rows for a user/week across the three weekly tables.</summary>
        private async Task<int> CountWeeklyRows(AnalyticsEntitiesContext db, int userId, DateTime monday)
        {
            return await ExecuteScalar<int>(db, $@"
                SELECT
                    (SELECT COUNT(*) FROM profiling.ActivitiesWeekly WHERE user_id = {userId} AND MetricDate = '{monday:yyyy-MM-dd}')
                  + (SELECT COUNT(*) FROM profiling.ActivitiesWeeklyColumns WHERE user_id = {userId} AND date = '{monday:yyyy-MM-dd}')
                  + (SELECT COUNT(*) FROM profiling.UsageWeekly WHERE user_id = {userId} AND date = '{monday:yyyy-MM-dd}')");
        }

        private async Task CleanupWeeklyRows(AnalyticsEntitiesContext db, int userId)
        {
            await ExecuteSql(db, $@"
                DELETE FROM profiling.ActivitiesWeekly WHERE user_id = {userId};
                DELETE FROM profiling.ActivitiesWeeklyColumns WHERE user_id = {userId};
                DELETE FROM profiling.UsageWeekly WHERE user_id = {userId};");
        }

        #endregion
    }
}
