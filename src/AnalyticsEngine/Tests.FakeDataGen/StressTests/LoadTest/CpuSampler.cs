using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Tests.FakeDataGen.StressTests.LoadTest
{
    /// <summary>
    /// Samples CPU usage of one or more processes over the lifetime of a measured run.
    ///
    /// Reports, per process:
    ///  - CPU-seconds: total processor time consumed during the run (from the endpoint deltas,
    ///    so it is accurate regardless of sample jitter). This is the "work done" - roughly
    ///    invariant to how many cores it is spread across, so it is the fairest cross-setting
    ///    comparison for a CPU-bound job.
    ///  - Peak CPU%: highest instantaneous utilisation seen, where 100% == one full logical core.
    ///    On a multi-core box a 20-thread burst can read several hundred %; that is exactly the
    ///    "how wide does it spike" signal that maps to the 100%-on-a-1-vCPU-plan complaint in #161.
    ///
    /// Also tracks the peak working set of the first (importer) process.
    /// </summary>
    public sealed class CpuSampler
    {
        private readonly Process[] _procs;
        private readonly int _intervalMs;

        private TimeSpan[] _startCpu;
        private TimeSpan[] _endCpu;
        private double[] _peakPct;
        private TimeSpan[] _lastCpu;
        private long _peakWorkingSetBytes;

        private Thread _thread;
        private volatile bool _running;
        private readonly Stopwatch _sinceLastSample = new Stopwatch();

        public CpuSampler(int intervalMs, params Process[] procs)
        {
            _intervalMs = Math.Max(50, intervalMs);
            _procs = procs ?? new Process[0];
            _startCpu = new TimeSpan[_procs.Length];
            _endCpu = new TimeSpan[_procs.Length];
            _lastCpu = new TimeSpan[_procs.Length];
            _peakPct = new double[_procs.Length];
        }

        public void Start()
        {
            for (int i = 0; i < _procs.Length; i++)
            {
                _startCpu[i] = SafeCpu(_procs[i]);
                _lastCpu[i] = _startCpu[i];
            }
            _peakWorkingSetBytes = SafeWorkingSet(_procs.Length > 0 ? _procs[0] : null);
            _sinceLastSample.Restart();
            _running = true;
            _thread = new Thread(SampleLoop) { IsBackground = true, Name = "CpuSampler" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_thread != null) _thread.Join(2000); } catch { /* best effort */ }
            for (int i = 0; i < _procs.Length; i++)
            {
                _endCpu[i] = SafeCpu(_procs[i]);
            }
        }

        private void SampleLoop()
        {
            while (_running)
            {
                Thread.Sleep(_intervalMs);
                var elapsedMs = _sinceLastSample.Elapsed.TotalMilliseconds;
                _sinceLastSample.Restart();
                if (elapsedMs <= 0) continue;

                for (int i = 0; i < _procs.Length; i++)
                {
                    var now = SafeCpu(_procs[i]);
                    var deltaMs = (now - _lastCpu[i]).TotalMilliseconds;
                    _lastCpu[i] = now;
                    if (deltaMs < 0) deltaMs = 0;
                    var pct = (deltaMs / elapsedMs) * 100.0; // 100% == one core
                    if (pct > _peakPct[i]) _peakPct[i] = pct;
                }

                var ws = SafeWorkingSet(_procs.Length > 0 ? _procs[0] : null);
                if (ws > _peakWorkingSetBytes) _peakWorkingSetBytes = ws;
            }
        }

        public double CpuSeconds(int procIndex)
        {
            if (procIndex < 0 || procIndex >= _procs.Length) return 0;
            return (_endCpu[procIndex] - _startCpu[procIndex]).TotalSeconds;
        }

        public double PeakCpuPercent(int procIndex)
        {
            if (procIndex < 0 || procIndex >= _procs.Length) return 0;
            return _peakPct[procIndex];
        }

        public double PeakWorkingSetMb { get { return _peakWorkingSetBytes / (1024.0 * 1024.0); } }

        private static TimeSpan SafeCpu(Process p)
        {
            if (p == null) return TimeSpan.Zero;
            try { p.Refresh(); return p.TotalProcessorTime; }
            catch { return TimeSpan.Zero; }
        }

        private static long SafeWorkingSet(Process p)
        {
            if (p == null) return 0;
            try { p.Refresh(); return p.WorkingSet64; }
            catch { return 0; }
        }
    }
}
