using Common.Entities.Config;
using DataUtils;
using System;
using System.Web.Http.ExceptionHandling;
using Web.AnalyticsWeb.Models.CopilotAdoption;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Sends unhandled web-tier exceptions to Application Insights.
    /// <para>
    /// The web project deliberately does not install the Application Insights SDK's HTTP modules, so
    /// nothing in the pipeline reports failures on its own: an unhandled exception became a bare 500 in
    /// the browser with no matching telemetry anywhere. That made a real customer fault
    /// (see issue #360) effectively undiagnosable - the only reason it was ever traced was that the
    /// Copilot adoption endpoint happened to log its own custom event.
    /// </para>
    /// <para>
    /// This routes through the same <see cref="AnalyticsLogger"/> the rest of the solution uses, so it
    /// needs no new package, no config change and no telemetry module.
    /// </para>
    /// </summary>
    public static class WebExceptionTelemetry
    {
        /// <summary>
        /// Marker written into <see cref="Exception.Data"/> once an exception has been reported.
        /// </summary>
        private const string ReportedKey = "AnalyticsWeb.TelemetryReported";

        /// <summary>
        /// Records that this exception has already been sent to Application Insights, so the Web API
        /// exception logger does not report it a second time.
        /// </summary>
        /// <remarks>
        /// The Copilot adoption analysis is SHARED: one background run can have many HTTP requests
        /// awaiting it. When it fails, awaiting each faulted task rethrows the very same exception
        /// instance to every one of them, so without this marker a single failure would be reported
        /// once by the analysis itself and then once more per waiting request - burying the real
        /// failure count.
        /// </remarks>
        public static void MarkReported(Exception ex)
        {
            if (ex == null) return;

            try
            {
                ex.Data[ReportedKey] = true;
            }
            catch (Exception)
            {
                // Some exception types expose a fixed IDictionary. Not worth failing over.
            }
        }

        private static bool AlreadyReported(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current.Data != null && current.Data.Contains(ReportedKey)) return true;
            }

            return false;
        }

        /// <summary>
        /// Reports an unhandled exception. Never throws: telemetry must not turn one failure into two,
        /// and this runs on paths that are already handling an error.
        /// </summary>
        /// <param name="ex">The exception to report. Ignored when null.</param>
        /// <param name="context">
        /// Where it came from, used as the App Insights operation name so web-tier failures can be told
        /// apart from web-job ones.
        /// </param>
        public static void Report(Exception ex, string context)
        {
            Report(ex, context, ctx =>
            {
                var config = new AppConfig();
                return new AnalyticsLogger(config.AppInsightsConnectionString, ctx);
            });
        }

        internal static void Report(
            Exception ex,
            string context,
            Func<string, AnalyticsLogger> loggerFactory)
        {
            if (ex == null || AlreadyReported(ex)) return;

            try
            {
                var logger = loggerFactory(context);
                CopilotAdoptionExceptionCorrelation.TryGetRunId(ex, out var runId);
                var properties = string.IsNullOrEmpty(runId)
                    ? null
                    : new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "RunId", runId },
                    };

                logger.TrackException(ex, properties, runId);

                // The exception telemetry carries the type and stack, but a searchable line of text is
                // what an operator actually greps for when a customer reports "it just returns a 500".
                var correlation = string.IsNullOrEmpty(runId) ? string.Empty : $" RunId {runId}.";
                logger.LogError($"Unhandled web request error in {context}.{correlation} {ex.GetBaseException().Message}");

                MarkReported(ex);
            }
            catch (Exception)
            {
                // Deliberately swallowed - see above.
            }
        }
    }

    /// <summary>
    /// Web API's hook for exceptions that escape a controller. Registered in <see cref="WebApiConfig"/>.
    /// <para>
    /// An <see cref="IExceptionLogger"/> observes only - it does not change the response - so adding it
    /// cannot alter what any existing caller sees.
    /// </para>
    /// </summary>
    public class AnalyticsWebApiExceptionLogger : ExceptionLogger
    {
        public override void Log(ExceptionLoggerContext context)
        {
            if (context?.Exception == null) return;

            // A client that navigates away mid-request cancels it. That is normal browser behaviour, not
            // a fault, and reporting it would bury the real errors this exists to surface.
            if (context.Exception is OperationCanceledException) return;

            var route = context.Request?.RequestUri?.AbsolutePath;
            var where = string.IsNullOrEmpty(route) ? "WebApi" : $"WebApi {route}";

            WebExceptionTelemetry.Report(context.Exception, where);
        }
    }
}
