using Common.Entities;
using Common.Entities.CopilotAdoption;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.FakeDataGen.StressTests
{
    /// <summary>
    /// Measures how long the Copilot Adoption page actually takes, by running the REAL
    /// <see cref="CopilotAdoptionService"/> end to end against whatever database the connection string
    /// points at, and reporting the per-step breakdown.
    ///
    /// <para>
    /// <b>Why this exists.</b> Issue #360 was raised because the page timed out on a large tenant, and the
    /// release that fixed the two slowest queries (Stable build 1810) did not make it fast enough - the
    /// page still spends minutes "Analysing Copilot adoption...". Optimising it further needs a number that
    /// can be produced before a change and again after it, on the same data, without a customer in the
    /// loop. That is all this test is.
    /// </para>
    ///
    /// <para>
    /// <b>It runs the real service on purpose.</b> Copying the SQL into a benchmark script measures a
    /// snapshot that silently rots the moment someone edits <see cref="CopilotAdoptionSql"/>. Driving
    /// <see cref="CopilotAdoptionService.AnalyseAsync"/> means this test measures whatever the product
    /// actually does today, including the C# scoring step, which no SQL-only benchmark would catch.
    /// </para>
    ///
    /// <para>
    /// <b>What it reports.</b> The service already times every step into
    /// <see cref="CopilotAdoptionDiagnostics"/> - the same instrumentation the page and App Insights use -
    /// so this reports that, per window, as a median over several runs. Those step names are compile-time
    /// constants and every value is a duration, so the output carries no tenant data and is safe to paste
    /// into a PR or an issue.
    /// </para>
    ///
    /// <para>
    /// <b>Reading the result.</b> The steps run SEQUENTIALLY, so the total is their sum and the slowest
    /// step is the only one worth optimising first. A step at or near <c>QueryTimeoutSecs</c> did not take
    /// that long - it FAILED, and the analysis carried on with an empty result. Those are flagged rather
    /// than averaged in silently, because a fast run made of failed steps is not an improvement.
    /// </para>
    ///
    /// <para>
    /// <b>Data.</b> This test never generates anything - point it at a database seeded by
    /// <see cref="UserActivityStressTest"/> (which scales to a large tenant) or at a restored copy. It is
    /// read-only apart from the schema check the host performs, so it is safe to re-run.
    /// </para>
    ///
    /// <para>
    /// <b>Before/after workflow.</b> Run it, keep the JSON it writes, make the change, run it again with
    /// <c>COPILOTPERF_BASELINE</c> pointing at the first file: it prints a per-step delta and fails the run
    /// if the total regressed by more than the tolerance. That is the loop this test is for.
    /// </para>
    /// </summary>
    public class CopilotAdoptionPerfTest : BaseStressTest
    {
        private const int DefaultRepeats = 3;

        /// <summary>
        /// Windows the page offers. 28 is the default the customer was on, but the window drives how much
        /// of copilot_chats each query reads, so a fix that only helps one window is not a fix.
        /// </summary>
        private static readonly int[] DefaultWindows = { 7, 28, 90, 180 };

        /// <summary>
        /// A step this close to the query timeout almost certainly failed rather than completed. Kept as a
        /// fraction so it tracks <see cref="CopilotAdoptionOptions.QueryTimeoutSecs"/> if that changes.
        /// </summary>
        private const double TimeoutSuspicionFraction = 0.95;

        protected override StressTestResult Execute()
        {
            Console.WriteLine("\n=== Copilot Adoption Performance Test ===\n");

            var connectionString = ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("This test measures real queries and needs a connection string on the command line.");
                Console.ResetColor();
                return new StressTestResult
                {
                    Success = false,
                    Message = "No connection string provided - CopilotAdoptionPerfTest requires SQL.",
                };
            }

            int repeats = GetIntegerInput(
                "Timed runs per window (medians reported; a cold run is done first and discarded)",
                DefaultRepeats, 1, 25, "COPILOTPERF_REPEATS");

            bool allWindows = GetBooleanInput(
                "Measure every window (7/28/90/180)? N measures 28 only",
                true, "COPILOTPERF_ALLWINDOWS");

            int concurrent = GetIntegerInput(
                "Simultaneous callers per measurement (proves the shared-analysis cache dedupes; 1 = off)",
                1, 1, 50, "COPILOTPERF_CONCURRENT");

            var windows = allWindows ? DefaultWindows : new[] { 28 };

            _memoryMonitor.Start();
            var overallWatch = Stopwatch.StartNew();
            var runs = new List<WindowResult>();
            long stepsMeasured = 0;

            foreach (var windowDays in windows)
            {
                var result = MeasureWindow(connectionString, windowDays, repeats, concurrent);
                if (result == null)
                {
                    return new StressTestResult
                    {
                        Success = false,
                        Message = $"The analysis threw for the {windowDays}-day window - see the exception above.",
                        Duration = overallWatch.Elapsed,
                    };
                }

                runs.Add(result);
                stepsMeasured += result.Steps.Count;
                _memoryMonitor.UpdatePeak();
            }

            overallWatch.Stop();
            _memoryMonitor.Stop();

            PrintReport(runs);

            var outputPath = WriteJson(runs);
            var regression = CompareToBaseline(runs);

            var slowest = runs.OrderByDescending(r => r.TotalMedianMs).FirstOrDefault();
            var anyFailed = runs.Any(r => r.Steps.Any(s => s.Failed || s.LooksLikeTimeout));

            var message = slowest == null
                ? "No windows measured."
                : $"Slowest window: {slowest.WindowDays}d at {slowest.TotalMedianMs:N0} ms"
                  + $" (worst step {slowest.SlowestStepName} at {slowest.SlowestStepMs:N0} ms)."
                  + (anyFailed ? " WARNING: at least one step failed or hit the query timeout - see above." : string.Empty)
                  + (outputPath != null ? $" Results: {outputPath}" : string.Empty)
                  + (regression != null ? $" {regression}" : string.Empty);

            return new StressTestResult
            {
                // A run whose steps failed is not a pass, however fast it was: the whole point of #360 was
                // that a failed query rendered as a confident, empty answer.
                Success = !anyFailed && regression == null,
                Message = message,
                ItemsProcessed = stepsMeasured,
                Duration = overallWatch.Elapsed,
                InitialMemoryBytes = _memoryMonitor.InitialMemoryBytes,
                PeakMemoryBytes = _memoryMonitor.PeakMemoryBytes,
                FinalMemoryBytes = _memoryMonitor.CurrentMemoryBytes,
            };
        }

        /// <summary>
        /// Runs one window: a discarded cold run, then <paramref name="repeats"/> timed runs, reported as
        /// medians. Medians rather than means because a single scheduling hiccup on a shared dev box
        /// otherwise dominates the number, which is also why the repo's SQL benchmarks are medians.
        /// </summary>
        private WindowResult MeasureWindow(string connectionString, int windowDays, int repeats, int concurrent)
        {
            Console.WriteLine($"--- {windowDays}-day window ---");

            var options = CopilotAdoptionOptions.Default;
            options.WindowDays = windowDays;

            Func<AnalyticsEntitiesContext> contextFactory =
                () => new AnalyticsEntitiesContext(connectionString, true, false);

            try
            {
                // Cold run, discarded: it pays for the EF model build, the connection pool and a cold
                // buffer pool, none of which a user on a warm site experiences.
                Console.Write("  cold run (discarded)... ");
                var cold = RunOnce(options, contextFactory, concurrent);
                Console.WriteLine($"{cold.TotalMs:N0} ms");

                var timed = new List<CopilotAdoptionDiagnostics>();
                for (var i = 1; i <= repeats; i++)
                {
                    Console.Write($"  run {i}/{repeats}... ");
                    var diagnostics = RunOnce(options, contextFactory, concurrent);
                    timed.Add(diagnostics);
                    Console.WriteLine($"{diagnostics.TotalMs:N0} ms");
                }

                return Summarise(windowDays, timed);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAILED: {ex.Message}");
                Console.ResetColor();
                return null;
            }
        }

        /// <summary>
        /// One measured analysis. When <paramref name="concurrent"/> is above 1 the same analysis is asked
        /// for by that many callers at once and the FIRST result is timed, reproducing several people
        /// opening the page together. The web layer caches a shared Task so they should cost the same as
        /// one - if this number scales with the caller count, that de-duplication has broken.
        /// </summary>
        private static CopilotAdoptionDiagnostics RunOnce(
            CopilotAdoptionOptions options,
            Func<AnalyticsEntitiesContext> contextFactory,
            int concurrent)
        {
            var watch = Stopwatch.StartNew();

            if (concurrent <= 1)
            {
                var single = new CopilotAdoptionService(options, contextFactory)
                    .AnalyseAsync(null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                watch.Stop();
                return WithTotal(single, watch.ElapsedMilliseconds);
            }

            var tasks = Enumerable.Range(0, concurrent)
                .Select(_ => new CopilotAdoptionService(options, contextFactory)
                    .AnalyseAsync(null, CancellationToken.None))
                .ToArray();

            Task.WaitAll(tasks);
            watch.Stop();

            return WithTotal(tasks[0].GetAwaiter().GetResult(), watch.ElapsedMilliseconds);
        }

        /// <summary>
        /// The service records per-step times; the wall clock around the whole call is measured here so the
        /// total includes anything the steps do not (model materialisation, serialisation of results).
        /// </summary>
        private static CopilotAdoptionDiagnostics WithTotal(CopilotAdoptionAnalysis analysis, long totalMs)
        {
            var diagnostics = analysis?.Summary?.Diagnostics ?? new CopilotAdoptionDiagnostics();
            diagnostics.TotalMs = totalMs;
            return diagnostics;
        }

        private static WindowResult Summarise(int windowDays, List<CopilotAdoptionDiagnostics> runs)
        {
            var timeoutMs = CopilotAdoptionService.QueryTimeoutSecs * 1000L * TimeoutSuspicionFraction;

            var steps = runs
                .SelectMany(r => r.Steps)
                .GroupBy(s => s.Step, StringComparer.Ordinal)
                .Select(g => new StepResult
                {
                    Step = g.Key,
                    MedianMs = Median(g.Select(s => s.DurationMs)),
                    MaxMs = g.Max(s => s.DurationMs),
                    Failed = g.Any(s => s.Failed),
                    LooksLikeTimeout = g.Any(s => s.DurationMs >= timeoutMs),
                })
                .OrderByDescending(s => s.MedianMs)
                .ToList();

            var slowest = steps.FirstOrDefault();

            return new WindowResult
            {
                WindowDays = windowDays,
                Runs = runs.Count,
                TotalMedianMs = Median(runs.Select(r => r.TotalMs)),
                TotalMaxMs = runs.Max(r => r.TotalMs),
                QueryTimeoutSecs = CopilotAdoptionService.QueryTimeoutSecs,
                Steps = steps,
                SlowestStepName = slowest?.Step,
                SlowestStepMs = slowest?.MedianMs ?? 0,
            };
        }

        private static long Median(IEnumerable<long> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            return sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        }

        private static void PrintReport(List<WindowResult> runs)
        {
            Console.WriteLine("\n=== Copilot Adoption timings (medians) ===\n");

            foreach (var window in runs)
            {
                Console.WriteLine($"{window.WindowDays}-day window - total {window.TotalMedianMs:N0} ms"
                                  + $" (worst run {window.TotalMaxMs:N0} ms, {window.Runs} run(s))");
                Console.WriteLine("  step                        median ms      max ms   share");
                Console.WriteLine("  ------------------------  -----------  ----------  ------");

                foreach (var step in window.Steps)
                {
                    var share = window.TotalMedianMs > 0
                        ? (100.0 * step.MedianMs / window.TotalMedianMs)
                        : 0;

                    var flag = step.Failed ? "  FAILED"
                             : step.LooksLikeTimeout ? "  ~TIMEOUT"
                             : string.Empty;

                    if (!string.IsNullOrEmpty(flag)) Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  {step.Step,-24}  {step.MedianMs,11:N0}  {step.MaxMs,10:N0}  {share,5:F1}%{flag}");
                    if (!string.IsNullOrEmpty(flag)) Console.ResetColor();
                }

                Console.WriteLine();
            }

            var worst = runs.OrderByDescending(r => r.TotalMedianMs).FirstOrDefault();
            if (worst != null)
            {
                Console.WriteLine($"The steps run sequentially, so the total is their sum: optimise"
                                  + $" '{worst.SlowestStepName}' first ({worst.SlowestStepMs:N0} ms at"
                                  + $" {worst.WindowDays} days).\n");
            }

            if (runs.Any(r => r.Steps.Any(s => s.LooksLikeTimeout || s.Failed)))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("At least one step FAILED or sat at the query timeout. Those steps returned"
                                  + " nothing, so the totals above understate the real work - fix the failure"
                                  + " before reading these as a benchmark.\n");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Writes the run to JSON so a later run can diff against it. Contains only step names, durations
        /// and counts, so it is safe to attach to an issue or PR.
        /// </summary>
        private static string WriteJson(List<WindowResult> runs)
        {
            try
            {
                var dir = Environment.GetEnvironmentVariable("COPILOTPERF_OUTDIR");
                if (string.IsNullOrWhiteSpace(dir)) dir = Path.GetTempPath();
                Directory.CreateDirectory(dir);

                var path = Path.Combine(
                    dir, $"copilot-adoption-perf-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

                File.WriteAllText(path, JsonConvert.SerializeObject(runs, Formatting.Indented));
                Console.WriteLine($"Results written to {path}");
                return path;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(could not write results JSON: {ex.Message})");
                return null;
            }
        }

        /// <summary>
        /// Diffs against a previous run when <c>COPILOTPERF_BASELINE</c> names one. Returns null when the
        /// run is acceptable, or a description of the regression - which the caller turns into a failure,
        /// so this can gate a change rather than just describe it.
        /// </summary>
        private static string CompareToBaseline(List<WindowResult> runs)
        {
            var baselinePath = Environment.GetEnvironmentVariable("COPILOTPERF_BASELINE");
            if (string.IsNullOrWhiteSpace(baselinePath)) return null;

            if (!File.Exists(baselinePath))
            {
                Console.WriteLine($"(baseline '{baselinePath}' not found - skipping comparison)");
                return null;
            }

            var tolerance = 0.10;
            var toleranceEnv = Environment.GetEnvironmentVariable("COPILOTPERF_TOLERANCE");
            if (!string.IsNullOrWhiteSpace(toleranceEnv)
                && double.TryParse(toleranceEnv, out var parsed) && parsed > 0)
            {
                tolerance = parsed;
            }

            List<WindowResult> baseline;
            try
            {
                baseline = JsonConvert.DeserializeObject<List<WindowResult>>(File.ReadAllText(baselinePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(could not read baseline: {ex.Message})");
                return null;
            }

            if (baseline == null || baseline.Count == 0) return null;

            Console.WriteLine($"=== Comparison against {Path.GetFileName(baselinePath)} ===\n");
            Console.WriteLine("  window       before ms      after ms     change");
            Console.WriteLine("  --------  ------------  ------------  ---------");

            string regression = null;

            foreach (var after in runs)
            {
                var before = baseline.FirstOrDefault(b => b.WindowDays == after.WindowDays);
                if (before == null) continue;

                var delta = before.TotalMedianMs == 0
                    ? 0
                    : (double)(after.TotalMedianMs - before.TotalMedianMs) / before.TotalMedianMs;

                var worse = delta > tolerance;
                if (worse) Console.ForegroundColor = ConsoleColor.Red;
                else if (delta < -tolerance) Console.ForegroundColor = ConsoleColor.Green;

                Console.WriteLine($"  {after.WindowDays,6}d  {before.TotalMedianMs,12:N0}  "
                                  + $"{after.TotalMedianMs,12:N0}  {delta * 100,8:F1}%");
                Console.ResetColor();

                if (worse)
                {
                    regression = $"REGRESSION: the {after.WindowDays}-day window went from "
                               + $"{before.TotalMedianMs:N0} ms to {after.TotalMedianMs:N0} ms "
                               + $"({delta * 100:F1}%, tolerance {tolerance * 100:F0}%).";
                }
            }

            Console.WriteLine();

            // Per-step deltas for the default window, which is where a change usually shows up first.
            var afterDefault = runs.FirstOrDefault(r => r.WindowDays == 28);
            var beforeDefault = baseline.FirstOrDefault(r => r.WindowDays == 28);
            if (afterDefault != null && beforeDefault != null)
            {
                Console.WriteLine("  per-step, 28-day window:");
                foreach (var step in afterDefault.Steps)
                {
                    var was = beforeDefault.Steps.FirstOrDefault(s => s.Step == step.Step);
                    if (was == null) continue;

                    var delta = step.MedianMs - was.MedianMs;
                    var sign = delta > 0 ? "+" : string.Empty;
                    Console.WriteLine($"    {step.Step,-24}  {was.MedianMs,9:N0} -> {step.MedianMs,9:N0} ms"
                                      + $"  ({sign}{delta:N0})");
                }
                Console.WriteLine();
            }

            if (regression != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(regression);
                Console.ResetColor();
            }

            return regression;
        }

        /// <summary>One measured reporting window. Serialised as the before/after baseline.</summary>
        private class WindowResult
        {
            public int WindowDays { get; set; }
            public int Runs { get; set; }
            public long TotalMedianMs { get; set; }
            public long TotalMaxMs { get; set; }
            public int QueryTimeoutSecs { get; set; }
            public string SlowestStepName { get; set; }
            public long SlowestStepMs { get; set; }
            public List<StepResult> Steps { get; set; } = new List<StepResult>();
        }

        /// <summary>One step of the analysis, aggregated across the timed runs.</summary>
        private class StepResult
        {
            public string Step { get; set; }
            public long MedianMs { get; set; }
            public long MaxMs { get; set; }
            public bool Failed { get; set; }

            /// <summary>
            /// At or above <see cref="TimeoutSuspicionFraction"/> of the query timeout. Such a step almost
            /// certainly failed and returned nothing rather than genuinely taking that long, so treating it
            /// as a timing would flatter the result.
            /// </summary>
            public bool LooksLikeTimeout { get; set; }
        }
    }
}
