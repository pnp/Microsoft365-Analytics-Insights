using Common.Entities.Config;
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
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

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
            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart, CopilotInteractionHistoryImporter.DedupWindowStart(new DateTime[0]));
            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart, CopilotInteractionHistoryImporter.DedupWindowStart(null));
            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart, CopilotInteractionHistoryImporter.DedupWindowStart(new[] { default(DateTime) }));
        }

        /// <summary>
        /// The dangerous case, and the one the original guard got wrong: a batch that mixes one unusable
        /// timestamp with normal ones. Skipping the default would compute a window that EXCLUDES the stored
        /// row it duplicates - a missed duplicate, a unique-index violation, and a failed batch. Any default
        /// must therefore make the whole window fall open.
        /// </summary>
        [TestMethod]
        public void DedupWindowStart_MixedDefaultAndRealTimestamps_FallsOpen()
        {
            var real = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart,
                CopilotInteractionHistoryImporter.DedupWindowStart(new[] { real, default(DateTime), real.AddHours(3) }),
                "A single unusable timestamp must widen the window to everything, not be skipped.");

            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart,
                CopilotInteractionHistoryImporter.DedupWindowStart(new[] { default(DateTime), real }),
                "Order must not matter.");
        }

        /// <summary>
        /// The fail-open sentinel must be a value SQL Server can accept as a parameter against
        /// <c>created_utc</c>, which is a <c>datetime</c> (floor 1753-01-01). Returning
        /// <see cref="DateTime.MinValue"/> risked a SqlDateTime overflow - the guard failing CLOSED by
        /// throwing, in precisely the case it exists to handle.
        /// </summary>
        [TestMethod]
        public void UnboundedDedupWindowStart_IsWithinSqlServerDatetimeRange()
        {
            var sentinel = CopilotInteractionHistoryImporter.UnboundedDedupWindowStart;

            Assert.IsTrue(sentinel >= System.Data.SqlTypes.SqlDateTime.MinValue.Value,
                "The sentinel must be >= SQL Server's datetime floor or it cannot be passed as a parameter.");
            Assert.IsTrue(sentinel <= System.Data.SqlTypes.SqlDateTime.MaxValue.Value);
            Assert.IsTrue(sentinel < new DateTime(2000, 1, 1),
                "It must still be early enough to be an effective 'no lower bound'.");
        }

        [TestMethod]
        public void DedupWindowStart_NearTheDatetimeFloor_DoesNotUnderflow()
        {
            var start = CopilotInteractionHistoryImporter.DedupWindowStart(new[] { DateTime.MinValue.AddDays(1) });
            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart, start);

            // A real timestamp just above the floor must not be pushed below it by the margin.
            var justAboveFloor = CopilotInteractionHistoryImporter.UnboundedDedupWindowStart.AddDays(1);
            Assert.AreEqual(CopilotInteractionHistoryImporter.UnboundedDedupWindowStart,
                CopilotInteractionHistoryImporter.DedupWindowStart(new[] { justAboveFloor }));
        }

        [TestMethod]
        public void DedupLookbackMargin_IsGenerousEnoughToBeSafe()
        {
            // The window is anchored to the BATCH's oldest timestamp, not to wall-clock, so outage length is
            // irrelevant. The margin absorbs timestamp re-statement by Graph and SQL Server's datetime
            // rounding (up to 3.33 ms) - milliseconds of real risk. Any value of a day or more is ample;
            // this guards against someone reducing it to zero and removing the safety entirely.
            Assert.IsTrue(CopilotInteractionHistoryImporter.DedupLookbackMarginDays >= 1,
                "The margin must stay non-trivial: it is what absorbs timestamp re-statement and datetime rounding at the window boundary.");
        }

        #endregion

        #region Issue #285 follow-up - the legacy daily activity reports had the same hole

        /// <summary>
        /// The daily usage-report loaders (SharePoint / Teams / OneDrive / Yammer / Exchange) had exactly
        /// the bug #285 described for the Copilot reports: a 403 returned an empty day, which was recorded
        /// as a successfully loaded empty day, after which the whole activity-report phase stamped itself
        /// complete for 24 hours. They now use strict paging, so the failure propagates and the phase is
        /// retried on the next cycle.
        /// </summary>
        [TestMethod]
        public async Task DailyActivityLoader_ForbiddenFromGraph_Throws_InsteadOfRecordingAnEmptyDay()
        {
            using (var handler = new StubHandler(HttpStatusCode.Forbidden, Forbidden))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var loader = new OutlookUserActivityLoader(client, new NoUsersHaveGroupsUserGroupsCache(NullLogger.Instance),
                    new UserGroupsFilterModel(string.Empty), NullLogger.Instance);

                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(
                    () => loader.PopulateLoadedReportPagesFromGraph(1));

                Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
                Assert.AreEqual(0, loader.LoadedReportPages.Count,
                    "A day that failed to download must NOT be recorded as a loaded (empty) day.");
            }
        }

        #endregion

        #region Issue #285 follow-up - what gets PERSISTED must not carry the URL

        [TestMethod]
        public void SummaryWithoutUrl_OmitsTheUrlButKeepsStatusAndErrorCode()
        {
            var ex = new GraphHttpException(HttpStatusCode.Forbidden,
                "https://graph.microsoft.com/v1.0/users/someone@contoso.com/messages", Forbidden, null);

            StringAssert.Contains(ex.Message, "someone@contoso.com", "The full message keeps the URL - it is written to logs, which already record it.");

            var stored = ex.SummaryWithoutUrl;
            Assert.IsFalse(stored.Contains("someone@contoso.com"), "The persisted summary must not carry a user principal name.");
            Assert.IsFalse(stored.Contains("graph.microsoft.com"), "The persisted summary must not carry the URL at all.");
            StringAssert.Contains(stored, "403");
            StringAssert.Contains(stored, "Authorization_RequestDenied");
        }

        [TestMethod]
        public void DescribeForStorage_FallsBackToTheMessageForOrdinaryExceptions()
        {
            Assert.AreEqual("boom", GraphHttpException.DescribeForStorage(new InvalidOperationException("boom")));
            Assert.IsNull(GraphHttpException.DescribeForStorage(null));

            var graphEx = new GraphHttpException(HttpStatusCode.InternalServerError, "https://graph.microsoft.com/x", ServerError, null);
            Assert.AreEqual(graphEx.SummaryWithoutUrl, GraphHttpException.DescribeForStorage(graphEx),
                "A Graph HTTP failure must be stored in its URL-free form.");
        }

        #endregion
    }
}
