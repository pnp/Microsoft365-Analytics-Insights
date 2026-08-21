using Microsoft.Extensions.Logging.Abstractions;
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
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #285: a Graph paging failure used to be reported as a SUCCESSFUL EMPTY import.
    ///
    /// The pageable loader treated any non-gateway-timeout HttpRequestException as "end of results" and
    /// returned the rows gathered so far without throwing. For the Copilot usage reports that meant a 403
    /// from a missing Reports.Read.All grant produced zero rows, which the loader then reported to the
    /// admin as "this tenant has no Microsoft 365 Copilot licences" - while writing a clean import log and
    /// letting the cadence gate mark the section done for 24 hours.
    ///
    /// These tests pin the fix: strict paging rethrows, the exception carries the HTTP status so the
    /// failure is actionable, and the long-standing lenient behaviour is preserved for every other caller.
    /// </summary>
    [TestClass]
    public class GraphPagingFailureTests
    {
        private const string Forbidden =
            "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}";
        private const string ServerError =
            "{\"error\":{\"code\":\"UnknownError\",\"message\":\"An internal server error occurred.\"}}";

        #region Fake HTTP handlers

        /// <summary>Returns the same canned status + body for every request.</summary>
        private class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public int CallCount { get; private set; }

            public StubHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body ?? string.Empty),
                    RequestMessage = request,
                });
            }
        }

        /// <summary>Plays back a scripted sequence of responses, so page 2 can fail after a good page 1.</summary>
        private class SequenceHandler : HttpMessageHandler
        {
            private readonly Queue<Tuple<HttpStatusCode, string>> _responses;
            public int CallCount { get; private set; }

            public SequenceHandler(params Tuple<HttpStatusCode, string>[] responses)
            {
                _responses = new Queue<Tuple<HttpStatusCode, string>>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                var next = _responses.Count > 0
                    ? _responses.Dequeue()
                    : Tuple.Create(HttpStatusCode.OK, "{\"value\":[]}");

                return Task.FromResult(new HttpResponseMessage(next.Item1)
                {
                    Content = new StringContent(next.Item2 ?? string.Empty),
                    RequestMessage = request,
                });
            }
        }

        private static string PageWithNextLink(string nextUrl)
        {
            return "{\"@odata.nextLink\":\"" + nextUrl + "\",\"value\":[{\"reportRefreshDate\":\"2026-08-01\"}]}";
        }

        #endregion

        #region The typed exception

        [TestMethod]
        public async Task Non404Failure_ThrowsGraphHttpException_CarryingStatusAndErrorCode()
        {
            using (var handler = new StubHandler(HttpStatusCode.Forbidden, Forbidden))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                    client.GetAsyncWithThrottleRetries<JObject>("https://graph.microsoft.com/beta/reports/anything"));

                Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode,
                    "The status must survive - HttpRequestException has no StatusCode on .NET Framework, which is why this type exists.");
                Assert.AreEqual("Authorization_RequestDenied", ex.GraphErrorCode);
                StringAssert.Contains(ex.Message, "403", "An admin searching the logs for the status code must find it.");
            }
        }

        [TestMethod]
        public void GraphResourceNotFoundException_IsAGraphHttpException_With404()
        {
            var ex = new GraphResourceNotFoundException("https://graph.microsoft.com/v1.0/x", "{\"error\":{\"code\":\"Request_ResourceNotFound\"}}", null);

            Assert.IsInstanceOfType(ex, typeof(GraphHttpException));
            Assert.IsInstanceOfType(ex, typeof(HttpRequestException), "Existing catch (HttpRequestException) handlers must keep working.");
            Assert.AreEqual(HttpStatusCode.NotFound, ex.StatusCode);
            Assert.AreEqual("Request_ResourceNotFound", ex.GraphErrorCode);
        }

        #endregion

        #region Strict vs lenient paging

        [TestMethod]
        public async Task StrictPaging_InitialForbidden_Throws_InsteadOfReturningAnEmptyResult()
        {
            using (var handler = new StubHandler(HttpStatusCode.Forbidden, Forbidden))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                    client.LoadAllPagesWithThrottleRetries<JObject>("https://graph.microsoft.com/beta/reports/anything",
                        NullLogger.Instance, throwOnNotFound: true, throwOnHttpError: true));

                Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
            }
        }

        [TestMethod]
        public async Task StrictPaging_FailureOnPageTwo_Throws_RatherThanReturningAPartialImport()
        {
            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.OK, PageWithNextLink("https://graph.microsoft.com/beta/reports/anything?page=2")),
                Tuple.Create(HttpStatusCode.InternalServerError, ServerError)))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                    client.LoadAllPagesWithThrottleRetries<JObject>("https://graph.microsoft.com/beta/reports/anything",
                        NullLogger.Instance, throwOnNotFound: true, throwOnHttpError: true));

                Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
                Assert.AreEqual(2, handler.CallCount, "Page 1 succeeded, page 2 failed - and the partial result must NOT be returned.");
            }
        }

        [TestMethod]
        public async Task StrictPaging_GenuinelyEmptyReport_StillSucceeds()
        {
            // The whole point of the fix is to keep "the tenant really has no Copilot licences" reportable
            // as a clean, successful, empty import.
            using (var handler = new StubHandler(HttpStatusCode.OK, "{\"value\":[]}"))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var results = await client.LoadAllPagesWithThrottleRetries<JObject>("https://graph.microsoft.com/beta/reports/anything",
                    NullLogger.Instance, throwOnNotFound: true, throwOnHttpError: true);

                Assert.IsNotNull(results);
                Assert.AreEqual(0, results.Count);
            }
        }

        [TestMethod]
        public async Task LenientPaging_IsUnchanged_ForEveryOtherCaller()
        {
            // Regression guard: the default must still truncate, because callers that treat "fewer rows"
            // as "less data" (rather than as a business outcome) rely on it.
            using (var handler = new StubHandler(HttpStatusCode.Forbidden, Forbidden))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var results = await client.LoadAllPagesWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance);

                Assert.IsNotNull(results);
                Assert.AreEqual(0, results.Count);
            }
        }

        #endregion

        #region The Copilot report source opts in

        [TestMethod]
        public async Task CopilotReportSource_UsesStrictPaging_SoAForbiddenIsNeverSeenAsAnEmptyReport()
        {
            using (var handler = new StubHandler(HttpStatusCode.Forbidden, Forbidden))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var source = new GraphCopilotReportSource(client, NullLogger.Instance);

                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                    source.LoadReportAsync(new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D7")));

                Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode,
                    "A missing Reports.Read.All grant must fail the report import, not report zero licences.");
            }
        }

        #endregion

        #region Issue #294 - the bounded de-duplication window

        [TestMethod]
        public void DedupWindowStart_IsTheOldestInteractionLessTheSafetyMargin()
        {
            var oldest = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
            var stamps = new[] { oldest.AddHours(5), oldest, oldest.AddDays(2) };

            var start = CopilotInteractionHistoryImporter.DedupWindowStart(stamps);

            Assert.AreEqual(oldest.AddDays(-CopilotInteractionHistoryImporter.DedupLookbackMarginDays), start);
            Assert.IsTrue(start < oldest, "The window must reach back BEFORE the batch, never into it.");
        }

        [TestMethod]
        public void DedupWindowStart_WithNoUsableTimestamps_FallsBackToReadingEverything()
        {
            // A missed duplicate hits the unique index and fails the batch, so the safe fallback when we
            // cannot work out a window is the old unbounded behaviour.
            Assert.AreEqual(DateTime.MinValue, CopilotInteractionHistoryImporter.DedupWindowStart(new DateTime[0]));
            Assert.AreEqual(DateTime.MinValue, CopilotInteractionHistoryImporter.DedupWindowStart(null));
            Assert.AreEqual(DateTime.MinValue, CopilotInteractionHistoryImporter.DedupWindowStart(new[] { default(DateTime) }));
        }

        [TestMethod]
        public void DedupWindowStart_NearDateTimeMinValue_DoesNotThrow()
        {
            var start = CopilotInteractionHistoryImporter.DedupWindowStart(new[] { DateTime.MinValue.AddDays(1) });
            Assert.AreEqual(DateTime.MinValue, start);
        }

        [TestMethod]
        public void DedupLookbackMargin_IsGenerousEnoughToBeSafe()
        {
            // The margin exists purely so the bound can never cause a MISS. Shrinking it below a couple of
            // days would start to matter for a tenant whose importer has been stopped over a weekend.
            Assert.IsTrue(CopilotInteractionHistoryImporter.DedupLookbackMarginDays >= 2,
                "A margin under two days risks missing a duplicate after a weekend outage.");
        }

        #endregion
    }
}
