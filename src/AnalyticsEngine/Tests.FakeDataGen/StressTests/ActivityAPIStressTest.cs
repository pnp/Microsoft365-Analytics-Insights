using DataUtils;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tests.FakeDataGen.StressTests.FakeLoaders;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// Stress test for ActivityAPI importing engine to detect memory leaks and performance issues
    /// </summary>
    public class ActivityAPIStressTest : BaseStressTest
    {
        // Runs entirely against the in-memory fake loaders - no SQL connection is opened,
        // so the DB upgrade check is unnecessary.
        public override bool RequiresDatabase => false;

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== ActivityAPI Import Stress Test Configuration ===\n");

            // Get test parameters from user
            int iterations = GetIntegerInput("Number of import iterations", 100, 1, 10000);
            int reportsPerLoad = GetIntegerInput("Reports per metadata load", 50, 1, 1000000);
            int reportsPerTimeSlot = GetIntegerInput("Report summaries per time slot", 10, 1, 100);
            int timeSlotCount = GetIntegerInput("Number of time slots", 5, 1, 50);
            int maxSavesPerBatch = GetIntegerInput("Max saves per batch", 100, 1, 1000);
            bool collectGarbageEachIteration = GetBooleanInput("Force GC after each iteration", false);
            bool verbose = GetBooleanInput("Verbose output", false);

            Console.WriteLine("\nCalculated load:");
            int totalReportsPerIteration = reportsPerLoad * reportsPerTimeSlot * timeSlotCount;
            Console.WriteLine($"  Reports per iteration: {totalReportsPerIteration:N0}");
            Console.WriteLine($"  Total reports across all iterations: {(totalReportsPerIteration * iterations):N0}");
            Console.WriteLine();
            Console.WriteLine("Press any key to start test...");
            Console.ReadKey();
            Console.WriteLine();

            var result = new StressTestResult { Success = true };

            try
            {
                _memoryMonitor.Start();
                var stopwatch = Stopwatch.StartNew();

                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var saveManager = new FakeActivityReportPersistenceManager();

                long totalItems = 0;

                for (int i = 0; i < iterations; i++)
                {
                    try
                    {
                        if (verbose || i % 10 == 0)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Iteration {i + 1}/{iterations} - Memory: {_memoryMonitor.GetMemoryString(_memoryMonitor.CurrentMemoryBytes)}");
                        }

                        var importer = new FakeActivityImporterForStress(
                            logger,
                            maxSavesPerBatch,
                            reportsPerLoad,
                            reportsPerTimeSlot,
                            timeSlotCount
                        );

                        var stats = Task.Run(async () => await importer.LoadReportsAndSave(saveManager)).Result;
                        totalItems += stats.Total;

                        _memoryMonitor.UpdatePeak();

                        if (collectGarbageEachIteration)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"ERROR in iteration {i + 1}: {ex.Message}");
                        Console.ResetColor();
                        result.Success = false;
                        result.Exception = ex;
                        break;
                    }
                }

                stopwatch.Stop();
                _memoryMonitor.Stop();

                result.ItemsProcessed = totalItems;
                result.Duration = stopwatch.Elapsed;
                result.InitialMemoryBytes = _memoryMonitor.InitialMemoryBytes;
                result.PeakMemoryBytes = _memoryMonitor.PeakMemoryBytes;
                result.FinalMemoryBytes = _memoryMonitor.CurrentMemoryBytes;

                if (result.Success)
                {
                    result.Message = $"Completed {iterations} iterations successfully";
                }

                // Check for potential memory leaks
                long memoryGrowth = result.FinalMemoryBytes - result.InitialMemoryBytes;
                double growthPercentage = (memoryGrowth / (double)result.InitialMemoryBytes) * 100;

                if (growthPercentage > 50)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\nWARNING: Memory grew by {growthPercentage:F1}% ({_memoryMonitor.GetMemoryString(memoryGrowth)})");
                    Console.WriteLine("This may indicate a memory leak.");
                    Console.ResetColor();
                    result.Message += $" | WARNING: {growthPercentage:F1}% memory growth";
                }

                _memoryMonitor.PrintReport();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Exception = ex;
                result.Message = $"Test failed: {ex.Message}";
            }

            return result;
        }
    }
}
