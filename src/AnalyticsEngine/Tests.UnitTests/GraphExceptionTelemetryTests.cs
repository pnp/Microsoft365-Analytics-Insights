using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphExceptionTelemetryTests
    {
        private const string ForbiddenBody =
            "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Denied\",\"innerError\":{\"request-id\":\"00000000-0000-0000-0000-000000000002\"}}}";

        [DataTestMethod]
        [DataRow(
            "https://graph.microsoft.com/v1.0/users/someone@contoso.com/mailFolders/sentitems/messages/delta?$deltatoken=secret-token",
            "GET /v1.0/users/{id}/mailFolders/sentitems/messages/delta")]
        [DataRow(
            "https://graph.microsoft.com/beta/sites/contoso.sharepoint.com,00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002/drive/items/01ABCDEF23456789!123/content",
            "GET /beta/sites/{id}/drive/items/{id}/content")]
        [DataRow(
            "https://graph.microsoft.com/v1.0/shares/u!aHR0cHM6Ly9jb250b3NvLnNoYXJlcG9pbnQuY29t/driveItem",
            "GET /v1.0/shares/{id}/{id}")]
        [DataRow(
            "https://graph.microsoft.com/v1.0/reports/getEmailActivityUserDetail(period='D30')",
            "GET /v1.0/reports/getEmailActivityUserDetail(...)")]
        [DataRow(
            "https://graph.microsoft.com/beta/teams/123/channels/19:thread-token@thread.tacv2/messages/456/",
            "GET /beta/teams/{id}/channels/{id}/messages/{id}/")]
        [DataRow(
            "https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com:/sites/Example/Shared%20Documents/%CE%9A%CE%B1%CE%BB%CE%B7%CE%BC%CE%AD%CF%81%CE%B1%20%CE%BA%CF%8C%CF%83%CE%BC%CE%B5.docx:/",
            "GET /v1.0/sites/{id}/{id}/{id}/{id}/{id}/")]
        public void EndpointTemplate_RemovesIdentifiersQueriesTokensAndCustomerText(string url, string expected)
        {
            Assert.AreEqual(expected, GraphEndpointTemplate.FromUrl("GET", url));
        }

        [TestMethod]
        public async Task ManualGraphFailure_EmitsOneSafeExceptionTelemetryRow()
        {
            var channel = new RecordingTelemetryChannel();
            using (var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), "Office365ActivityImporter");
                var url = "https://graph.microsoft.com/v1.0/users/someone@contoso.com/mailFolders/sentitems/messages/delta?$deltatoken=secret-token";

                using (var handler = new StubHandler(HttpStatusCode.Forbidden, ForbiddenBody, "00000000-0000-0000-0000-000000000003"))
                using (var client = new ManualGraphCallClient(handler, logger))
                {
                    var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                        client.GetAsyncWithThrottleRetries<JObject>(url));

                    logger.LogError(ex, "Caller logged the same Graph exception after catching it.");
                }

                var exception = channel.Sent.OfType<ExceptionTelemetry>().Single();
                Assert.AreEqual("Office365ActivityImporter", exception.Context.Operation.Name);
                Assert.AreEqual("GET /v1.0/users/{id}/mailFolders/sentitems/messages/delta", exception.Properties["GraphEndpoint"]);
                Assert.AreEqual("GET", exception.Properties["HttpMethod"]);
                Assert.AreEqual("403", exception.Properties["HttpStatusCode"]);
                Assert.AreEqual("Authorization_RequestDenied", exception.Properties["GraphErrorCode"]);
                Assert.AreEqual("00000000-0000-0000-0000-000000000003", exception.Properties["GraphRequestId"]);
                StringAssert.Contains(exception.ProblemId, "GET /v1.0/users/{id}/mailFolders/sentitems/messages/delta");
                AssertTelemetryDoesNotContain(exception, "someone@contoso.com");
                AssertTelemetryDoesNotContain(exception, "secret-token");
                AssertTelemetryDoesNotContain(exception, "$deltatoken");
                AssertTelemetryDoesNotContain(exception, "?");
            }
        }

        [TestMethod]
        public async Task ImportCycleScope_StampsTracesEventsAndExceptionsWithTheSameOperationId()
        {
            var channel = new RecordingTelemetryChannel();
            using (var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), "Office365ActivityImporter");
                var operationId = "00000000000000000000000000000616";

                using (logger.BeginOperationScope(operationId))
                {
                    logger.LogInformation("cycle trace");
                    logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle, "cycle event");

                    using (var handler = new StubHandler(HttpStatusCode.BadRequest, ForbiddenBody, null))
                    using (var client = new ManualGraphCallClient(handler, logger))
                    {
                        await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                            client.GetStringAsyncWithThrottleRetries("https://graph.microsoft.com/v1.0/users/00000000-0000-0000-0000-000000000001/messages"));
                    }

                    logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.HealthCheck, new Dictionary<string, string>(), null, "explicit-operation", DateTimeOffset.UtcNow);
                }

                var scopedItems = channel.Sent
                    .Where(t => !(t is EventTelemetry e && e.Name == nameof(AnalyticsLogger.AnalyticsEvent.HealthCheck)))
                    .ToList();
                Assert.IsTrue(scopedItems.OfType<TraceTelemetry>().Any(), "The test must include a trace.");
                Assert.IsTrue(scopedItems.OfType<EventTelemetry>().Any(), "The test must include an event.");
                Assert.IsTrue(scopedItems.OfType<ExceptionTelemetry>().Any(), "The test must include an exception.");
                Assert.IsTrue(scopedItems.All(t => t.Context.Operation.Id == operationId));

                var explicitEvent = channel.Sent.OfType<EventTelemetry>().Single(e => e.Name == nameof(AnalyticsLogger.AnalyticsEvent.HealthCheck));
                Assert.AreEqual("explicit-operation", explicitEvent.Context.Operation.Id, "Existing explicit operation ids must still win.");
            }
        }

        private static void AssertTelemetryDoesNotContain(ExceptionTelemetry telemetry, string forbidden)
        {
            var text = string.Join("|", new[]
            {
                telemetry.ProblemId,
                telemetry.Exception?.Message,
                string.Join("|", telemetry.Properties.Select(p => p.Key + "=" + p.Value))
            }.Where(s => s != null));

            Assert.IsFalse(text.Contains(forbidden), $"Exception telemetry must not contain '{forbidden}'. Actual: {text}");
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            private readonly string _requestId;

            public StubHandler(HttpStatusCode status, string body, string requestId)
            {
                _status = status;
                _body = body;
                _requestId = requestId;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body ?? string.Empty),
                    RequestMessage = request,
                };

                if (!string.IsNullOrEmpty(_requestId))
                {
                    response.Headers.Add("request-id", _requestId);
                }

                return Task.FromResult(response);
            }
        }

        private sealed class RecordingTelemetryChannel : ITelemetryChannel
        {
            private readonly object _gate = new object();
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();

            public IList<ITelemetry> Sent
            {
                get
                {
                    lock (_gate)
                    {
                        return _sent.ToList();
                    }
                }
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(ITelemetry item)
            {
                lock (_gate)
                {
                    _sent.Add(item);
                }
            }

            public void Flush()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
