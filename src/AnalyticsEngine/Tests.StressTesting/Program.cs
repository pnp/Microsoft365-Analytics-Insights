using System;
using System.Collections.Generic;
using Tests.StressTesting.StressTests;

namespace Tests.StressTesting
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  Office 365 Activity Importer - Stress Testing");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            // Parse optional connection string from command-line (same pattern as Tests.FakeDataGen)
            string connectionString = args.Length > 0 ? string.Join(" ", args) : null;
            if (!string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine($"Raw args count: {args.Length}");
                Console.WriteLine($"Connection string: \"{connectionString}\"");
                DisplayConnectionInfo(connectionString);
            }
            else
            {
                Console.WriteLine("No SQL connection string provided. Tests requiring DB will run in-memory only.");
                Console.WriteLine("Usage: Tests.StressTesting.exe \"<SQL Connection String>\"");
            }
            Console.WriteLine();

            var stressTests = new Dictionary<int, (string Name, Func<BaseStressTest> Factory)>
            {
                { 1, ("ActivityAPI Import Stress Test", () => new ActivityAPIStressTest()) },
                { 2, ("Copilot Event Import Stress Test", () => new CopilotStressTest()) }
            };

            bool running = true;
            while (running)
            {
                Console.WriteLine("\nAvailable Stress Tests:");
                Console.WriteLine("------------------------");
                foreach (var test in stressTests)
                {
                    Console.WriteLine($"{test.Key}. {test.Value.Name}");
                }
                Console.WriteLine("0. Exit");
                Console.WriteLine();
                Console.Write("Select test to run (0 to exit): ");

                string input = Console.ReadLine();
                if (int.TryParse(input, out int selection))
                {
                    if (selection == 0)
                    {
                        running = false;
                        Console.WriteLine("\nExiting...");
                    }
                    else if (stressTests.ContainsKey(selection))
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Starting: {stressTests[selection].Name}");
                        Console.WriteLine(new string('-', 60));

                        try
                        {
                            var stressTest = stressTests[selection].Factory();
                            stressTest.ConnectionString = connectionString;
                            stressTest.Run();
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"\nERROR: {ex.Message}");
                            Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");
                            Console.ResetColor();
                        }

                        Console.WriteLine(new string('-', 60));
                        Console.WriteLine("Test completed. Press any key to continue...");
                        Console.ReadKey();
                        Console.Clear();
                        Console.WriteLine("==================================================");
                        Console.WriteLine("  Office 365 Activity Importer - Stress Testing");
                        Console.WriteLine("==================================================");
                        Console.WriteLine();
                    }
                    else
                    {
                        Console.WriteLine("Invalid selection. Please try again.");
                    }
                }
                else
                {
                    Console.WriteLine("Invalid input. Please enter a number.");
                }
            }
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
    }
}
