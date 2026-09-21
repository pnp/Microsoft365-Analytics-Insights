using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using Tests.FakeDataGen.Copilot;
using Tests.FakeDataGen.Demo;
using Tests.FakeDataGen.Dlp;
using Tests.FakeDataGen.Office365;

namespace Tests.FakeDataGen
{
    /// <summary>
    /// Console host for synthetic data generation used by manual and UI testing.
    /// The optional SQL connection string is accepted as the first argument.
    /// </summary>
    internal class Program
    {
        // Menu options are registered up-front so the dispatcher stays in one place.
        private static readonly List<MenuItem> MenuItems = CreateMenuItems();

        private static List<MenuItem> CreateMenuItems()
        {
            var items = new List<MenuItem>
            {
                new MenuItem(DemoInteractive.MenuTitle, MenuCategory.DataGeneration, ctx => DemoInteractive.Run())
            };
            foreach (var area in DemoAreas.Catalogue)
                items.Add(new MenuItem(area.Title + " (existing or new DB)", MenuCategory.DataGeneration,
                    ctx => DemoInteractive.RunArea(area, ctx.ConnectionString)));
            return items;
        }

        static void Main(string[] args)
        {
            PrintBanner();

            if (args.Length > 0 && args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = DemoCommand.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0].Equals("append", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("append requires a TEST database connection string and --confirm-existing.");
                    Environment.ExitCode = 2;
                }
                else Environment.ExitCode = DemoCommand.RunExisting(args.Skip(2).ToArray(), args[1]);
                return;
            }

            string connectionString = args.Length > 0 ? string.Join(" ", args) : null;
            if (!string.IsNullOrEmpty(connectionString))
            {
                DisplayConnectionInfo(connectionString);
            }
            else
            {
                Console.WriteLine("No SQL connection string provided.");
                Console.WriteLine("The full demo and individual activity menus can create a new LocalDB database.");
                Console.WriteLine("Existing-database appends need a startup connection string.");
                Console.WriteLine("Usage: Tests.FakeDataGen.exe \"<SQL Connection String>\"");
                Console.WriteLine("Safe one-command demo: Tests.FakeDataGen.exe demo --help (or pick it from the menu below)");
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
            Console.WriteLine("  Microsoft 365 Analytics - Synthetic Data Generator");
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
        /// Attaches fake Microsoft Purview DLP policy activity to data that is already in the database,
        /// so the "DLP impact on Copilot" report has something to show.
        /// </summary>
        /// <remarks>
        /// Runs against EXISTING Copilot interactions on purpose: the report is about which agents and
        /// people are affected, so the blocks have to hang off the same agents the Copilot reports show.
        /// Generate Copilot activity first if the database is empty.
        /// </remarks>
        private static void RunDlpActivityGenerator(string connectionString)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Generate Fake DLP Policy Activity");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine("Attaches DLP policy matches to Copilot interactions and audit events that");
            Console.WriteLine("already exist. Generate Copilot activity first if this database is empty.");
            Console.WriteLine();

            if (!ConfirmDatabaseSafeToWrite(connectionString))
            {
                Console.WriteLine("Operation cancelled by user.");
                return;
            }

            int affectedPercent = PromptInt("Percentage of Copilot interactions affected by a policy (0-100)", 8, 0, 100);
            int tenantMatches = PromptInt("How many tenant-wide (DLP.All) rule matches?", 500, 0, int.MaxValue);

            Console.WriteLine();
            new DlpActivityGenerator(connectionString).GenerateDlpActivity(affectedPercent, tenantMatches);

            Console.WriteLine();
            Console.WriteLine("DLP activity generation completed successfully!");
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
            DataGeneration
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
            /// Used by legacy data generators that require an existing database connection.
            /// Also guarantees the database is on the latest schema before inserting rows.
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
            /// against the configured connection string before a legacy generator touches the database.
            /// No-op when no connection string was supplied.
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
