extern alias AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using Common.Entities.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>The stable token-refresh regressions, preserving Core's explicit reply contract.</summary>
    [TestClass]
    public class SiteTokenRefreshCoreTests
    {
        [TestMethod]
        public async Task NoStoredRefreshTokenOffersConnectWithoutAttemptingRefresh()
        {
            Task<RefreshOAuthToken> Unexpected(string _) => throw new AssertFailedException("Nothing to refresh.");
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(null, Unexpected));
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(new RefreshOAuthToken(), Unexpected));
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { AccessToken = "synthetic-stale-access" }, Unexpected));
        }

        [TestMethod]
        public async Task RefreshUsesTheStoredRefreshTokenAndReturnsOnlyTheFreshAccessToken()
        {
            string used = null;
            var result = await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { RefreshToken = "synthetic-refresh" }, value =>
                {
                    used = value;
                    return Task.FromResult(new RefreshOAuthToken { AccessToken = "synthetic-fresh", RefreshToken = "synthetic-rotated" });
                });
            Assert.AreEqual("synthetic-refresh", used);
            Assert.AreEqual("synthetic-fresh", result.AccessToken);
        }

        [TestMethod]
        public async Task ARefreshWithoutAnAccessTokenNeverReturnsTheOldOne()
        {
            var result = await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { AccessToken = "synthetic-stale", RefreshToken = "synthetic-refresh" },
                _ => Task.FromResult(new RefreshOAuthToken()));
            Assert.IsNull(result);
        }

        [DataTestMethod]
        [DataRow(503, "{\"error\":\"temporarily_unavailable\",\"error_codes\":[90079]}")]
        [DataRow(500, "<html><body>Synthetic gateway error</body></html>")]
        [DataRow(504, null)]
        [DataRow(429, "{\"error\":\"temporarily_unavailable\"}")]
        [DataRow(429, null)]
        [DataRow(400, "{\"error\":\"temporarily_unavailable\",\"error_description\":\"AADSTS90007: Synthetic.\"}")]
        public async Task ATemporaryIdentityRefusalIs503WithOnlyAStableCode(int status, string body) =>
            await AssertEntraUnavailable(_ => throw Refusal(status, body));

        [TestMethod]
        public async Task AnUnreachableIdentityServiceIs503() =>
            await AssertEntraUnavailable(_ => throw new HttpRequestException("Synthetic connection failure.",
                new WebException("Synthetic DNS failure.", WebExceptionStatus.NameResolutionFailure)));

        [TestMethod]
        public async Task AnIdentityTimeoutIs503() =>
            await AssertEntraUnavailable(_ => throw new TaskCanceledException("Synthetic timeout."));

        [DataTestMethod]
        [DataRow("{\"error\":\"invalid_grant\",\"suberror\":\"bad_token\",\"error_codes\":[70008]}")]
        [DataRow("{\"error\":\"invalid_grant\",\"error_codes\":[700082]}")]
        [DataRow("{\"error\":\"invalid_grant\",\"error_codes\":[50173]}")]
        [DataRow("{\"error\":\"invalid_grant\",\"suberror\":\"basic_action\",\"error_codes\":[50076]}")]
        [DataRow("{\"error\":\"invalid_grant\",\"error_codes\":[53003]}")]
        [DataRow("{\"error\":\"invalid_grant\",\"error_codes\":[70044]}")]
        [DataRow("{\"error\":\"interaction_required\",\"error_codes\":[50079]}")]
        [DataRow("{\"error\":\"invalid_grant\"}")]
        public async Task TokensRequiringInteractiveSignInOfferConnectWithoutReturningStaleAccess(string body)
        {
            var result = await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { AccessToken = "synthetic-stale", RefreshToken = "synthetic-refresh" },
                _ => throw Refusal(400, body));
            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task UnrecognizedFailuresRemainNotConnected()
        {
            var failures = new Func<string, Task<RefreshOAuthToken>>[]
            {
                _ => throw Refusal(401, "{\"error\":\"invalid_client\",\"error_codes\":[7000222]}"),
                _ => throw Refusal(400, "{\"error\":\"invalid_request\",\"error_codes\":[900144]}"),
                _ => throw Refusal(403, "<html>Synthetic proxy page</html>"),
                _ => throw new InvalidOperationException("synthetic"),
                _ => throw new Newtonsoft.Json.JsonReaderException("synthetic"),
            };
            foreach (var failure in failures)
                Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(
                    new RefreshOAuthToken { RefreshToken = "synthetic-refresh" }, failure));
        }

        [TestMethod]
        public async Task RefreshWarningsPreserveClassificationWithoutSecretsOrUserDescriptions()
        {
            const string correlation = "00000000-0000-0000-0000-0000000000c1";
            const string trace = "00000000-0000-0000-0000-0000000000a1";
            var body = "{\"error\":\"invalid_grant\",\"suberror\":\"basic_action\",\"error_codes\":[50076]," +
                "\"error_description\":\"AADSTS50076: synthetic user@contoso.com\"," +
                $"\"trace_id\":\"{trace}\",\"correlation_id\":\"{correlation}\"}}";
            var disconnected = new CapturingLogger();
            Assert.IsNull(await SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { RefreshToken = "synthetic-refresh-secret" },
                _ => throw Refusal(400, body), disconnected));
            var warning = disconnected.Single(Microsoft.Extensions.Logging.LogLevel.Warning);
            foreach (var expected in new[] { "outcome=notConnected401", "remedy=SignInAgain", "httpStatus=400",
                "error=invalid_grant", "suberror=basic_action", "aadstsCodes=[50076]",
                "traceId=" + trace, "correlationId=" + correlation })
                StringAssert.Contains(warning, expected);
            Assert.IsFalse(warning.Contains("synthetic-refresh-secret") || warning.Contains("contoso.com"), warning);

            var unavailable = new CapturingLogger();
            await Assert.ThrowsExceptionAsync<ApiReplyException>(() => SiteTokenAPIController.MintAccessTokenAsync(
                new RefreshOAuthToken { RefreshToken = "synthetic-refresh-secret" },
                _ => throw Refusal(503, "{\"error\":\"temporarily_unavailable\"}"), unavailable));
            var outage = unavailable.Single(Microsoft.Extensions.Logging.LogLevel.Warning);
            foreach (var expected in new[] { "outcome=entraUnavailable503", "remedy=TryAgainLater",
                "httpStatus=503", "error=temporarily_unavailable" })
                StringAssert.Contains(outage, expected);
        }

        private static async Task AssertEntraUnavailable(Func<string, Task<RefreshOAuthToken>> refresh)
        {
            var failure = await Assert.ThrowsExceptionAsync<ApiReplyException>(() =>
                SiteTokenAPIController.MintAccessTokenAsync(new RefreshOAuthToken { RefreshToken = "synthetic-refresh" }, refresh));
            Assert.IsInstanceOfType(failure.Result, typeof(ObjectResult));
            var result = (ObjectResult)failure.Result;
            Assert.AreEqual((int)HttpStatusCode.ServiceUnavailable, result.StatusCode);
            var body = JObject.FromObject(result.Value);
            Assert.AreEqual("entraUnavailable", (string)body["code"]);
            Assert.AreEqual(1, body.Count);
        }

        private static RefreshTokenRequestException Refusal(int status, string body) =>
            new RefreshTokenRequestException("Synthetic token refusal.", (HttpStatusCode)status, body, null);
    }
}
