extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using Common.Entities.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Security.Claims;

namespace Tests.UnitTests
{
    [TestClass]
    public class DelegatedGraphConsentTests
    {
        [TestMethod]
        public void OrdinarySignIn_AsksOnlyForIdentity()
        {
            Assert.AreEqual("openid email profile", DelegatedGraphConsent.SignInScopes);
            Assert.IsFalse(DelegatedGraphConsent.SignInScopes.Contains("offline_access"));
            Assert.IsFalse(DelegatedGraphConsent.SignInScopes.Contains("graph.microsoft.com"));
            Assert.AreEqual(
                DelegatedGraphConsent.SignInScopes,
                DelegatedGraphConsent.ScopesFor(new AuthenticationProperties()));
        }

        [TestMethod]
        public void TeamsConnection_IsMarkedAndReturnsOnlyToTheTeamsPage()
        {
            var properties = DelegatedGraphConsent.CreateTeamsConnectProperties();

            Assert.IsTrue(DelegatedGraphConsent.IsTeamsConnect(properties));
            Assert.AreEqual(DelegatedGraphConsent.TeamsPermissionsRoute, properties.RedirectUri);
            Assert.AreEqual(
                DelegatedGraphConsent.TeamsConnectScopes,
                DelegatedGraphConsent.ScopesFor(properties));
            StringAssert.Contains(DelegatedGraphConsent.TeamsConnectScopes, "offline_access");
            StringAssert.Contains(DelegatedGraphConsent.TeamsConnectScopes, "Team.ReadBasic.All");
            StringAssert.Contains(DelegatedGraphConsent.TeamsConnectScopes, "ChannelMessage.Read.All");
        }

        [TestMethod]
        public void ConfigureChallenge_ChangesOnlyAMarkedTeamsRequest()
        {
            var identity = new ClaimsIdentity("synthetic");
            identity.AddClaim(new Claim("preferred_username", "administrator@contoso.com"));
            var principal = new ClaimsPrincipal(identity);

            var ordinary = new OpenIdConnectMessage { Scope = DelegatedGraphConsent.SignInScopes };
            DelegatedGraphConsent.ConfigureChallenge(new AuthenticationProperties(), ordinary, principal);
            Assert.AreEqual(DelegatedGraphConsent.SignInScopes, ordinary.Scope);
            Assert.IsTrue(string.IsNullOrEmpty(ordinary.LoginHint));

            var teams = new OpenIdConnectMessage();
            DelegatedGraphConsent.ConfigureChallenge(
                DelegatedGraphConsent.CreateTeamsConnectProperties(),
                teams,
                principal);
            Assert.AreEqual(DelegatedGraphConsent.TeamsConnectScopes, teams.Scope);
            Assert.AreEqual("administrator@contoso.com", teams.LoginHint);
        }

        [TestMethod]
        public void TeamsToken_IsCapturedOnlyAfterTheMarkedRoundTrip()
        {
            var properties = DelegatedGraphConsent.CreateTeamsConnectProperties();
            var response = new OpenIdConnectMessage { RefreshToken = "synthetic-refresh-token" };
            var identity = new ClaimsIdentity("synthetic");
            var principal = new ClaimsPrincipal(identity);

            DelegatedGraphConsent.CaptureRefreshToken(properties, response);
            DelegatedGraphConsent.CompleteTokenValidation(properties, principal);

            Assert.AreEqual(
                "synthetic-refresh-token",
                identity.FindFirst(GraphTokenClaims.RefreshToken)?.Value);
            Assert.IsFalse(properties.Items.ContainsKey(DelegatedGraphConsent.RefreshTokenPropertyKey));
        }

        [TestMethod]
        public void OrdinarySignIn_NeverCapturesTheGraphRefreshToken()
        {
            var properties = new AuthenticationProperties();
            var identity = new ClaimsIdentity("synthetic");

            DelegatedGraphConsent.CaptureRefreshToken(
                properties,
                new OpenIdConnectMessage { RefreshToken = "synthetic-refresh-token" });
            DelegatedGraphConsent.CompleteTokenValidation(properties, new ClaimsPrincipal(identity));

            Assert.IsNull(identity.FindFirst(GraphTokenClaims.RefreshToken));
        }

        [DataTestMethod]
        [DataRow("consent_required", null, null, DelegatedGraphConsent.OutcomeConsentRequired)]
        [DataRow("access_denied", null, null, DelegatedGraphConsent.OutcomeAccessDenied)]
        [DataRow("server_error", null, null, DelegatedGraphConsent.OutcomeFailed)]
        [DataRow("invalid_grant", null, "AADSTS65001", DelegatedGraphConsent.OutcomeConsentRequired)]
        [DataRow("invalid_grant", null, "AADSTS65004", DelegatedGraphConsent.OutcomeAccessDenied)]
        public void AuthorizationFailures_GetAStableOutcome(
            string error,
            string subError,
            string description,
            string expected)
        {
            var message = new OpenIdConnectMessage
            {
                Error = error,
                ErrorDescription = description,
            };
            if (subError != null)
            {
                message.SetParameter("suberror", subError);
            }

            Assert.AreEqual(
                expected,
                DelegatedGraphConsent.DescribeAuthorizationFailure(message, null).Outcome);
        }

        [TestMethod]
        public void FailureReturnUri_EchoesOnlyASafeErrorIdentifier()
        {
            Assert.AreEqual(
                "/#/admin/teams-permissions?teamsConnect=consent_required&teamsConnectError=AADSTS65001",
                DelegatedGraphConsent.FailureReturnUri(
                    new ConsentFailure(DelegatedGraphConsent.OutcomeConsentRequired, "AADSTS65001")));

            var unsafeUri = DelegatedGraphConsent.FailureReturnUri(
                new ConsentFailure(DelegatedGraphConsent.OutcomeFailed, "bad code&next=https://contoso.invalid"));
            Assert.IsFalse(unsafeUri.Contains("next="));
            Assert.IsFalse(unsafeUri.Contains("bad code"));
        }

        [TestMethod]
        public void LoginHint_UsesAValidatedSignedInNameOnly()
        {
            var identity = new ClaimsIdentity("synthetic");
            identity.AddClaim(new Claim(ClaimTypes.Upn, "administrator@contoso.com"));
            Assert.AreEqual(
                "administrator@contoso.com",
                DelegatedGraphConsent.LoginHintFor(new ClaimsPrincipal(identity)));

            Assert.IsNull(DelegatedGraphConsent.LoginHintFor(new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }
}
