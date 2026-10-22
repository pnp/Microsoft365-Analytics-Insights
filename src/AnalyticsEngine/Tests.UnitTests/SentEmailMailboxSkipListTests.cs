using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers the handling of Graph 404s in the sent-email import path.
    ///
    /// Background: users with no Exchange Online mailbox (unlicensed, on-premises, inactive, or guest
    /// accounts) return HTTP 404 from Graph forever. Previously each such user produced three
    /// Application Insights exception records on every 10-minute import cycle, and was re-checked
    /// indefinitely. These tests pin the fix: a 404 is a typed, non-error outcome, it is logged once,
    /// and the user is negatively cached until the next full sweep.
    /// </summary>
    [TestClass]
    public class SentEmailMailboxSkipListTests
    {
        private const string MailboxNotEnabled =
            "{\"error\":{\"code\":\"MailboxNotEnabledForRESTAPI\",\"message\":\"The mailbox is either inactive, soft-deleted, or is hosted on-premise.\"}}";
        private const string ResourceNotFound =
            "{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource 'guest_contoso.com' does not exist.\"}}";
        private const string InternalServerError =
            "{\"error\":{\"code\":\"ErrorInternalServerError\",\"message\":\"The server encountered an internal error.\"}}";
        private const string AccessDenied =
            "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access is denied.\"}}";

        #region Fake HTTP handler

        /// <summary>Returns a canned status code + body for every request.</summary>
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

        /// <summary>Plays back scripted responses so a later page can fail after an earlier page succeeded.</summary>
        private class SequenceHandler : HttpMessageHandler
        {
            private readonly Queue<Tuple<HttpStatusCode, string>> _responses;
            public int CallCount { get; private set; }

            /// <summary>Every requested URL, as the caller built it, in order.</summary>
            public List<string> RequestUrls { get; } = new List<string>();

            public SequenceHandler(params Tuple<HttpStatusCode, string>[] responses)
            {
                _responses = new Queue<Tuple<HttpStatusCode, string>>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                RequestUrls.Add(request.RequestUri.OriginalString);
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

        private static ManualGraphCallClient ClientReturning(HttpStatusCode status, string body, out StubHandler handler)
        {
            handler = new StubHandler(status, body);
            return new ManualGraphCallClient(handler, NullLogger.Instance);
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            return (T)instance.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(instance);
        }

        private sealed class ThrowingSentEmailSourceLoader : ISentEmailSourceLoader
        {
            private readonly Exception _exception;

            public ThrowingSentEmailSourceLoader(Exception exception)
            {
                _exception = exception;
            }

            public Task<bool> HasMailReadAccessAsync()
            {
                return Task.FromResult(true);
            }

            public Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(Common.Entities.User user, bool includeBody)
            {
                var tcs = new TaskCompletionSource<SentEmailLoadResult>();
                tcs.SetException(_exception);
                return tcs.Task;
            }
        }

        #endregion

        #region Strict sent-email paging failures

        [TestMethod]
        public async Task SentEmailSource_PageTwo500_ThrowsAndDoesNotAdvanceDeltaToken()
        {
            var page1 =
                "{\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/alice@contoso.com/mailFolders/sentitems/messages/delta?page=2\"," +
                "\"value\":[{\"id\":\"msg-1\",\"from\":{\"emailAddress\":{\"address\":\"alice@contoso.com\"}}," +
                "\"toRecipients\":[{\"emailAddress\":{\"address\":\"bob@contoso.com\"}}]}]}";

            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.OK, page1),
                Tuple.Create(HttpStatusCode.InternalServerError, InternalServerError)))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var tokenStore = new InMemoryDeltaTokenStore();
                var loader = new GraphSentEmailSourceLoader(
                    client,
                    tokenStore,
                    appIdentity: null,
                    logger: AnalyticsLogger.ConsoleOnlyTracer());

                var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
                var ex = await Assert.ThrowsExceptionAsync<GraphHttpException>(() =>
                    loader.LoadSentEmailsForUserAsync(user, includeBody: false));

                Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
                Assert.AreEqual("ErrorInternalServerError", ex.GraphErrorCode);
                Assert.AreEqual(2, handler.CallCount, "Page 1 succeeded, then page 2 failed.");
                Assert.IsNull(await tokenStore.GetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user)),
                    "A page failure must not advance the delta token because the mailbox load did not complete.");
            }
        }

        [TestMethod]
        public async Task SentEmailSource_PageOne404_StillThrowsTypedNotFoundForSkipList()
        {
            using (var client = ClientReturning(HttpStatusCode.NotFound, MailboxNotEnabled, out var handler))
            {
                var loader = new GraphSentEmailSourceLoader(
                    client,
                    new InMemoryDeltaTokenStore(),
                    appIdentity: null,
                    logger: AnalyticsLogger.ConsoleOnlyTracer());

                var ex = await Assert.ThrowsExceptionAsync<GraphResourceNotFoundException>(() =>
                    loader.LoadSentEmailsForUserAsync(
                        new Common.Entities.User { UserPrincipalName = "nomailbox@contoso.com" },
                        includeBody: false));

                Assert.AreEqual("MailboxNotEnabledForRESTAPI", ex.GraphErrorCode);
                Assert.AreEqual(1, handler.CallCount);
            }
        }

        [TestMethod]
        public async Task SentEmailImporter_PageOne404_AddsUserToNoMailboxSkipList()
        {
            var user = new Common.Entities.User { UserPrincipalName = "nomailbox@contoso.com" };
            var importer = new SentEmailImporter(
                AnalyticsLogger.ConsoleOnlyTracer(),
                new AppConfig(),
                new ThrowingSentEmailSourceLoader(
                    new GraphResourceNotFoundException("https://graph.microsoft.com/v1.0/users/nomailbox@contoso.com/messages", MailboxNotEnabled, null)),
                NullSentEmailSentimentScorer.Instance);

            await importer.ImportSentEmailsForUser(user);

            Assert.AreEqual(1, GetPrivateField<int>(importer, "_mailboxesNotFound"));
            var noMailboxUpns = GetPrivateField<ConcurrentDictionary<string, byte>>(importer, "_noMailboxUpns");
            Assert.IsTrue(noMailboxUpns.ContainsKey("nomailbox@contoso.com"));
        }

        [TestMethod]
        public async Task SentEmailImporter_ForbiddenFailure_IsCountedByStatusAndGraphCodeWithoutUpnInSummary()
        {
            const string failedUrl = "https://graph.microsoft.com/v1.0/users/alice@contoso.com/mailFolders/sentitems/messages/delta";
            var importer = new SentEmailImporter(
                AnalyticsLogger.ConsoleOnlyTracer(),
                new AppConfig(),
                new ThrowingSentEmailSourceLoader(
                    new GraphHttpException(HttpStatusCode.Forbidden, failedUrl, AccessDenied, null)),
                NullSentEmailSentimentScorer.Instance);

            await importer.ImportSentEmailsForUser(new Common.Entities.User { UserPrincipalName = "alice@contoso.com" });

            var failures = GetPrivateField<int>(importer, "_mailboxesFailed");
            var buckets = GetPrivateField<ConcurrentDictionary<string, int>>(importer, "_mailboxFailureBuckets");
            var summary = SentEmailImporter.FormatMailboxFailureSummary(failures, buckets);

            Assert.AreEqual("1 (403 ErrorAccessDenied x1)", summary);
            Assert.IsFalse(summary.Contains("alice@contoso.com"), "The run summary must not contain a UPN.");
            Assert.IsFalse(summary.Contains("/users/"), "The run summary must not contain the Graph URL.");
        }

        [TestMethod]
        public void SentEmailImporter_FailureSummary_IsBoundedAndSorted()
        {
            var buckets = new Dictionary<string, int>
            {
                ["500 ErrorInternalServerError"] = 5,
                ["403 ErrorAccessDenied"] = 2,
                ["HttpRequestException"] = 1,
                ["InvalidOperationException"] = 1,
                ["TaskCanceledException"] = 1,
                ["TimeoutException"] = 1,
            };

            var summary = SentEmailImporter.FormatMailboxFailureSummary(11, buckets, maxBuckets: 3);

            Assert.AreEqual("11 (500 ErrorInternalServerError x5, 403 ErrorAccessDenied x2, HttpRequestException x1, 3 more)", summary);
        }

        #endregion

        #region Rejected sync checkpoints and bounded reads

        private const string StoredToken = "synthetic-stored-token";
        private const string MailboxBase = "https://graph.microsoft.com/v1.0/users/alice%40contoso.com/mailFolders('sentitems')/messages/delta";

        private static Tuple<HttpStatusCode, string> Ok(string body) => Tuple.Create(HttpStatusCode.OK, body);

        private static string DeltaLink(string token) => MailboxBase + "?$deltatoken=" + token;

        private static string SkipLink(string token) => MailboxBase + "?$skiptoken=" + token;

        // A sent-items page. Messages carry only an id, so the importer has nothing to write to SQL for them.
        private static string SentItemsPage(string nextLink, string deltaLink, params string[] messageIds)
        {
            var page = new Newtonsoft.Json.Linq.JObject
            {
                ["value"] = new Newtonsoft.Json.Linq.JArray(messageIds.Select(id => new Newtonsoft.Json.Linq.JObject { ["id"] = id })),
            };
            if (nextLink != null) page["@odata.nextLink"] = nextLink;
            if (deltaLink != null) page["@odata.deltaLink"] = deltaLink;
            return page.ToString();
        }

        private static GraphSentEmailSourceLoader NewLoader(ManualGraphCallClient client, IDeltaTokenStore tokens)
            => new GraphSentEmailSourceLoader(client, tokens, appIdentity: null, logger: AnalyticsLogger.ConsoleOnlyTracer());

        /// <summary>Records every state read, write and delete, as each is an Azure Table round trip in production.</summary>
        private sealed class RecordingDeltaTokenStore : IDeltaTokenStore
        {
            private readonly InMemoryDeltaTokenStore _inner = new InMemoryDeltaTokenStore();

            public ConcurrentQueue<string> Reads { get; } = new ConcurrentQueue<string>();
            public ConcurrentQueue<string> Writes { get; } = new ConcurrentQueue<string>();
            public ConcurrentQueue<string> Deletes { get; } = new ConcurrentQueue<string>();

            /// <summary>Stores a value without recording it.</summary>
            public Task Seed(string key, string value) => _inner.SetDeltaToken(key, value);

            public Task<string> GetDeltaToken(string key)
            {
                Reads.Enqueue(key);
                return _inner.GetDeltaToken(key);
            }

            public Task SetDeltaToken(string key, string deltaToken)
            {
                Writes.Enqueue(key);
                return _inner.SetDeltaToken(key, deltaToken);
            }

            public Task ClearDeltaToken(string key)
            {
                Deletes.Enqueue(key);
                return _inner.ClearDeltaToken(key);
            }
        }

        [TestMethod]
        public async Task SentEmailImporter_SteadyStateIncrementalRead_TouchesOnlyTheTokenKey()
        {
            // At 200,000 mailboxes every extra state call is 200,000 more Table round trips a cycle: an incremental read
            // must neither look for nor delete a continuation that cannot exist.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokenKey = GraphSentEmailSourceLoader.BuildDeltaKey(user);
            var tokens = new RecordingDeltaTokenStore();
            await tokens.Seed(tokenKey, StoredToken);

            using (var handler = new SequenceHandler(Ok(SentItemsPage(null, DeltaLink("next-token"), "msg-1"))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var importer = new SentEmailImporter(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), NewLoader(client, tokens), NullSentEmailSentimentScorer.Instance);

                await importer.ImportSentEmailsForUser(user);

                CollectionAssert.AreEqual(new[] { tokenKey }, tokens.Reads.ToArray(), "One state read, as before bounded reads.");
                CollectionAssert.AreEqual(new[] { tokenKey }, tokens.Writes.ToArray());
                Assert.AreEqual(0, tokens.Deletes.Count, "No continuation was read, so there is none to delete.");
                Assert.AreEqual("next-token", await tokens.GetDeltaToken(tokenKey));
            }
        }

        [TestMethod]
        public async Task SentEmailImporter_IncrementalReadThatOutgrowsACycle_ResumesFromItsContinuation()
        {
            // An incremental round can outgrow one cycle too - after the importer was stopped for a while, say. A load
            // only looks for a continuation when no token is stored, so committing one must retire the token: otherwise
            // every later cycle would restart the round from the token and never finish it.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokenKey = GraphSentEmailSourceLoader.BuildDeltaKey(user);
            var continuationKey = GraphSentEmailSourceLoader.BuildContinuationKey(user);
            var tokens = new RecordingDeltaTokenStore();
            await tokens.Seed(tokenKey, StoredToken);

            using (var handler = new SequenceHandler(
                Ok(SentItemsPage(SkipLink("page-2"), null, "msg-1", "msg-2")),
                Ok(SentItemsPage(null, DeltaLink("round-done"), "msg-3"))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var loader = NewLoader(client, tokens);
                loader.MaxMessagesPerMailboxPerCycle = 2;
                var importer = new SentEmailImporter(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader, NullSentEmailSentimentScorer.Instance);

                await importer.ImportSentEmailsForUser(user);
                StringAssert.Contains(handler.RequestUrls[0], "$deltatoken=" + StoredToken);
                Assert.AreEqual(SkipLink("page-2"), await tokens.GetDeltaToken(continuationKey));
                Assert.IsNull(await tokens.GetDeltaToken(tokenKey), "The token is retired while its round is unfinished.");

                await importer.ImportSentEmailsForUser(user);
                Assert.AreEqual(SkipLink("page-2"), handler.RequestUrls[1], "The next cycle continues the round instead of restarting it.");
                Assert.AreEqual("round-done", await tokens.GetDeltaToken(tokenKey));
                Assert.IsNull(await tokens.GetDeltaToken(continuationKey), "A finished round leaves no continuation behind.");
            }
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.Gone, "{\"error\":{\"code\":\"SyncStateNotFound\",\"message\":\"Synthetic.\"}}", DisplayName = "410 Gone")]
        [DataRow(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"syncStateNotFound\",\"message\":\"Synthetic.\"}}", DisplayName = "404 syncStateNotFound")]
        [DataRow(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"Request_UnsupportedQuery\",\"message\":\"Synthetic.\"}}", DisplayName = "400 Request_UnsupportedQuery")]
        public async Task SentEmailSource_RejectedDeltaToken_IsDiscardedAndTheMailboxReadAgainFromTheStart(HttpStatusCode status, string body)
        {
            // The token used to be sent on every cycle for ever: the mailbox failed every time, and nothing cleared it.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new InMemoryDeltaTokenStore();
            await tokens.SetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user), StoredToken);

            using (var handler = new SequenceHandler(Tuple.Create(status, body), Ok(SentItemsPage(null, DeltaLink("fresh-token"), "msg-1"))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var result = await NewLoader(client, tokens).LoadSentEmailsForUserAsync(user, includeBody: false);

                Assert.AreEqual(2, handler.CallCount, "One rejected request, then one full read in the same call.");
                StringAssert.Contains(handler.RequestUrls[0], "$deltatoken=" + StoredToken);
                Assert.IsFalse(handler.RequestUrls[1].Contains("$deltatoken"), "The re-read must start from scratch, not from any token.");
                Assert.AreEqual(1, result.Messages.Count);
                Assert.AreEqual("fresh-token", result.NextDeltaToken, "The new token is handed back, for the importer to commit once the mail is saved.");
                Assert.IsTrue(result.CheckpointWasReset);
                Assert.IsNull(await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user)), "The dead token is discarded.");
            }
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.Forbidden, AccessDenied, DisplayName = "403")]
        [DataRow(HttpStatusCode.InternalServerError, InternalServerError, DisplayName = "500")]
        [DataRow(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"SyntheticUnrecognisedCode\",\"message\":\"Synthetic.\"}}", DisplayName = "400, unrecognised code")]
        public async Task SentEmailSource_FailureThatSaysNothingAboutTheToken_KeepsIt(HttpStatusCode status, string body)
        {
            // Discarding a token over a permission or server fault would turn it into a full re-read of the mailbox. A
            // delta token rides on a query this loader builds, so an unrecognised 400 may be about the query, not it.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new InMemoryDeltaTokenStore();
            await tokens.SetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user), StoredToken);

            using (var handler = new SequenceHandler(Tuple.Create(status, body)))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                await Assert.ThrowsExceptionAsync<GraphHttpException>(() => NewLoader(client, tokens).LoadSentEmailsForUserAsync(user, includeBody: false));

                Assert.AreEqual(1, handler.CallCount);
                Assert.AreEqual(StoredToken, await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user)));
            }
        }

        [TestMethod]
        public async Task SentEmailSource_RejectionOfALaterPage_KeepsTheToken()
        {
            // Only the first request carried the stored token; a later page follows Graph's own $skiptoken link.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new InMemoryDeltaTokenStore();
            await tokens.SetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user), StoredToken);

            using (var handler = new SequenceHandler(
                Ok(SentItemsPage(SkipLink("page-2"), null, "msg-1")),
                Tuple.Create(HttpStatusCode.Gone, "{\"error\":{\"code\":\"SyncStateNotFound\",\"message\":\"Synthetic.\"}}")))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                await Assert.ThrowsExceptionAsync<GraphHttpException>(() => NewLoader(client, tokens).LoadSentEmailsForUserAsync(user, includeBody: false));

                Assert.AreEqual(2, handler.CallCount, "No re-read: the stored token was not the one refused.");
                Assert.AreEqual(StoredToken, await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user)));
            }
        }

        [TestMethod]
        public async Task SentEmailImporter_SyncStateNotFound404_IsNotMistakenForAUserWithoutAMailbox()
        {
            // A 404 used to mean only "no mailbox", so a dead token put a real mailbox on the skip list as well.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new InMemoryDeltaTokenStore();
            await tokens.SetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user), StoredToken);

            using (var handler = new SequenceHandler(
                Tuple.Create(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"syncStateNotFound\",\"message\":\"Synthetic.\"}}"),
                Ok(SentItemsPage(null, DeltaLink("fresh-token")))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var importer = new SentEmailImporter(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), NewLoader(client, tokens), NullSentEmailSentimentScorer.Instance);

                await importer.ImportSentEmailsForUser(user);

                Assert.AreEqual(0, GetPrivateField<int>(importer, "_mailboxesNotFound"));
                Assert.IsFalse(GetPrivateField<ConcurrentDictionary<string, byte>>(importer, "_noMailboxUpns").ContainsKey(user.UserPrincipalName));
                Assert.AreEqual(0, GetPrivateField<int>(importer, "_mailboxesFailed"));
                Assert.AreEqual("fresh-token", await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user)),
                    "The re-read's token is committed, so the next cycle is an ordinary incremental read.");
            }
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.Gone, "{\"error\":{\"code\":\"SyncStateNotFound\",\"message\":\"Synthetic.\"}}", DisplayName = "410 Gone")]
        [DataRow(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"SyntheticUnrecognisedCode\",\"message\":\"Synthetic.\"}}", DisplayName = "400, any code")]
        public async Task SentEmailSource_RejectedContinuation_IsDiscardedAndTheMailboxReadAgain(HttpStatusCode status, string body)
        {
            // Graph minted the continuation itself, so a 400 for it means the link went stale (say, the importer was
            // stopped for a while) - not that the query is wrong - whatever error code comes with it. While a
            // continuation is stored there is no token: committing one retires it.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new InMemoryDeltaTokenStore();
            await tokens.SetDeltaToken(GraphSentEmailSourceLoader.BuildContinuationKey(user), SkipLink("expired"));

            using (var handler = new SequenceHandler(
                Tuple.Create(status, body),
                Ok(SentItemsPage(null, DeltaLink("fresh-token"), "msg-1"))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var result = await NewLoader(client, tokens).LoadSentEmailsForUserAsync(user, includeBody: false);

                Assert.AreEqual(SkipLink("expired"), handler.RequestUrls[0], "The stored continuation is resumed.");
                Assert.IsFalse(handler.RequestUrls[1].Contains("token="), "The re-read starts from scratch.");
                Assert.AreEqual("fresh-token", result.NextDeltaToken);
                Assert.IsNull(await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildContinuationKey(user)), "The dead continuation is discarded.");
            }
        }

        [DataTestMethod]
        [DataRow(false, DisplayName = "No value property")]
        [DataRow(true, DisplayName = "value: null")]
        public async Task SentEmailImporter_PageWithoutItsValueCollection_FailsTheMailboxAndCommitsNothing(bool explicitNull)
        {
            // Such a page used to read as an empty one: the read went on to the deltaLink and the token advanced past
            // messages it never saw - lost for good.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new RecordingDeltaTokenStore();
            var pageWithoutValue = new Newtonsoft.Json.Linq.JObject { ["@odata.nextLink"] = SkipLink("page-3") };
            if (explicitNull) pageWithoutValue["value"] = Newtonsoft.Json.Linq.JValue.CreateNull();

            Tuple<HttpStatusCode, string>[] Script() => new[]
            {
                Ok(SentItemsPage(SkipLink("page-2"), null, "msg-1")),
                Ok(pageWithoutValue.ToString()),
                Ok(SentItemsPage(null, DeltaLink("must-not-be-stored"), "msg-3")),
            };

            using (var handler = new SequenceHandler(Script()))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => NewLoader(client, tokens).LoadSentEmailsForUserAsync(user, includeBody: false));

                Assert.AreEqual(2, handler.CallCount, "The read stops at the page it cannot trust.");
                StringAssert.Contains(ex.Message, "page 2");
                Assert.IsFalse(ex.Message.Contains("https://") || ex.Message.Contains(user.UserPrincipalName) || ex.Message.Contains("token"),
                    "The message must carry neither the URL, the mailbox nor its sync token.");
            }

            using (var handler = new SequenceHandler(Script()))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var importer = new SentEmailImporter(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), NewLoader(client, tokens), NullSentEmailSentimentScorer.Instance);

                await importer.ImportSentEmailsForUser(user);

                Assert.AreEqual(1, GetPrivateField<int>(importer, "_mailboxesFailed"), "The mailbox fails this cycle, and only this mailbox.");
                Assert.AreEqual(0, tokens.Writes.Count, "Neither a delta token nor a continuation may be committed.");
                Assert.IsNull(await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildDeltaKey(user)));
                Assert.IsNull(await tokens.GetDeltaToken(GraphSentEmailSourceLoader.BuildContinuationKey(user)));
            }
        }

        [TestMethod]
        public async Task SentEmailImporter_LargeFirstRead_IsSpreadOverCyclesAndResumesWhereItStopped()
        {
            // A first read used to pull the whole Sent Items folder - with bodies when sentiment is on - into memory with
            // up to 24 other mailboxes, and to start again from page one after any failure.
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            var tokens = new InMemoryDeltaTokenStore();
            var continuationKey = GraphSentEmailSourceLoader.BuildContinuationKey(user);
            var tokenKey = GraphSentEmailSourceLoader.BuildDeltaKey(user);

            using (var handler = new SequenceHandler(
                Ok(SentItemsPage(SkipLink("page-2"), null, "msg-1", "msg-2")),
                Ok(SentItemsPage(SkipLink("page-3"), null, "msg-3", "msg-4")),
                Ok(SentItemsPage(null, DeltaLink("round-done"), "msg-5")),
                Ok(SentItemsPage(null, DeltaLink("steady-state")))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var loader = NewLoader(client, tokens);
                loader.MaxMessagesPerMailboxPerCycle = 2;
                var importer = new SentEmailImporter(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader, NullSentEmailSentimentScorer.Instance);

                await importer.ImportSentEmailsForUser(user);
                Assert.AreEqual(1, handler.CallCount, "The first cycle stops at its bound instead of reading the whole folder.");
                Assert.AreEqual(SkipLink("page-2"), await tokens.GetDeltaToken(continuationKey));
                Assert.IsNull(await tokens.GetDeltaToken(tokenKey), "No token until the round is complete.");

                await importer.ImportSentEmailsForUser(user);
                Assert.AreEqual(SkipLink("page-2"), handler.RequestUrls[1], "The next cycle continues where the last one stopped.");
                Assert.AreEqual(SkipLink("page-3"), await tokens.GetDeltaToken(continuationKey));

                await importer.ImportSentEmailsForUser(user);
                Assert.AreEqual(SkipLink("page-3"), handler.RequestUrls[2]);
                Assert.AreEqual("round-done", await tokens.GetDeltaToken(tokenKey));
                Assert.IsNull(await tokens.GetDeltaToken(continuationKey), "A finished round leaves no continuation behind.");

                await importer.ImportSentEmailsForUser(user);
                StringAssert.Contains(handler.RequestUrls[3], "$deltatoken=round-done", "From then on it is the ordinary incremental read.");
                Assert.AreEqual(5, GetPrivateField<int>(importer, "_messagesSeen"), "Every message was read exactly once.");
            }
        }

        [TestMethod]
        public async Task SentEmailSource_BodiesAreReadInSmallerBounds()
        {
            var user = new Common.Entities.User { UserPrincipalName = "alice@contoso.com" };
            using (var handler = new SequenceHandler(
                Ok(SentItemsPage(SkipLink("page-2"), null, "msg-1", "msg-2")),
                Ok(SentItemsPage(null, DeltaLink("round-done"), "msg-3"))))
            using (var client = new ManualGraphCallClient(handler, NullLogger.Instance))
            {
                var loader = NewLoader(client, new InMemoryDeltaTokenStore());
                loader.MaxMessagesPerMailboxPerCycle = 10;
                loader.MaxMessagesWithBodyPerMailboxPerCycle = 2;

                var result = await loader.LoadSentEmailsForUserAsync(user, includeBody: true);

                Assert.AreEqual(2, result.Messages.Count);
                Assert.AreEqual(SkipLink("page-2"), result.NextPageLink);
                Assert.IsNull(result.NextDeltaToken);
            }
        }

        #endregion

        #region 404 classification

        [TestMethod]
        public async Task Get404_ThrowsTypedNotFoundException_WithGraphErrorCode()
        {
            using (var client = ClientReturning(HttpStatusCode.NotFound, MailboxNotEnabled, out _))
            {
                try
                {
                    await client.GetAsyncWithThrottleRetries<object>("https://graph.microsoft.com/v1.0/users/nobody/mailFolders/sentitems/messages/delta");
                    Assert.Fail("Expected a GraphResourceNotFoundException.");
                }
                catch (GraphResourceNotFoundException ex)
                {
                    Assert.AreEqual("MailboxNotEnabledForRESTAPI", ex.GraphErrorCode);
                    StringAssert.Contains(ex.Message, "404");
                }
            }
        }

        [TestMethod]
        public async Task Get404_TypedExceptionIsStillAnHttpRequestException()
        {
            // Existing callers catch HttpRequestException - that must keep working.
            using (var client = ClientReturning(HttpStatusCode.NotFound, ResourceNotFound, out _))
            {
                try
                {
                    await client.GetAsyncWithThrottleRetries<object>("https://graph.microsoft.com/v1.0/users/guest/messages");
                    Assert.Fail("Expected an exception.");
                }
                catch (HttpRequestException ex)
                {
                    Assert.IsInstanceOfType(ex, typeof(GraphResourceNotFoundException));
                    Assert.AreEqual("Request_ResourceNotFound", ((GraphResourceNotFoundException)ex).GraphErrorCode);
                }
            }
        }

        [TestMethod]
        public async Task NonNotFoundStatus_StillThrowsPlainHttpRequestException()
        {
            using (var client = ClientReturning(HttpStatusCode.InternalServerError, "boom", out _))
            {
                try
                {
                    await client.GetAsyncWithThrottleRetries<object>("https://graph.microsoft.com/v1.0/users/someone/messages");
                    Assert.Fail("Expected an exception.");
                }
                catch (HttpRequestException ex)
                {
                    Assert.IsNotInstanceOfType(ex, typeof(GraphResourceNotFoundException),
                        "Only a 404 should be classified as 'resource not found'.");
                }
            }
        }

        [TestMethod]
        public void ExtractGraphErrorCode_HandlesJunkWithoutThrowing()
        {
            Assert.IsNull(GraphResourceNotFoundException.ExtractGraphErrorCode(null));
            Assert.IsNull(GraphResourceNotFoundException.ExtractGraphErrorCode(""));
            Assert.IsNull(GraphResourceNotFoundException.ExtractGraphErrorCode("   "));
            Assert.IsNull(GraphResourceNotFoundException.ExtractGraphErrorCode("<html>404</html>"));
            Assert.IsNull(GraphResourceNotFoundException.ExtractGraphErrorCode("{\"unexpected\":true}"));
            Assert.AreEqual("MailboxNotEnabledForRESTAPI", GraphResourceNotFoundException.ExtractGraphErrorCode(MailboxNotEnabled));
        }

        #endregion

        #region Pageable loader opt-in

        [TestMethod]
        public async Task PageableLoader_ByDefault_Swallows404AndReturnsPartialResults()
        {
            // Every pre-existing caller (usage reports, user loaders) relies on this behaviour.
            using (var client = ClientReturning(HttpStatusCode.NotFound, MailboxNotEnabled, out var handler))
            {
                var results = await client.LoadAllPagesWithThrottleRetries<object>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance);

                Assert.IsNotNull(results);
                Assert.AreEqual(0, results.Count);
                Assert.AreEqual(1, handler.CallCount, "A 404 is terminal - it must never be retried.");
            }
        }

        [TestMethod]
        public async Task PageableLoader_WithThrowOnNotFound_PropagatesToCaller()
        {
            using (var client = ClientReturning(HttpStatusCode.NotFound, MailboxNotEnabled, out var handler))
            {
                await Assert.ThrowsExceptionAsync<GraphResourceNotFoundException>(() =>
                    client.LoadAllPagesWithThrottleRetries<object>("https://graph.microsoft.com/v1.0/anything", NullLogger.Instance, throwOnNotFound: true));

                Assert.AreEqual(1, handler.CallCount);
            }
        }

        #endregion

        #region Skip list store

        [TestMethod]
        public async Task InMemorySkipList_RoundTrips()
        {
            var store = new InMemorySentEmailMailboxSkipList();

            var initial = await store.LoadAsync();
            Assert.IsNull(initial.GeneratedUtc);
            Assert.AreEqual(0, initial.Upns.Count);

            var when = new DateTime(2026, 08, 19, 09, 00, 00, DateTimeKind.Utc);
            await store.SaveAsync(new MailboxSkipList { GeneratedUtc = when, Upns = new List<string> { "a@contoso.com", "b@contoso.com" } });

            var loaded = await store.LoadAsync();
            Assert.AreEqual(when, loaded.GeneratedUtc);
            CollectionAssert.AreEquivalent(new[] { "a@contoso.com", "b@contoso.com" }, loaded.Upns);
        }

        [TestMethod]
        public async Task InMemorySkipList_ReturnsDefensiveCopy()
        {
            var store = new InMemorySentEmailMailboxSkipList();
            await store.SaveAsync(new MailboxSkipList { GeneratedUtc = DateTime.UtcNow, Upns = new List<string> { "a@contoso.com" } });

            var first = await store.LoadAsync();
            first.Upns.Add("mutated@contoso.com");

            var second = await store.LoadAsync();
            Assert.AreEqual(1, second.Upns.Count, "Mutating a loaded copy must not corrupt the stored set.");
        }

        [TestMethod]
        public void UpnSet_IsCaseInsensitive()
        {
            var list = new MailboxSkipList { Upns = new List<string> { "Person@Contoso.com" } };
            Assert.IsTrue(list.UpnSet.Contains("person@contoso.com"),
                "UPN casing varies between Graph and the DB, so matching must be case-insensitive.");
        }

        #endregion

        #region Skip list update semantics

        private static readonly DateTime Now = new DateTime(2026, 08, 19, 12, 00, 00, DateTimeKind.Utc);

        [TestMethod]
        public void BuildUpdatedSkipList_FullSweep_ReplacesSetAndStampsTime()
        {
            var previous = new MailboxSkipList
            {
                GeneratedUtc = Now.AddDays(-1),
                Upns = new List<string> { "stale@contoso.com" },
            };

            var updated = SentEmailImporter.BuildUpdatedSkipList(
                previous,
                discoveredNoMailbox: new[] { "nomailbox@contoso.com" },
                skippedThisRun: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                fullSweepDue: true,
                nowUtc: Now);

            Assert.AreEqual(Now, updated.GeneratedUtc, "A full sweep must move the timestamp on.");
            CollectionAssert.AreEquivalent(new[] { "nomailbox@contoso.com" }, updated.Upns);
            CollectionAssert.DoesNotContain(updated.Upns, "stale@contoso.com",
                "A user who has since been licensed must drop out of the skip list after a full sweep.");
        }

        [TestMethod]
        public void BuildUpdatedSkipList_Incremental_CarriesSkippedForwardAndKeepsTimestamp()
        {
            var swept = Now.AddHours(-3);
            var previous = new MailboxSkipList { GeneratedUtc = swept, Upns = new List<string> { "known@contoso.com" } };

            var updated = SentEmailImporter.BuildUpdatedSkipList(
                previous,
                discoveredNoMailbox: new[] { "newlyfound@contoso.com" },
                skippedThisRun: new HashSet<string>(new[] { "known@contoso.com" }, StringComparer.OrdinalIgnoreCase),
                fullSweepDue: false,
                nowUtc: Now);

            Assert.AreEqual(swept, updated.GeneratedUtc,
                "An incremental run must not push the next full sweep out, or users would never be re-checked.");
            CollectionAssert.AreEquivalent(new[] { "known@contoso.com", "newlyfound@contoso.com" }, updated.Upns);
        }

        [TestMethod]
        public void BuildUpdatedSkipList_DeduplicatesCaseInsensitively()
        {
            var updated = SentEmailImporter.BuildUpdatedSkipList(
                new MailboxSkipList { GeneratedUtc = Now.AddHours(-1) },
                discoveredNoMailbox: new[] { "Person@Contoso.com" },
                skippedThisRun: new HashSet<string>(new[] { "person@contoso.com" }, StringComparer.OrdinalIgnoreCase),
                fullSweepDue: false,
                nowUtc: Now);

            Assert.AreEqual(1, updated.Upns.Count);
        }

        [TestMethod]
        public void BuildUpdatedSkipList_NeverSweptBefore_StampsNow()
        {
            var updated = SentEmailImporter.BuildUpdatedSkipList(
                MailboxSkipList.Empty(),
                discoveredNoMailbox: Enumerable.Empty<string>(),
                skippedThisRun: null,
                fullSweepDue: false,
                nowUtc: Now);

            Assert.AreEqual(Now, updated.GeneratedUtc);
            Assert.AreEqual(0, updated.Upns.Count);
        }

        #endregion

        #region Re-check cadence

        [TestMethod]
        public void SkipList_IsBypassedEntirely_WhenRetryHoursIsZero()
        {
            // 0 = disabled, so every cycle is a "full sweep" and nobody is skipped.
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddMinutes(-1), 0, force: false, nowUtc: Now));
        }

        [TestMethod]
        public void SkipList_ReSweepsOnlyAfterTheRetryInterval()
        {
            Assert.IsFalse(ImportCadenceGate.ShouldRun(Now.AddHours(-1), 24, force: false, nowUtc: Now),
                "Within the interval, mailbox-less users stay skipped.");
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddHours(-25), 24, force: false, nowUtc: Now),
                "After the interval, every mailbox is re-checked so newly-licensed users are picked up.");
            Assert.IsTrue(ImportCadenceGate.ShouldRun(null, 24, force: false, nowUtc: Now),
                "Never swept - must check everyone.");
        }

        #endregion
    }
}
