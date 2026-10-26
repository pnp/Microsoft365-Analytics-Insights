using Azure;
using Common.Entities.CopilotCosts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Runtime.Caching;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Web.AnalyticsWeb.Models.CopilotCosts;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Core equivalent of the terminal callback before ordinary OIDC. This connection never signs
    /// the reader into the portal or replaces the operator's cookie.
    /// </summary>
    public static class CopilotCostHosting
    {
        public const string CsrfCookie = "__Host-CopilotCostCsrf";
        public const string CsrfHeader = "X-Copilot-Cost-CSRF";

        public static IServiceCollection AddCopilotCostConnection(this IServiceCollection services)
        {
            services.AddAntiforgery(options =>
            {
                options.Cookie.Name = CsrfCookie;
                options.Cookie.Path = "/";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.IsEssential = true;
                options.HeaderName = CsrfHeader;
            });
            return services;
        }

        public static IApplicationBuilder UseCopilotCostCallback(this IApplicationBuilder app)
        {
            app.Map(CostConnectionRuntime.CallbackPath, branch => branch.Run(HandleCallbackAsync));
            return app;
        }

        private static async Task HandleCallbackAsync(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var outcome = "invalidCallback";
            try
            {
                if (!context.Request.IsHttps || context.Request.Path.HasValue)
                    throw new CostConnectionException("invalidCallback");
                var runtime = CostConnectionRuntime.Default.Value;
                runtime.RequireAvailable();
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var form = await ReadCostAuthorizationFormAsync(context.Request, timeout.Token);
                    await runtime.Service.StageCallbackAsync(form["code"], form["state"], form["error"] != null,
                        context.Request.Cookies[CostConnectionRuntime.BindingCookie], timeout.Token);
                    // form_post keeps codes out of URLs. The same-site GET restores the ordinary Lax cookie.
                    context.Response.Redirect(CostConnectionRuntime.CallbackPath);
                    return;
                }
                if (!HttpMethods.IsGet(context.Request.Method) || context.Request.QueryString.HasValue)
                    throw new CostConnectionException("invalidCallback");
                var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = auth.Principal;
                if (!CostConnectionRuntime.CanManage(principal, runtime.Config.TenantGUID.ToString(),
                    runtime.Config.ClientID, PortalAccess.PolicyFor(context.Request)))
                    throw new CostConnectionException("administrationRequired");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await runtime.Service.CompleteAsync(CostConnectionRuntime.OperatorId(principal),
                        context.Request.Cookies[CostConnectionRuntime.BindingCookie], timeout.Token);
                }
                MemoryCache.Default.Remove("copilot-cost-connection-status");
                outcome = "connected";
            }
            catch (CostConnectionException ex) { outcome = ex.Status; }
            catch (RequestFailedException) { outcome = "storageUnavailable"; }
            catch (HttpRequestException) { outcome = "identityUnavailable"; }
            catch (OperationCanceledException) { outcome = "identityUnavailable"; }
            if (outcome != "connected")
                System.Diagnostics.Trace.TraceWarning("Copilot cost callback outcome: {0}", outcome);
            context.Response.Redirect("/#/admin/copilot-cost-connection?costConnection=" + Uri.EscapeDataString(outcome));
        }

        public static async Task<NameValueCollection> ReadCostAuthorizationFormAsync(HttpRequest request, CancellationToken ct)
        {
            if (!string.Equals(request.ContentType?.Split(';')[0], "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
                throw new CostConnectionException("invalidCallback");
            using var buffer = new MemoryStream();
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
