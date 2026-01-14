using System;

namespace Tests.StressTesting.Infrastructure
{
    /// <summary>
    /// Results from a stress test execution
    /// </summary>
    public class StressTestResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public long ItemsProcessed { get; set; }
        public TimeSpan Duration { get; set; }
        public long InitialMemoryBytes { get; set; }
        public long PeakMemoryBytes { get; set; }
        public long FinalMemoryBytes { get; set; }
        public Exception Exception { get; set; }

        public double ItemsPerSecond
        {
            get
            {
                if (Duration.TotalSeconds > 0)
                    return ItemsProcessed / Duration.TotalSeconds;
                return 0;
            }
        }

        public void Print()
        {
            Console.WriteLine("\n=== Stress Test Results ===");
            Console.WriteLine($"Status: {(Success ? "SUCCESS" : "FAILED")}");
            if (!string.IsNullOrEmpty(Message))
            {
                Console.WriteLine($"Message: {Message}");
            }
            Console.WriteLine($"Items Processed: {ItemsProcessed:N0}");
            Console.WriteLine($"Duration: {Duration.TotalSeconds:F2} seconds");
            Console.WriteLine($"Throughput: {ItemsPerSecond:F2} items/second");
            Console.WriteLine($"Initial Memory: {FormatBytes(InitialMemoryBytes)}");
            Console.WriteLine($"Peak Memory: {FormatBytes(PeakMemoryBytes)}");
            Console.WriteLine($"Final Memory: {FormatBytes(FinalMemoryBytes)}");
            Console.WriteLine($"Memory Delta: {FormatBytes(FinalMemoryBytes - InitialMemoryBytes)}");

            if (Exception != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nException: {Exception.Message}");
                Console.WriteLine($"Stack Trace:\n{Exception.StackTrace}");
                Console.ResetColor();
            }
            Console.WriteLine("===========================\n");
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            else if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F2} KB";
            else if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            else
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}
