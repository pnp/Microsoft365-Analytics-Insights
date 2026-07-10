using System;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// Base class for all stress tests
    /// </summary>
    public abstract class BaseStressTest
    {
        protected MemoryMonitor _memoryMonitor;

        /// <summary>
        /// Optional SQL connection string passed via command-line argument.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// When true, the test never blocks on console input: <see cref="GetIntegerInput"/> /
        /// <see cref="GetBooleanInput"/> resolve from environment variables (or their defaults) and
        /// <see cref="PauseIfInteractive"/> is a no-op. Enables scripted, repeatable before/after
        /// perf runs (e.g. baseline vs optimised). Set by the host when launched with <c>--run</c>
        /// or when the <c>STRESS_NONINTERACTIVE</c> environment variable is truthy.
        /// </summary>
        public bool NonInteractive { get; set; }
            = IsTruthy(Environment.GetEnvironmentVariable("STRESS_NONINTERACTIVE"));

        /// <summary>
        /// Result of the most recent <see cref="Run"/>, so a non-interactive host can set its
        /// process exit code from <see cref="StressTestResult.Success"/>.
        /// </summary>
        public StressTestResult LastResult { get; private set; }

        /// <summary>
        /// True if this stress test reads or writes the analytics database.
        /// When true, the host runs <c>DatabaseUpgrader.CheckDbUpgraded</c> once per session
        /// before the first DB-bound test executes, so the schema, custom SQL scripts and
        /// stored procedures (e.g. profiling) are always up to date before the test starts
        /// inserting data. Override to <c>false</c> for tests that run purely in-memory
        /// against fake loaders.
        /// </summary>
        public virtual bool RequiresDatabase => true;

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
                LastResult = result;
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
                LastResult = new StressTestResult { Success = false, Exception = ex, Message = ex.Message };
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
        /// True for "1", "true", "yes", "y" (case-insensitive). Used for env-var flags.
        /// </summary>
        protected static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Blocks for a key press only in interactive mode; a no-op when <see cref="NonInteractive"/>.
        /// </summary>
        protected void PauseIfInteractive(string message = "Press any key to continue...")
        {
            if (NonInteractive) return;
            Console.WriteLine(message);
            Console.ReadKey();
        }

        /// <summary>
        /// Get test configuration from user. In non-interactive mode (or whenever <paramref name="envKey"/>
        /// is set and present in the environment) the value comes from the environment variable, else the
        /// default — so scripted before/after runs are fully repeatable.
        /// </summary>
        protected int GetIntegerInput(string prompt, int defaultValue, int min = 1, int max = int.MaxValue, string envKey = null)
        {
            if (!string.IsNullOrEmpty(envKey))
            {
                var env = Environment.GetEnvironmentVariable(envKey);
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out int envVal))
                {
                    var clamped = Math.Min(Math.Max(envVal, min), max);
                    Console.WriteLine($"{prompt}: {clamped} (from {envKey})");
                    return clamped;
                }
            }

            if (NonInteractive)
            {
                Console.WriteLine($"{prompt}: {defaultValue} (default)");
                return defaultValue;
            }

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

        protected bool GetBooleanInput(string prompt, bool defaultValue, string envKey = null)
        {
            if (!string.IsNullOrEmpty(envKey))
            {
                var env = Environment.GetEnvironmentVariable(envKey);
                if (!string.IsNullOrWhiteSpace(env))
                {
                    bool envVal = IsTruthy(env);
                    Console.WriteLine($"{prompt}: {(envVal ? "Y" : "N")} (from {envKey})");
                    return envVal;
                }
            }

            if (NonInteractive)
            {
                Console.WriteLine($"{prompt}: {(defaultValue ? "Y" : "N")} (default)");
                return defaultValue;
            }

            Console.Write($"{prompt} (Y/N, default {(defaultValue ? "Y" : "N")}): ");
            string input = Console.ReadLine()?.Trim().ToUpper();

            if (string.IsNullOrWhiteSpace(input))
                return defaultValue;

            return input == "Y" || input == "YES";
        }
    }
}
