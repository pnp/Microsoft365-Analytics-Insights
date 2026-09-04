using System;
using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using System.Web.Hosting;
using System.Web.Routing;
using System.Web.SessionState;
using Web.AnalyticsWeb.Models.CopilotAdoption;

namespace Web.AnalyticsWeb
{
    public class Global : System.Web.HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            GlobalConfiguration.Configure(WebApiConfig.Register);
        }
        protected void Application_PostAuthorizeRequest()
        {
            // ASP.NET session state is DISABLED deliberately - do not set this back to Required.
            //
            // Nothing in this solution reads or writes Session or TempData; sign-in is OIDC and is held
            // in an auth cookie, not in session. Requiring session state therefore bought nothing, and
            // cost a great deal: SessionStateModule takes an EXCLUSIVE per-session lock for the whole of
            // every request that requires session state, so every request carrying one browser's
            // ASP.NET_SessionId cookie is processed strictly one at a time.
            //
            // That is invisible on fast pages and fatal on slow ones. The Copilot Adoption SPA polls
            // three endpoints concurrently, and each poll parks for up to
            // CopilotAdoptionAPIController.FirstResponseBudget waiting for the shared analysis. Serialised,
            // the site can only answer ONE poll per budget while three arrive every budget, so the queue
            // grows without bound: response times climb by a whole budget per round until the client's
            // polling ceiling gives up and reports the analysis as "taking longer than expected" - which
            // is never what has actually gone wrong. A browser trace of the failure shows the signature
            // plainly: consecutive responses spaced exactly one budget apart, on separate connections,
            // with near-zero stalled time, so it is the server serialising and not the browser queueing.
            //
            // Reproduced with three concurrent requests to an async handler with a 2s budget:
            // Required + a declared Session_Start = 6,287ms with handler starts 2s apart (one at a time);
            // Disabled = 2,146ms with all three starting together.
            //
            // The declared (empty) Session_Start that used to live in this class mattered too: declaring
            // it is what made ASP.NET persist the new session and issue the cookie that the requests then
            // contended over. With no session there is nothing to start, so it has gone with it.
            HttpContext.Current.SetSessionStateBehavior(SessionStateBehavior.Disabled);
        }

        protected void Application_BeginRequest(object sender, EventArgs e)
        {
        }

        protected void Application_AuthenticateRequest(object sender, EventArgs e)
        {
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            // This handler existed but was empty, so every unhandled ASP.NET-pipeline error was dropped
            // silently. Web API failures are covered separately by AnalyticsWebApiExceptionLogger; this
            // catches the MVC and pipeline half.
            WebExceptionTelemetry.Report(Server?.GetLastError(), "AspNet pipeline");
        }

        /// <summary>
        /// Drains the Copilot adoption telemetry on shutdown, recording why the host stopped.
        /// </summary>
        /// <remarks>
        /// The <c>HostStopping</c> stage this emits is what separates "the worker was recycled out
        /// from under a run" from "the run hung" when diagnosing a stuck analysis - the distinction
        /// that identified issue #441. Losing it makes that class of fault materially harder to see.
        /// <para>
        /// <c>Application_End</c> and <c>HostingEnvironment.ShutdownReason</c> are System.Web only.
        /// A move to ASP.NET Core must re-home this onto
        /// <c>IHostApplicationLifetime.ApplicationStopping</c> (or an <c>IHostedService.StopAsync</c>),
        /// otherwise it simply stops being called and the stage silently disappears.
        /// </para>
        /// </remarks>
        protected void Application_End(object sender, EventArgs e)
        {
            CopilotAdoptionTelemetryHost.Shutdown(HostingEnvironment.ShutdownReason.ToString());
        }
    }
}