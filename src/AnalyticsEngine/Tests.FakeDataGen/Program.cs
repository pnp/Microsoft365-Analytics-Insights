using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Tests.FakeDataGen.Copilot;
using Tests.FakeDataGen.StressTests;

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

            // Stress tests
            new MenuItem("ActivityAPI import stress test", MenuCategory.StressTest,
                ctx => RunStressTest(new ActivityAPIStressTest(), ctx)),
            new MenuItem("Copilot event import stress test", MenuCategory.StressTest,
                ctx => RunStressTest(new CopilotStressTest(), ctx)),
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

            string connectionString = args.Length > 0 ? string.Join(" ", args) : null;
            if (!string.IsNullOrEmpty(connectionString))
            {
                DisplayConnectionInfo(connectionString);
            }
            else
            {
                Console.WriteLine("No SQL connection string provided.");
                Console.WriteLine("Stress tests that don't need SQL will still run; everything else will be disabled.");
                Console.WriteLine("Usage: Tests.FakeDataGen.exe \"<SQL Connection String>\"");
            }
            Console.WriteLine();

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

            int count = PromptInt("How many events to generate?", 100, 1, int.MaxValue);
            int agentPercent = PromptInt("Percentage with agents (0-100)", 30, 0, 100);
            int customAgentPercent = PromptInt("Percentage with custom agents (0-100)", 10, 0, 100);
            int copilotLicensePercent = PromptInt("Percentage of users with Copilot licenses (0-100)", 50, 0, 100);

            Console.WriteLine();
            var generator = new CopilotActivityGenerator(connectionString);
            generator.GenerateCopilotActivity(count, customAgentPercent, agentPercent, copilotLicensePercent);

            Console.WriteLine();
            Console.WriteLine("Copilot activity generation completed successfully!");
        }

        private static bool ConfirmDatabaseSafeToWrite(string connectionString)
        {
            try
            {
                using (var db = new CopilotActivityGenerator(connectionString).CreateContext())
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
