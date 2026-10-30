extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.Config;
using Common.Entities;
using Common.Entities.CopilotCosts;
using Common.Entities.State;
using Microsoft.Identity.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class AgentCostConnectionTests
    {
        private const string Tenant = "00000000-0000-0000-0000-000000000000";
        private const string ObjectId = "00000000-0000-0000-0000-000000000001";
        private const string Redirect = "https://contoso.example/signin-agent-costs";
        private const string Secret = "synthetic-runtime-credential-for-unit-tests";

        [TestMethod]
        public async Task ConsumptionFailureSurvivesRefreshAndOldGenerationCannotClearNewConnectionHealth()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            var config = new AppConfig { ImportJobSettings = new ImportTaskSettings { CopilotStudioCredits = true } };
            await store.ConnectAsync("synthetic-cache");
            var first = await store.GetHeadAsync();
            var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            await store.RecordHealthAsync(first.Version, "permissionDenied", now);
            await store.SaveCacheAsync(first.Version, "refreshed-synthetic-cache");
            Assert.AreEqual("permissionDenied", (await CostApiHealthReader.ReadStudioAsync(config, () => store)).Status);
            await store.ConnectAsync("new-synthetic-cache");
            var second = await store.GetHeadAsync();
            Assert.AreEqual("permissionDenied", (await CostApiHealthReader.ReadStudioAsync(config, () => store)).Status,
                "Reconnect must not resolve an alert before the new connection reads consumption.");
            await store.RecordHealthAsync(second.Version, "throttled", now);
            await store.RecordHealthAsync(first.Version, null, now.AddMinutes(1));
            var health = await CostApiHealthReader.ReadStudioAsync(config, () => store);
            Assert.AreEqual("failing", health.Status);
            Assert.IsNull(health.LastSuccessfulRead);
            Assert.AreEqual(DataUtils.Health.HealthStatus.Degraded, health.ToRuntimeResult(now).Status);
            Assert.AreEqual(DataUtils.Health.HealthStatus.Unhealthy, health.ToRuntimeResult(now.AddMinutes(15)).Status);
            await store.RecordHealthAsync(second.Version, null, now.AddMinutes(20));
            health = await CostApiHealthReader.ReadStudioAsync(config, () => store);
            Assert.AreEqual("healthy", health.Status);
            Assert.AreEqual(now.AddMinutes(20), health.LastSuccessfulRead);
            Assert.IsNull(health.FailureSince);
        }

        [TestMethod]
        public void CacheEncryption_IsRandomAuthenticatedAndSecretFree()
        {
            var protection = new AgentCostTokenProtection(Secret);
            var bytes = Encoding.UTF8.GetBytes("synthetic-refresh-token");
            var first = protection.Protect(bytes);
            var second = protection.Protect(bytes);
            Assert.AreNotEqual(first, second);
            Assert.IsFalse(first.Contains("synthetic-refresh-token"));
            CollectionAssert.AreEqual(bytes, protection.Unprotect(first));
            var envelope = JObject.Parse(first);
            envelope["Ciphertext"] = Convert.ToBase64String(new byte[32]);
            Assert.ThrowsException<CryptographicException>(() => protection.Unprotect(envelope.ToString()));
            Assert.ThrowsException<CryptographicException>(() => new AgentCostTokenProtection("rotated-synthetic-secret").Unprotect(first));
        }

        [TestMethod]
        public void CertificateProtection_RoundTrips()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=Contoso Synthetic Billing", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)))
                {
                    var protection = new AgentCostTokenProtection(null, cert);
                    var data = Encoding.UTF8.GetBytes("synthetic-cache");
                    CollectionAssert.AreEqual(data, protection.Unprotect(protection.Protect(data)));
                }
            }
        }

        [TestMethod]
        public async Task DisconnectAndReconnect_CannotBeUndoneByOldRefreshWriters()
        {
            var raw = new InMemoryKeyValueStore();
            var store = new AgentCostConnectionStore(raw);
            await store.ConnectAsync("encrypted-synthetic-cache");
            var first = await store.GetHeadAsync();
            Assert.AreEqual("connected", await store.GetStatusAsync());
            await store.DisconnectAsync();
            await store.SaveCacheAsync(first.Version, "stale-refresh");
            Assert.AreEqual("disconnected", await store.GetStatusAsync());
            await store.ConnectAsync("new-cache");
            var second = await store.GetHeadAsync();
            await store.RequireReconnectAsync(first.Version);
            Assert.AreNotEqual(first.Version, second.Version);
            Assert.AreEqual("connected", await store.GetStatusAsync());
            await store.RequireReconnectAsync(second.Version);
            Assert.AreEqual("reconnectNeeded", await store.GetStatusAsync());
            var serialized = await raw.GetStringAsync(AgentCostConnectionStore.HeadKey);
            Assert.IsFalse(serialized.Contains("cache"));
            Assert.IsFalse(serialized.Contains("token"));
        }

        [TestMethod]
        public async Task Msal_RedeemsRefreshesAndPersistsRotatedCredentials()
        {
            var handler = new MsalServer();
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            var provider = Provider(store, handler);
            string verifiedToken = null;
            await provider.ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, token =>
            {
                verifiedToken = token;
                return Task.CompletedTask;
            });
            Assert.AreEqual("synthetic-access-2", verifiedToken);
            var head = await store.GetHeadAsync();
            Assert.IsFalse((await store.GetCacheAsync(head.Version)).Contains("synthetic"));
            var requests = handler.TokenRequests;
            var details = await Provider(store, handler).GetConnectionDetailsAsync(head.Version);
            Assert.AreEqual("billing-admin@contoso.example", details.Account);
            Assert.IsNotNull(details.VerifiedUtc);
            Assert.AreEqual(requests, handler.TokenRequests, "Reading account details must not redeem or refresh tokens.");
            var renewed = await Provider(store, handler).AcquireAsync(head.Version);
            Assert.AreEqual("synthetic-access-3", renewed.Token);
            Assert.AreEqual("synthetic-refresh-1", handler.RefreshTokens[0]);
            Assert.AreEqual("synthetic-refresh-2", handler.RefreshTokens[1]);
            // Force expiry again, then prove a newly built provider reads the rotated refresh token from Storage.
            await Provider(store, handler).AcquireAsync(head.Version);
            Assert.AreEqual("synthetic-refresh-3", handler.RefreshTokens.Last());
            Assert.AreEqual(details.VerifiedUtc, (await Provider(store, handler).GetConnectionDetailsAsync(head.Version)).VerifiedUtc);
        }

        [TestMethod]
        public async Task ExistingConnection_ShowsAccountWithoutInventingVerificationTime()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            var server = new MsalServer();
            var provider = Provider(store, server);
            await provider.ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, _ => Task.CompletedTask);
            var head = await store.GetHeadAsync();
            var protection = new AgentCostTokenProtection(Secret);
            var payload = JObject.Parse(Encoding.UTF8.GetString(protection.Unprotect(await store.GetCacheAsync(head.Version))));
            payload.Remove("VerifiedUtc");
            await store.SaveCacheAsync(head.Version, protection.Protect(Encoding.UTF8.GetBytes(payload.ToString())));
            var details = await Provider(store, server).GetConnectionDetailsAsync(head.Version);
            Assert.AreEqual("billing-admin@contoso.example", details.Account);
            Assert.IsNull(details.VerifiedUtc);
            Assert.AreEqual(2, server.TokenRequests);
        }

        [TestMethod]
        public async Task UnreadableConnectionDetails_RequireReconnectWithoutExposingCache()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            await store.ConnectAsync("invalid-synthetic-cache");
            using (var host = new PortalTestHost(new[] { typeof(AgentCostConnectionAPIController) },
                PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing,
                _ => new AgentCostConnectionAPIController(() => store, s => Provider(s, new MsalServer()))))
            {
                var status = JObject.Parse(await host.Client.GetStringAsync("api/AgentCostConnection"));
                Assert.AreEqual("reconnectNeeded", (string)status["state"]);
                Assert.AreEqual(1, status.Properties().Count(), "An unreadable cache must not expose account or credential data.");
            }
            Assert.AreEqual("reconnectNeeded", await store.GetStatusAsync());
        }

        [DataTestMethod]
        [DataRow("invalid_grant", "")]
        [DataRow("interaction_required", "basic_action")]
        public async Task RevocationOrConditionalAccess_RequiresExplicitReconnectWithoutRawError(string error, string suberror)
        {
            var handler = new MsalServer();
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            await Provider(store, handler).ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, _ => Task.CompletedTask);
            var head = await store.GetHeadAsync();
            handler.Error = error;
            handler.Suberror = suberror;
            var exception = await Assert.ThrowsExceptionAsync<AgentCostConnectionException>(
                () => Provider(store, handler).AcquireAsync(head.Version));
            Assert.AreEqual("reconnectNeeded", exception.Code);
            Assert.IsFalse(exception.ToString().Contains("private-token-endpoint-details"));
            Assert.IsNull(exception.InnerException);
            Assert.AreEqual("reconnectNeeded", await store.GetStatusAsync());
            var requests = handler.TokenRequests;
            await Assert.ThrowsExceptionAsync<AgentCostConnectionException>(() => Provider(store, handler).AcquireAsync(head.Version));
            Assert.AreEqual(requests, handler.TokenRequests, "A known failed connection must not loop on refresh.");
        }

        [TestMethod]
        public async Task TransientTokenFailure_DoesNotDiscardTheConnection()
        {
            var handler = new MsalServer();
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            await Provider(store, handler).ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, _ => Task.CompletedTask);
            var head = await store.GetHeadAsync();
            handler.Error = "temporarily_unavailable";
            var exception = await Assert.ThrowsExceptionAsync<AgentCostConnectionException>(() => Provider(store, handler).AcquireAsync(head.Version));
            Assert.AreEqual("tokenUnavailable", exception.Code);
            Assert.AreEqual("connected", await store.GetStatusAsync());
        }

        [TestMethod]
        public async Task FailedVerification_DoesNotPublishOrReplaceConnection()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            await store.ConnectAsync("previous-encrypted-cache");
            var original = await store.GetHeadAsync();
            await Assert.ThrowsExceptionAsync<AgentCostConnectionException>(() => Provider(store, new MsalServer()).ConnectAsync(
                "synthetic-code", Redirect, Tenant, ObjectId, _ => throw new AgentCostConnectionException("accessDenied")));
            Assert.AreEqual(original.Version, (await store.GetHeadAsync()).Version);
        }

        [TestMethod]
        public async Task NoRefreshCredential_DoesNotClaimTheConnectionIsDurable()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            var server = new MsalServer { OmitRefreshToken = true };
            var exception = await Assert.ThrowsExceptionAsync<AgentCostConnectionException>(() =>
                Provider(store, server).ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, _ => Task.CompletedTask));
            Assert.AreEqual("consentOrPolicy", exception.Code);
            Assert.AreEqual("disconnected", await store.GetStatusAsync());
        }

        [TestMethod]
        public async Task OidcCallback_IsAccountTenantRoleAndExpiryBound_AndNeverIssuesTokenCookie()
        {
            var config = new AppConfig { TenantGUID = Guid.Parse(Tenant), ClientID = ObjectId, WebAppURL = "https://contoso.example/" };
            var calls = 0;
            var options = new OpenIdConnectOptions();
            AgentCostConsent.ConfigureOptions(options, config, (user, properties, code) => { calls++; return Task.CompletedTask; });
            Assert.AreEqual("code id_token", options.ResponseType);
            StringAssert.Contains(options.Authority, "/v2.0");
            CollectionAssert.Contains(options.Scope.ToArray(), AgentCostDelegatedTokenProvider.Scope);
            Assert.IsFalse(options.Scope.Any(s => s.Contains("graph.microsoft.com")));
            Assert.AreEqual(AgentCostDelegatedTokenProvider.CallbackPath, options.CallbackPath.Value);
            Assert.IsFalse(options.SaveTokens);
            Assert.IsFalse(options.UsePkce);
            foreach (var kind in new[] { "valid", "account", "tenant", "role", "expired" })
            {
                var original = Principal();
                var properties = AgentCostConsent.PropertiesFor(original);
                var callbackUser = Principal(kind == "account" ? Tenant : ObjectId, kind == "tenant" ? ObjectId : Tenant, kind != "role");
                if (kind == "expired") properties.Items[AgentCostConsent.ExpiresProperty] = "0";
                properties.RedirectUri = "https://untrusted.example/";
                var context = new DefaultHttpContext { User = original };
                var notification = new AuthorizationCodeReceivedContext(context,
                    new AuthenticationScheme(AgentCostConsent.AuthenticationType, null, typeof(OpenIdConnectHandler)),
                    options, properties)
                {
                    ProtocolMessage = new OpenIdConnectMessage { Code = "synthetic-code" },
                    Principal = callbackUser,
                };
                await options.Events.AuthorizationCodeReceived(notification);
                Assert.IsTrue(notification.Result.Handled);
                Assert.AreEqual(AgentCostConsent.ReturnRoute + "?connection=" + (kind == "valid" ? "connected" : "identityMismatch"),
                    context.Response.Headers.Location.ToString());
                Assert.IsFalse(context.Response.Headers.ContainsKey("Set-Cookie"));
            }
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void ChangingApis_RequireSameOriginAndAdministration()
        {
            foreach (var method in new[] { "Begin", "Disconnect" })
                Assert.IsTrue(typeof(AgentCostConnectionAPIController).GetMethod(method)
                    .IsDefined(typeof(RequireSameOriginXhrAttribute), true));
            var restriction = typeof(AgentCostConnectionAPIController)
                .GetCustomAttributes(typeof(RequirePortalPermissionAttribute), true).Cast<RequirePortalPermissionAttribute>().Single();
            Assert.AreEqual(PortalPermission.Administration, restriction.Permission);
        }

        [TestMethod]
        public async Task CoreConnectionApi_EnforcesAdministrationSameOriginAndNoStore()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            var server = new MsalServer();
            await Provider(store, server).ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, _ => Task.CompletedTask);
            using (var host = new PortalTestHost(new[] { typeof(AgentCostConnectionAPIController) },
                PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing,
                _ => new AgentCostConnectionAPIController(() => store, s => Provider(s, server))))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden,
                    (await host.Client.GetAsync("api/AgentCostConnection")).StatusCode);
                host.Principal = Principal();
                using (var status = await host.Client.GetAsync("api/AgentCostConnection"))
                {
                    Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
                    Assert.IsTrue(status.Headers.CacheControl.NoStore);
                    var body = JObject.Parse(await status.Content.ReadAsStringAsync());
                    Assert.AreEqual("connected", (string)body["state"]);
                    Assert.AreEqual("billing-admin@contoso.example", (string)body["details"]["account"]);
                }
                foreach (var action in new[] { "begin", "disconnect" })
                {
                    Assert.AreEqual(HttpStatusCode.Forbidden,
                        (await host.Client.PostAsync("api/AgentCostConnection/" + action, null)).StatusCode);
                    using (var crossSite = new HttpRequestMessage(HttpMethod.Post, "api/AgentCostConnection/" + action))
                    {
                        crossSite.Headers.Add("X-Requested-With", "XMLHttpRequest");
                        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
                        Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);
                    }
                }
                Assert.AreEqual("connected", await store.GetStatusAsync(), "Rejected calls must not change the connection.");
                host.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
                host.Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
                using (var begin = await host.Client.PostAsync("api/AgentCostConnection/begin", null))
                {
                    Assert.AreEqual(HttpStatusCode.OK, begin.StatusCode);
                    Assert.IsTrue(begin.Headers.CacheControl.NoStore);
                    StringAssert.StartsWith((string)JObject.Parse(await begin.Content.ReadAsStringAsync())["url"],
                        "/Account/ConnectAgentCosts?intent=");
                }
                using (var disconnected = await host.Client.PostAsync("api/AgentCostConnection/disconnect", null))
                {
                    Assert.AreEqual(HttpStatusCode.OK, disconnected.StatusCode);
                    Assert.IsTrue(disconnected.Headers.CacheControl.NoStore);
                }
                Assert.AreEqual("disconnected", await store.GetStatusAsync());
            }
        }

        [TestMethod]
        public void ConnectIntent_IsProtectedShortLivedAndAccountBound()
        {
            var user = Principal();
            var protection = new EphemeralDataProtectionProvider();
            var intent = AgentCostConsent.CreateIntent(user, protection);
            Assert.IsNotNull(AgentCostConsent.ReadIntent(intent, user, protection));
            Assert.IsNull(AgentCostConsent.ReadIntent(intent, Principal(oid: Tenant), protection));
            Assert.IsNull(AgentCostConsent.ReadIntent(intent, Principal(tenant: ObjectId), protection));
            Assert.IsNull(AgentCostConsent.ReadIntent(intent, Principal(administrator: false), protection));
            Assert.IsNull(AgentCostConsent.ReadIntent("tampered-" + intent, user, protection));
            Assert.IsNull(AgentCostConsent.ReadIntent(null, user, protection));
            Assert.IsNull(AgentCostConsent.ReadIntent(intent, user, new EphemeralDataProtectionProvider()));
            var properties = AgentCostConsent.PropertiesFor(user);
            properties.Items[AgentCostConsent.ExpiresProperty] = "0";
            Assert.IsFalse(AgentCostConsent.IsBound(properties, user));
            var expiredIntent = protection.CreateProtector("AgentCostConnectIntent.v1")
                .Protect(JsonConvert.SerializeObject(properties.Items));
            Assert.IsNull(AgentCostConsent.ReadIntent(expiredIntent, user, protection));
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.OK, null)]
        [DataRow(HttpStatusCode.Unauthorized, "accessDenied")]
        [DataRow(HttpStatusCode.Forbidden, "accessDenied")]
        [DataRow(HttpStatusCode.InternalServerError, "failed")]
        public async Task ConsentVerification_ProbesThePerUserRouteNotTheTenantWideOne(HttpStatusCode status, string expectedCode)
        {
            var probe = new ProbeHandler(status);
            var original = AgentCostConsent.VerificationClient;
            AgentCostConsent.VerificationClient = new HttpClient(probe);
            try
            {
                string code = null;
                try { await AgentCostConsent.VerifyAccessAsync("synthetic-token"); }
                catch (Common.Entities.State.AgentCostConnectionException ex) { code = ex.Message; }

                Assert.AreEqual(expectedCode, code == null ? null : code.Replace("Copilot Studio billing connection: ", ""));
                var uri = new Uri(probe.Requests.Single());
                StringAssert.EndsWith(uri.AbsolutePath, "/MCSMessages/users");
                StringAssert.Contains(uri.Query, "pageSize=1");
                Assert.IsFalse(uri.AbsolutePath.EndsWith("/resources"), "The tenant-wide per-agent route is refused to every client.");
            }
            finally
            {
                AgentCostConsent.VerificationClient = original;
            }
        }

        private sealed class ProbeHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            public ProbeHandler(HttpStatusCode status) { _status = status; }
            public List<string> Requests { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri.ToString());
                return Task.FromResult(new HttpResponseMessage(_status));
            }
        }

        [TestMethod]
        public async Task ConnectionApi_EnforcesRolesPerRequestAndNeverReturnsTokens()
        {
            var store = new AgentCostConnectionStore(new InMemoryKeyValueStore());
            var server = new MsalServer();
            await Provider(store, server).ConnectAsync("synthetic-code", Redirect, Tenant, ObjectId, _ => Task.CompletedTask);
            using (var host = new PortalTestHost(new[] { typeof(AgentCostConnectionAPIController) },
                PortalTestHost.SignedIn(), PortalAccessPolicy.Enforcing,
                _ => new AgentCostConnectionAPIController(() => store, s => Provider(s, server))))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.GetAsync("api/AgentCostConnection")).StatusCode);
                host.Principal = PortalTestHost.SignedIn(PortalRoles.Administration);
                var response = await host.Client.GetAsync("api/AgentCostConnection");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var status = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("connected", (string)status["state"]);
                Assert.AreEqual("billing-admin@contoso.example", (string)status["details"]["account"]);
                Assert.IsNotNull(status["details"]["verifiedUtc"]);
                CollectionAssert.AreEquivalent(new[] { "account", "verifiedUtc" },
                    ((JObject)status["details"]).Properties().Select(p => p.Name).ToArray());
                Assert.IsFalse(status.ToString().Contains("synthetic-access"));
                Assert.IsFalse(status.ToString().Contains("synthetic-refresh"));
                Assert.AreEqual(2, server.TokenRequests);
                Assert.AreEqual(HttpStatusCode.Forbidden,
                    (await host.Client.PostAsync("api/AgentCostConnection/disconnect", new StringContent(""))).StatusCode);
                host.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
                host.Principal = PortalTestHost.SignedIn();
                Assert.AreEqual(HttpStatusCode.Forbidden,
                    (await host.Client.PostAsync("api/AgentCostConnection/disconnect", new StringContent(""))).StatusCode);
                host.Principal = PortalTestHost.SignedIn(PortalRoles.Administration);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await host.Client.PostAsync("api/AgentCostConnection/disconnect", new StringContent(""))).StatusCode);
                Assert.AreEqual("disconnected", await store.GetStatusAsync());
            }
        }

        [TestMethod]
        public async Task MissingStorage_IsExplicitAndCannotClaimDurableConnection()
        {
            using (var host = new PortalTestHost(new[] { typeof(AgentCostConnectionAPIController) },
                PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing,
                _ => new AgentCostConnectionAPIController(() => null)))
            {
                Assert.AreEqual("storageNotConfigured", (string)JObject.Parse(await host.Client.GetStringAsync("api/AgentCostConnection"))["state"]);
                host.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable,
                    (await host.Client.PostAsync("api/AgentCostConnection/begin", new StringContent(""))).StatusCode);
            }
        }

        private static ClaimsPrincipal Principal(string oid = ObjectId, string tenant = Tenant, bool administrator = true)
        {
            var claims = new List<Claim> { new Claim("oid", oid), new Claim("tid", tenant) };
            if (administrator) claims.Add(new Claim(ClaimTypes.Role, PortalRoles.Administration));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "synthetic"));
        }

        private static AgentCostDelegatedTokenProvider Provider(AgentCostConnectionStore store, MsalServer server) =>
            new AgentCostDelegatedTokenProvider(store,
                () => Task.FromResult(ConfidentialClientApplicationBuilder.Create(ObjectId)
                    .WithClientSecret(Secret).WithAuthority("https://login.microsoftonline.com/" + Tenant)
                    .WithRedirectUri(Redirect).WithInstanceDiscovery(false).WithHttpClientFactory(server).Build()),
                () => Task.FromResult(new AgentCostTokenProtection(Secret)));

        private sealed class MsalServer : HttpMessageHandler, IMsalHttpClientFactory
        {
            private readonly HttpClient _http;
            public MsalServer() { _http = new HttpClient(this); }
            public int TokenRequests { get; private set; }
            public List<string> RefreshTokens { get; } = new List<string>();
            public string Error { get; set; }
            public string Suberror { get; set; }
            public bool OmitRefreshToken { get; set; }
            public HttpClient GetHttpClient() => _http;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var root = "https://login.microsoftonline.com/" + Tenant;
                if (request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, new
                    {
                        authorization_endpoint = root + "/oauth2/v2.0/authorize", token_endpoint = root + "/oauth2/v2.0/token",
                        issuer = root + "/v2.0", jwks_uri = root + "/keys", end_session_endpoint = root + "/logout",
                    });
                TokenRequests++;
                var form = System.Web.HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync());
                if (form["refresh_token"] != null) RefreshTokens.Add(form["refresh_token"]);
                if (Error != null)
                    return Json(HttpStatusCode.BadRequest, new { error = Error, suberror = Suberror, error_description = "private-token-endpoint-details" });
                var payload = Base64(JsonConvert.SerializeObject(new
                {
                    oid = ObjectId, tid = Tenant, sub = ObjectId, preferred_username = "billing-admin@contoso.example",
                    aud = ObjectId, iss = root + "/v2.0", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                }));
                return Json(HttpStatusCode.OK, new
                {
                    token_type = "Bearer", scope = AgentCostDelegatedTokenProvider.Scope, expires_in = 0,
                    access_token = "synthetic-access-" + TokenRequests,
                    refresh_token = OmitRefreshToken ? null : "synthetic-refresh-" + TokenRequests,
                    id_token = Base64("{\"alg\":\"none\"}") + "." + payload + ".",
                    client_info = Base64(JsonConvert.SerializeObject(new { uid = ObjectId, utid = Tenant })),
                });
            }

            private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            private static HttpResponseMessage Json(HttpStatusCode status, object body) => new HttpResponseMessage(status)
            { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") };
        }
    }
}
