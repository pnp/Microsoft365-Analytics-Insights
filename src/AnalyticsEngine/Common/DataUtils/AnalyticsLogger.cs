using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

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
            Console.WriteLine($"{DateTime.Now.ToString("HH-mm-ss:ff")}: {sayWut}");

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

        public override void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = string.Empty;
            if (formatter != null) message += formatter(state, exception);

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
            FinishedImportCycle
        }
    }
}
