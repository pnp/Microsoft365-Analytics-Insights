using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    public static class UsageReportSaveStageIds
    {
        public const string ReportPhaseStarted = "ReportPhaseStarted";
        public const string ReportPhaseCompleted = "ReportPhaseCompleted";
        public const string ReportPhaseFailed = "ReportPhaseFailed";
        public const string SaveStarted = "SaveStarted";
        public const string ExistingRowsLoaded = "ExistingRowsLoaded";
        public const string RowsProcessed = "RowsProcessed";
        public const string SaveBatch = "SaveBatch";
        public const string RowsReleased = "RowsReleased";
        public const string SaveCompleted = "SaveCompleted";
        public const string SaveFailed = "SaveFailed";
    }

    public interface IUsageReportSaveInstrumentation
    {
        bool IsEnabled { get; }
        void Track(UsageReportSaveTelemetryPoint point);
    }

    public sealed class UsageReportSaveTelemetryPoint
    {
        public string Stage { get; set; }
        public string ReportId { get; set; }
        public string LoaderType { get; set; }
        public string ReportTable { get; set; }
        public string Outcome { get; set; }
        public string ExceptionType { get; set; }
        public Dictionary<string, double> Metrics { get; } = new Dictionary<string, double>();
    }

    public sealed class NullUsageReportSaveInstrumentation : IUsageReportSaveInstrumentation
    {
        public static readonly NullUsageReportSaveInstrumentation Instance = new NullUsageReportSaveInstrumentation();
        private NullUsageReportSaveInstrumentation() { }
        public bool IsEnabled => false;
        public void Track(UsageReportSaveTelemetryPoint point) { }
    }

    public sealed class AnalyticsUsageReportSaveInstrumentation : IUsageReportSaveInstrumentation
    {
        public const string EnableEnvironmentVariable = "AI_USAGE_REPORT_SAVE_DIAGNOSTICS";
        private readonly AnalyticsLogger _logger;
        private readonly string _runId;

        private AnalyticsUsageReportSaveInstrumentation(AnalyticsLogger logger)
        {
            _logger = logger;
            _runId = Guid.NewGuid().ToString("N");
        }

        public bool IsEnabled => true;

        public static IUsageReportSaveInstrumentation ForLogger(ILogger logger)
        {
            var enabled = Environment.GetEnvironmentVariable(EnableEnvironmentVariable);
            if (!IsTruthy(enabled))
            {
                return NullUsageReportSaveInstrumentation.Instance;
            }

            var analyticsLogger = logger as AnalyticsLogger;
            return analyticsLogger == null
                ? (IUsageReportSaveInstrumentation)NullUsageReportSaveInstrumentation.Instance
                : new AnalyticsUsageReportSaveInstrumentation(analyticsLogger);
        }

        public void Track(UsageReportSaveTelemetryPoint point)
        {
            if (point == null) return;

            var dimensions = new Dictionary<string, string>
            {
                { "RunId", _runId },
                { "Stage", point.Stage ?? string.Empty },
                { "ReportId", point.ReportId ?? string.Empty },
                { "LoaderType", point.LoaderType ?? string.Empty },
                { "ReportTable", point.ReportTable ?? string.Empty },
                { "Outcome", point.Outcome ?? string.Empty },
            };
            if (!string.IsNullOrEmpty(point.ExceptionType))
            {
                dimensions["ExceptionType"] = point.ExceptionType;
            }

            _logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.UsageReportSaveStage, dimensions, point.Metrics);
        }

        private static bool IsTruthy(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class UsageReportSaveInstrumentationRuntime
    {
        private static int _activeDailyLoaders;

        public static int IncrementActiveDailyLoaders() => Interlocked.Increment(ref _activeDailyLoaders);
        public static int DecrementActiveDailyLoaders() => Interlocked.Decrement(ref _activeDailyLoaders);

        public static void AddRuntimeMetrics(Dictionary<string, double> metrics)
        {
            if (metrics == null) return;

            metrics["ManagedHeapBytes"] = GC.GetTotalMemory(false);
            metrics["Gen0Collections"] = GC.CollectionCount(0);
            metrics["Gen1Collections"] = GC.CollectionCount(1);
            metrics["Gen2Collections"] = GC.CollectionCount(2);
            using (var process = Process.GetCurrentProcess())
            {
                metrics["WorkingSetBytes"] = process.WorkingSet64;
            }
        }
    }
}


