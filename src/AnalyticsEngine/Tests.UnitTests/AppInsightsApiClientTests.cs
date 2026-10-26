using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;

namespace Tests.UnitTests
{
    /// <summary>
    /// The App Insights HTTP client's retry, back-off, transient classification and token-refresh rules,
    /// driven against a stub HttpMessageHandler. Zero network. See issue #374.
    /// </summary>
    [TestClass]
    public class AppInsightsApiClientTests
    {
        private const string ConnectionString =
            "InstrumentationKey=11111111-1111-1111-1111-111111111111;ApplicationId=22222222-2222-2222-2222-222222222222";

        private const string EmptyTableJson =
            "{\"tables\":[{\"name\":\"PrimaryResult\",\"columns\":[{\"name\":\"timestamp\",\"type\":\"datetime\"}],\"rows\":[]}]}";

        /// <summary>Returns a queued sequence of responses and counts the requests it saw.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> _responses = new Queue<Func<HttpResponseMessage>>();

            public int RequestCount { get; private set; }
            public List<string> AuthorizationHeaders { get; } = new List<string>();

            /// <summary>The decoded KQL of every request, in order.</summary>
            public List<string> Queries { get; } = new List<string>();

            /// <summary>When set, answers every request from its decoded KQL instead of the queue.</summary>
            public Func<string, HttpResponseMessage> RespondToQuery { get; set; }

            public StubHandler Then(Func<HttpResponseMessage> response)
            {
                _responses.Enqueue(response);
                return this;
            }

            /// <summary>Used when the queue runs dry - lets a test say "and then always this".</summary>
            public Func<HttpResponseMessage> Fallback { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
                var query = request.RequestUri.Query;
                var kql = query.StartsWith("?query=") ? Uri.UnescapeDataString(query.Substring("?query=".Length)) : query;
                Queries.Add(kql);
                if (RespondToQuery != null)
                {
                    return Task.FromResult(RespondToQuery(kql));
                }
                var factory = _responses.Count > 0 ? _responses.Dequeue() : Fallback;
                if (factory == null)
                {
                    throw new InvalidOperationException("Stub handler ran out of queued responses.");
                }
                return Task.FromResult(factory());
            }
        }

        private static HttpResponseMessage Ok() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmptyTableJson)
        };

        private static HttpResponseMessage Status(HttpStatusCode code, int? retryAfterSeconds = null)
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent("{}") };
            if (retryAfterSeconds.HasValue)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture));
            }
            return response;
        }

        /// <summary>Hands out tokens with a caller-controlled lifetime, counting how often it was asked.</summary>
        private sealed class CountingCredential : TokenCredential
        {
            private readonly TimeSpan _lifetime;
            private int _issued;

            public CountingCredential(TimeSpan lifetime)
            {
                _lifetime = lifetime;
            }

            public int TokensIssued => _issued;

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                _issued++;
                return new AccessToken($"token-{_issued}", DateTimeOffset.UtcNow.Add(_lifetime));
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        /// <summary>Records every retry wait the client asked for, and returns immediately.</summary>
        private sealed class RecordingDelay
        {
            public List<TimeSpan> Requested { get; } = new List<TimeSpan>();

            public Task Wait(TimeSpan delay)
            {
                Requested.Add(delay);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// The retry wait is always replaced, so no test here measures wall-clock time. Elapsed time would
        /// include scheduling, GC and deserialisation as well as the back-off, which makes it both flaky
        /// under CI load and capable of passing after a no-back-off regression.
        /// </summary>
        private static AppInsightsAPIClient NewClient(StubHandler handler, TokenCredential credential = null, RecordingDelay delay = null)
        {
            var client = new AppInsightsAPIClient(ConnectionString, credential ?? new CountingCredential(TimeSpan.FromHours(1)),
                NullLogger.Instance, handler);
            client.RetryDelay = (delay ?? new RecordingDelay()).Wait;
            return client;
        }

        private static readonly DateTime Day = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AppInsightsClient_PartialResults_AreRejectedForBothImports(bool hasRows)
        {
            var tableJson = hasRows
                ? EmptyTableJson.Replace("\"rows\":[]", "\"rows\":[[\"2026-05-30T12:00:00Z\"]]")
                : EmptyTableJson;
            var partial = tableJson.TrimEnd('}')
                + ",\"error\":{\"code\":\"PartialError\",\"message\":\"Query limit reached.\"}}";
            var parsed = JsonConvert.DeserializeObject<AppInsightsQueryResult>(partial);
            Assert.AreEqual(hasRows ? 1 : 0, parsed.Tables[0].Rows.Count);
            Assert.ThrowsException<System.IO.InvalidDataException>(() => { var table = parsed.DefaultTable; });

            var handler = new StubHandler
            {
                Fallback = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(partial) }
            };
            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.GetPageViewsAsync(Day, false));
                await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.GetCustomEventsAsync(Day, false));
            }
        }

        [TestMethod]
        public async Task HealthQueryClient_RejectsPartialAndNullResponsesAtTheHttpBoundary()
        {
            foreach (var body in new[]
            {
                EmptyTableJson.TrimEnd('}') + ",\"error\":{\"code\":\"PartialError\"}}",
                EmptyTableJson.Replace("\"rows\":[]", "\"rows\":[[\"2026-05-30T12:00:00Z\"]]").TrimEnd('}')
                    + ",\"error\":{\"code\":\"PartialError\"}}",
                "null",
            })
            {
                var handler = new StubHandler
                {
                    Fallback = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                };
                using (var client = new DataUtils.AppInsights.AppInsightsQueryClient(ConnectionString,
                    new CountingCredential(TimeSpan.FromHours(1)), NullLogger.Instance, handler))
                {
                    await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.RunQueryAsync("customEvents | count"));
                    await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.RunQueryRawAsync("customEvents | count"));
                }
            }
        }

        [TestMethod]
        public async Task HealthQueryClient_CompleteResponseStillReturnsThePrimaryTable()
        {
            using (var client = new DataUtils.AppInsights.AppInsightsQueryClient(ConnectionString,
                new CountingCredential(TimeSpan.FromHours(1)), NullLogger.Instance, new StubHandler { Fallback = Ok }))
            {
                var table = await client.RunQueryAsync("customEvents | count");
                Assert.AreEqual("PrimaryResult", table.Name);
                Assert.AreEqual(0, table.RowCount);
            }
        }

        /// <summary>
        /// HttpClient reports its timeout as a TaskCanceledException. The Health client used to retry it three times,
        /// so a query App Insights never answered cost four 100-second attempts plus back-off before the page heard.
        /// </summary>
        [TestMethod]
        public async Task HealthQueryClient_ATimedOutQueryIsNotRetried()
        {
            var handler = new StubHandler { Fallback = () => throw new TaskCanceledException("synthetic: HttpClient.Timeout elapsed") };
            using (var client = new DataUtils.AppInsights.AppInsightsQueryClient(ConnectionString,
                new CountingCredential(TimeSpan.FromHours(1)), NullLogger.Instance, handler))
            {
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.RunQueryAsync("customEvents | count"));
                Assert.AreEqual(1, handler.RequestCount);
            }
        }

        [TestMethod]
        public async Task HealthQueryClient_CancellationStopsAQueryThatIsNeverAnswered()
        {
            using (var handler = new NeverAnsweringHandler())
            using (var client = new DataUtils.AppInsights.AppInsightsQueryClient(ConnectionString,
                new CountingCredential(TimeSpan.FromHours(1)), NullLogger.Instance, handler))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await client.RunQueryAsync("customEvents | count", cancellation.Token);
                    Assert.Fail("A query nobody answers cannot succeed.");
                }
                catch (OperationCanceledException)
                {
                }

                Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {watch.Elapsed}.");
            }
        }

        /// <summary>Waits until the request is cancelled - an App Insights endpoint that accepted the connection and went quiet.</summary>
        private sealed class NeverAnsweringHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_DisposesResponsesAfterReadingOrRejectingThem()
        {
            foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden })
            {
                var content = new TrackingContent(EmptyTableJson);
                var handler = new StubHandler().Then(() => new HttpResponseMessage(status) { Content = content });
                using (var client = NewClient(handler))
                {
                    if (status == HttpStatusCode.OK)
                        await client.GetPageViewsAsync(Day, false);
                    else
                        await Assert.ThrowsExceptionAsync<AppInsightsQueryHttpException>(() => client.GetPageViewsAsync(Day, false));
                    Assert.IsTrue(content.Disposed, "The response must not retain its buffered body until a later GC.");
                }
            }
        }

        private sealed class TrackingContent : StringContent
        {
            internal TrackingContent(string value) : base(value) { }
            internal bool Disposed { get; private set; }
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_TransientHttpError_IsRetriedWithExponentialBackoff()
        {
            var handler = new StubHandler()
                .Then(() => Status(HttpStatusCode.ServiceUnavailable))
                .Then(() => Status(HttpStatusCode.ServiceUnavailable))
                .Then(() => Status(HttpStatusCode.ServiceUnavailable))
                .Then(Ok);
            var delay = new RecordingDelay();

            using (var client = NewClient(handler, delay: delay))
            {
                var result = await client.GetPageViewsAsync(Day, false);

                Assert.IsNotNull(result);
                Assert.AreEqual(4, handler.RequestCount, "A 503 must be retried.");

                // 2^0, 2^1, 2^2 seconds. Asserted on the delays the client actually asked for, so a
                // regression that dropped the wait - or made it constant - fails here.
                CollectionAssert.AreEqual(
                    new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
                    delay.Requested.ToArray());
            }
        }

        [TestMethod]
        public void AppInsightsClient_BackoffDoublesThenCapsSoAnOutageCannotStallTheWebJob()
        {
            // Asserted on the pure calculation, not through the retry loop: MaxRetries stops the loop at
            // attempt 4 (16 seconds), so the cap is unreachable from there and a test driven through the
            // loop would stay green if Math.Min were deleted.
            Assert.AreEqual(1, AppInsightsAPIClient.BackoffSecondsFor(0));
            Assert.AreEqual(2, AppInsightsAPIClient.BackoffSecondsFor(1));
            Assert.AreEqual(16, AppInsightsAPIClient.BackoffSecondsFor(4));
            Assert.AreEqual(32, AppInsightsAPIClient.BackoffSecondsFor(5));

            // 2^6 = 64 > 60, so this is the first attempt the cap actually bites on.
            Assert.AreEqual(AppInsightsAPIClient.MaxBackoffSeconds, AppInsightsAPIClient.BackoffSecondsFor(6));
            Assert.AreEqual(AppInsightsAPIClient.MaxBackoffSeconds, AppInsightsAPIClient.BackoffSecondsFor(20));
        }

        [TestMethod]
        public async Task AppInsightsClient_RetryAfterHeader_IsPreferredOverExponentialBackoff()
        {
            // The server said "wait 7 seconds"; exponential back-off would have asked for 1 then 2.
            var handler = new StubHandler()
                .Then(() => Status((HttpStatusCode)429, retryAfterSeconds: 7))
                .Then(() => Status((HttpStatusCode)429, retryAfterSeconds: 7))
                .Then(Ok);
            var delay = new RecordingDelay();

            using (var client = NewClient(handler, delay: delay))
            {
                await client.GetCustomEventsAsync(Day, false);

                Assert.AreEqual(3, handler.RequestCount);
                CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, delay.Requested.ToArray());
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_NonTransientError_IsNotRetried()
        {
            // A 403 means a missing API permission. Retrying it five times just delays the error the
            // operator needs to see.
            var handler = new StubHandler();
            handler.Fallback = () => Status(HttpStatusCode.Forbidden);

            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<AppInsightsQueryHttpException>(() => client.GetPageViewsAsync(Day, false));
                Assert.AreEqual(1, handler.RequestCount);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_TransientErrors_AreCappedAndTheFailureSurfaces()
        {
            var handler = new StubHandler();
            handler.Fallback = () => Status(HttpStatusCode.BadGateway, retryAfterSeconds: 0);

            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<AppInsightsQueryHttpException>(() => client.GetPageViewsAsync(Day, false));

                // The initial attempt plus MaxRetries more, then the failure is returned rather than
                // retried forever - an App Insights outage must not hang the web-job indefinitely.
                Assert.AreEqual(AppInsightsAPIClient.MaxRetries + 1, handler.RequestCount);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_ExpiredToken_IsRefreshedBeforeTheNextCall()
        {
            // The client refreshes when the cached token is within 5 minutes of expiry.
            var shortLived = new CountingCredential(TimeSpan.FromMinutes(1));
            var handler = new StubHandler { Fallback = Ok };

            using (var client = NewClient(handler, shortLived))
            {
                await client.GetPageViewsAsync(Day, false);
                await client.GetPageViewsAsync(Day.AddDays(1), false);

                Assert.AreEqual(2, shortLived.TokensIssued, "A token expiring inside the refresh margin must be re-fetched.");
                Assert.AreEqual("token-1", handler.AuthorizationHeaders[0]);
                Assert.AreEqual("token-2", handler.AuthorizationHeaders[1]);
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_ValidToken_IsReusedRatherThanRefetchedPerRequest()
        {
            var longLived = new CountingCredential(TimeSpan.FromHours(1));
            var handler = new StubHandler { Fallback = Ok };

            using (var client = NewClient(handler, longLived))
            {
                await client.GetPageViewsAsync(Day, false);
                await client.GetCustomEventsAsync(Day, false);
                await client.GetPageViewsAsync(Day.AddDays(1), false);

                Assert.AreEqual(1, longLived.TokensIssued, "A still-valid token must not cost an Entra ID round-trip per request.");
            }
        }

        [TestMethod]
        public void AppInsightsClient_BuildsKqlWindow_ForRequestedDayInUtc_UnderAnyCulture()
        {
            // The current culture picks the CALENDAR, not just the separators: under th-TH the year renders
            // as 2569 and under ar-SA the whole date shifts, and KQL todatetime() then matches nothing -
            // silently, with an empty result set rather than an error. See issue #398.
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (var cultureName in new[] { "th-TH", "ar-SA", "en-US" })
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                    using (var client = NewClient(new StubHandler { Fallback = Ok }))
                    {
                        var where = client.GetWhereString(Day);
                        StringAssert.Contains(where, "timestamp >= todatetime('2026-05-30 00:00:00')", $"culture {cultureName}");
                        StringAssert.Contains(where, "timestamp < todatetime('2026-05-31 00:00:00')", $"culture {cultureName}");
                    }
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        #region Query shape and partial-result splitting

        private static readonly Regex WindowPattern =
            new Regex(@"timestamp >= todatetime\('(?<from>[^']+)'\) and timestamp < todatetime\('(?<to>[^']+)'\)");

        private static (DateTime From, DateTime To) WindowOf(string kql)
        {
            var match = WindowPattern.Match(kql);
            Assert.IsTrue(match.Success, "No time window in: " + kql);
            DateTime Parse(string value) => DateTime.SpecifyKind(
                DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            return (Parse(match.Groups["from"].Value), Parse(match.Groups["to"].Value));
        }

        private static string[] ProjectedColumns(string kql)
        {
            var match = Regex.Match(kql, @"\| project (?<columns>[^|]+)");
            Assert.IsTrue(match.Success, "No project clause in: " + kql);
            return match.Groups["columns"].Value.Split(',').Select(c => c.Trim()).ToArray();
        }

        /// <summary>A 200 response whose table holds <paramref name="rows"/>, optionally with a top-level error.</summary>
        private static HttpResponseMessage Table(string errorCode, string[] columns, IEnumerable<object[]> rows)
        {
            var body = new Dictionary<string, object>
            {
                ["tables"] = new[] { new { name = "PrimaryResult", columns = columns.Select(c => new { name = c, type = "string" }), rows } }
            };
            if (errorCode != null)
            {
                body["error"] = new { code = errorCode, message = "Query limit reached." };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(body)) };
        }

        private static HttpResponseMessage PageViewRows(string errorCode, params DateTime[] timestamps) =>
            Table(errorCode, new[] { "timestamp", "url" },
                timestamps.Select(t => new object[] { t, "https://contoso.sharepoint.com/sites/example/Καλημέρα-" + t.Ticks + ".aspx" }));

        private static HttpResponseMessage PageExitRows(string errorCode, params DateTime[] timestamps) =>
            Table(errorCode, new[] { "timestamp", "customDimensions", "user_AuthenticatedId", "name" },
                timestamps.Select(t => new object[]
                {
                    t,
                    JsonConvert.SerializeObject(new { pageRequestId = Guid.NewGuid(), activeTime = 3 }),
                    "user@contoso.com",
                    "PAGE_EXIT"
                }));

        [TestMethod]
        public async Task AppInsightsClient_QueriesProjectOnlyTheColumnsTheParsersRead()
        {
            // A whole day of every column is what runs into the API's row and response-size caps first.
            var handler = new StubHandler { Fallback = Ok };
            using (var client = NewClient(handler))
            {
                await client.GetPageViewsAsync(Day, false);
                await client.GetCustomEventsAsync(Day, false);
            }

            StringAssert.StartsWith(handler.Queries[0], "pageViews ");
            CollectionAssert.AreEquivalent(
                new[] { "timestamp", "customDimensions", "user_AuthenticatedId", "url", "client_Model", "client_OS", "duration",
                    "client_City", "client_StateOrProvince", "client_CountryOrRegion", "client_Browser" },
                ProjectedColumns(handler.Queries[0]));

            StringAssert.StartsWith(handler.Queries[1], "customEvents ");
            CollectionAssert.AreEquivalent(new[] { "timestamp", "customDimensions", "user_AuthenticatedId", "name" },
                ProjectedColumns(handler.Queries[1]));
            foreach (var eventName in new[] { "PAGE_EXIT", "UserSearch", "LinkClick", "PageMetadataUpdate" })
            {
                StringAssert.Contains(handler.Queries[1], eventName, "Only the custom events the importer reads are requested.");
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_PartialResultForADay_IsSplitInHalf_AndEveryRowIsReturnedOnceInOrder()
        {
            var noon = Day.AddHours(12);
            var handler = new StubHandler
            {
                RespondToQuery = kql =>
                {
                    var window = WindowOf(kql);
                    if (window == (Day, Day.AddDays(1))) return PageViewRows("PartialError", Day.AddHours(1));  // truncated: discarded
                    if (window == (Day, noon)) return PageViewRows(null, Day.AddHours(1), Day.AddHours(11));
                    if (window == (noon, Day.AddDays(1))) return PageViewRows(null, noon, Day.AddHours(23));
                    throw new AssertFailedException("Unexpected window " + window);
                }
            };

            using (var client = NewClient(handler))
            {
                var views = await client.GetPageViewsAsync(Day, false);

                // A row exactly on the split point belongs to the later half only: the windows are half-open.
                CollectionAssert.AreEqual(new[] { Day.AddHours(1), Day.AddHours(11), noon, Day.AddHours(23) },
                    views.Rows.Select(r => r.AppInsightsTimestamp).ToArray());
            }
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        public async Task AppInsightsClient_PartialResultForAHalf_IsSplitAgain()
        {
            var sixAm = Day.AddHours(6);
            var noon = Day.AddHours(12);
            var handler = new StubHandler
            {
                RespondToQuery = kql =>
                {
                    var window = WindowOf(kql);
                    if (window == (Day, Day.AddDays(1)) || window == (Day, noon)) return PageExitRows("PartialError", Day.AddHours(1));
                    if (window == (Day, sixAm)) return PageExitRows(null, Day.AddHours(1));
                    if (window == (sixAm, noon)) return PageExitRows(null, sixAm);
                    if (window == (noon, Day.AddDays(1))) return PageExitRows(null, Day.AddHours(13));
                    throw new AssertFailedException("Unexpected window " + window);
                }
            };

            using (var client = NewClient(handler))
            {
                var events = await client.GetCustomEventsAsync(Day, false);

                CollectionAssert.AreEqual(new[] { Day.AddHours(1), sixAm, Day.AddHours(13) },
                    events.Rows.Select(r => r.AppInsightsTimestamp).ToArray());
            }
            CollectionAssert.AreEqual(
                new[] { (Day, Day.AddDays(1)), (Day, noon), (Day, sixAm), (sixAm, noon), (noon, Day.AddDays(1)) },
                handler.Queries.Select(WindowOf).ToArray());
        }

        [TestMethod]
        public async Task AppInsightsClient_StillPartialAtTheSmallestWindow_FailsRatherThanSavingTruncatedData()
        {
            var handler = new StubHandler { RespondToQuery = kql => PageViewRows("PartialError", Day.AddHours(1)) };

            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.GetPageViewsAsync(Day, false));
            }

            var windows = handler.Queries.Select(WindowOf).ToList();
            Assert.IsTrue(windows.Count > 1, "Smaller windows must be tried before giving up.");
            Assert.IsTrue(windows.Count < 20, "Splitting must stop at a minimum window rather than going on for ever.");
            Assert.IsTrue(windows.All(w => w.To - w.From >= TimeSpan.FromMinutes(1)), "No window is smaller than a minute.");
        }

        [TestMethod]
        public async Task AppInsightsClient_AnErrorThatIsNotAPartialResult_FailsWithoutSplitting()
        {
            var handler = new StubHandler { Fallback = () => PageViewRows("BadArgumentError") };

            using (var client = NewClient(handler))
            {
                await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.GetPageViewsAsync(Day, false));
            }
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task AppInsightsClient_AWindowWithIngestionBounds_FiltersOnIngestionTime_BesideTheTimestampWindow()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                var handler = new StubHandler { Fallback = Ok };
                var window = new AppInsightsReadWindow(Day.AddHours(1), Day.AddHours(2), Day.AddHours(3), Day.AddHours(3).AddMinutes(30));
                using (var client = NewClient(handler))
                {
                    await client.GetPageViewsAsync(window, false);
                    await client.GetCustomEventsAsync(window, false);
                }

                foreach (var kql in handler.Queries)
                {
                    Assert.AreEqual((Day.AddHours(1), Day.AddHours(2)), WindowOf(kql));
                    StringAssert.Contains(kql, "ingestion_time() >= todatetime('2026-05-30 03:00:00')");
                    StringAssert.Contains(kql, "ingestion_time() < todatetime('2026-05-30 03:30:00')");
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [TestMethod]
        public async Task AppInsightsClient_AWindowWithoutIngestionBounds_HasNoIngestionFilter()
        {
            var handler = new StubHandler { Fallback = Ok };
            using (var client = NewClient(handler))
            {
                await client.GetPageViewsAsync(Day, false);
            }
            Assert.IsFalse(handler.Queries.Single().Contains("ingestion_time()"));
        }

        [TestMethod]
        public async Task AppInsightsClient_HourlyRowCounts_CountBothKindsInTheRange_AndAreParsed()
        {
            var range = new AppInsightsReadWindow(Day, Day.AddDays(3), Day.AddDays(1), Day.AddDays(1).AddHours(1));
            var handler = new StubHandler
            {
                Fallback = () => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(new
                    {
                        tables = new[]
                        {
                            new
                            {
                                name = "PrimaryResult",
                                columns = new[] { new { name = "Kind", type = "string" }, new { name = "Hour", type = "datetime" }, new { name = "RowCount", type = "long" } },
                                rows = new[]
                                {
                                    new object[] { "pageview", "2026-05-30T13:00:00Z", 1500 },
                                    new object[] { "event", "2026-05-31T02:00:00Z", 7 },
                                }
                            }
                        }
                    }))
                }
            };

            IReadOnlyList<AppInsightsHourlyRowCount> counts;
            using (var client = NewClient(handler))
            {
                counts = await client.GetHourlyRowCountsAsync(range);
            }

            var kql = handler.Queries.Single();
            StringAssert.StartsWith(kql, "union (pageViews | where ");
            StringAssert.Contains(kql, "(customEvents | where ");
            StringAssert.Contains(kql, "PAGE_EXIT", "Only the custom events the importer reads are counted.");
            Assert.AreEqual(2, Regex.Matches(kql, Regex.Escape("ingestion_time() >= todatetime('2026-05-31 00:00:00')")).Count, "Both legs carry the ingestion range.");
            StringAssert.EndsWith(kql, "| summarize RowCount = count() by Kind, Hour = bin(timestamp, 1h)");

            Assert.AreEqual(2, counts.Count);
            Assert.AreEqual(AppInsightsTelemetryKind.PageViews, counts[0].Kind);
            Assert.AreEqual(new DateTime(2026, 5, 30, 13, 0, 0, DateTimeKind.Utc), counts[0].HourUtc);
            Assert.AreEqual(1500, counts[0].Count);
            Assert.AreEqual(AppInsightsTelemetryKind.Events, counts[1].Kind);
            Assert.AreEqual(new DateTime(2026, 5, 31, 2, 0, 0, DateTimeKind.Utc), counts[1].HourUtc);
        }

        [TestMethod]
        public async Task AppInsightsClient_ARefusedQuery_ReportsItsStatusAndErrorCode_ButNeverTheResponseBody()
        {
            // The day and cycle outcome lines carry the exception chain, so the message must not echo what the API sent.
            var handler = new StubHandler
            {
                Fallback = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"BadArgumentError\",\"message\":\"Query could not be parsed near 'contoso-secret-text'\"}}")
                }
            };

            using (var client = NewClient(handler))
            {
                var ex = await Assert.ThrowsExceptionAsync<AppInsightsQueryHttpException>(() => client.GetPageViewsAsync(Day, false));
                Assert.AreEqual(400, ex.StatusCode);
                Assert.AreEqual("BadArgumentError", ex.ErrorCode);
                Assert.IsFalse(ex.Message.Contains("contoso-secret-text"), ex.Message);
                Assert.AreEqual("400", DataUtils.ExceptionDescriber.Dimensions(ex)["HttpStatus"]);
            }
        }

        #endregion
    }
}
