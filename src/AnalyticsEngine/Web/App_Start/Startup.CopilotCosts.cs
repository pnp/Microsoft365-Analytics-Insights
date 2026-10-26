using Azure;
using Microsoft.Owin;
using Microsoft.Owin.Security.Cookies;
using Owin;
using System;
using System.Net.Http;
using System.IO;
using System.Collections.Specialized;
using System.Text;
using System.Web;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.CopilotCosts;
using Common.Entities.CopilotCosts;

namespace Web.AnalyticsWeb
{
    public partial class Startup
    {
        private static void ConfigureCostCallback(IAppBuilder app)
        {
            // Terminal branch BEFORE normal Katana OIDC: its code/state must never be processed as
            // ordinary portal sign-in, and the delegated reader must never replace the portal cookie.
            app.Map(CostConnectionRuntime.CallbackPath, branch => branch.Run(async context =>
            {
                context.Response.Headers.Set("Cache-Control", "no-store");
                context.Response.Headers.Set("Referrer-Policy", "no-referrer");
                var outcome = "invalidCallback";
                try
                {
                    if (!context.Request.IsSecure)
                        throw new CostConnectionException("invalidCallback");
                    var runtime = CostConnectionRuntime.Default.Value;
                    runtime.RequireAvailable();
                    if (context.Request.Method == "POST")
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        {
                            var form = await ReadCostAuthorizationFormAsync(context.Request, timeout.Token);
                            await runtime.Service.StageCallbackAsync(form["code"], form["state"], form["error"] != null,
                                context.Request.Cookies[CostConnectionRuntime.BindingCookie], timeout.Token);
                        }
                        // form_post keeps authorization codes out of URL/request telemetry. A same-site
                        // redirect restores the ordinary Lax portal cookie before checking the operator.
                        context.Response.Redirect(CostConnectionRuntime.CallbackPath);
                        return;
                    }
                    if (context.Request.Method != "GET" || context.Request.QueryString.HasValue)
                        throw new CostConnectionException("invalidCallback");
                    var auth = await context.Authentication.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationType);
                    var principal = auth?.Identity == null ? null : new ClaimsPrincipal(auth.Identity);
                    if (!CostConnectionRuntime.CanManage(principal, runtime.Config.TenantGUID.ToString(), runtime.Config.ClientID))
                        throw new CostConnectionException("administrationRequired");
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    {
                        await runtime.Service.CompleteAsync(CostConnectionRuntime.OperatorId(principal),
                            context.Request.Cookies[CostConnectionRuntime.BindingCookie], timeout.Token);
                    }
                    System.Runtime.Caching.MemoryCache.Default.Remove("copilot-cost-connection-status");
                    outcome = "connected";
                }
                catch (CostConnectionException ex) { outcome = ex.Status; }
                catch (RequestFailedException) { outcome = "storageUnavailable"; }
                catch (HttpRequestException) { outcome = "identityUnavailable"; }
                catch (OperationCanceledException) { outcome = "identityUnavailable"; }
                if (outcome != "connected")
                    System.Diagnostics.Trace.TraceWarning("Copilot cost callback outcome: {0}", outcome);
                context.Response.Redirect("/#/admin/copilot-cost-connection?costConnection=" + Uri.EscapeDataString(outcome));
            }));
        }

        public static async Task<NameValueCollection> ReadCostAuthorizationFormAsync(IOwinRequest request, CancellationToken ct)
        {
            if (!string.Equals(request.ContentType?.Split(';')[0], "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                throw new CostConnectionException("invalidCallback");
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[1024];
                int count;
                while ((count = await request.Body.ReadAsync(bytes, 0, bytes.Length, ct)) > 0)
                {
                    if (buffer.Length + count > 16384) throw new CostConnectionException("invalidCallback");
                    buffer.Write(bytes, 0, count);
                }
                var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(buffer.ToArray()));
                foreach (var key in new[] { "code", "state", "error" })
                    if (form.GetValues(key)?.Length > 1) throw new CostConnectionException("invalidCallback");
                return form;
            }
        }
    }
}
