using System;
using System.Linq;

namespace Tests.FakeDataGen
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Microsoft 365 Analytics Fake Data Generator");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            // Parse command-line arguments
            string connectionString = GetConnectionStringFromArgs(args);

            if (string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine("Error: No SQL connection string provided.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  Tests.FakeDataGen.exe \"<SQL Connection String>\"");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  Tests.FakeDataGen.exe \"Server=localhost;Database=Analytics;Integrated Security=true\"");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("Connection string configured.");
            Console.WriteLine();

            // Display SQL Server and Database information
            DisplayConnectionInfo(connectionString);
            Console.WriteLine();

            // Check if database has existing data
            if (!CheckDatabaseAndConfirm(connectionString))
            {
                Console.WriteLine("Operation cancelled by user.");
                return;
            }

            // Main menu loop
            bool exit = false;
            while (!exit)
            {
                ShowMenu();

                var key = Console.ReadKey(true);
                Console.WriteLine();

                switch (key.KeyChar)
                {
                    case '1':
                        GenerateCopilotActivity(connectionString);
                        break;
                    case '0':
                    case 'q':
                    case 'Q':
                        exit = true;
                        Console.WriteLine("Exiting...");
                        break;
                    default:
                        Console.WriteLine("Invalid selection. Please try again.");
                        Console.WriteLine();
                        break;
                }

                if (!exit)
                {
                    Console.WriteLine();
                    Console.WriteLine("Press any key to continue...");
                    Console.ReadKey(true);
                    Console.Clear();
                }
            }
        }

        static void ShowMenu()
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Select Data Generation Job:");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine("  1. Generate Fake Copilot Activity");
            Console.WriteLine();
            Console.WriteLine("  0. Exit");
            Console.WriteLine();
            Console.Write("Select an option: ");
        }

        static string GetConnectionStringFromArgs(string[] args)
        {
            if (args.Length > 0)
            {
                return args[0];
            }
            return null;
        }

        static void DisplayConnectionInfo(string connectionString)
        {
            try
            {
                var builder = new System.Data.SqlClient.SqlConnectionStringBuilder(connectionString);

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

        static void GenerateCopilotActivity(string connectionString)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  Generate Fake Copilot Activity");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            // Get number of events
            Console.Write("How many events to generate? (default: 100): ");
            string countInput = Console.ReadLine();
            int count = 100;
            if (!string.IsNullOrWhiteSpace(countInput) && int.TryParse(countInput, out int parsedCount) && parsedCount > 0)
            {
                count = parsedCount;
            }

            // Get agent percentage
            Console.Write("Percentage with agents (0-100, default: 30): ");
            string agentPercentInput = Console.ReadLine();
            int agentPercent = 30;
            if (!string.IsNullOrWhiteSpace(agentPercentInput) && int.TryParse(agentPercentInput, out int parsedAgentPercent) && parsedAgentPercent >= 0 && parsedAgentPercent <= 100)
            {
                agentPercent = parsedAgentPercent;
            }

            // Get custom agent percentage
            Console.Write("Percentage with custom agents (0-100, default: 10): ");
            string customAgentPercentInput = Console.ReadLine();
            int customAgentPercent = 10;
            if (!string.IsNullOrWhiteSpace(customAgentPercentInput) && int.TryParse(customAgentPercentInput, out int parsedCustomAgentPercent) && parsedCustomAgentPercent >= 0 && parsedCustomAgentPercent <= 100)
            {
                customAgentPercent = parsedCustomAgentPercent;
            }

            // Get Copilot license percentage
            Console.Write("Percentage of users with Copilot licenses (0-100, default: 30): ");
            string copilotLicensePercentInput = Console.ReadLine();
            int copilotLicensePercent = 50;
            if (!string.IsNullOrWhiteSpace(copilotLicensePercentInput) && int.TryParse(copilotLicensePercentInput, out int parsedCopilotLicensePercent) && parsedCopilotLicensePercent >= 0 && parsedCopilotLicensePercent <= 100)
            {
                copilotLicensePercent = parsedCopilotLicensePercent;
            }

            Console.WriteLine();

            try
            {
                var generator = new CopilotActivityGenerator(connectionString);
                generator.GenerateCopilotActivity(count, customAgentPercent, agentPercent, copilotLicensePercent);

                Console.WriteLine();
                Console.WriteLine("Copilot activity generation completed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("ERROR: Failed to generate copilot activity.");
                Console.WriteLine($"Message: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Stack trace:");
                Console.WriteLine(ex.ToString());
            }
        }

        static bool CheckDatabaseAndConfirm(string connectionString)
        {
            try
            {
                using (var db = new CopilotActivityGenerator(connectionString).CreateContext())
                {
                    // Check for existing copilot data
                    int copilotCount = db.CopilotChats.Count();
                    int userCount = db.users.Count();
                    int auditEventCount = db.AuditEventsCommon.Count();

                    if (copilotCount > 0 || userCount > 0 || auditEventCount > 0)
                    {
                        Console.WriteLine("WARNING: Database already contains data!");
                        Console.WriteLine("==========================================");
                        Console.WriteLine($"  Copilot Events: {copilotCount}");
                        Console.WriteLine($"  Users: {userCount}");
                        Console.WriteLine($"  Audit Events: {auditEventCount}");
                        Console.WriteLine("==========================================");
                        Console.WriteLine();
                        Console.WriteLine("Generating fake data may create duplicate or inconsistent records.");
                        Console.WriteLine();
                        Console.Write("Do you want to continue? (yes/no): ");

                        string response = Console.ReadLine()?.Trim().ToLower();
                        if (response != "yes" && response != "y")
                        {
                            return false;
                        }
                        Console.WriteLine();
                    }
                    else
                    {
                        Console.WriteLine("Database appears to be empty. Ready to generate data.");
                        Console.WriteLine();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not check database state: {ex.Message}");
                Console.WriteLine();
                Console.Write("Continue anyway? (yes/no): ");
                string response = Console.ReadLine()?.Trim().ToLower();
                return response == "yes" || response == "y";
            }
        }
    }
}
