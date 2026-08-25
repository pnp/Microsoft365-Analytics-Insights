using App.ControlPanel.Engine;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Regression test for issue #133. The installer "Test Configuration" Teams user-activity
    /// report check must send a valid OData period (<c>period='D7'</c>), not the double-quoted
    /// <c>period=''D7''</c> that the Graph v6 (Kiota) typed builder produces when handed a
    /// pre-quoted value - which Graph rejects with "Syntax error at position 13 in 'period=''D7'''".
    /// </summary>
    [TestClass]
    public class TeamsUserActivityReportUrlTests
    {
        [TestMethod]
        public async Task ReadTeamsUserActivityReport_SendsSingleQuotedODataPeriod()
        {
            // Arrange: a Graph client whose transport captures the SDK-composed request URI and
            // returns an empty CSV report (200). This exercises the real Kiota URL composition -
            // where the bug lives - with no network call and no credentials.
            var handler = new GraphReportResponseHandler(
                HttpStatusCode.OK,
                "Report Refresh Date,User Principal Name\r\n",
                "application/octet-stream");
            var httpClient = new HttpClient(handler);
            var graphClient = new GraphServiceClient(httpClient, new BearerTokenAuthenticationProvider("test-token"));

            // Act: the exact production call used by SolutionInstallVerifier.VerifyUserActivityImport.
            await SolutionInstallVerifier.ReadTeamsUserActivityReportAsync(graphClient);

            // Assert: the typed builder wraps the period in single quotes itself, so the value must
            // be the bare "D7". A pre-quoted "'D7'" yields period=''D7'' (the issue #133 bug).
            Assert.IsNotNull(handler.CapturedUri, "Expected the Graph SDK to send a request.");
            var url = Uri.UnescapeDataString(handler.CapturedUri.AbsoluteUri);
            StringAssert.Contains(url, "getTeamsUserActivityUserDetail(period='D7')",
                $"Period must be valid single-quoted OData. Actual URL: {url}");
            Assert.IsFalse(url.Contains("period=''"),
                $"Period must not be double-quoted (issue #133). Actual URL: {url}");
        }

        [TestMethod]
        public async Task ReadTeamsUserActivityReport_RecognizesNestedUnknownTenantError()
        {
            var responseBody =
                "{\"error\":{\"code\":\"UnknownError\",\"message\":\"{\\\"error\\\":{\\\"code\\\":\\\"UnknownTenantId\\\",\\\"message\\\":\\\"We do not recognize this tenant ID 00000000-0000-0000-0000-000000000000.\\\"}}\"}}";
            var handler = new GraphReportResponseHandler(HttpStatusCode.NotFound, responseBody, "application/json");
            var graphClient = new GraphServiceClient(
                new HttpClient(handler),
                new BearerTokenAuthenticationProvider("test-token"));

            var exception = await CaptureReportExceptionAsync(graphClient);

            Assert.IsTrue(
                SolutionInstallVerifier.IsGraphReportsUnknownTenant(exception),
                $"Expected the nested Graph Reports error code to be recognized. Exception: {exception}");
        }

        [TestMethod]
        public async Task ReadTeamsUserActivityReport_DoesNotClassifyPermissionFailureAsUnknownTenant()
        {
            var responseBody =
                "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}";
            var handler = new GraphReportResponseHandler(HttpStatusCode.Forbidden, responseBody, "application/json");
            var graphClient = new GraphServiceClient(
                new HttpClient(handler),
                new BearerTokenAuthenticationProvider("test-token"));

            var exception = await CaptureReportExceptionAsync(graphClient);

            Assert.IsFalse(
                SolutionInstallVerifier.IsGraphReportsUnknownTenant(exception),
                "A genuine authorization failure must retain the installer's permissions error.");
        }

        private static async Task<Exception> CaptureReportExceptionAsync(GraphServiceClient graphClient)
        {
            Exception exception = null;
            try
            {
                await SolutionInstallVerifier.ReadTeamsUserActivityReportAsync(graphClient);
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception, "Expected the stubbed Graph error response to throw.");
            return exception;
        }

        /// <summary>
        /// Test transport that records the outgoing request URI and returns a stubbed Graph response,
        /// so the typed report call completes without hitting Microsoft Graph.
        /// </summary>
        private sealed class GraphReportResponseHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _body;
            private readonly string _mediaType;

            public GraphReportResponseHandler(HttpStatusCode statusCode, string body, string mediaType)
            {
                _statusCode = statusCode;
                _body = body;
                _mediaType = mediaType;
            }

            public Uri CapturedUri { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedUri = request.RequestUri;
                var response = new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_body, Encoding.UTF8, _mediaType),
                    RequestMessage = request,
                };
                return Task.FromResult(response);
            }
        }
    }
}
