extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class DelegatedGraphConsentPipelineTests
    {
        [TestMethod]
        public void ConnectTeams_IssuesOnlyTheMarkedOidcChallenge()
        {
            var result = new AccountController().ConnectTeams() as ChallengeResult;
            Assert.IsNotNull(result);
            CollectionAssert.AreEqual(new[] { OpenIdConnectDefaults.AuthenticationScheme }, result.AuthenticationSchemes.ToArray());
            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(result.Properties));
            Assert.AreEqual(DelegatedGraphConsent.TeamsPermissionsRoute, result.Properties.RedirectUri);
        }

        [TestMethod]
        public void ConnectTeams_RequiresSignInAndAdministration()
        {
            var action = typeof(AccountController).GetMethod(nameof(AccountController.ConnectTeams));
            Assert.IsNotNull(action.GetCustomAttribute<AuthorizeAttribute>());
            Assert.AreEqual(PortalPermission.Administration,
                action.GetCustomAttributes<RequirePortalPermissionAttribute>().Single().Permission);
        }

        [TestMethod]
        public void SignInChallenge_IsNotATeamsConsentRequest()
        {
            var result = new AccountController().SignIn() as ChallengeResult;
            Assert.IsNotNull(result);
            Assert.IsFalse(DelegatedGraphConsent.IsTeamsConnect(result.Properties));
            Assert.AreEqual("/", result.Properties.RedirectUri);
        }

        [TestMethod]
        public void BillingMvcChallenge_RequiresTheSameOriginPostIntent_AndUsesOnlyBillingScheme()
        {
            var protection = new EphemeralDataProtectionProvider();
            var user = BillingSite.Administrator();
            var controller = new AccountController
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
            };
            Assert.AreEqual(403, ((StatusCodeResult)controller.ConnectAgentCosts(null, protection)).StatusCode);
            Assert.AreEqual(403, ((StatusCodeResult)controller.ConnectAgentCosts("foreign-intent", protection)).StatusCode);
            var intent = AgentCostConsent.CreateIntent(user, protection);
            var challenge = controller.ConnectAgentCosts(intent, protection) as ChallengeResult;
            Assert.IsNotNull(challenge);
            CollectionAssert.AreEqual(new[] { AgentCostConsent.AuthenticationType }, challenge.AuthenticationSchemes.ToArray());
            Assert.AreEqual(AgentCostConsent.ReturnRoute, challenge.Properties.RedirectUri);
            Assert.IsFalse(DelegatedGraphConsent.IsTeamsConnect(challenge.Properties));

            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("oid", Guid.Empty.ToString()), new Claim("tid", Guid.Empty.ToString()),
                new Claim("roles", PortalRoles.Administration),
            }, "synthetic"));
            Assert.AreEqual(403, ((StatusCodeResult)controller.ConnectAgentCosts(intent, protection)).StatusCode);
        }

        [TestMethod]
        public async Task BillingConnect_UsesSeparateCallbackAndPreservesThePortalCookie()
        {
            using (var site = new BillingSite())
            {
                var cookie = await site.SignInAsync();
                var challenge = await site.ChallengeAsync(cookie);
                var query = QueryHelpers.ParseQuery(challenge.Headers.Location.Query);
                Assert.AreEqual("https://contoso.example/signin-agent-costs", query["redirect_uri"].ToString());
                StringAssert.Contains(query["scope"].ToString(), AgentCostDelegatedTokenProvider.Scope);
                Assert.IsFalse(query["scope"].ToString().Contains("graph.microsoft.com"));
                var properties = site.Options.StateDataFormat.Unprotect(query["state"]);
                Assert.IsTrue(AgentCostConsent.IsBound(properties, BillingSite.Administrator()));
                Assert.IsFalse(DelegatedGraphConsent.IsTeamsConnect(properties));

                var callback = await site.CallbackAsync(challenge);
                Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
                Assert.IsNotNull(site.ValidatedPrincipal, "Hybrid callback must validate the signed identity before redeeming the code.");
                Assert.IsTrue(site.CallbackBound, "The validated callback identity must match the protected administrator intent.");
                Assert.IsTrue(site.CodeCallbackBound, "Authorization-code event must retain the validated principal and bound intent: " + site.CallbackFailure);
                Assert.AreEqual("synthetic-code", site.CallbackCode);
                Assert.AreEqual(AgentCostConsent.ReturnRoute + "?connection=connected", callback.Headers.Location.ToString());
                Assert.AreEqual(1, site.ConnectCalls);
                Assert.IsFalse(callback.Headers.TryGetValues("Set-Cookie", out var cookies)
                    && cookies.Any(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)),
                    "Billing must not replace portal authentication or issue a token cookie.");
                using (var request = new HttpRequestMessage(HttpMethod.Get, "/session"))
                {
                    request.Headers.Add("Cookie", cookie);
                    Assert.AreEqual(HttpStatusCode.OK, (await site.Client.SendAsync(request)).StatusCode);
                }
            }
        }

        [DataTestMethod]
        [DataRow("nonce")]
        [DataRow("state")]
        [DataRow("cookie")]
        [DataRow("signature")]
        public async Task BillingConnect_RejectsCallbackCsrfBeforeCodeRedemption(string tamper)
        {
            using (var site = new BillingSite())
            {
                var challenge = await site.ChallengeAsync(await site.SignInAsync());
                var callback = await site.CallbackAsync(challenge, tamper);
                Assert.AreEqual(0, site.ConnectCalls);
                Assert.AreNotEqual(AgentCostConsent.ReturnRoute + "?connection=connected", callback.Headers.Location?.ToString());
            }
        }

        private sealed class BillingSite : IDisposable
        {
            private const string Issuer = "https://login.contoso.example/tenant/v2.0";
            private const string ClientId = "00000000-0000-0000-0000-000000000001";
            private static readonly SymmetricSecurityKey Key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("synthetic-signing-key-for-core-consent-tests-000000000000"));
            private readonly IHost _host;
            internal HttpClient Client { get; }
            internal int ConnectCalls { get; private set; }
            internal ClaimsPrincipal ValidatedPrincipal { get; private set; }
            internal bool CallbackBound { get; private set; }
            internal bool CodeCallbackBound { get; private set; }
            internal string CallbackCode { get; private set; }
            internal string CallbackFailure { get; private set; }
            internal OpenIdConnectOptions Options => _host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(AgentCostConsent.AuthenticationType);

            internal BillingSite()
            {
                _host = new HostBuilder().ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                            .AddCookie()
                            .AddOpenIdConnect(AgentCostConsent.AuthenticationType, options =>
                            {
                                AgentCostConsent.ConfigureOptions(options, new AppConfig
                                {
                                    TenantGUID = Guid.Empty, ClientID = ClientId, WebAppURL = "https://contoso.example/",
                                }, (user, properties, code) =>
                                {
                                    Assert.AreEqual("synthetic-code", code);
                                    ConnectCalls++;
                                    return Task.CompletedTask;
                                });
                                options.Configuration = new OpenIdConnectConfiguration
                                {
                                    Issuer = Issuer,
                                    AuthorizationEndpoint = "https://login.contoso.example/authorize",
                                };
                                options.Configuration.SigningKeys.Add(Key);
                                var remoteFailure = options.Events.OnRemoteFailure;
                                options.Events.OnRemoteFailure = async context =>
                                {
                                    CallbackFailure = context.Failure?.ToString();
                                    await remoteFailure(context);
                                };
                                var codeReceived = options.Events.OnAuthorizationCodeReceived;
                                options.Events.OnAuthorizationCodeReceived = async context =>
                                {
                                    CodeCallbackBound = AgentCostConsent.IsBound(context.Properties, context.Principal);
                                    CallbackCode = context.ProtocolMessage.Code;
                                    await codeReceived(context);
                                };
                                options.Events.OnTokenValidated = context =>
                                {
                                    ValidatedPrincipal = context.Principal;
                                    CallbackBound = AgentCostConsent.IsBound(context.Properties, context.Principal);
                                    return Task.CompletedTask;
                                };
                            });
                    });
                    web.Configure(app =>
                    {
                        app.UseAuthentication();
                        app.Run(async context =>
                        {
                            if (context.Request.Path == "/seed-session")
                                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Administrator());
                            else if (context.Request.Path == "/connect")
                                await context.ChallengeAsync(AgentCostConsent.AuthenticationType,
                                    AgentCostConsent.PropertiesFor(context.User));
                            else context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true ? 200 : 401;
                        });
                    });
                }).Start();
                Client = _host.GetTestClient();
                Client.BaseAddress = new Uri("https://contoso.example");
            }

            internal static ClaimsPrincipal Administrator() => new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("oid", ClientId), new Claim("tid", Guid.Empty.ToString()),
                new Claim("roles", PortalRoles.Administration),
            }, CookieAuthenticationDefaults.AuthenticationScheme));

            internal async Task<string> SignInAsync() => CookieHeader(await Client.GetAsync("/seed-session"));

            internal Task<HttpResponseMessage> ChallengeAsync(string cookie)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/connect");
                request.Headers.Add("Cookie", cookie);
                return Client.SendAsync(request);
            }

            internal Task<HttpResponseMessage> CallbackAsync(HttpResponseMessage challenge, string tamper = null)
            {
                var query = QueryHelpers.ParseQuery(challenge.Headers.Location.Query);
                const string code = "synthetic-code";
                var claims = Administrator().Claims.ToList();
                claims.Add(new Claim("sub", "synthetic-subject"));
                claims.Add(new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
                claims.Add(new Claim("nonce", tamper == "nonce" ? "foreign-nonce" : query["nonce"].ToString()));
                var hash = SHA256.HashData(Encoding.ASCII.GetBytes(code));
                claims.Add(new Claim("c_hash", Base64UrlEncoder.Encode(hash, 0, hash.Length / 2)));
                var token = new JwtSecurityToken(Issuer, ClientId, claims, DateTime.UtcNow.AddMinutes(-1),
                    DateTime.UtcNow.AddMinutes(5), new SigningCredentials(
                        tamper == "signature" ? new SymmetricSecurityKey(new byte[64]) : Key, SecurityAlgorithms.HmacSha256));
                var request = new HttpRequestMessage(HttpMethod.Post, "/signin-agent-costs")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["code"] = code, ["id_token"] = new JwtSecurityTokenHandler().WriteToken(token),
                        ["state"] = tamper == "state" ? "foreign-state" : query["state"].ToString(),
                    }),
                };
                if (tamper != "cookie") request.Headers.Add("Cookie", CookieHeader(challenge));
                return Client.SendAsync(request);
            }

            private static string CookieHeader(HttpResponseMessage response) =>
                response.Headers.TryGetValues("Set-Cookie", out var cookies)
                    ? string.Join("; ", cookies.Select(c => c.Split(';')[0])) : "";

            public void Dispose() { Client.Dispose(); _host.Dispose(); }
        }
    }
}
