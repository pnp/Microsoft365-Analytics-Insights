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

        /// <summary>
        /// Issue #664: the optional observer lets a caller keep the lenient partial result and still act on the
        /// failure - the user import uses it to recognise a delta token that Graph has rejected. It must not
        /// change what the load returns.
        /// </summary>
        [TestMethod]
        public async Task LenientPaging_TellsAnOptionalObserverWhichFailureStoppedItAndOnWhichPage()
        {
            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.OK, PageWithNextLink("https://graph.microsoft.com/v1.0/anything?page=2")),
                Tuple.Create(HttpStatusCode.Forbidden, Forbidden)))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var failures = new List<Tuple<HttpRequestException, int>>();

                var results = await client.LoadAllPagesPlusDeltaWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance,
                    deltaLink => Task.CompletedTask,
                    onPageFailed: (ex, page) => failures.Add(Tuple.Create(ex, page)));

                Assert.AreEqual(1, results.Count, "Still the lenient result: the rows from page 1.");
                Assert.AreEqual(1, failures.Count);
                Assert.AreEqual(2, failures[0].Item2, "Page 2 is the one that failed.");
                Assert.AreEqual(HttpStatusCode.Forbidden, ((GraphHttpException)failures[0].Item1).StatusCode);
            }
        }

        [TestMethod]
        public async Task LenientPaging_Observer_IsNotCalledWhenEveryPageLoads_NorWhenStrictPagingThrows()
        {
            var calls = 0;

            using (var handler = new StubHandler(HttpStatusCode.OK, "{\"value\":[]}"))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                await client.LoadAllPagesPlusDeltaWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance,
                    deltaLink => Task.CompletedTask, onPageFailed: (ex, page) => calls++);
            }

            using (var handler = new StubHandler(HttpStatusCode.Forbidden, Forbidden))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                    client.LoadAllPagesPlusDeltaWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance,
                        deltaLink => Task.CompletedTask, throwOnNotFound: true, throwOnHttpError: true, onPageFailed: (ex, page) => calls++));
            }

            Assert.AreEqual(0, calls, "The observer only reports a failure that the lenient path swallowed.");
        }

        #endregion

        #region A 200 page without its value collection

        private const string PageWithoutValueBody = "{\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/anything?$skiptoken=secret-skip-token\"}";

        [TestMethod]
        public async Task PageableResponse_RecordsWhetherTheValueCollectionWasThere()
        {
            // Through the client's own deserialiser, exactly as the pager reads every page.
            Assert.IsTrue(await HasValueCollection("{\"value\":[{\"id\":\"1\"}]}"));
            Assert.IsTrue(await HasValueCollection("{\"value\":[]}"), "An empty page is still a page.");
            Assert.IsFalse(await HasValueCollection(PageWithoutValueBody), "No value collection at all.");
            Assert.IsFalse(await HasValueCollection("{\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/anything?$deltatoken=x\",\"value\":null}"), "\"value\": null.");
        }

        private static async Task<bool> HasValueCollection(string body)
        {
            using (var handler = new StubHandler(HttpStatusCode.OK, body))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var page = await client.GetAsyncWithThrottleRetries<PageableGraphResponseWithDelta<JObject>>("https://graph.microsoft.com/v1.0/anything");
                Assert.IsNotNull(page.PageResults, "Existing callers rely on PageResults never being null.");
                return page.HasValueCollection;
            }
        }

        [TestMethod]
        public async Task LenientPaging_APageWithoutValue_FailsThatPage_AndNeverReachesTheDeltaLink()
        {
            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.OK, PageWithNextLink("https://graph.microsoft.com/v1.0/anything?page=2")),
                Tuple.Create(HttpStatusCode.OK, PageWithoutValueBody),
                Tuple.Create(HttpStatusCode.OK, "{\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/anything?$deltatoken=new\",\"value\":[{\"id\":\"3\"}]}")))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var failures = new List<Tuple<HttpRequestException, int>>();
                var deltaLinks = new List<string>();

                var results = await client.LoadAllPagesPlusDeltaWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance,
                    deltaLink => { deltaLinks.Add(deltaLink); return Task.CompletedTask; },
                    onPageFailed: (ex, page) => failures.Add(Tuple.Create(ex, page)));

                Assert.AreEqual(1, results.Count, "Page 1's rows, as for any failed page.");
                Assert.AreEqual(2, handler.CallCount, "Paging stops at the page that came back without its rows.");
                Assert.AreEqual(0, deltaLinks.Count, "The deltaLink is never reached, so no checkpoint can be taken past the lost page.");
                Assert.AreEqual(2, failures.Single().Item2);
                Assert.AreEqual(2, ((GraphPageWithoutValueException)failures.Single().Item1).PageNumber);
            }
        }

        [TestMethod]
        public async Task LenientPaging_ValueNull_IsAFailedPageToo()
        {
            using (var handler = new StubHandler(HttpStatusCode.OK, "{\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/anything?$deltatoken=new\",\"value\":null}"))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var failedPages = new List<int>();
                var deltaLinks = 0;

                var results = await client.LoadAllPagesPlusDeltaWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance,
                    deltaLink => { deltaLinks++; return Task.CompletedTask; },
                    onPageFailed: (ex, page) => failedPages.Add(page));

                Assert.AreEqual(0, results.Count);
                CollectionAssert.AreEqual(new[] { 1 }, failedPages);
                Assert.AreEqual(0, deltaLinks, "A deltaLink on a page whose rows are unknown is not taken.");
            }
        }

        [TestMethod]
        public async Task StrictPaging_APageWithoutValue_Throws_NamingThePageButNotTheUrl()
        {
            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.OK, PageWithNextLink("https://graph.microsoft.com/beta/reports/anything?$skiptoken=secret-skip-token")),
                Tuple.Create(HttpStatusCode.OK, PageWithoutValueBody)))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var ex = await Assert.ThrowsExceptionAsync<GraphPageWithoutValueException>(() =>
                    client.LoadAllPagesWithThrottleRetries<JObject>("https://graph.microsoft.com/beta/reports/anything",
                        NullLogger.Instance, throwOnNotFound: true, throwOnHttpError: true));

                Assert.AreEqual(2, ex.PageNumber);
                StringAssert.Contains(ex.Message, "page 2");
                Assert.IsFalse(ex.Message.Contains("graph.microsoft.com") || ex.Message.Contains("secret-skip-token"), "The URL can carry a token.");
                Assert.IsInstanceOfType(ex, typeof(HttpRequestException), "Every existing failed-page handler still catches it.");
            }
        }

        [TestMethod]
        public async Task Paging_ANormalMultiPageDeltaRead_IsUnchanged()
        {
            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.OK, PageWithNextLink("https://graph.microsoft.com/v1.0/anything?page=2")),
                Tuple.Create(HttpStatusCode.OK, "{\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/anything?$deltatoken=new\",\"value\":[{\"id\":\"2\"}]}")))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var deltaLinks = new List<string>();
                var failures = 0;

                var results = await client.LoadAllPagesPlusDeltaWithThrottleRetries<JObject>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance,
                    deltaLink => { deltaLinks.Add(deltaLink); return Task.CompletedTask; },
                    throwOnHttpError: true, onPageFailed: (ex, page) => failures++);

                Assert.AreEqual(2, results.Count);
                CollectionAssert.AreEqual(new[] { "https://graph.microsoft.com/v1.0/anything?$deltatoken=new" }, deltaLinks);
                Assert.AreEqual(0, failures);
            }
        }

        /// <summary>The strict usage-report callers: a page without its rows fails the day rather than recording it short.</summary>
        [TestMethod]
        public async Task DailyActivityLoader_PageWithoutValue_Throws_InsteadOfRecordingAPartialDay()
        {
            using (var handler = new StubHandler(HttpStatusCode.OK, PageWithoutValueBody))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var loader = new OutlookUserActivityLoader(client, Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance);

                await Assert.ThrowsExceptionAsync<GraphPageWithoutValueException>(() => loader.PopulateLoadedReportPagesFromGraph(1));

                Assert.AreEqual(0, loader.LoadedReportPages.Count, "A day whose rows are unknown must NOT be recorded as loaded.");
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
                var loader = new OutlookUserActivityLoader(client, Common.Entities.UserScope.UserImportScope.Unfiltered, NullLogger.Instance);

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

            Assert.IsFalse(ex.Message.Contains("someone@contoso.com"), "Even the full message carries the endpoint shape, not the URL: it is logged and surfaced in the Health page.");
            Assert.IsTrue(ex.Message.Contains("/v1.0/users/{id}/messages"), "The endpoint shape stays, so a failure is still attributable.");
            Assert.AreEqual("https://graph.microsoft.com/v1.0/users/someone@contoso.com/messages", ex.Url, "The URL stays on the exception for code that needs it.");

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

        #region Graph call logging: never the URL, never the body

        private sealed class CapturedLog : Microsoft.Extensions.Logging.ILogger
        {
            public List<string> Messages { get; } = new List<string>();
            public List<Microsoft.Extensions.Logging.LogLevel> Levels { get; } = new List<Microsoft.Extensions.Logging.LogLevel>();
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception exception, Func<TState, Exception, string> formatter)
            {
                Levels.Add(logLevel);
                Messages.Add(formatter(state, exception));
            }
        }

        private const string MailboxUrl = "https://graph.microsoft.com/v1.0/users/alex.contoso@contoso.com/mailFolders/sentitems/messages/delta?$select=id,subject&$top=200&$deltatoken=SYNTHETIC-DELTA-TOKEN";
        private const string Subject = "Καλημέρα κόσμε Q3 redundancies";

        private static void AssertNothingSensitiveLogged(CapturedLog log)
        {
            Assert.IsTrue(log.Messages.Count > 0, "The call must still be logged.");
            foreach (var message in log.Messages)
            {
                Assert.IsFalse(message.Contains("alex.contoso@contoso.com"), "No UPN: " + message);
                Assert.IsFalse(message.Contains("SYNTHETIC-DELTA-TOKEN"), "No delta or skip token: " + message);
                Assert.IsFalse(message.Contains("$deltatoken") || message.Contains("$top"), "No query string: " + message);
                Assert.IsFalse(message.Contains(Subject), "No response body: " + message);
            }
        }

        [TestMethod]
        public async Task FailedGraphCall_LogsEndpointShapeStatusAndRequestIds_NeverTheUrlOrBody()
        {
            var body = "{\"error\":{\"code\":\"UnknownError\",\"message\":\"" + Subject + "\"}}";
            var handler = new SequenceHandler(Tuple.Create(HttpStatusCode.BadRequest, body));
            var log = new CapturedLog();
            using (var client = new ManualGraphCallClient(handler, log))
            {
                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() => client.GetAsyncWithThrottleRetries<JObject>(MailboxUrl));
                Assert.IsFalse(ex.Message.Contains("alex.contoso@contoso.com") || ex.Message.Contains("SYNTHETIC-DELTA-TOKEN"), "The exception message is logged too: " + ex.Message);
            }

            AssertNothingSensitiveLogged(log);
            var line = log.Messages.Single(m => m.StartsWith("GraphCallFailed:"));
            StringAssert.Contains(line, "endpoint=GET /v1.0/users/{id}/mailFolders/sentitems/messages/delta");
            StringAssert.Contains(line, "status=400");
            StringAssert.Contains(line, "graphErrorCode=UnknownError");
            StringAssert.Contains(line, "payloadChars=" + body.Length);
        }

        [TestMethod]
        public async Task MalformedOkResponse_IsLoggedWithoutItsBody()
        {
            // A 200 that isn't the JSON we expected can still hold message subjects and bodies.
            var handler = new SequenceHandler(Tuple.Create(HttpStatusCode.OK, "<html>" + Subject + "</html>"));
            var log = new CapturedLog();
            using (var client = new ManualGraphCallClient(handler, log))
            {
                var result = await client.GetAsyncWithThrottleRetries<JObject>(MailboxUrl);
                Assert.IsNull(result, "A body that can't be parsed is no result, as before.");
            }

            AssertNothingSensitiveLogged(log);
            var line = log.Messages.Single(m => m.StartsWith("GraphCallMalformedResponse:"));
            StringAssert.Contains(line, "endpoint=GET /v1.0/users/{id}/mailFolders/sentitems/messages/delta");
            StringAssert.Contains(line, "status=200");
        }

        [TestMethod]
        public async Task NotFound_IsLoggedAtDebugWithoutTheUrl()
        {
            var handler = new SequenceHandler(Tuple.Create(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"MailboxNotEnabledForRESTAPI\",\"message\":\"" + Subject + "\"}}"));
            var log = new CapturedLog();
            using (var client = new ManualGraphCallClient(handler, log))
            {
                await Assert.ThrowsExceptionAsync<GraphResourceNotFoundException>(() => client.GetStringAsyncWithThrottleRetries(MailboxUrl));
            }

            AssertNothingSensitiveLogged(log);
            Assert.IsFalse(log.Levels.Any(l => l >= Microsoft.Extensions.Logging.LogLevel.Warning), "A missing mailbox is expected, not an error.");
        }

        #endregion
    }
}
