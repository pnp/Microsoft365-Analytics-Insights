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

            var stressTests = new Dictionary<int, (string Name, Func<BaseStressTest> Factory)>
            {
                { 1, ("ActivityAPI Import Stress Test", () => new ActivityAPIStressTest()) }
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
    }
}
