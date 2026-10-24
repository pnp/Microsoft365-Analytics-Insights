using Azure;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Helpers;
using System.Web.Http;
using Web.AnalyticsWeb.Models.CopilotCosts;
using Common.Entities.CopilotCosts;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RoutePrefix("api/CopilotCostConnection")]
    public sealed class CopilotCostConnectionAPIController : ApiController
    {
        private static readonly HttpClient ProbeHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
        private const string StatusCacheKey = "copilot-cost-connection-status";
        private const string CollectionCacheKey = "copilot-cost-collection-status";
        private const string CsrfCookie = "__Host-CopilotCostCsrf";
        private CostConnectionRuntime Runtime => CostConnectionRuntime.Default.Value;

        [HttpGet, Route("")]
        public async Task<IHttpActionResult> Status()
        {
            NoStore();
            var runtime = Runtime;
            if (!CostConnectionRuntime.IsOperator(User as ClaimsPrincipal, runtime.Config.TenantGUID.ToString(), runtime.Config.ClientID))
                return Ok(new { status = "operatorRequired" });
            if (runtime.Availability != "available") return Ok(new { status = runtime.Availability });
            try
            {
                var state = MemoryCache.Default[StatusCacheKey] as CostConnectionState;
                if (state == null)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        state = await runtime.Service.ReadAsync(timeout.Token);
                    MemoryCache.Default.Set(StatusCacheKey, state, DateTimeOffset.UtcNow.AddSeconds(30));
                }
                var context = HttpContext.Current;
                AntiForgery.GetTokens(context.Request.Cookies[CsrfCookie]?.Value, out var cookie, out var token);
                if (cookie != null) context.Response.Cookies.Add(new HttpCookie(CsrfCookie, cookie)
                { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
                if (context.Request.Cookies[CostConnectionRuntime.BindingCookie] == null)
                    context.Response.Cookies.Add(new HttpCookie(CostConnectionRuntime.BindingCookie, CostConnectionEncoding.Random())
                    { Secure = true, HttpOnly = true, SameSite = SameSiteMode.None, Path = "/" });
                return Ok(new
                {
                    status = state.Status, csrfToken = token, connectedAt = state.ConnectedAt, tokenAcquiredAt = state.RefreshedAt,
                    probe = state.Probe == null ? null : new
                    {
                        phase = state.Probe.Phase, directoryUsers = state.Probe.UserCount,
                        inventoryComplete = state.Probe.Phase != "context" && state.Probe.Phase != "inventory",
                        observed = state.Probe.Observed, measured = state.Probe.Measured, empty = state.Probe.Empty,
                        partial = state.Probe.Partial, unevaluated = state.Probe.Unevaluated, failed = state.Probe.Failed,
                        denied = state.Probe.Denied, lastOutcome = state.Probe.LastOutcome
                    }
                });
            }
            catch (CostConnectionException ex) { return Failure(ex.Status); }
            catch (RequestFailedException) { return Failure("storageUnavailable"); }
            catch (OperationCanceledException) { return Failure("storageUnavailable"); }
        }

        [HttpGet, Route("collection")]
        public async Task<IHttpActionResult> Collection()
        {
            NoStore();
            var runtime = Runtime;
            if (!CostConnectionRuntime.IsOperator(User as ClaimsPrincipal, runtime.Config.TenantGUID.ToString(), runtime.Config.ClientID))
                return Ok(new { status = "operatorRequired" });
            if (runtime.Availability != "available") return Ok(new { status = runtime.Availability });
            try
            {
                var state = MemoryCache.Default[CollectionCacheKey] as CostCollectorStatus;
                if (state == null)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        state = await CostCollectorRuntime.CreateHistoryStore(runtime.Config).ReadStatusAsync(timeout.Token);
                    MemoryCache.Default.Set(CollectionCacheKey, state, DateTimeOffset.UtcNow.AddSeconds(30));
                }
                return Ok(CollectionMetadata(state));
            }
            catch (CostConnectionException ex) { return Failure(ex.Status); }
            catch (OperationCanceledException) { return Failure("databaseUnavailable"); }
        }

        public static object CollectionMetadata(CostCollectorStatus state) => new
        {
            status = "available", enabled = true, phase = state.Phase == "notStarted" ? "idle" : state.Phase,
            directoryUsers = state.DirectoryUsers, inventoryComplete = state.InventoryComplete,
            observed = state.Observed, measured = state.Measured, empty = state.Empty,
            partial = state.Partial, unevaluated = state.Unevaluated,
            pending = state.Pending, retrying = state.Retrying, failed = state.Failed, denied = state.Denied,
            lastAttempt = state.LastAttempt, lastCompleted = state.LastCompleted,
            lastOutcome = state.LastOutcome == "notStarted" ? null : state.LastOutcome
        };

        [HttpPost, Route("connect")]
        public Task<IHttpActionResult> Connect() => Execute(async ct =>
        {
            var browser = HttpContext.Current.Request.Cookies[CostConnectionRuntime.BindingCookie]?.Value;
            if (browser == null) throw new CostConnectionException("invalidCallback");
            var url = await Runtime.Service.StartAsync(CostConnectionRuntime.OperatorId(User as ClaimsPrincipal), browser, ct);
            return Ok(new { authorizationUrl = url });
        });

        [HttpPost, Route("disconnect")]
        public Task<IHttpActionResult> Disconnect() => Execute(async ct =>
        {
            await Runtime.Service.DisconnectAsync(ct);
            return Ok(new { status = "disconnected" });
        });

        [HttpPost, Route("probe")]
        public Task<IHttpActionResult> Probe() => Execute(async ct =>
        {
            string outcome = null;
            await Runtime.Service.RunWithTokenAsync(async (state, token, cancellation) =>
            {
                string directoryToken = null;
                if (state.Probe?.Phase == "inventory")
                {
                    try
                    {
                        var app = await AuthHelper.GetNewClientApp(Runtime.Config.TenantGUID.ToString(), Runtime.Config.ClientID,
                            Runtime.Config.ClientSecret, Runtime.Config.KeyVaultUrl, Runtime.Config.UseClientCertificate, NullLogger.Instance);
                        directoryToken = (await app.AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" }).ExecuteAsync(cancellation)).AccessToken;
                    }
                    catch (Microsoft.Identity.Client.MsalException) { throw new CostConnectionException("directoryUnavailable"); }
                    catch (Azure.Identity.AuthenticationFailedException) { throw new CostConnectionException("directoryUnavailable"); }
                    catch (RequestFailedException) { throw new CostConnectionException("directoryUnavailable"); }
                }
                await new CostProbeRunner(Runtime.Store, ProbeHttp, tokenCt => Runtime.Service.AssertOwnershipAsync(state, tokenCt))
                    .StepAsync(state, token, directoryToken, cancellation);
                outcome = state.Probe.LastOutcome;
            }, ct);
            return outcome == "sliceCompleted" ? Ok(new { status = outcome }) : Failure(outcome);
        });

        [HttpPost, Route("resetProbe")]
        public Task<IHttpActionResult> ResetProbe() => Execute(async ct =>
        {
            await Runtime.Service.ResetProbeAsync(ct);
            return Ok(new { status = "sliceCompleted" });
        });

        private async Task<IHttpActionResult> Execute(Func<CancellationToken, Task<IHttpActionResult>> action)
        {
            NoStore();
            if (!CostConnectionRuntime.IsOperator(User as ClaimsPrincipal, Runtime.Config.TenantGUID.ToString(), Runtime.Config.ClientID))
                return Content(HttpStatusCode.Forbidden, new { status = "operatorRequired" });
            try
            {
                Runtime.RequireAvailable();
                ValidateManagementRequest(Runtime.Callback, HttpContext.Current.Request.Headers["Origin"],
                    HttpContext.Current.Request.Cookies[CsrfCookie]?.Value,
                    HttpContext.Current.Request.Headers["X-Copilot-Cost-CSRF"]);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40))) return await action(timeout.Token);
            }
            catch (System.Web.Mvc.HttpAntiForgeryException) { return Failure("invalidRequest"); }
            catch (CostConnectionException ex) { return Failure(ex.Status); }
            catch (RequestFailedException) { return Failure("storageUnavailable"); }
            catch (HttpRequestException) { return Failure("sourceUnavailable"); }
            catch (OperationCanceledException) { return Failure("sourceUnavailable"); }
            finally
            {
                MemoryCache.Default.Remove(StatusCacheKey);
                MemoryCache.Default.Remove(CollectionCacheKey);
            }
        }

        public static void ValidateManagementRequest(string callback, string origin, string cookie, string token)
        {
            if (origin != new Uri(callback).GetLeftPart(UriPartial.Authority))
                throw new CostConnectionException("invalidRequest");
            AntiForgery.Validate(cookie, token);
        }

        private IHttpActionResult Failure(string status)
        {
            System.Diagnostics.Trace.TraceWarning("Copilot cost connection outcome: {0}", status);
            return Content(HttpStatusCode.ServiceUnavailable, new { status });
        }

        private static void NoStore()
        {
            HttpContext.Current.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            HttpContext.Current.Response.Cache.SetNoStore();
        }
    }
}
