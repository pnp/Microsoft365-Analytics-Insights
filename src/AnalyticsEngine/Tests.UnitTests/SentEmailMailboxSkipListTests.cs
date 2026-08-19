using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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

        private static ManualGraphCallClient ClientReturning(HttpStatusCode status, string body, out StubHandler handler)
        {
            handler = new StubHandler(status, body);
            return new ManualGraphCallClient(handler, NullLogger.Instance);
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
