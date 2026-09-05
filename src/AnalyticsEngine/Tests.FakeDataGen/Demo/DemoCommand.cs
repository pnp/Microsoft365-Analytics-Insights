using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Tests.FakeDataGen.Demo
{
    internal static class DemoCommand
    {
        public static int Run(string[] args)
        {
            FileStream summaryFile = null;
            try
            {
                var options = DemoOptions.Parse(args, DateTime.UtcNow);
                if (options.Help) { Console.WriteLine(DemoOptions.HelpText); return 0; }
                if (options.Output != null)
                    summaryFile = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var summary = NewSummary(options);
                var stopwatch = Stopwatch.StartNew();
                using (var cancellation = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler cancel = (sender, e) => { e.Cancel = true; cancellation.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try
                    {
                        Console.WriteLine($"Synthetic demo: {options.Users:N0} users, {options.Skus} SKUs, {options.Days} days, seed {options.Seed}, as-of {options.AsOf:yyyy-MM-dd}.");
                        Console.WriteLine("Current assignments only: no historical seat ownership or purchased capacity is inferred.");
                        if (options.Preview)
                        {
                            using (var sink = new CountingDemoSink(summary))
                                new DemoGenerator(options, cancellation.Token).Generate(sink, summary, Console.WriteLine);
                            summary.Status = "Preview";
                        }
                        else
                        {
                            using (var database = new SqlDemoDatabase(options, cancellation.Token))
                            {
                                database.Open(Console.WriteLine);
                                if (database.AlreadyComplete) summary.Status = "AlreadyComplete";
                                else
                                {
                                    using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                                        new DemoGenerator(options, cancellation.Token).Generate(sink, summary, Console.WriteLine);
                                    database.ValidateAndComplete(summary, Console.WriteLine);
                                    summary.Status = "Complete";
                                }
                            }
                        }
                    }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                stopwatch.Stop();
                summary.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                using (var process = Process.GetCurrentProcess()) summary.PeakWorkingSetBytes = process.PeakWorkingSet64;
                Console.WriteLine($"{summary.Status}: {summary.TotalRows:N0} source rows; {summary.CompletedProfileWeeks} complete profiling weeks; {stopwatch.Elapsed.TotalSeconds:F1}s.");
                Console.WriteLine("Cohorts: " + string.Join(", ", summary.Cohorts.Select(p => p.Key + "=" + p.Value)));
                Console.WriteLine("Copilot audit bands (latest 28 audit days, real scorer): " + string.Join(", ", summary.AdoptionBands.Select(p => p.Key + "=" + p.Value)));
                Console.WriteLine("Fingerprint: " + summary.Fingerprint);
                if (options.Preview) Console.WriteLine("Preview only: no database or weekly profiles were written.");
                if (summaryFile != null)
                {
                    using (var writer = new StreamWriter(summaryFile)) writer.Write(JsonConvert.SerializeObject(summary, Formatting.Indented));
                    summaryFile = null;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Demo generation FAILED: " + ex.Message);
                Console.Error.WriteLine("Targets are never reset automatically. Incomplete or changed targets are refused; an already completed target remains a read-only no-op.");
                if (summaryFile != null) Console.Error.WriteLine("No successful JSON summary was produced; the reserved output file may be empty or incomplete.");
                return ex is ArgumentException ? 2 : ex is OperationCanceledException ? 130 : 1;
            }
            finally { summaryFile?.Dispose(); }
        }

        internal static DemoSummary NewSummary(DemoOptions options) => new DemoSummary
        {
            Fingerprint = options.Fingerprint, Users = options.Users, Skus = options.Skus, Seed = options.Seed,
            AsOf = options.AsOf, FirstActivityDate = options.Start, LastReportDate = options.ReportEnd
        };
    }
}
