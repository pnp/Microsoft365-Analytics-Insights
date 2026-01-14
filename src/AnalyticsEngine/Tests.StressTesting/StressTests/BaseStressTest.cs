using System;
using Tests.StressTesting.Infrastructure;

namespace Tests.StressTesting.StressTests
{
    /// <summary>
    /// Base class for all stress tests
    /// </summary>
    public abstract class BaseStressTest
    {
        protected MemoryMonitor _memoryMonitor;

        public BaseStressTest()
        {
            _memoryMonitor = new MemoryMonitor();
        }

        /// <summary>
        /// Run the stress test
        /// </summary>
        public virtual void Run()
        {
            Console.WriteLine($"\n{GetType().Name} starting...\n");

            try
            {
                var result = Execute();
                result.Print();

                if (!result.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Test completed with warnings or errors.");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Test completed successfully!");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nTest failed with exception: {ex.Message}");
                Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Execute the stress test and return results
        /// </summary>
        protected abstract StressTestResult Execute();

        /// <summary>
        /// Get test configuration from user
        /// </summary>
        protected int GetIntegerInput(string prompt, int defaultValue, int min = 1, int max = int.MaxValue)
        {
            Console.Write($"{prompt} (default {defaultValue}): ");
            string input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                return defaultValue;

            if (int.TryParse(input, out int value))
            {
                if (value >= min && value <= max)
                    return value;
                else
                {
                    Console.WriteLine($"Value must be between {min} and {max}. Using default: {defaultValue}");
                    return defaultValue;
                }
            }
            else
            {
                Console.WriteLine($"Invalid input. Using default: {defaultValue}");
                return defaultValue;
            }
        }

        protected bool GetBooleanInput(string prompt, bool defaultValue)
        {
            Console.Write($"{prompt} (Y/N, default {(defaultValue ? "Y" : "N")}): ");
            string input = Console.ReadLine()?.Trim().ToUpper();

            if (string.IsNullOrWhiteSpace(input))
                return defaultValue;

            return input == "Y" || input == "YES";
        }
    }
}
