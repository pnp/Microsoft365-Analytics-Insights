using Common.Entities.Config;
using Common.Entities.LicenceActivity;
using DataUtils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Web.AnalyticsWeb.Models.LicenceActivity
{
    internal sealed class LicenceActivityDiagnosticEvent
    {
        internal string RunId;
        internal string Stage;
        internal string ExceptionType;
        internal long ElapsedMs;
        internal long DurationMs;
        internal long Sequence;
        internal DateTimeOffset OccurredUtc;
        internal Dictionary<string, string> Dimensions;
        internal Dictionary<string, double> Measurements;
    }

    internal sealed class LicenceActivityRunDiagnostics : ILicenceActivityDiagnostics, IDisposable
    {
        private static readonly HashSet<string> Stages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Started", "ConnectionOpened", "CoverageStarted", "CoverageCompleted",
            "OverviewSqlStarted", "OverviewSqlCompleted", "UsersSqlStarted", "UsersSqlCompleted",
            "MaterialisationStarted", "MaterialisationCompleted", "ProjectionCompleted",
            "CachePublished", "Failed", "HostStopping", "CoverageWeek", "WorkloadEvidence"
        };
        private readonly string _runId;
        private readonly Func<LicenceActivityDiagnosticEvent, bool> _enqueue;
        private readonly Action _dispose;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _sequence;

        internal LicenceActivityRunDiagnostics(
            string runId, Func<LicenceActivityDiagnosticEvent, bool> enqueue, Action dispose = null)
        {
            _runId = runId;
            _enqueue = enqueue;
            _dispose = dispose;
        }

        public void Stage(string stage, long elapsedMs = 0)
        {
            if (!Stages.Contains(stage)) throw new ArgumentException("Unknown licence activity diagnostic stage.", nameof(stage));
            Send(stage, elapsedMs, null);
        }

        public void CoverageWeek(LicenceActivityCoverageWeek week)
        {
            RequireWorkload(week.Workload);
            Send("CoverageWeek", 0, null, new Dictionary<string, string>
            {
                { "Workload", week.Workload },
                { "FromUtc", Date(week.FromUtc) }, { "ToUtc", Date(week.ToUtc) },
                { "MissingDates", string.Join(",", week.MissingDates.Select(Date)) },
                { "Reason", !week.Settled ? "Unsettled" :
                    week.PresentDays != week.ExpectedDays ? "MissingReportDays" : "Complete" }
            }, new Dictionary<string, double>
            {
                { "ExpectedDays", week.ExpectedDays }, { "PresentDays", week.PresentDays },
                { "MissingDays", week.ExpectedDays - week.PresentDays },
                { "Settled", week.Settled ? 1 : 0 },
                { "Selected", week.Settled && week.PresentDays == week.ExpectedDays ? 1 : 0 }
            });
        }

        public void Evidence(LicenceActivityCoverage coverage, int usersWithRows, int usersWithActivity,
            int usersWithCompleteEvidence, bool groupFiltered)
        {
            RequireWorkload(coverage.Workload);
            if (!new[] { "available", "partial", "missingCoverage", "unmatchableIdentity", "notImported", "disabled" }
                .Contains(coverage.Status, StringComparer.Ordinal))
                throw new ArgumentException("Unknown licence activity coverage status.", nameof(coverage));
            if (!new[] { "microsoftGraphUsageReport", "microsoftGraphCopilotUsageReport",
                "copilotAudit", "copilotInteractions" }
                .Contains(coverage.Source, StringComparer.Ordinal))
                throw new ArgumentException("Unknown licence activity source.", nameof(coverage));
            Send("WorkloadEvidence", 0, null, new Dictionary<string, string>
            {
                { "Workload", coverage.Workload }, { "CoverageStatus", coverage.Status },
                { "Source", coverage.Source },
                { "GroupFiltered", groupFiltered ? "true" : "false" },
                { "Reason", coverage.Status == "partial" ? "IncompletePeriod" : coverage.Status }
            }, new Dictionary<string, double>
            {
                { "ExpectedSamples", coverage.ExpectedSamples }, { "ObservedSamples", coverage.ObservedSamples },
                { "UsersWithRows", usersWithRows }, { "UsersWithActivity", usersWithActivity },
                { "UsersWithCompleteEvidence", usersWithCompleteEvidence }
            });
        }

        private static string Date(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static void RequireWorkload(string workload)
        {
            if (!LicenceActivityQuery.Workloads.Contains(workload, StringComparer.Ordinal))
                throw new ArgumentException("Unknown licence activity workload.", nameof(workload));
        }

        internal bool Failed(Exception exception) => Send("Failed", 0, exception.GetBaseException().GetType().Name);

        private bool Send(string stage, long durationMs, string exceptionType,
            Dictionary<string, string> dimensions = null, Dictionary<string, double> measurements = null)
        {
            try
            {
                return _enqueue(new LicenceActivityDiagnosticEvent
                {
                    RunId = _runId, Stage = stage, ExceptionType = exceptionType,
                    Sequence = Interlocked.Increment(ref _sequence), ElapsedMs = _watch.ElapsedMilliseconds,
                    DurationMs = durationMs, OccurredUtc = DateTimeOffset.UtcNow,
                    Dimensions = dimensions, Measurements = measurements
                });
            }
            catch (Exception)
            {
                // An optional diagnostic adapter must not fail a report or its publication.
                return false;
            }
        }

        public void Dispose() => _dispose?.Invoke();
    }

    internal static class LicenceActivityTelemetry
    {
        private static readonly string InstanceId = Guid.NewGuid().ToString("N");
        private static readonly ConcurrentDictionary<string, LicenceActivityRunDiagnostics> Active =
            new ConcurrentDictionary<string, LicenceActivityRunDiagnostics>();
        private static readonly BlockingCollection<LicenceActivityDiagnosticEvent> Queue =
            new BlockingCollection<LicenceActivityDiagnosticEvent>(256);
        private static readonly Lazy<Thread> Worker = new Lazy<Thread>(() =>
        {
            var thread = new Thread(Drain) { IsBackground = true, Name = "LicenceActivityTelemetry" };
            thread.Start();
            return thread;
        });
        private static int _dropped;
        private static int _stopping;

        internal static LicenceActivityRunDiagnostics Start(string runId)
        {
            var run = new LicenceActivityRunDiagnostics(runId, Enqueue, () => Active.TryRemove(runId, out _));
            Active[runId] = run;
            run.Stage("Started");
            return run;
        }

        private static bool Enqueue(LicenceActivityDiagnosticEvent item)
        {
            try
            {
                if (Volatile.Read(ref _stopping) == 0)
                {
                    _ = Worker.Value;
                    if (Queue.TryAdd(item)) return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Shutdown can complete the collection between the stopping check and TryAdd.
            }
            Interlocked.Increment(ref _dropped);
            return false;
        }

        private static void Drain()
        {
            DrainEvents(Queue.GetConsumingEnumerable(),
                () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "LicenceActivity"),
                () => Thread.Sleep(1000));
        }

        internal static void DrainEvents(
            IEnumerable<LicenceActivityDiagnosticEvent> events,
            Func<AnalyticsLogger> loggerFactory,
            Action afterFlush)
        {
            AnalyticsLogger logger = null;
            try
            {
                foreach (var item in events)
                {
                    try
                    {
                        if (logger == null) logger = loggerFactory();
                        var dimensions = new Dictionary<string, string>
                        {
                            { "RunId", item.RunId }, { "InstanceId", InstanceId }, { "Stage", item.Stage }
                        };
                        if (item.ExceptionType != null) dimensions.Add("ExceptionType", item.ExceptionType);
                        if (item.Dimensions != null)
                            foreach (var pair in item.Dimensions) dimensions.Add(pair.Key, pair.Value);
                        var measurements = new Dictionary<string, double>
                        {
                            { "ElapsedMs", item.ElapsedMs }, { "DurationMs", item.DurationMs },
                            { "Sequence", item.Sequence }, { "DroppedEvents", Volatile.Read(ref _dropped) },
                            { "ManagedHeapBytes", GC.GetTotalMemory(false) }
                        };
                        if (item.Measurements != null)
                            foreach (var pair in item.Measurements) measurements.Add(pair.Key, pair.Value);
                        logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.LicenceActivityLifecycle, dimensions,
                            measurements, item.RunId, item.OccurredUtc);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _dropped);
                        if (item.Stage == "Failed")
                            ReportFailure(item.RunId, new InvalidOperationException("Diagnostic delivery failed."));
                    }
                }
            }
            finally
            {
                try
                {
                    logger?.Flush();
                    if (logger != null) afterFlush();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _dropped);
                    Console.WriteLine("Licence activity telemetry could not flush during shutdown.");
                }
            }
        }

        internal static void ReportFailure(string runId, Exception exception)
        {
            // Never give the general exception reporter SQL messages or request/filter values.
            WebExceptionTelemetry.Report(new InvalidOperationException(
                "Licence activity failed (" + exception.GetBaseException().GetType().Name + "). RunId " + runId),
                "LicenceActivity");
        }

        internal static void Shutdown()
        {
            foreach (var run in Active.Values) run.Stage("HostStopping");
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            Queue.CompleteAdding();
            if (Worker.IsValueCreated && Thread.CurrentThread != Worker.Value) Worker.Value.Join(TimeSpan.FromSeconds(2));
        }
    }
}
