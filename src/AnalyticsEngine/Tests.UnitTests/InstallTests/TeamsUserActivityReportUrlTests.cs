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
            var handler = new UriCapturingHandler("Report Refresh Date,User Principal Name\r\n");
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

        /// <summary>
        /// Test transport that records the outgoing request URI and returns a stubbed 200 CSV
        /// response, so the typed report call completes without hitting Microsoft Graph.
        /// </summary>
        private sealed class UriCapturingHandler : HttpMessageHandler
        {
            private readonly string _csvBody;

            public UriCapturingHandler(string csvBody)
            {
                _csvBody = csvBody;
            }

            public Uri CapturedUri { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedUri = request.RequestUri;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_csvBody, Encoding.UTF8, "application/octet-stream"),
                    RequestMessage = request,
                };
                return Task.FromResult(response);
            }
        }
    }
}
