using Common.Entities.Config;
using DataUtils;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
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
        private const int Unreported = 0;
        private const int Reporting = 1;
        private const int Reported = 2;
        private static readonly ConditionalWeakTable<Exception, StrongBox<int>> ReportStates =
            new ConditionalWeakTable<Exception, StrongBox<int>>();

        /// <summary>
        /// Records that this exception has already been sent to Application Insights, so another
        /// telemetry path does not report it a second time.
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
                Interlocked.Exchange(ref ReportState(ex).Value, Reported);
            }
            catch (Exception)
            {
                // Telemetry bookkeeping must not make the original failure worse.
            }
        }

        internal static bool TryClaim(Exception ex)
        {
            if (ex == null) return false;

            try
            {
                if (AnyClaimedOrReported(ex)) return false;
                return Interlocked.CompareExchange(
                           ref ReportState(ex).Value,
                           Reporting,
                           Unreported)
                       == Unreported;
            }
            catch (Exception)
            {
                // If the dedup marker itself fails, prefer a possible duplicate over silent loss.
                return true;
            }
        }

        internal static void ReleaseClaim(Exception ex)
        {
            if (ex == null) return;

            try
            {
                Interlocked.CompareExchange(
                    ref ReportState(ex).Value,
                    Unreported,
                    Reporting);
            }
            catch (Exception)
            {
                // Best effort only.
            }
        }

        private static bool AnyClaimedOrReported(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (Volatile.Read(ref ReportState(current).Value) != Unreported) return true;
            }

            return false;
        }

        private static StrongBox<int> ReportState(Exception ex) =>
            ReportStates.GetValue(ex, _ => new StrongBox<int>(Unreported));

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
            Report(ex, context, ctx => new AnalyticsLogger(AppInsightsConnectionString.Value, ctx));
        }

        private static readonly Lazy<string> AppInsightsConnectionString =
            new Lazy<string>(() => new AppConfig().AppInsightsConnectionString);

        public static bool IsRoutineNotFound(Exception ex) =>
            ex is BadHttpRequestException http && http.StatusCode == StatusCodes.Status404NotFound;

        internal static void Report(
            Exception ex,
            string context,
            Func<string, AnalyticsLogger> loggerFactory)
        {
            if (!TryClaim(ex)) return;

            try
            {
                var logger = loggerFactory(context);
                CopilotAdoptionExceptionCorrelation.TryGetRunId(ex, out var runId);
                var errorId = WebErrorReference.For(ex);
                var properties = new System.Collections.Generic.Dictionary<string, string>
                {
                    { WebErrorReference.PropertyName, errorId },
                };
                if (!string.IsNullOrEmpty(runId)) properties["RunId"] = runId;
                var operationId = string.IsNullOrEmpty(runId) ? errorId : runId;

                logger.TrackException(ex, properties, operationId);

                // The exception telemetry carries the type and stack, but a searchable line of text is
                // what an operator actually greps for when a customer reports "it just returns a 500".
                var correlation = (string.IsNullOrEmpty(runId) ? string.Empty : $" RunId {runId}.") + $" ErrorId {errorId}.";
                using (logger.BeginOperationScope(operationId))
                {
                    logger.LogError($"Unhandled web request error in {context}.{correlation} {ex.GetBaseException().Message}");
                }

                MarkReported(ex);
            }
            catch (Exception)
            {
                ReleaseClaim(ex);
                // Deliberately swallowed - see above.
            }
        }
    }

    /// <summary>
    /// One reference per exception, shared by generic API replies, HTML and telemetry.
    /// </summary>
    public static class WebErrorReference
    {
        public const string PropertyName = "ErrorId";
        public const string UnexpectedCode = "unexpected";
        private static readonly ConditionalWeakTable<Exception, string> Ids = new ConditionalWeakTable<Exception, string>();

        public static string For(Exception ex) => ex == null
            ? Guid.NewGuid().ToString("D")
            : Ids.GetValue(ex, _ => Guid.NewGuid().ToString("D"));
    }

    /// <summary>
    /// Core equivalent of the stable Web API handler and MVC reporter. API failures receive only a
    /// code/reference; MVC failures continue to Home/Error with the original exception feature.
    /// </summary>
    public class AnalyticsWebExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, System.Threading.CancellationToken cancellationToken)
        {
            if (exception == null) return false;

            // A client that navigates away mid-request cancels it. That is normal browser behaviour, not
            // a fault, and reporting it would bury the real errors this exists to surface.
            if (exception is OperationCanceledException || WebExceptionTelemetry.IsRoutineNotFound(exception)) return false;

            var route = httpContext?.Request?.Path.Value;
            var where = string.IsNullOrEmpty(route) ? "Web" : $"Web {route}";

            WebExceptionTelemetry.Report(exception, where);

            if (!AuthRequestRules.IsApiRequest(httpContext.Request)) return false;
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            httpContext.Response.Headers.CacheControl = "no-store, private";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                code = WebErrorReference.UnexpectedCode,
                errorId = WebErrorReference.For(exception),
            }, cancellationToken);
            return true;
        }
    }
}