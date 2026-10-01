extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Common.Entities.Models;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Owin;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.DataHandler;
using Microsoft.Owin.Security.DataProtection;
using Microsoft.Owin.Security.Notifications;
using Microsoft.Owin.Security.OpenIdConnect;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #670: a tenant that hadn't granted the runtime app the delegated Teams permissions could not sign
    /// in to the portal at all.
    /// </summary>
    /// <remarks>
    /// Every sign-in redeemed its authorisation code for <c>Team.ReadBasic.All</c> and
    /// <c>ChannelMessage.Read.All</c>, which only Teams deep analytics uses. Without consent the token endpoint
    /// answered <c>AADSTS65001</c>, the exception escaped Katana's callback handling, and the sign-in became a
    /// server error. These tests drive the real notification delegates the site registers
    /// (<c>Startup.CreateOpenIdConnectOptions</c>). <see cref="DelegatedGraphConsentPipelineTests"/> runs the
    /// same options through the real Katana middleware.
    /// </remarks>
    [TestClass]
    public class DelegatedGraphConsentTests
    {
        internal const string ClientId = "00000000-0000-0000-0000-000000000001";
        internal const string WebAppUrl = "https://contoso.example/";
        internal const string AdminUpn = "admin@contoso.example";

        /// <summary>
        /// The shape of the token-endpoint response that locked a customer out. Every value is synthetic.
        /// </summary>
        internal const string ConsentRequiredBody =
            "{\"error\":\"invalid_grant\"," +
            "\"error_description\":\"AADSTS65001: The user or administrator has not consented to use the application with ID " +
            "'00000000-0000-0000-0000-000000000000' named 'Contoso Analytics Runtime'. Send an interactive authorization request " +
            "for this user and resource. Trace ID: 00000000-0000-0000-0000-000000000000 Correlation ID: " +
            "00000000-0000-0000-0000-000000000000 Timestamp: 2026-01-01 00:00:00Z\"," +
            "\"error_codes\":[65001],\"timestamp\":\"2026-01-01 00:00:00Z\"," +
            "\"trace_id\":\"00000000-0000-0000-0000-000000000000\",\"correlation_id\":\"00000000-0000-0000-0000-000000000000\"," +
            "\"suberror\":\"consent_required\"," +
            "\"claims\":\"{\\\"access_token\\\":{\\\"capolids\\\":{\\\"essential\\\":true,\\\"values\\\":[\\\"00000000-0000-0000-0000-000000000000\\\"]}}}\"}";

        internal static OAuthTokenRequestException ConsentRequired() =>
            new OAuthTokenRequestException(
                $"Got error 'Response status code does not indicate success: 400 (Bad Request).' trying to get OAuth token from Azure AD.\nResponse body: '{ConsentRequiredBody}'",
                HttpStatusCode.BadRequest, ConsentRequiredBody, new HttpRequestException("400 (Bad Request)"));

        // ---- Sign-in -----------------------------------------------------------------------------------

        [TestMethod]
        public void SignIn_AsksForNoTeamsPermissions()
        {
            var options = new Harness().Options;

            Assert.AreEqual("openid email profile", options.Scope);
            Assert.AreEqual(DelegatedGraphConsent.SignInScopes, options.Scope);
            foreach (var teamsOnly in new[] { "Team.ReadBasic.All", "ChannelMessage.Read.All", "offline_access", "graph.microsoft.com" })
            {
                Assert.IsFalse(options.Scope.Contains(teamsOnly), $"A sign-in must not ask for {teamsOnly}.");
            }
        }

        [TestMethod]
        public async Task SignIn_RedirectKeepsTheSignInScopes()
        {
            var harness = new Harness();
            var context = new OwinContext();

            var notification = await harness.RedirectAsync(context);

            Assert.AreEqual(DelegatedGraphConsent.SignInScopes, notification.ProtocolMessage.Scope);
            Assert.IsNull(notification.ProtocolMessage.LoginHint);
            Assert.IsFalse(notification.HandledResponse);
        }

        [TestMethod]
        public async Task SignIn_DoesNotRedeemTheCode_SoMissingTeamsConsentCannotBlockIt()
        {
            // The redeemer fails exactly as the customer's token endpoint did. A sign-in must never call it.
            var harness = new Harness { Redeem = (code, scopes) => throw ConsentRequired() };
            var ticket = Ticket(new AuthenticationProperties { RedirectUri = WebAppUrl });

            await harness.CodeReceivedAsync(ticket);

            Assert.AreEqual(0, harness.RedeemCalls.Count, "A sign-in has nothing to redeem the code for.");
            Assert.IsNull(ticket.Identity.FindFirst(GraphTokenClaims.RefreshToken));
            Assert.AreEqual(WebAppUrl, ticket.Properties.RedirectUri);
            Assert.AreEqual(0, harness.Reports.Count);
        }

        [TestMethod]
        public async Task ApiRequest_StillGetsA401InsteadOfARedirect()
        {
            // RedirectToIdentityProvider was restructured for the Teams connection; the API rule must survive it.
            var harness = new Harness();
            var context = new OwinContext();
            context.Request.Path = new PathString("/api/Reports");
            context.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

            var notification = await harness.RedirectAsync(context);

            Assert.IsTrue(notification.HandledResponse);
            Assert.AreEqual(401, context.Response.StatusCode);
            Assert.AreEqual("true", context.Response.Headers[Startup.SessionExpiredHeader]);
        }

        // ---- The Teams connection: the challenge ------------------------------------------------------

        [TestMethod]
        public async Task TeamsConnect_AsksForTheTeamsPermissions_AsTheSignedInAdmin()
        {
            var harness = new Harness();
            var context = new OwinContext();
            context.Request.User = SignedIn(new Claim(ClaimTypes.Upn, AdminUpn));
            context.Authentication.Challenge(DelegatedGraphConsent.CreateTeamsConnectProperties(), OpenIdConnectAuthenticationDefaults.AuthenticationType);

            var notification = await harness.RedirectAsync(context);

            Assert.AreEqual(DelegatedGraphConsent.TeamsConnectScopes, notification.ProtocolMessage.Scope);
            StringAssert.Contains(notification.ProtocolMessage.Scope, "offline_access");
            StringAssert.Contains(notification.ProtocolMessage.Scope, "https://graph.microsoft.com/Team.ReadBasic.All");
            StringAssert.Contains(notification.ProtocolMessage.Scope, "https://graph.microsoft.com/ChannelMessage.Read.All");
            Assert.AreEqual(AdminUpn, notification.ProtocolMessage.LoginHint);
            Assert.IsFalse(notification.HandledResponse);
        }

        [TestMethod]
        public void TeamsConnect_ReturnsToTheTeamsPermissionsPage_AndNowhereElse()
        {
            var properties = DelegatedGraphConsent.CreateTeamsConnectProperties();

            Assert.AreEqual("/#/admin/teams-permissions", properties.RedirectUri);
            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(properties));
            Assert.IsFalse(DelegatedGraphConsent.IsTeamsConnect(new AuthenticationProperties()));
            Assert.IsFalse(DelegatedGraphConsent.IsTeamsConnect(null));
        }

        [TestMethod]
        public void LoginHint_IsOnlyASignInName()
        {
            Assert.AreEqual(AdminUpn, DelegatedGraphConsent.LoginHintFor(SignedIn(new Claim(ClaimTypes.Upn, AdminUpn))));
            Assert.AreEqual(AdminUpn, DelegatedGraphConsent.LoginHintFor(SignedIn(new Claim("preferred_username", AdminUpn))));

            // A guest has neither, and just gets Entra ID's own account selection.
            Assert.IsNull(DelegatedGraphConsent.LoginHintFor(SignedIn(new Claim(ClaimTypes.Name, "Ada Contoso"))));
            Assert.IsNull(DelegatedGraphConsent.LoginHintFor(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Upn, AdminUpn) }))));
            Assert.IsNull(DelegatedGraphConsent.LoginHintFor(null));
        }

        // ---- The Teams connection: redeeming the code --------------------------------------------------

        [TestMethod]
        public async Task TeamsConnect_WithoutConsent_KeepsTheAdminSignedIn_AndSaysWhy()
        {
            var harness = new Harness { Redeem = (code, scopes) => throw ConsentRequired() };
            var ticket = Ticket(DelegatedGraphConsent.CreateTeamsConnectProperties());

            // The incident: this used to throw, and Katana turned it into a 500 for the whole sign-in.
            await harness.CodeReceivedAsync(ticket);

            Assert.AreEqual(1, harness.RedeemCalls.Count);
            Assert.AreEqual(DelegatedGraphConsent.TeamsConnectScopes, harness.RedeemCalls[0].Scopes);
            Assert.IsNull(ticket.Identity.FindFirst(GraphTokenClaims.RefreshToken));
            Assert.AreEqual(
                "/#/admin/teams-permissions?teamsConnect=consent_required&teamsConnectError=AADSTS65001",
                ticket.Properties.RedirectUri);

            Assert.AreEqual(1, harness.Reports.Count);
            StringAssert.Contains(harness.Reports[0].Message, "consent_required, AADSTS65001");
            StringAssert.Contains(harness.Reports[0].Message, "grant admin consent");
            Assert.IsInstanceOfType(harness.Reports[0].Exception, typeof(OAuthTokenRequestException));
        }

        [TestMethod]
        public async Task TeamsConnect_Succeeds_KeepsTheRefreshTokenInTheEncryptedAuthCookie()
        {
            var token = new RefreshOAuthToken { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh" };
            var harness = new Harness { Redeem = (code, scopes) => Task.FromResult(token) };
            var ticket = Ticket(DelegatedGraphConsent.CreateTeamsConnectProperties());

            await harness.CodeReceivedAsync(ticket);

            Assert.AreEqual("synthetic-refresh", ticket.Identity.FindFirst(GraphTokenClaims.RefreshToken)?.Value);
            Assert.AreEqual(DelegatedGraphConsent.TeamsPermissionsRoute, ticket.Properties.RedirectUri);
            Assert.AreEqual(0, harness.Reports.Count);
        }

        [TestMethod]
        public async Task TeamsConnect_NetworkFailure_IsAGenericFailure()
        {
            var harness = new Harness { Redeem = (code, scopes) => throw new HttpRequestException("Synthetic: the remote name could not be resolved") };
            var ticket = Ticket(DelegatedGraphConsent.CreateTeamsConnectProperties());

            await harness.CodeReceivedAsync(ticket);

            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=failed", ticket.Properties.RedirectUri);
            Assert.IsFalse(harness.Reports[0].Message.Contains("grant admin consent"),
                "A network failure mustn't send an admin off to change consent.");
        }

        [TestMethod]
        public async Task TeamsConnect_WithoutARefreshToken_IsAFailure()
        {
            var harness = new Harness { Redeem = (code, scopes) => Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-access" }) };
            var ticket = Ticket(DelegatedGraphConsent.CreateTeamsConnectProperties());

            await harness.CodeReceivedAsync(ticket);

            Assert.IsNull(ticket.Identity.FindFirst(GraphTokenClaims.RefreshToken));
            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=failed", ticket.Properties.RedirectUri);
        }

        [TestMethod]
        public async Task TeamsConnect_ATelemetryFailureIsNotAConnectionFailure()
        {
            var harness = new Harness
            {
                Redeem = (code, scopes) => throw ConsentRequired(),
                Report = (message, ex) => throw new InvalidOperationException("Synthetic telemetry failure"),
            };
            var ticket = Ticket(DelegatedGraphConsent.CreateTeamsConnectProperties());

            await harness.CodeReceivedAsync(ticket);

            StringAssert.StartsWith(ticket.Properties.RedirectUri, "/#/admin/teams-permissions?teamsConnect=consent_required");
        }

        // ---- The Teams connection: Entra ID refusing at sign-in ----------------------------------------

        [TestMethod]
        public async Task TeamsConnect_DeclinedAtEntra_ReturnsToTheTeamsPage()
        {
            var harness = new Harness();
            var context = new OwinContext();

            var notification = await harness.AuthenticationFailedAsync(context,
                DelegatedGraphConsent.CreateTeamsConnectProperties(),
                "access_denied", "AADSTS65004: User declined to consent to access the app.");

            Assert.IsTrue(notification.HandledResponse);
            Assert.AreEqual(302, context.Response.StatusCode);
            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=access_denied&teamsConnectError=AADSTS65004",
                context.Response.Headers["Location"]);
            Assert.AreEqual(1, harness.Reports.Count);
        }

        [TestMethod]
        public async Task TeamsConnect_AdminApprovalRequired_IsAMissingConsent_NotACancellation()
        {
            // "Need admin approval", then "return to the application" comes back as access_denied - but the
            // admin didn't cancel anything: consent is missing, and saying so is what gets it fixed.
            var harness = new Harness();
            var context = new OwinContext();

            await harness.AuthenticationFailedAsync(context,
                DelegatedGraphConsent.CreateTeamsConnectProperties(),
                "access_denied", "AADSTS90094: The grant requires admin permission.");

            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=consent_required&teamsConnectError=AADSTS90094",
                context.Response.Headers["Location"]);
        }

        [TestMethod]
        public async Task SignIn_Failure_KeepsKatanasDefaultHandling()
        {
            var harness = new Harness();
            var context = new OwinContext();

            var notification = await harness.AuthenticationFailedAsync(context,
                new AuthenticationProperties { RedirectUri = WebAppUrl },
                "access_denied", "AADSTS65004: User declined to consent to access the app.");

            Assert.IsFalse(notification.HandledResponse);
            Assert.IsFalse(notification.Skipped);
            Assert.IsNull(context.Response.Headers["Location"]);
            Assert.AreEqual(0, harness.Reports.Count);
        }

        [TestMethod]
        public void AForeignOrTamperedState_IsNotATeamsConnection()
        {
            var format = new PropertiesDataFormat(new PassThroughProtector());
            var genuine = StateFor(format, DelegatedGraphConsent.CreateTeamsConnectProperties());

            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(DelegatedGraphConsent.ReadStateProperties(genuine, format)));
            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(DelegatedGraphConsent.ReadStateProperties("a=b&" + genuine + "&c=d", format)));

            foreach (var state in new[] { null, "", "not-a-state", "OpenIdConnect.AuthenticationProperties=", "OpenIdConnect.AuthenticationProperties=%%%" })
            {
                Assert.IsNull(DelegatedGraphConsent.ReadStateProperties(state, format), state ?? "(null)");
            }

            Assert.IsNull(DelegatedGraphConsent.ReadStateProperties(genuine, null));
        }

        // ---- Classification and the return address ----------------------------------------------------

        [DataTestMethod]
        [DataRow("invalid_grant", "consent_required", new[] { 65001 }, "consent_required")]
        [DataRow("invalid_grant", null, new[] { 65001 }, "consent_required")]
        [DataRow("consent_required", null, new int[0], "consent_required")]
        [DataRow("invalid_client", null, new[] { 650057 }, "consent_required")]
        [DataRow("access_denied", null, new[] { 90094 }, "consent_required")]
        [DataRow("access_denied", null, new[] { 65004 }, "access_denied")]
        [DataRow("access_denied", null, new int[0], "access_denied")]
        [DataRow("interaction_required", null, new[] { 50076 }, "failed")]
        [DataRow("invalid_grant", null, new[] { 700082 }, "failed")]
        [DataRow(null, null, null, "failed")]
        public void Classify(string error, string subError, int[] codes, string expected)
        {
            Assert.AreEqual(expected, DelegatedGraphConsent.Classify(error, subError, codes));
        }

        [TestMethod]
        public void FailureReturnUri_EchoesOnlyAKnownOutcomeAndAPlainErrorCode()
        {
            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=failed",
                DelegatedGraphConsent.FailureReturnUri(new ConsentFailure("something_else", null)));
            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=failed",
                DelegatedGraphConsent.FailureReturnUri(null));
            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=failed",
                DelegatedGraphConsent.FailureReturnUri(new ConsentFailure("failed", "<script>")));
            Assert.AreEqual("/#/admin/teams-permissions?teamsConnect=failed&teamsConnectError=invalid_grant",
                DelegatedGraphConsent.FailureReturnUri(new ConsentFailure("failed", "invalid_grant")));
        }

        [TestMethod]
        public void TokenFailure_IsDescribedFromEntrasErrorBody()
        {
            var failure = DelegatedGraphConsent.DescribeTokenFailure(ConsentRequired());

            Assert.AreEqual(DelegatedGraphConsent.OutcomeConsentRequired, failure.Outcome);
            Assert.AreEqual("AADSTS65001", failure.ErrorCode);
        }

        // ---- OAuthTokenRequestException ----------------------------------------------------------------

        [TestMethod]
        public void OAuthTokenRequestException_ReadsEntrasErrorFields_AndKeepsTheOldMessage()
        {
            var ex = ConsentRequired();

            Assert.IsInstanceOfType(ex, typeof(ApplicationException), "Existing catch (ApplicationException) blocks must still catch it.");
            StringAssert.StartsWith(ex.Message, "Got error 'Response status code does not indicate success: 400 (Bad Request).' trying to get OAuth token from Azure AD.");
            Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
            Assert.AreEqual("invalid_grant", ex.Error);
            Assert.AreEqual("consent_required", ex.SubError);
            StringAssert.StartsWith(ex.ErrorDescription, "AADSTS65001:");
            CollectionAssert.AreEqual(new[] { 65001 }, ex.ErrorCodes.ToArray());
        }

        [TestMethod]
        public void OAuthTokenRequestException_ToleratesABodyThatIsNotEntrasJson()
        {
            foreach (var body in new[]
            {
                null,
                "",
                "<html><body>502 Bad Gateway</body></html>",
                "[1,2]",
                "{\"error\":{\"nested\":true},\"error_codes\":[\"x\",1.5,99999999999,123456789012345678901234567890,65001]}",
            })
            {
                var ex = new OAuthTokenRequestException("synthetic", HttpStatusCode.BadGateway, body, null);

                Assert.IsNull(ex.Error, body ?? "(null)");
                Assert.IsNull(ex.SubError, body ?? "(null)");
                Assert.IsTrue(ex.ErrorCodes.All(code => code == 65001), body ?? "(null)");
            }
        }

        // ---- SiteTokenAPI: the Graph token the Teams permissions page gets --------------------------------

        [TestMethod]
        public async Task SiteToken_NoStoredToken_IsNoToken_SoThePageOffersToConnect()
        {
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(null, RefreshShouldNotHappen));
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(new RefreshOAuthToken(), RefreshShouldNotHappen));
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { AccessToken = "synthetic-stored-access" }, RefreshShouldNotHappen));
        }

        [TestMethod]
        public async Task SiteToken_MintsAFreshAccessToken()
        {
            string refreshedWith = null;

            var token = await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { RefreshToken = "synthetic-refresh" },
                refreshToken =>
                {
                    refreshedWith = refreshToken;
                    return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-fresh-access", RefreshToken = "synthetic-rotated" });
                });

            Assert.AreEqual("synthetic-refresh", refreshedWith);
            Assert.AreEqual("synthetic-fresh-access", token?.AccessToken);
        }

        [TestMethod]
        public async Task SiteToken_ARefreshTokenThatNoLongerWorks_IsNoToken_NotTheStaleAccessTokenBesideIt()
        {
            // A carried access token expires within an hour. If the refresh token has gone too, handing that stale
            // access token to the page makes Graph reject it and the page can only say "No Teams found".
            var stored = new RefreshOAuthToken { AccessToken = "synthetic-stale-access", RefreshToken = "synthetic-revoked-refresh" };

            var token = await SiteTokenAPIController.MintAccessTokenAsync(stored,
                refreshToken => throw new HttpRequestException("Synthetic: 400 (Bad Request) AADSTS700082"));

            Assert.IsNull(token, "A refresh token that no longer works must read as 'not connected', so the page offers Connect.");
        }

        [TestMethod]
        public async Task SiteToken_ARefreshThatReturnsNoAccessToken_IsNoToken()
        {
            var token = await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { AccessToken = "synthetic-stale-access", RefreshToken = "synthetic-refresh" },
                refreshToken => Task.FromResult(new RefreshOAuthToken()));

            Assert.IsNull(token);
        }

        private static Task<RefreshOAuthToken> RefreshShouldNotHappen(string refreshToken) =>
            throw new AssertFailedException("There was nothing to refresh.");

        // ---- Helpers -----------------------------------------------------------------------------------

        internal static ClaimsPrincipal SignedIn(params Claim[] claims) =>
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));

        private static AuthenticationTicket Ticket(AuthenticationProperties properties) =>
            new AuthenticationTicket(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, AdminUpn), new Claim(ClaimTypes.Upn, AdminUpn) }, "Cookies"),
                properties);

        /// <summary>The OIDC <c>state</c> as Katana 4.2.3 writes it.</summary>
        internal static string StateFor(ISecureDataFormat<AuthenticationProperties> format, AuthenticationProperties properties) =>
            "OpenIdConnect.AuthenticationProperties=" + Uri.EscapeDataString(format.Protect(properties));

        internal sealed class PassThroughProtector : IDataProtector
        {
            public byte[] Protect(byte[] userData) => userData;

            public byte[] Unprotect(byte[] protectedData) => protectedData;
        }

        /// <summary>
        /// The site's real OIDC options, with the token endpoint and telemetry replaced by recorders.
        /// </summary>
        private sealed class Harness
        {
            public Func<string, string, Task<RefreshOAuthToken>> Redeem { get; set; } =
                (code, scopes) => throw new AssertFailedException("The code was not expected to be redeemed.");

            public Action<string, Exception> Report { get; set; }

            public List<(string Code, string Scopes)> RedeemCalls { get; } = new List<(string, string)>();

            public List<(string Message, Exception Exception)> Reports { get; } = new List<(string, Exception)>();

            private OpenIdConnectAuthenticationOptions _options;

            public OpenIdConnectAuthenticationOptions Options => _options ?? (_options = Create());

            private OpenIdConnectAuthenticationOptions Create()
            {
                var capture = new DelegatedGraphTokenCapture(
                    redeemCode: (code, scopes) =>
                    {
                        RedeemCalls.Add((code, scopes));
                        return Redeem(code, scopes);
                    },
                    report: (message, ex) =>
                    {
                        Reports.Add((message, ex));
                        Report?.Invoke(message, ex);
                    });

                var options = Startup.CreateOpenIdConnectOptions(
                    ClientId, "https://login.contoso.example/00000000-0000-0000-0000-000000000000", WebAppUrl, capture, new CookieManager());
                options.StateDataFormat = new PropertiesDataFormat(new PassThroughProtector());
                return options;
            }

            public async Task<RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>> RedirectAsync(IOwinContext context)
            {
                var notification = new RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(context, Options)
                {
                    ProtocolMessage = new OpenIdConnectMessage
                    {
                        RequestType = OpenIdConnectRequestType.Authentication,
                        Scope = Options.Scope,
                    },
                };

                await Options.Notifications.RedirectToIdentityProvider(notification);
                return notification;
            }

            public async Task CodeReceivedAsync(AuthenticationTicket ticket)
            {
                var notification = new AuthorizationCodeReceivedNotification(new OwinContext(), Options)
                {
                    AuthenticationTicket = ticket,
                    Code = "synthetic-code",
                };

                await Options.Notifications.AuthorizationCodeReceived(notification);
            }

            public async Task<AuthenticationFailedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>> AuthenticationFailedAsync(
                IOwinContext context, AuthenticationProperties challenge, string error, string description)
            {
                var response = new OpenIdConnectMessage
                {
                    State = StateFor(Options.StateDataFormat, challenge),
                    Error = error,
                    ErrorDescription = description,
                };

                var notification = new AuthenticationFailedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(context, Options)
                {
                    ProtocolMessage = response,
                    Exception = new OpenIdConnectProtocolException($"Synthetic: {error} {description}"),
                };

                await Options.Notifications.AuthenticationFailed(notification);
                return notification;
            }
        }
    }
}
