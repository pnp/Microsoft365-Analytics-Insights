using Azure;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.CopilotCosts;
using Common.Entities.CopilotCosts;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/CopilotCostConnection")]
    public sealed class CopilotCostConnectionAPIController : ControllerBase
    {
        private static readonly HttpClient ProbeHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
        private const string StatusCacheKey = "copilot-cost-connection-status";
        private const string CollectionCacheKey = "copilot-cost-collection-status";
        private readonly IAntiforgery _antiforgery;
        private CostConnectionRuntime Runtime => CostConnectionRuntime.Default.Value;

        public CopilotCostConnectionAPIController(IAntiforgery antiforgery)
            => _antiforgery = antiforgery ?? throw new ArgumentNullException(nameof(antiforgery));

        [HttpGet, Route("")]
        public async Task<IActionResult> Status()
        {
            NoStore();
            var runtime = Runtime;
            if (!CostConnectionRuntime.CanManage(User as ClaimsPrincipal, runtime.Config.TenantGUID.ToString(), runtime.Config.ClientID, PortalAccess.PolicyFor(Request)))
                return PortalPermissionDenied.Result(Request, PortalPermission.Administration);
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
                var token = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                if (Request.Cookies[CostConnectionRuntime.BindingCookie] == null)
                    Response.Cookies.Append(CostConnectionRuntime.BindingCookie, CostConnectionEncoding.Random(),
                        new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.None, Path = "/" });
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
        public async Task<IActionResult> Collection()
        {
            NoStore();
            var runtime = Runtime;
            if (!CostConnectionRuntime.CanManage(User as ClaimsPrincipal, runtime.Config.TenantGUID.ToString(), runtime.Config.ClientID, PortalAccess.PolicyFor(Request)))
                return PortalPermissionDenied.Result(Request, PortalPermission.Administration);
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
        public Task<IActionResult> Connect() => Execute(async ct =>
        {
            var browser = Request.Cookies[CostConnectionRuntime.BindingCookie];
            if (browser == null) throw new CostConnectionException("invalidCallback");
            var url = await Runtime.Service.StartAsync(CostConnectionRuntime.OperatorId(User as ClaimsPrincipal), browser, ct);
            return Ok(new { authorizationUrl = url });
        });

        [HttpPost, Route("disconnect")]
        public Task<IActionResult> Disconnect() => Execute(async ct =>
        {
            await Runtime.Service.DisconnectAsync(ct);
            return Ok(new { status = "disconnected" });
        });

        [HttpPost, Route("probe")]
        public Task<IActionResult> Probe() => Execute(async ct =>
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
        public Task<IActionResult> ResetProbe() => Execute(async ct =>
        {
            await Runtime.Service.ResetProbeAsync(ct);
            return Ok(new { status = "sliceCompleted" });
        });

        private async Task<IActionResult> Execute(Func<CancellationToken, Task<IActionResult>> action)
        {
            NoStore();
            if (!CostConnectionRuntime.CanManage(User as ClaimsPrincipal, Runtime.Config.TenantGUID.ToString(), Runtime.Config.ClientID, PortalAccess.PolicyFor(Request)))
                return PortalPermissionDenied.Result(Request, PortalPermission.Administration);
            try
            {
                Runtime.RequireAvailable();
                await ValidateManagementRequestAsync(HttpContext, _antiforgery, Runtime.Callback);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40))) return await action(timeout.Token);
            }
            catch (AntiforgeryValidationException) { return Failure("invalidRequest"); }
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

        public static async Task ValidateManagementRequestAsync(HttpContext context, IAntiforgery antiforgery, string callback)
        {
            if (context.Request.Headers.Origin.ToString() != new Uri(callback).GetLeftPart(UriPartial.Authority))
                throw new CostConnectionException("invalidRequest");
            await antiforgery.ValidateRequestAsync(context);
        }

        private IActionResult Failure(string status)
        {
            System.Diagnostics.Trace.TraceWarning("Copilot cost connection outcome: {0}", status);
            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { status });
        }

        private void NoStore()
        {
            Response.Headers.CacheControl = "no-cache, no-store, private";
        }
    }
}
