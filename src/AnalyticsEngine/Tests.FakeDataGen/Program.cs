using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Tests.FakeDataGen.Copilot;
using Tests.FakeDataGen.Demo;
using Tests.FakeDataGen.Office365;
using Tests.FakeDataGen.StressTests;
using Tests.FakeDataGen.StressTests.LoadTest;

namespace Tests.FakeDataGen
{
    /// <summary>
    /// Console host that exposes two related capabilities behind one menu:
    ///   - Data generation: insert realistic-looking rows for manual / UI testing.
    ///   - Stress testing: drive the importer + SQL commit paths under load.
    /// Both modes accept an optional SQL connection string as the first argument.
    /// </summary>
    internal class Program
    {
        // Menu options are registered up-front so the dispatcher stays in one place.
        private static readonly List<MenuItem> MenuItems = new List<MenuItem>
        {
            // Data generation
            new MenuItem("Generate fake Copilot activity", MenuCategory.DataGeneration,
                ctx => RunCopilotActivityGenerator(ctx.RequireConnectionString())),
            new MenuItem("Generate fake O365 audit activity", MenuCategory.DataGeneration,
                ctx => RunOffice365ActivityGenerator(ctx.RequireConnectionString())),
            new MenuItem("Generate combined profiling data (O365 + Copilot)", MenuCategory.DataGeneration,
                ctx => RunCombinedActivityGenerator(ctx.RequireConnectionString())),
            new MenuItem("Generate fake Copilot prompt history (AI interaction history)", MenuCategory.DataGeneration,
                ctx => RunCopilotInteractionHistoryGenerator(ctx.RequireConnectionString())),

            // Stress tests
            new MenuItem("ActivityAPI import stress test", MenuCategory.StressTest,
                ctx => RunStressTest(new ActivityAPIStressTest(), ctx)),
            new MenuItem("ActivityAPI import stress test (DB-backed, COLD+WARM)", MenuCategory.StressTest,
                ctx => RunStressTest(new ActivityApiDbStressTest(), ctx)),
            new MenuItem("Copilot event import stress test", MenuCategory.StressTest,
                ctx => RunStressTest(new CopilotStressTest(), ctx)),
            new MenuItem("Copilot Adoption page performance test (read-only, before/after)", MenuCategory.StressTest,
                ctx => RunStressTest(new CopilotAdoptionPerfTest(), ctx)),
            new MenuItem("Power Platform event import stress test", MenuCategory.StressTest,
                ctx => RunStressTest(new PowerPlatformStressTest(), ctx)),
            new MenuItem("Sent email importer stress test", MenuCategory.StressTest,
                ctx => RunStressTest(new SentEmailImporterStressTest(), ctx)),
            new MenuItem("User activity data stress test (profiling SQL inputs)", MenuCategory.StressTest,
                ctx => RunStressTest(new UserActivityStressTest(), ctx)),
        };

        static void Main(string[] args)
        {
            PrintBanner();

            if (args.Length > 0 && args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = DemoCommand.Run(args.Skip(1).ToArray());
                return;
            }

            // Non-interactive load-test mode (issue #161 / PR #162). Usage:
            //   Tests.FakeDataGen.exe loadtest "<SQL Connection String>" [targetItemsPerArea] [csvPath]
            if (args.Length >= 2 && args[0].Equals("loadtest", StringComparison.OrdinalIgnoreCase))
            {
                RunLoadTest(args);
                return;
            }

            // Pull optional flags out first so they aren't mistaken for connection-string fragments.
            //   Tests.FakeDataGen.exe "<SQL Connection String>" --run copilot
            // A non-empty --run launches that stress test non-interactively (env-var config) and exits with
            // code 0/1, so baseline-vs-optimised perf runs can be scripted and compared repeatably.
            var argList = new List<string>(args);
            string directRun = TakeOptionValue(argList, "--run");

            string connectionString = argList.Count > 0 ? string.Join(" ", argList) : null;
            if (!string.IsNullOrEmpty(connectionString))
            {
                DisplayConnectionInfo(connectionString);
            }
            else
            {
                Console.WriteLine("No SQL connection string provided.");
                Console.WriteLine("Stress tests that don't need SQL will still run; everything else will be disabled.");
                Console.WriteLine("Usage: Tests.FakeDataGen.exe \"<SQL Connection String>\" [--run <copilot|copilotadoption|activityapi|activityapidb|powerplatform|sentemail|useractivity>]");
                Console.WriteLine("Safe one-command demo: Tests.FakeDataGen.exe demo --help");
            }
            Console.WriteLine();

            if (!string.IsNullOrEmpty(directRun))
            {
                Environment.ExitCode = RunTestDirect(directRun, new RunContext(connectionString)) ? 0 : 1;
                return;
            }

            var ctx = new RunContext(connectionString);

            bool running = true;
            while (running)
            {
                ShowMenu();

                Console.Write("Select an option: ");
                string input = Console.ReadLine();
                Console.WriteLine();

                if (string.IsNullOrWhiteSpace(input)) continue;

                if (input.Trim() == "0" || input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    running = false;
                    Console.WriteLine("Exiting...");
                    break;
                }

                if (!int.TryParse(input, out int selection) || selection < 1 || selection > MenuItems.Count)
                {
                    Console.WriteLine("Invalid selection. Please try again.\n");
                    continue;
                }

                var item = MenuItems[selection - 1];
                Console.WriteLine($"Starting: {item.Title}");
                Console.WriteLine(new string('-', 60));

                try
                {
                    item.Run(ctx);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\nERROR: {ex.Message}");
                    Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");
                    Console.ResetColor();
                }

                Console.WriteLine(new string('-', 60));
                Console.WriteLine("Press any key to return to the menu...");
                Console.ReadKey(true);
                Console.Clear();
                PrintBanner();
            }
        }

        private static void RunLoadTest(string[] args)
        {
            string connectionString = args[1];
            int targetItems = 100000;
            if (args.Length >= 3 && int.TryParse(args[2], out var ti) && ti > 0) targetItems = ti;
            int reps = 3;
            if (args.Length >= 4 && int.TryParse(args[3], out var rp) && rp > 0) reps = rp;
            string csvPath = args.Length >= 5
                ? args[4]
                : System.IO.Path.Combine(Environment.CurrentDirectory, "loadtest-results.csv");
            string areas = args.Length >= 6 ? args[5] : null;

            DisplayConnectionInfo(connectionString);

            // Bring schema (EF migrations + custom SQL scripts) up to date before importing.
            Console.WriteLine("Ensuring database schema is up to date (DatabaseUpgrader.CheckDbUpgraded)...");
            var initInfo = new DatabaseUpgradeInfo { ConnectionString = connectionString };
            DatabaseUpgrader.CheckDbUpgraded(initInfo, msg => Console.WriteLine($"[DB] {msg}"));
            Console.WriteLine("Schema ready.");

            new LoadTestSuite(connectionString, csvPath, targetItems, reps, areas).Run();
        }

        private static void PrintBanner()
        {
            Console.WriteLine("===========================================================");
            Console.WriteLine("  Microsoft 365 Analytics - Fake Data & Stress Testing");
            Console.WriteLine("===========================================================");
            Console.WriteLine();
        }

        private static void ShowMenu()
        {
            Console.WriteLine();
            Console.WriteLine("DATA GENERATION");
            int index = 1;
            foreach (var item in MenuItems.Where(m => m.Category == MenuCategory.DataGeneration))
            {
                Console.WriteLine($"  {index}. {item.Title}");
                index++;
            }

            Console.WriteLine();
            Console.WriteLine("STRESS TESTS");
            foreach (var item in MenuItems.Where(m => m.Category == MenuCategory.StressTest))
            {
                Console.WriteLine($"  {index}. {item.Title}");
                index++;
            }

            Console.WriteLine();
            Console.WriteLine("  0. Exit");
            Console.WriteLine();
        }

        private static void DisplayConnectionInfo(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                Console.WriteLine("SQL Server Connection Information:");
                Console.WriteLine("-------------------------------------------");
                Console.WriteLine($"  Server: {builder.DataSource}");
                Console.WriteLine($"  Database: {builder.InitialCatalog}");
                Console.WriteLine($"  Authentication: {(builder.IntegratedSecurity ? "Windows (Integrated Security)" : "SQL Server")}");
                Console.WriteLine("-------------------------------------------");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not parse connection string details: {ex.Message}");
            }
        }

        private static void RunStressTest(BaseStressTest test, RunContext ctx)
        {
            test.ConnectionString = ctx.ConnectionString;
            if (test.RequiresDatabase)
            {
                ctx.EnsureDbUpgraded();
            }
            test.Run();
        }

        /// <summary>
        /// Stress tests that can be launched non-interactively via <c>--run &lt;name&gt;</c>. Keyed by a short
        /// stable name so scripted perf comparisons don't depend on menu ordering.
        /// </summary>
        private static readonly Dictionary<string, Func<BaseStressTest>> DirectRunnableTests =
            new Dictionary<string, Func<BaseStressTest>>(StringComparer.OrdinalIgnoreCase)
            {
                { "copilot", () => new CopilotStressTest() },
                { "copilotadoption", () => new CopilotAdoptionPerfTest() },
                { "activityapi", () => new ActivityAPIStressTest() },
                { "activityapidb", () => new ActivityApiDbStressTest() },
                { "powerplatform", () => new PowerPlatformStressTest() },
                { "sentemail", () => new SentEmailImporterStressTest() },
                { "useractivity", () => new UserActivityStressTest() },
            };

        /// <summary>
        /// Runs a single stress test non-interactively (config via environment variables) and returns whether
        /// it succeeded, so the host can set its process exit code. Used for scripted before/after perf runs.
        /// </summary>
        private static bool RunTestDirect(string name, RunContext ctx)
        {
            if (!DirectRunnableTests.TryGetValue(name.Trim(), out var factory))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Unknown --run test '{name}'. Known: {string.Join(", ", DirectRunnableTests.Keys)}");
                Console.ResetColor();
                return false;
            }

            var test = factory();
            test.NonInteractive = true;
            test.ConnectionString = ctx.ConnectionString;

            try
            {
                if (test.RequiresDatabase)
                {
                    ctx.EnsureDbUpgraded();
                }
                test.Run();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nERROR: {ex.Message}");
                Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");
                Console.ResetColor();
                return false;
            }

            return test.LastResult != null && test.LastResult.Success;
        }

        /// <summary>
        /// Removes <paramref name="optionName"/> and its following value from <paramref name="argList"/> and
        /// returns that value (null if the option is absent). Lets flags coexist with a space-containing
        /// connection string passed as the remaining args.
        /// </summary>
        private static string TakeOptionValue(List<string> argList, string optionName)
        {
            int idx = argList.FindIndex(a => string.Equals(a, optionName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return null;
            }

            string value = null;
            if (idx + 1 < argList.Count)
            {
                value = argList[idx + 1];
                argList.RemoveAt(idx + 1);
            }
            argList.RemoveAt(idx);
            return value;
        }

        private static void RunCopilotActivityGenerator(string connectionString)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Generate Fake Copilot Activity");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            if (!ConfirmDatabaseSafeToWrite(connectionString))
            {
                Console.WriteLine("Operation cancelled by user.");
                return;
            }

            int count = PromptInt("How many events to generate?", 5000, 1, int.MaxValue);
            int userCount = PromptInt("How many users to generate (if the database has none)?", 250, 1, int.MaxValue);
            int agentPercent = PromptInt("Percentage with agents (0-100)", 30, 0, 100);
            int customAgentPercent = PromptInt("Percentage with custom agents (0-100)", 10, 0, 100);
            int copilotLicensePercent = PromptInt("Percentage of users with Copilot licenses (0-100)", 50, 0, 100);

            Console.WriteLine();
            var generator = new CopilotActivityGenerator(connectionString);
            generator.GenerateCopilotActivity(count, customAgentPercent, agentPercent, copilotLicensePercent, userCount);

            Console.WriteLine();
            Console.WriteLine("Copilot activity generation completed successfully!");
        }

        /// <summary>
        /// Generates the per-turn Copilot "prompt history" tables so interaction reports can be built and
        /// measured without a real tenant. No prompt text is generated or stored - the real import keeps only
        /// counts, so this does too.
        /// </summary>
        private static void RunCopilotInteractionHistoryGenerator(string connectionString)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Generate Fake Copilot Prompt History");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            if (!ConfirmDatabaseSafeToWrite(connectionString))
            {
                Console.WriteLine("Operation cancelled by user.");
                return;
            }

            int userCount = PromptInt("How many users should have history?", 250, 1, int.MaxValue);
            int sessionsPerUser = PromptInt("Average conversations per user", 8, 1, 10000);
            int turnsPerSession = PromptInt("Average turns per conversation (each turn = a prompt + a response)", 6, 1, 10000);
            int daysBack = PromptInt("How many days back should history be spread across?", 90, 1, 3650);
            int cognitivePercent = PromptInt("Percentage of prompts with sentiment / language / key phrases (0-100)", 70, 0, 100);
            int sharedPercent = PromptInt("Percentage of conversations shared with a second user (0-100)", 5, 0, 100);

            Console.WriteLine();
            var generator = new CopilotInteractionHistoryGenerator(connectionString);
            generator.GenerateInteractionHistory(userCount, sessionsPerUser, turnsPerSession, daysBack,
                cognitivePercent, sharedPercent);

            Console.WriteLine();
            Console.WriteLine("Copilot prompt history generation completed successfully!");
        }

        private static void RunOffice365ActivityGenerator(string connectionString)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Generate Fake O365 Audit Activity");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            if (!ConfirmDatabaseSafeToWrite(connectionString))
            {
                Console.WriteLine("Operation cancelled by user.");
                return;
            }

            int count = PromptInt("How many events to generate?", 5000, 1, int.MaxValue);
            int userCount = PromptInt("How many users to generate (if the database has none)?", 250, 1, int.MaxValue);
            int daysBack = PromptInt("How many days back should activity be spread across?", 90, 1, 3650);

            Console.WriteLine();
            var generator = new Office365ActivityGenerator(connectionString);
            generator.GenerateOffice365Activity(count, userCount, daysBack);

            Console.WriteLine();
            Console.WriteLine("O365 audit activity generation completed successfully!");
        }

        private static void RunCombinedActivityGenerator(string connectionString)
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("  Generate Combined Profiling Data");
            Console.WriteLine("  O365 Usage + Copilot Activity");
            Console.WriteLine("====================================================");
            Console.WriteLine();

            if (!ConfirmDatabaseSafeToWrite(connectionString))
            {
                Console.WriteLine("Operation cancelled by user.");
                return;
            }

            int count = PromptInt("How many events should each generator create?", 5000, 1, int.MaxValue);
            int userCount = PromptInt("How many users should both generators share?", 250, 1, int.MaxValue);
            int daysBack = PromptInt("How many days back should both data sets cover?", 90, 1, 3650);

            // On by default: the combined data set is what the Copilot Adoption report is demonstrated
            // from, and a purely random scatter puts every licensed user in the same band.
            bool adoptionScenario = PromptYesNo(
                "Shape Copilot usage into adoption personas (all funnel stages, contrasting departments)?", true);

            int agentPercent = 0;
            int customAgentPercent = 0;
            if (!adoptionScenario)
            {
                agentPercent = PromptInt("Percentage of Copilot events with agents (0-100)", 30, 0, 100);
                customAgentPercent = PromptInt("Percentage of agent events using custom agents (0-100)", 10, 0, 100);
            }

            int copilotLicensePercent = PromptInt("Percentage of users with Copilot licenses (0-100)", 50, 0, 100);

            DateTime windowEndUtc = DateTime.UtcNow;

            Console.WriteLine();
            if (adoptionScenario)
            {
                Console.WriteLine($"Generating persona-shaped Copilot activity + {count:N0} O365 events across the same {daysBack:N0}-day window...");
                Console.WriteLine("(Copilot volume follows the persona plan rather than the event count, so the bands come out as intended.)");
            }
            else
            {
                Console.WriteLine($"Generating {count:N0} Copilot + {count:N0} O365 events across the same {daysBack:N0}-day window...");
            }
            Console.WriteLine();

            // Copilot runs first so an empty database gets one shared user population with
            // the requested Copilot-license distribution. O365 then reuses those users.
            var copilotGenerator = new CopilotActivityGenerator(connectionString);
            copilotGenerator.GenerateCopilotActivity(
                count,
                customAgentPercent,
                agentPercent,
                copilotLicensePercent,
                userCount,
                daysBack,
                windowEndUtc,
                adoptionScenario);

            Console.WriteLine();
            var office365Generator = new Office365ActivityGenerator(connectionString);
            office365Generator.GenerateOffice365Activity(
                count,
                userCount,
                daysBack,
                windowEndUtc);

            Console.WriteLine();
            Console.WriteLine("Combined O365 + Copilot profiling data generation completed successfully!");
        }

        private static bool ConfirmDatabaseSafeToWrite(string connectionString)
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext(connectionString, true, false))
                {
                    int copilotCount = db.CopilotChats.Count();
                    int userCount = db.users.Count();
                    int auditEventCount = db.AuditEventsCommon.Count();

                    if (copilotCount == 0 && userCount == 0 && auditEventCount == 0)
                    {
                        Console.WriteLine("Database appears to be empty. Ready to generate data.\n");
                        return true;
                    }

                    Console.WriteLine("WARNING: Database already contains data!");
                    Console.WriteLine("==========================================");
                    Console.WriteLine($"  Copilot Events: {copilotCount}");
                    Console.WriteLine($"  Users: {userCount}");
                    Console.WriteLine($"  Audit Events: {auditEventCount}");
                    Console.WriteLine("==========================================");
                    Console.WriteLine();
                    Console.WriteLine("Generating fake data may create duplicate or inconsistent records.");
                    Console.Write("Do you want to continue? (yes/no): ");
                    string response = Console.ReadLine()?.Trim().ToLowerInvariant();
                    Console.WriteLine();
                    return response == "yes" || response == "y";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not check database state: {ex.Message}");
                Console.Write("Continue anyway? (yes/no): ");
                string response = Console.ReadLine()?.Trim().ToLowerInvariant();
                return response == "yes" || response == "y";
            }
        }

        private static int PromptInt(string prompt, int defaultValue, int min, int max)
        {
            Console.Write($"{prompt} (default {defaultValue}): ");
            string input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input) && int.TryParse(input, out int parsed) && parsed >= min && parsed <= max)
            {
                return parsed;
            }
            return defaultValue;
        }

        private static bool PromptYesNo(string prompt, bool defaultValue)
        {
            Console.Write($"{prompt} [{(defaultValue ? "Y/n" : "y/N")}]: ");
            string input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;

            var trimmed = input.Trim();
            if (trimmed.StartsWith("y", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("n", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        private enum MenuCategory
        {
            DataGeneration,
            StressTest
        }

        private class MenuItem
        {
            public string Title { get; }
            public MenuCategory Category { get; }
            public Action<RunContext> Run { get; }

            public MenuItem(string title, MenuCategory category, Action<RunContext> run)
            {
                Title = title;
                Category = category;
                Run = run;
            }
        }

        private class RunContext
        {
            private bool _dbUpgraded;

            public string ConnectionString { get; }

            public RunContext(string connectionString)
            {
                ConnectionString = connectionString;
            }

            /// <summary>
            /// Used by data generators that have nowhere to write without a connection string.
            /// Also guarantees the database is on the latest schema before the caller starts
            /// inserting rows, so consumers never see "missing column / table / proc" errors.
            /// Throws a clear exception that is caught by the menu loop.
            /// </summary>
            public string RequireConnectionString()
            {
                if (string.IsNullOrEmpty(ConnectionString))
                {
                    throw new InvalidOperationException(
                        "This option needs a SQL connection string. Re-run the tool and pass it as the first argument.");
                }
                EnsureDbUpgraded();
                return ConnectionString;
            }

            /// <summary>
            /// Runs <see cref="DatabaseUpgrader.CheckDbUpgraded"/> exactly once per process
            /// against the configured connection string, so EF migrations + the custom SQL
            /// scripts under <c>App.ControlPanel.Engine\SqlExtentions</c> (profiling schema,
            /// stored procedures, etc.) are applied before any data generator or stress test
            /// touches the database. No-op when no connection string was supplied.
            /// </summary>
            public void EnsureDbUpgraded()
            {
                if (_dbUpgraded || string.IsNullOrEmpty(ConnectionString))
                {
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Ensuring database schema is up to date (DatabaseUpgrader.CheckDbUpgraded)...");
                Console.WriteLine(new string('-', 60));

                var initInfo = new DatabaseUpgradeInfo { ConnectionString = ConnectionString };
                DatabaseUpgrader.CheckDbUpgraded(initInfo, msg => Console.WriteLine($"[DB] {msg}"));

                Console.WriteLine(new string('-', 60));
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Database upgrade check complete.");
                Console.ResetColor();
                Console.WriteLine();

                _dbUpgraded = true;
            }
        }
    }
}
