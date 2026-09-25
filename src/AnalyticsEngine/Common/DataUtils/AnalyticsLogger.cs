using DataUtils.Health;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DataUtils
{
    public abstract class BaseAnalyticsLogger : ILogger
    {
        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel == LogLevel.Information || logLevel == LogLevel.Warning || logLevel == LogLevel.Error || logLevel == LogLevel.Critical;
        }

        public virtual IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public abstract void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter);
    }

    /// <summary>
    /// Unified console & AppInsights tracer
    /// </summary>
    public class AnalyticsLogger : BaseAnalyticsLogger
    {
        private const string ExceptionTelemetryTrackedKey = "DataUtils.AnalyticsLogger.ExceptionTelemetryTracked";
        private static readonly AsyncLocal<string> CurrentOperationId = new AsyncLocal<string>();

        private TelemetryClient AppInsights { get; set; }

        #region Constructors

        private AnalyticsLogger() : this(string.Empty, string.Empty)
        {
        }

        public AnalyticsLogger(TelemetryClient appInsights, string context)
        {
            AppInsights = appInsights;
            if (AppInsights != null && !string.IsNullOrEmpty(context))
            {
                AppInsights.Context.Operation.Name = context;
            }
        }

        public AnalyticsLogger(string appInsightsConnectionString, string context)
        {
            if (!string.IsNullOrEmpty(appInsightsConnectionString))
            {
                AppInsights = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration()
                {
                    ConnectionString = appInsightsConnectionString
                });

                if (!string.IsNullOrEmpty(context))
                {
                    AppInsights.Context.Operation.Name = context;
                }
            }
            else
            {
                Console.WriteLine("WARNING: No AppInsights connection string provided. AppInsights logging disabled.");
            }
        }

        public static AnalyticsLogger ConsoleOnlyTracer() { return new AnalyticsLogger(); }


        #endregion

        public override IDisposable BeginScope<TState>(TState state)
        {
            var operationId = TryGetOperationId(state);
            return string.IsNullOrEmpty(operationId)
                ? null
                : BeginOperationScope(operationId);
        }

        public IDisposable BeginOperationScope(string operationId)
        {
            var previous = CurrentOperationId.Value;
            CurrentOperationId.Value = operationId;
            return new OperationScope(() => CurrentOperationId.Value = previous);
        }

        public void TrackException(Exception ex)
        {
            TrackException(ex, null, null);
        }

        public void TrackException(
            Exception ex,
            IDictionary<string, string> properties,
            string operationId)
        {
            if (AppInsights != null && ex != null)
            {
                var safeDetails = ex as IExceptionTelemetryDetails;
                if (safeDetails != null && !TryMarkExceptionAsTracked(ex))
                {
                    return;
                }

                var telemetry = new ExceptionTelemetry(safeDetails?.ToTelemetryException() ?? ex);
                if (safeDetails != null && !string.IsNullOrEmpty(safeDetails.TelemetryProblemId))
                {
                    telemetry.ProblemId = safeDetails.TelemetryProblemId;
                }

                var effectiveOperationId = operationId ?? CurrentOperationId.Value;
                if (!string.IsNullOrEmpty(effectiveOperationId))
                {
                    telemetry.Context.Operation.Id = effectiveOperationId;
                }
                if (!string.IsNullOrEmpty(AppInsights.Context.Operation.Name))
                {
                    telemetry.Context.Operation.Name = AppInsights.Context.Operation.Name;
                }
                if (safeDetails?.TelemetryProperties != null)
                {
                    foreach (var property in safeDetails.TelemetryProperties)
                    {
                        telemetry.Properties[property.Key] = property.Value;
                    }
                }
                if (properties != null)
                {
                    foreach (var property in properties)
                    {
                        telemetry.Properties[property.Key] = property.Value;
                    }
                }

                AppInsights.TrackException(telemetry);
            }
        }

        void TrackTrace(string sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel severityLevel)
        {
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")}: {sayWut}");

            if (AppInsights != null)
            {
                var telemetry = new TraceTelemetry(sayWut, severityLevel);
                if (!string.IsNullOrEmpty(CurrentOperationId.Value))
                {
                    telemetry.Context.Operation.Id = CurrentOperationId.Value;
                }
                if (!string.IsNullOrEmpty(AppInsights.Context.Operation.Name))
                {
                    telemetry.Context.Operation.Name = AppInsights.Context.Operation.Name;
                }
                AppInsights.TrackTrace(telemetry);
            }
        }

        public void LogCritical(string sayWut)
        {
            TrackTrace(sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Critical);
        }
        public void LogInformation(string sayWut)
        {
            TrackTrace(sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Information);
        }
        public void LogDebug(string sayWut)
        {
            TrackTrace(sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Verbose);
        }

        public void LogError(string sayWut)
        {
            TrackTrace(sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Error);
        }
        public void LogWarning(string sayWut)
        {
            TrackTrace(sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Warning);
        }
        /// <summary>
        /// Track event with a default "context=X" value for X
        /// </summary>
        public void TrackEvent(AnalyticsEvent analyticsEvent, string defaultContextData)
        {
            var context = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(defaultContextData))
                context.Add("context", defaultContextData);
            TrackEvent(analyticsEvent, context);
        }

        public void TrackEvent(AnalyticsEvent analyticsEvent, Dictionary<string, string> context)
        {
            TrackEvent(analyticsEvent, context, null);
        }

        /// <summary>
        /// Track an event with optional numeric measurements.
        ///
        /// Measurements matter for anything performance-related: App Insights stores them as numbers in
        /// <c>customMeasurements</c>, so they can be averaged, percentiled and charted directly. The same
        /// value in <c>customDimensions</c> is a string, and every query against it needs a cast first.
        /// </summary>
        public void TrackEvent(AnalyticsEvent analyticsEvent, Dictionary<string, string> context, Dictionary<string, double> metrics)
        {
            TrackEvent(analyticsEvent, context, metrics, null, null);
        }

        /// <summary>
        /// Tracks an event with an explicit operation id and occurrence time.
        ///
        /// The id is applied to the event item, not to the shared client context, so concurrent callers
        /// cannot overwrite each other's correlation. The explicit timestamp preserves when an event
        /// happened when a non-blocking dispatcher submits it later.
        /// </summary>
        public void TrackEvent(
            AnalyticsEvent analyticsEvent,
            Dictionary<string, string> context,
            Dictionary<string, double> metrics,
            string operationId,
            DateTimeOffset? timestamp)
        {
            const string SEP = ";";
            var contextString = string.Empty;
            if (context != null && context.Count > 0)
            {
                foreach (var kv in context)
                {
                    contextString += $"{kv.Key}={kv.Value}{SEP}";
                }
                contextString = contextString.TrimEnd(SEP.ToCharArray());
            }
            Console.WriteLine($"New event '{Enum.GetName(typeof(AnalyticsEvent), analyticsEvent)}'; '{contextString}'.");

            if (AppInsights != null)
            {
                string eventName = Enum.GetName(typeof(AnalyticsEvent), analyticsEvent);
                var telemetry = new EventTelemetry(eventName);
                if (timestamp.HasValue)
                {
                    telemetry.Timestamp = timestamp.Value;
                }
                var effectiveOperationId = operationId ?? CurrentOperationId.Value;
                if (!string.IsNullOrEmpty(effectiveOperationId))
                {
                    telemetry.Context.Operation.Id = effectiveOperationId;
                }
                if (!string.IsNullOrEmpty(AppInsights.Context.Operation.Name))
                {
                    telemetry.Context.Operation.Name = AppInsights.Context.Operation.Name;
                }
                if (context != null)
                {
                    foreach (var item in context)
                    {
                        telemetry.Properties[item.Key] = item.Value;
                    }
                }
                if (metrics != null)
                {
                    foreach (var item in metrics)
                    {
                        telemetry.Metrics[item.Key] = item.Value;
                    }
                }

                AppInsights.TrackEvent(telemetry);
            }
        }

        /// <summary>
        /// Requests immediate transmission of buffered telemetry. The SDK call is non-blocking; callers
        /// that are shutting down must allow their own bounded drain interval afterwards.
        /// </summary>
        public void Flush()
        {
            AppInsights?.Flush();
        }

        /// <summary>
        /// Emit a structured <c>HealthCheck</c> event (issue #144 Appendix E) for a single dependency/capability.
        /// Uniform shape so runtime monitoring can alert with a couple of generic rules
        /// (e.g. "any HealthCheck Status == Unhealthy", "Credential DaysToExpiry &lt; N").
        /// </summary>
        /// <param name="component">Which dependency/capability was checked.</param>
        /// <param name="status">Result of the check.</param>
        /// <param name="detail">Optional free-text reason. MUST NOT contain secrets or customer data.</param>
        /// <param name="daysToExpiry">Optional; for <see cref="HealthComponent.Credential"/>, days until the credential expires.</param>
        /// <param name="reasonKey">Optional stable key the web portal can translate while keeping <paramref name="detail"/> as an English fallback.</param>
        public void TrackHealthCheck(HealthComponent component, HealthStatus status, string detail = null, int? daysToExpiry = null, string reasonKey = null)
        {
            var context = new Dictionary<string, string>
            {
                { "Component", component.ToString() },
                { "Status", status.ToString() },
            };
            if (!string.IsNullOrEmpty(detail))
            {
                context.Add("Detail", detail);
            }
            if (daysToExpiry.HasValue)
            {
                context.Add("DaysToExpiry", daysToExpiry.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(reasonKey))
            {
                context.Add("ReasonKey", reasonKey);
            }
            TrackEvent(AnalyticsEvent.HealthCheck, context);
        }

        /// <summary>
        /// Emit a structured <c>ImporterHeartbeat</c> event (issue #144 Appendix E), one per import job per cycle.
        /// Absence of this signal is the canonical "web-job died / crash-looping" detector.
        /// </summary>
        /// <param name="jobName">Importer job name, e.g. "Office365ActivityImporter" or "AppInsightsImporter".</param>
        /// <param name="lastCycleUtc">UTC timestamp of the cycle that just completed.</param>
        /// <param name="lastCycleDurationSeconds">Wall-clock duration of the cycle, in seconds.</param>
        public void TrackImporterHeartbeat(string jobName, DateTime lastCycleUtc, double lastCycleDurationSeconds)
        {
            var context = new Dictionary<string, string>
            {
                { "JobName", jobName ?? string.Empty },
                { "LastCycleUtc", lastCycleUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "LastCycleDurationSeconds", lastCycleDurationSeconds.ToString(CultureInfo.InvariantCulture) },
            };
            TrackEvent(AnalyticsEvent.ImporterHeartbeat, context);
        }

        /// <summary>
        /// Emit a structured <c>CopilotAdoptionAnalysis</c> event: one per completed analysis run, with
        /// per-step durations as measurements.
        ///
        /// The Copilot adoption analysis is the most expensive thing the web application does, and its
        /// failure mode is quiet - a query that exceeds the command timeout degrades to a warning on the
        /// page rather than an error, so a tenant can sit with a half-populated report indefinitely and
        /// never raise a ticket. One event per run, with durations, makes that visible and alertable.
        ///
        /// Durations go in <c>customMeasurements</c> rather than <c>customDimensions</c> so they can be
        /// percentiled and charted without casting. Step names are compile-time constants, so a saved
        /// query keeps working. Nothing here is derived from tenant data.
        /// </summary>
        /// <param name="windowDays">Reporting window the analysis was run for.</param>
        /// <param name="totalMs">Wall-clock duration of the whole analysis.</param>
        /// <param name="stepDurationsMs">Per-step wall-clock durations, keyed by step name.</param>
        /// <param name="warningCount">
        /// How many caveats the page shows. Informational only: most real tenants carry at least one caveat
        /// that is not a failure (for example the Cowork eligibility note), so this no longer decides
        /// <c>Outcome</c>.
        /// </param>
        /// <param name="timedOut">Whether any query was classified as a timeout - the signal that matters most.</param>
        /// <param name="slowestStep">Name of the slowest step, for triage without unpacking the measurements.</param>
        /// <param name="figuresIncomplete">Whether any dataset failed to load, so some figures are too low or missing.</param>
        /// <param name="incompleteReasonCount">How many datasets failed.</param>
        /// <param name="failedQueryCount">How many queries failed, of any kind.</param>
        /// <param name="timedOutQueryCount">How many of those were timeouts.</param>
        /// <param name="failedSteps">Comma-separated names of the failed steps (compile-time constants).</param>
        public void TrackCopilotAdoptionAnalysis(
            int windowDays,
            long totalMs,
            IDictionary<string, long> stepDurationsMs,
            int warningCount,
            bool timedOut,
            string slowestStep,
            string operationId = null,
            bool figuresIncomplete = false,
            int incompleteReasonCount = 0,
            int failedQueryCount = 0,
            int timedOutQueryCount = 0,
            string failedSteps = null)
        {
            // Degraded means something FAILED, not that the page carries a caveat. It used to be
            // "WarningCount > 0", which was true on nearly every real tenant - one caveat is added on every run
            // that sees any Cowork use - so an alert on it fired constantly and hid the runs that had actually
            // lost data.
            var degraded = figuresIncomplete || failedQueryCount > 0 || !string.IsNullOrEmpty(failedSteps);

            var context = new Dictionary<string, string>
            {
                { "WindowDays", windowDays.ToString(CultureInfo.InvariantCulture) },
                { "WarningCount", warningCount.ToString(CultureInfo.InvariantCulture) },
                { "TimedOut", timedOut ? "true" : "false" },
                { "Outcome", degraded ? "Degraded" : "Complete" },
                { "FiguresIncomplete", figuresIncomplete ? "true" : "false" },
            };
            if (!string.IsNullOrEmpty(slowestStep))
            {
                context.Add("SlowestStep", slowestStep);
            }
            if (!string.IsNullOrEmpty(failedSteps))
            {
                context.Add("FailedSteps", failedSteps);
            }

            var metrics = new Dictionary<string, double>
            {
                { "TotalMs", totalMs },
                { "FailedQueryCount", failedQueryCount },
                { "TimedOutQueryCount", timedOutQueryCount },
                { "IncompleteReasonCount", incompleteReasonCount },
            };
            if (stepDurationsMs != null)
            {
                foreach (var step in stepDurationsMs)
                {
                    metrics[step.Key + "Ms"] = step.Value;
                }
            }

            TrackEvent(AnalyticsEvent.CopilotAdoptionAnalysis, context, metrics, operationId, null);
        }

        public override void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = string.Empty;
            if (formatter != null) message += formatter(state, exception);

            // Capture the Exception explicitly. The default ILogger formatter drops the Exception object, so a
            // LogError(ex, "...") would otherwise send only the message text (as a trace) and lose the type /
            // stack trace entirely - it is NOT recorded as App Insights exception telemetry unless we do this.
            if (exception != null) TrackException(exception);

            if (logLevel == LogLevel.Debug)
            {
                LogInformation(message);
            }
            else if (logLevel == LogLevel.Information)
            {
                LogInformation(message);
            }
            else if (logLevel == LogLevel.Warning)
            {
                LogWarning(message);
            }
            else if (logLevel == LogLevel.Error)
            {
                LogError(message);
            }
            else if (logLevel == LogLevel.Critical)
            {
                LogCritical(message);
            }
            else
            {
                // Unknown log level
                LogInformation(message);
            }
        }

        public enum AnalyticsEvent
        {
            Unknown,
            AzureAIQuery,
            FinishedSectionImport,
            FinishedImportCycle,
            HealthCheck,
            ImporterHeartbeat,
            CopilotAdoptionAnalysis,
            CopilotAdoptionLifecycle,
            LicenceActivityLifecycle,
            UsageReportSaveStage
        }

        private static bool TryMarkExceptionAsTracked(Exception ex)
        {
            lock (ex)
            {
                if (ex.Data.Contains(ExceptionTelemetryTrackedKey))
                {
                    return false;
                }

                ex.Data[ExceptionTelemetryTrackedKey] = true;
                return true;
            }
        }

        private static string TryGetOperationId<TState>(TState state)
        {
            if (state is IEnumerable<KeyValuePair<string, object>> objectPairs)
            {
                foreach (var pair in objectPairs)
                {
                    if (IsOperationIdKey(pair.Key))
                    {
                        return pair.Value?.ToString();
                    }
                }
            }

            if (state is IEnumerable<KeyValuePair<string, string>> stringPairs)
            {
                foreach (var pair in stringPairs)
                {
                    if (IsOperationIdKey(pair.Key))
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }

        private static bool IsOperationIdKey(string key)
        {
            return string.Equals(key, "OperationId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "operation_Id", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class OperationScope : IDisposable
        {
            private Action _dispose;

            public OperationScope(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                var dispose = Interlocked.Exchange(ref _dispose, null);
                dispose?.Invoke();
            }
        }
    }
}
