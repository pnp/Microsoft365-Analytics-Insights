using Common.Entities.Config;
using DataUtils;
using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Web;
using System.Net.Http;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Results;
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

        /// <summary>
        /// Read once: this runs for every failed request, and a full <see cref="AppConfig"/> per failure was the
        /// most expensive part of reporting one. A configuration change recycles the application anyway.
        /// </summary>
        private static readonly Lazy<string> AppInsightsConnectionString =
            new Lazy<string>(() => new AppConfig().AppInsightsConnectionString);

        /// <summary>
        /// A request for something that does not exist - a scanner probing for <c>/wp-login.php</c>, a stale
        /// bookmark. That is the caller's mistake, not a fault in this application, and anyone on the internet can
        /// produce them by the thousand, so they are not sent to Application Insights as exceptions.
        /// </summary>
        public static bool IsRoutineNotFound(Exception ex)
            => ex is HttpException http && http.GetHttpCode() == (int)HttpStatusCode.NotFound;

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

                // The id the caller is shown (WebApiExceptionHandler), so an admin quoting it finds this exception and
                // its error trace. It is the operation id too, unless a Copilot adoption run already owns that.
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
    /// The reference a remote caller gets in place of an exception (<c>{ code: "unexpected", errorId }</c>), also stamped
    /// on that exception's telemetry. One id per exception instance for its whole life, so a cached failure that is
    /// rethrown to later requests (<c>SharedComputation</c>) hands every one of them the id the original was logged under.
    /// </summary>
    public static class WebErrorReference
    {
        /// <summary>The custom property on exception telemetry; the same value is in the error trace's text.</summary>
        public const string PropertyName = "ErrorId";

        /// <summary>The <c>code</c> of the generic 500 body. The SPA words it (<c>errors.http.unexpected</c>).</summary>
        public const string UnexpectedCode = "unexpected";

        private static readonly ConditionalWeakTable<Exception, string> Ids = new ConditionalWeakTable<Exception, string>();

        public static string For(Exception ex)
        {
            return ex == null
                ? Guid.NewGuid().ToString("D")
                : Ids.GetValue(ex, _ => Guid.NewGuid().ToString("D"));
        }
    }

    /// <summary>
    /// Answers an exception that escaped a Web API controller with a generic 500 - <c>{ code, errorId }</c> - and nothing
    /// from the exception. Registered in <see cref="WebApiConfig"/>.
    /// </summary>
    /// <remarks>
    /// Without it the body was empty for a remote caller (<c>IncludeErrorDetailPolicy.LocalOnly</c>), so nothing tied what
    /// the admin saw to the exception <see cref="AnalyticsWebApiExceptionLogger"/> had sent to Application Insights. A
    /// cancelled request is left to Web API's default: the client has gone, and there is nobody to tell.
    /// </remarks>
    public class AnalyticsWebApiExceptionHandler : ExceptionHandler
    {
        public override void Handle(ExceptionHandlerContext context)
        {
            var exception = context?.Exception;
            if (exception == null || exception is OperationCanceledException || context.Request == null) return;

            var body = new { code = WebErrorReference.UnexpectedCode, errorId = WebErrorReference.For(exception) };
            context.Result = new ResponseMessageResult(context.Request.CreateResponse(HttpStatusCode.InternalServerError, body));
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

    /// <summary>
    /// Reports an exception from an MVC action. Registered in <see cref="FilterConfig"/>.
    /// </summary>
    /// <remarks>
    /// MVC failures used to reach Application Insights only through <c>Application_Error</c>. With custom errors on
    /// for remote callers (the Release configuration), <see cref="System.Web.Mvc.HandleErrorAttribute"/> handles the
    /// exception itself to render the error page, so <c>Application_Error</c> never sees it - this keeps those
    /// failures visible. An exception both report is sent once (<see cref="WebExceptionTelemetry.TryClaim"/>).
    /// </remarks>
    public sealed class AnalyticsMvcExceptionReporter : System.Web.Mvc.IExceptionFilter
    {
        public void OnException(System.Web.Mvc.ExceptionContext filterContext)
        {
            var exception = filterContext?.Exception;
            if (exception == null || WebExceptionTelemetry.IsRoutineNotFound(exception)) return;

            var path = filterContext.HttpContext?.Request?.Path;
            WebExceptionTelemetry.Report(exception, string.IsNullOrEmpty(path) ? "Mvc" : $"Mvc {path}");
        }
    }
}
