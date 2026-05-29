using System;
using System.Diagnostics;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// Monitors memory usage during stress tests
    /// </summary>
    public class MemoryMonitor
    {
        private long _initialMemory;
        private long _peakMemory;
        private Stopwatch _stopwatch;

        public MemoryMonitor()
        {
            _stopwatch = new Stopwatch();
        }

        public void Start()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            _initialMemory = GC.GetTotalMemory(false);
            _peakMemory = _initialMemory;
            _stopwatch.Start();
        }

        public void UpdatePeak()
        {
            long currentMemory = GC.GetTotalMemory(false);
            if (currentMemory > _peakMemory)
            {
                _peakMemory = currentMemory;
            }
        }

        public void Stop()
        {
            _stopwatch.Stop();
            UpdatePeak();
        }

        public long InitialMemoryBytes => _initialMemory;
        public long PeakMemoryBytes => _peakMemory;
        public long CurrentMemoryBytes => GC.GetTotalMemory(false);
        public long MemoryDeltaBytes => CurrentMemoryBytes - _initialMemory;
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public string GetMemoryString(long bytes)
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

        public void PrintReport()
        {
            Console.WriteLine("\n--- Memory Report ---");
            Console.WriteLine($"Initial Memory:  {GetMemoryString(InitialMemoryBytes)}");
            Console.WriteLine($"Peak Memory:     {GetMemoryString(PeakMemoryBytes)}");
            Console.WriteLine($"Current Memory:  {GetMemoryString(CurrentMemoryBytes)}");
            Console.WriteLine($"Memory Delta:    {GetMemoryString(MemoryDeltaBytes)}");
            Console.WriteLine($"Duration:        {Elapsed.TotalSeconds:F2} seconds");
            Console.WriteLine("--------------------");
        }
    }
}
