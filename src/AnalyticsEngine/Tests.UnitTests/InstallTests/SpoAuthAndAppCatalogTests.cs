using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.SPO.AppCatalog;
using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Engine.SPO.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Covers the SharePoint auth &amp; app-catalog code that replaced OfficeDevPnP.Core
    /// (GetWebLoginClientContext + ALM.AppManager).
    /// </summary>
    [TestClass]
    public class SpoAuthAndAppCatalogTests
    {
        readonly ILogger _logger;

        public SpoAuthAndAppCatalogTests()
        {
            _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("");
        }

        #region Interactive authenticator

        [TestMethod]
        public void BlankClientIdUsesSpoManagementShellApp()
        {
            // The whole point of the default: an admin needs no app registration to install web tracking.
            using (var auth = new InteractiveSpoAuthenticator(clientId: null, tenantId: null, logger: _logger))
            {
                Assert.IsTrue(auth.UsingDefaultClientId);
            }
            using (var auth = new InteractiveSpoAuthenticator(clientId: "   ", tenantId: "  ", logger: _logger))
            {
                Assert.IsTrue(auth.UsingDefaultClientId);
            }
            Assert.AreEqual("9bc3ab49-b65d-410a-85ad-de819febfddc", InteractiveSpoAuthenticator.CLIENTID_SPO_MANAGEMENT_SHELL);
        }

        [TestMethod]
        public void ConfiguredClientIdOverridesTheDefaultApp()
        {
            var config = new SharePointInstallConfig("SPOInsights", "AITracker.js",
                new List<string> { "https://contoso.sharepoint.com/sites/corp" },
                "https://contoso.sharepoint.com/sites/appcatalog")
            {
                AuthClientId = "11111111-1111-1111-1111-111111111111",
                AuthTenantId = "contoso.onmicrosoft.com"
            };

            using (var auth = new InteractiveSpoAuthenticator(config, _logger))
            {
                Assert.IsFalse(auth.UsingDefaultClientId);
            }
        }

        [TestMethod]
        public async Task InvalidSiteUrlIsRejectedBeforeAnySignIn()
        {
            using (var auth = new InteractiveSpoAuthenticator(clientId: null, tenantId: null, logger: _logger))
            {
                await Assert.ThrowsExceptionAsync<ArgumentException>(() => auth.GetAccessTokenAsync(string.Empty));
                await Assert.ThrowsExceptionAsync<ArgumentException>(() => auth.GetAccessTokenAsync("not-a-url"));
            }
        }
        #endregion

        #region Optional-app-registration config validation

        [TestMethod]
        public void AuthClientIdIsOptionalButMustBeAGuidWhenSet()
        {
            var config = new SharePointInstallConfig("SPOInsights", "AITracker.js",
                new List<string> { "https://contoso.sharepoint.com/sites/corp" },
                "https://contoso.sharepoint.com/sites/appcatalog");

            Assert.AreEqual(0, config.ValidatInputAndGetErrors().Count, "Blank AuthClientId must be valid - it means 'use the built-in SPO Management Shell app'");

            config.AuthClientId = "not-a-guid";
            Assert.IsTrue(config.ValidatInputAndGetErrors().Any(e => e.Contains("application ID")));

            // A custom app registration is single-tenant by default, and Entra rejects the multi-tenant
            // /organizations authority for those (AADSTS50194) - so the tenant becomes required.
            config.AuthClientId = " 11111111-1111-1111-1111-111111111111 ";
            Assert.IsTrue(config.ValidatInputAndGetErrors().Any(e => e.Contains("tenant")),
                "A custom client ID without a tenant should be rejected up-front rather than failing at sign-in");

            config.AuthTenantId = "contoso.onmicrosoft.com";
            Assert.AreEqual(0, config.ValidatInputAndGetErrors().Count, "Client ID + tenant should be accepted");
        }

        #endregion

        #region Tenant app catalog REST

        [TestMethod]
        public async Task AddUploadsPackageBytesToTenantAppCatalogAndReturnsAppId()
        {
            var appId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var seenUrls = new List<string>();
            var handler = new FakeHandler(request =>
            {
                seenUrls.Add(Uri.UnescapeDataString(request.RequestUri.ToString()));

                if (request.Method == HttpMethod.Get)
                {
                    // availability poll
                    return Respond(HttpStatusCode.OK, "{\"ID\":\"" + appId + "\"}");
                }

                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("https://contoso.sharepoint.com/sites/appcatalog/_api/web/tenantappcatalog/Add(overwrite=true, url='test.sppkg')",
                    Uri.UnescapeDataString(request.RequestUri.ToString()));
                Assert.AreEqual("Bearer", request.Headers.Authorization.Scheme);
                Assert.AreEqual("fake-token", request.Headers.Authorization.Parameter);
                Assert.IsTrue(request.Headers.Contains("binaryStringRequestBody"), "SharePoint needs this header for a raw-bytes upload");

                return Respond(HttpStatusCode.OK, "{\"UniqueId\":\"" + appId + "\"}");
            });

            var packagePath = Path.Combine(Path.GetTempPath(), "test.sppkg");
            System.IO.File.WriteAllBytes(packagePath, new byte[] { 1, 2, 3 });
            try
            {
                using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
                {
                    var returned = await manager.AddAsync("https://contoso.sharepoint.com/sites/appcatalog/", packagePath);
                    Assert.AreEqual(appId, returned);
                }
            }
            finally
            {
                System.IO.File.Delete(packagePath);
            }

            Assert.IsTrue(seenUrls.Any(u => u.EndsWith($"AvailableApps/GetById('{appId}')")),
                "Add must confirm the app is available before returning, or the caller's Deploy races SharePoint");
        }

        [TestMethod]
        public async Task AddWaitsForTheAppToBecomeAvailableBeforeReturning()
        {
            var appId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var pollCount = 0;
            var handler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    pollCount++;
                    // SharePoint hasn't finished processing the upload yet on the first poll.
                    return pollCount < 2
                        ? Respond(HttpStatusCode.NotFound, "{}")
                        : Respond(HttpStatusCode.OK, "{\"ID\":\"" + appId + "\"}");
                }
                return Respond(HttpStatusCode.OK, "{\"UniqueId\":\"" + appId + "\"}");
            });

            var packagePath = Path.Combine(Path.GetTempPath(), "test-wait.sppkg");
            System.IO.File.WriteAllBytes(packagePath, new byte[] { 1 });
            try
            {
                using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
                {
                    manager.AvailabilityRetryDelay = TimeSpan.FromMilliseconds(1);
                    var returned = await manager.AddAsync("https://contoso.sharepoint.com/sites/appcatalog", packagePath);
                    Assert.AreEqual(appId, returned);
                }
            }
            finally
            {
                System.IO.File.Delete(packagePath);
            }

            Assert.AreEqual(2, pollCount, "Should have retried the availability check after the 404");
        }

        [TestMethod]
        public async Task TransportFailureIsReportedAsAnAppCatalogProblemNotAnUnhandledCrash()
        {
            // A dropped connection / timeout must leave the admin on the "do this step manually" path,
            // rather than escaping as HttpRequestException and aborting the whole SharePoint install.
            var handler = new FakeHandler(_ => throw new HttpRequestException("connection reset"));

            var packagePath = Path.Combine(Path.GetTempPath(), "test-transport.sppkg");
            System.IO.File.WriteAllBytes(packagePath, new byte[] { 1 });
            try
            {
                using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
                {
                    var ex = await Assert.ThrowsExceptionAsync<SpoAppCatalogException>(
                        () => manager.AddAsync("https://contoso.sharepoint.com/sites/appcatalog", packagePath));
                    StringAssert.Contains(ex.Message, "connection reset");
                }
            }
            finally
            {
                System.IO.File.Delete(packagePath);
            }
        }

        [TestMethod]
        public async Task UnreadableUploadResponseIsReportedAsAnAppCatalogProblem()
        {
            var handler = new FakeHandler(_ => Respond(HttpStatusCode.OK, "<html>not json</html>"));

            var packagePath = Path.Combine(Path.GetTempPath(), "test-badjson.sppkg");
            System.IO.File.WriteAllBytes(packagePath, new byte[] { 1 });
            try
            {
                using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
                {
                    await Assert.ThrowsExceptionAsync<SpoAppCatalogException>(
                        () => manager.AddAsync("https://contoso.sharepoint.com/sites/appcatalog", packagePath));
                }
            }
            finally
            {
                System.IO.File.Delete(packagePath);
            }
        }

        [TestMethod]
        public async Task DeployCallsTheDeployEndpointWithSkipFeatureDeployment()
        {
            string body = null;
            var appId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var handler = new FakeHandler(request =>
            {
                Assert.AreEqual($"https://contoso.sharepoint.com/sites/appcatalog/_api/web/tenantappcatalog/AvailableApps/GetById('{appId}')/Deploy",
                    request.RequestUri.ToString());
                body = request.Content.ReadAsStringAsync().Result;
                return Respond(HttpStatusCode.OK, "{}");
            });

            using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
            {
                await manager.DeployAsync("https://contoso.sharepoint.com/sites/appcatalog", appId);
            }
            StringAssert.Contains(body, "skipFeatureDeployment");
            StringAssert.Contains(body.ToLower(), "true");
        }

        [TestMethod]
        public async Task FailedAppCatalogCallSurfacesSharePointsErrorText()
        {
            var handler = new FakeHandler(_ => Respond(HttpStatusCode.Forbidden, "Access denied. You do not have permission."));

            var packagePath = Path.Combine(Path.GetTempPath(), "test-denied.sppkg");
            System.IO.File.WriteAllBytes(packagePath, new byte[] { 1 });
            try
            {
                using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
                {
                    var ex = await Assert.ThrowsExceptionAsync<SpoAppCatalogException>(
                        () => manager.AddAsync("https://contoso.sharepoint.com/sites/appcatalog", packagePath));

                    StringAssert.Contains(ex.Message, "403");
                    StringAssert.Contains(ex.Message, "Access denied");
                    StringAssert.Contains(ex.Message, "SharePoint administrator");
                }
            }
            finally
            {
                System.IO.File.Delete(packagePath);
            }
        }

        [TestMethod]
        public async Task MissingPackageFileFailsBeforeAnyHttpCall()
        {
            var handler = new FakeHandler(_ => throw new AssertFailedException("No HTTP call should be made for a missing package"));
            using (var manager = new TenantAppCatalogManager(new FakeAuthenticator(), _logger, new HttpClient(handler)))
            {
                await Assert.ThrowsExceptionAsync<FileNotFoundException>(
                    () => manager.AddAsync("https://contoso.sharepoint.com/sites/appcatalog", Path.Combine(Path.GetTempPath(), "does-not-exist.sppkg")));
            }
        }

        #endregion

        #region REST error classification

        // The app-catalog preflight in SharePointWebComponentsInstallJob decides what to tell the admin from
        // SpoRestException.Status: only a 404 means "this app catalog isn't there". These two tests pin that
        // distinction down, because if a transport failure ever started reporting a status the installer would
        // go back to telling admins to check a URL that was fine, then carry on as though the install worked.

        [TestMethod]
        public async Task TransportFailureLeavesStatusUnsetSoItIsNotMistakenForAMissingAppCatalog()
        {
            var handler = new FakeHandler(_ => throw new HttpRequestException("the network is down"));
            using (var rest = new SpoRestClient(new FakeAuthenticator(), _logger, new HttpClient(handler)))
            {
                var ex = await Assert.ThrowsExceptionAsync<SpoRestException>(
                    () => rest.GetAsync("https://contoso.sharepoint.com/sites/appcatalog/_api/web?$select=WebTemplate"));

                Assert.IsNull(ex.Status, "A transport failure has no HTTP status, so it must not look like a 404.");
                Assert.IsFalse(ex.IsAccessDenied);
            }
        }

        [TestMethod]
        public async Task MissingResourceIsReportedAs404SoItCanBeToldApartFromAnOutage()
        {
            var handler = new FakeHandler(_ => Respond(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Not found.\"}}"));
            using (var rest = new SpoRestClient(new FakeAuthenticator(), _logger, new HttpClient(handler)))
            {
                var ex = await Assert.ThrowsExceptionAsync<SpoRestException>(
                    () => rest.GetAsync("https://contoso.sharepoint.com/sites/appcatalog/_api/web?$select=WebTemplate"));

                Assert.AreEqual(HttpStatusCode.NotFound, ex.Status);
                Assert.IsFalse(ex.IsAccessDenied);
            }
        }

        [TestMethod]
        public async Task AccessDeniedIsDistinctFromBothMissingAndTransportFailures()
        {
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            {
                var handler = new FakeHandler(_ => Respond(code, "{\"error\":{\"message\":\"Access denied.\"}}"));
                using (var rest = new SpoRestClient(new FakeAuthenticator(), _logger, new HttpClient(handler)))
                {
                    var ex = await Assert.ThrowsExceptionAsync<SpoRestException>(
                        () => rest.GetAsync("https://contoso.sharepoint.com/sites/appcatalog/_api/web"));

                    Assert.IsTrue(ex.IsAccessDenied, $"{code} should be reported as access denied.");
                    Assert.AreNotEqual(HttpStatusCode.NotFound, ex.Status);
                }
            }
        }

        #endregion

        static HttpResponseMessage Respond(HttpStatusCode code, string content)
        {
            return new HttpResponseMessage(code) { Content = new StringContent(content) };
        }

        class FakeAuthenticator : ISpoAuthenticator
        {
            public Task<string> GetAccessTokenAsync(string siteUrl) => Task.FromResult("fake-token");
            public void Dispose() { }
        }

        class FakeHandler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }
    }
}
