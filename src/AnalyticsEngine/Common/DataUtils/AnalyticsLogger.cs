using DataUtils.Health;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DataUtils
{
    public abstract class BaseAnalyticsLogger : ILogger
    {
        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel == LogLevel.Information || logLevel == LogLevel.Warning || logLevel == LogLevel.Error || logLevel == LogLevel.Critical;
        }

        public IDisposable BeginScope<TState>(TState state)
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
        private TelemetryClient AppInsights { get; set; }

        #region Constructors

        private AnalyticsLogger() : this(string.Empty, string.Empty)
        {
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

        public void TrackException(Exception ex)
        {
            if (AppInsights != null)
            {
                AppInsights.TrackException(ex);
            }
        }

        void TrackTrace(string sayWut, Microsoft.ApplicationInsights.DataContracts.SeverityLevel severityLevel)
        {
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")}: {sayWut}");

            if (AppInsights != null)
            {
                AppInsights.TrackTrace(sayWut, severityLevel);
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
            const string SEP = ";";
            var contextString = string.Empty;
            if (context.Count > 0)
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
                if (context != null)
                {
                    AppInsights.TrackEvent(eventName, context);
                }
                else
                {
                    AppInsights.TrackEvent(eventName);
                }
            }
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
        public void TrackHealthCheck(HealthComponent component, HealthStatus status, string detail = null, int? daysToExpiry = null)
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
            ImporterHeartbeat
        }
    }
}
