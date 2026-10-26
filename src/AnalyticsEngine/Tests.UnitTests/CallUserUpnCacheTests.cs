using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
using Common.Entities.Models;

namespace Tests.UnitTests
{
    /// <summary>
    /// The process-lifetime user look-up cache for Teams calls, and the call import's rate-limited warnings and outcome
    /// lines. The schema and queue behaviour they support is in <see cref="CallsImportTests"/>.
    /// </summary>
    [TestClass]
    public class CallUserUpnCacheTests
    {
        private static readonly DateTime Start = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

        private static string UpnOf(string id) => $"{id}@contoso.com";

        [TestMethod]
        public async Task ManyWorkersAskingForTheSamePersonShareOneRequest()
        {
            var fetches = 0;
            var release = new TaskCompletionSource<bool>();
            var cache = new CallUserUpnCache(async id =>
            {
                Interlocked.Increment(ref fetches);
                await release.Task;
                return UpnOf(id);
            });

            var workers = Enumerable.Range(0, 20).Select(_ => cache.GetUpnAsync("user-1")).ToList();
            release.SetResult(true);
            var upns = await Task.WhenAll(workers);

            Assert.AreEqual(1, fetches, "One Graph request however many queue workers ask at once.");
            Assert.IsTrue(upns.All(u => u == UpnOf("user-1")));
            Assert.AreEqual(1, cache.Lookups);
            Assert.AreEqual(19, cache.Hits);
        }

        [TestMethod]
        public async Task APersonIsRememberedForADayAndThenLookedUpAgain()
        {
            var clock = new FixedClock(Start);
            var fetches = 0;
            var cache = new CallUserUpnCache(id => { fetches++; return Task.FromResult(UpnOf(id)); }, clock);

            await cache.GetUpnAsync("user-1");
            clock.Advance(TimeSpan.FromHours(23));
            await cache.GetUpnAsync("user-1");
            Assert.AreEqual(1, fetches, "Still remembered after 23 hours.");

            clock.Advance(TimeSpan.FromHours(2));
            await cache.GetUpnAsync("user-1");
            Assert.AreEqual(2, fetches, "Expired after 24 hours, so a renamed UPN is picked up.");
        }

        [TestMethod]
        public async Task APersonEntraIdDoesNotKnowIsRememberedShorterThanOneItDoes()
        {
            var clock = new FixedClock(Start);
            var fetches = 0;
            var cache = new CallUserUpnCache(id => { fetches++; return Task.FromResult<string>(null); }, clock);

            Assert.IsNull(await cache.GetUpnAsync("deleted-user"));
            Assert.IsNull(await cache.GetUpnAsync("deleted-user"));
            Assert.AreEqual(1, fetches, "A deleted account is not asked about again for every call.");

            clock.Advance(CallUserUpnCache.DefaultNegativeTimeToLive + TimeSpan.FromMinutes(1));
            await cache.GetUpnAsync("deleted-user");
            Assert.AreEqual(2, fetches);
        }

        [TestMethod]
        public async Task AFailedLookupIsNeverRemembered_AndReachesTheCaller()
        {
            var fail = true;
            var cache = new CallUserUpnCache(id => fail
                ? Task.FromException<string>(new ApiException("Graph returned a transient HTTP error.") { ResponseStatusCode = 503 })
                : Task.FromResult(UpnOf(id)));

            var thrown = await Assert.ThrowsExceptionAsync<ApiException>(() => cache.GetUpnAsync("user-1"));
            Assert.AreEqual(503, thrown.ResponseStatusCode);

            fail = false;
            Assert.AreEqual(UpnOf("user-1"), await cache.GetUpnAsync("user-1"), "The outage left nothing behind.");
        }

        [TestMethod]
        public async Task TheCacheIsBounded_ForATenantWithManyMorePeopleThanItHolds()
        {
            var cache = new CallUserUpnCache(id => Task.FromResult(UpnOf(id)), maxEntries: 50);

            for (var i = 0; i < 500; i++)
            {
                await cache.GetUpnAsync($"user-{i}");
            }

            Assert.IsTrue(cache.Count <= 50, $"Held {cache.Count} entries.");
            Assert.AreEqual(UpnOf("user-499"), await cache.GetUpnAsync("user-499"));
        }

        [TestMethod]
        public async Task LookupsAcrossTheProcessAreCapped()
        {
            var inFlight = 0;
            var peak = 0;
            var cache = new CallUserUpnCache(async id =>
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (PeakLock) { peak = Math.Max(peak, now); }
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return UpnOf(id);
            }, maxConcurrentLookups: 3);

            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => cache.GetUpnAsync($"user-{i}")));

            Assert.IsTrue(peak <= 3, $"Peak of {peak} look-ups in flight.");
            Assert.AreEqual(30, cache.Lookups);
        }

        private static readonly object PeakLock = new object();

        /// <summary>
        /// A person who can't be told apart from another tenant's cached answer must never be: the cache is keyed by the
        /// Entra object id, compared case-insensitively as Graph does.
        /// </summary>
        [TestMethod]
        public async Task IdsAreComparedWithoutRegardToCase()
        {
            var fetches = 0;
            var cache = new CallUserUpnCache(id => { fetches++; return Task.FromResult(UpnOf(id)); });

            await cache.GetUpnAsync("ABCDEF00-0000-0000-0000-000000000001");
            await cache.GetUpnAsync("abcdef00-0000-0000-0000-000000000001");

            Assert.AreEqual(1, fetches);
        }

        /// <summary>
        /// Through the call import: a user-lookup outage that throws a Kiota ApiException(503) from the cache pauses the
        /// queue (two different calls) and burns none of either call's own attempts.
        /// </summary>
        [TestMethod]
        public async Task AUserLookupOutageThrowingApiException503_PausesTheQueueAndSpendsNoPerCallAttempt()
        {
            var first = new SyntheticMultiPageCall(2, 1, shape: CallPayloadShape.V2);
            var second = new SyntheticMultiPageCall(2, 1, shape: CallPayloadShape.V2);
            var graph = first.CreateGraph();
            second.ServeOn(graph);
            var context = SyntheticMultiPageCall.CreateLoadContext(graph);
            context.CallUsers = new CallUserUpnCache(id => Task.FromException<string>(new ApiException("Graph returned a transient HTTP error.") { ResponseStatusCode = 503 }));
            var logger = new CallsImportTests.CapturingLogger();
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph, context), new InMemoryCallRecordPersistenceManager(), logger);
            var clock = new FixedClock(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc));
            var breaker = new CallImportCircuitBreaker();
            var handler = new CallQueueMessageHandler(async n => (await importer.Import(n)).Outcome, logger, clock, CallQueueRetryPolicy.Default, breaker,
                (wait, token) => Task.CompletedTask);

            var firstSettlement = new FakeCallQueueMessageSettlement();
            var secondSettlement = new FakeCallQueueMessageSettlement();
            await handler.HandleAsync(Message(first.CallId, "m1"), firstSettlement);
            await handler.HandleAsync(Message(second.CallId, "m2"), secondSettlement);

            Assert.AreEqual(0, firstSettlement.ScheduledState.Failures);
            Assert.AreEqual(1, firstSettlement.ScheduledState.SystemicFailures);
            Assert.AreEqual(0, secondSettlement.ScheduledState.Failures);
            Assert.IsTrue(breaker.IsPaused);
            var retry = logger.Entries.Single(e => e.Message.Contains($"call='{first.CallId}'") && e.Message.StartsWith("CallImport outcome:"));
            Assert.AreEqual(LogLevel.Warning, retry.Level);
            StringAssert.Contains(retry.Message, "outcome=Retry:Systemic");
            StringAssert.Contains(retry.Message, "failure=ApiException/503");
            StringAssert.Contains(retry.Message, "elapsedMs=");
        }

        private static CallQueueMessage Message(string callId, string messageId) => new CallQueueMessage
        {
            MessageId = messageId,
            Body = Newtonsoft.Json.JsonConvert.SerializeObject(new GraphChangeNotification { ResourceData = new ResourceData { Id = callId } })
        };

        /// <summary>
        /// The warning for calls whose organiser can't be resolved is let through once per interval per reason, saying how
        /// many were held back - a tenant with many external organisers must not flood the log.
        /// </summary>
        [TestMethod]
        public async Task TheUnresolvedOrganiserWarningIsRateLimitedAndCountsWhatItHeldBack()
        {
            var clock = new FixedClock(Start);
            var logger = new CallsImportTests.CapturingLogger();
            var calls = Enumerable.Range(0, 4).Select(_ => new SyntheticMultiPageCall(2, 1, shape: CallPayloadShape.V2, organiserTenantId: SyntheticMultiPageCall.OtherTenantId)).ToList();
            var graph = calls[0].CreateGraph();
            calls.Skip(1).ToList().ForEach(c => c.ServeOn(graph));
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(graph), new InMemoryCallRecordPersistenceManager(), logger, null, clock);

            for (var i = 0; i < 3; i++)
            {
                await importer.Import(new GraphChangeNotification { ResourceData = new ResourceData { Id = calls[i].CallId } });
            }
            clock.Advance(CallRecordImporter.WarningInterval + TimeSpan.FromMinutes(1));
            await importer.Import(new GraphChangeNotification { ResourceData = new ResourceData { Id = calls[3].CallId } });

            var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
            Assert.AreEqual(2, warnings.Count);
            StringAssert.Contains(warnings[1].Message, "(2 similar call(s)");
            Assert.AreEqual(4, logger.Entries.Count(e => e.Message.StartsWith(CallImportLog.Prefix)), "Still one outcome line per call.");
        }

        [TestMethod]
        public async Task EveryCallLeavesOneOutcomeLineWithItsCountsAndNothingSensitive()
        {
            var call = new SyntheticMultiPageCall(firstPageAttendees: 2, secondPageAttendees: 1, shape: CallPayloadShape.V2);
            var logger = new CallsImportTests.CapturingLogger();
            var importer = new CallRecordImporter(SyntheticMultiPageCall.CreateSourceLoader(call.CreateGraph()), new InMemoryCallRecordPersistenceManager(), logger);

            await importer.Import(new GraphChangeNotification { ClientState = "synthetic-client-state", ResourceData = new ResourceData { Id = call.CallId } });

            var line = logger.Entries.Single(e => e.Message.StartsWith(CallImportLog.Prefix));
            Assert.AreEqual(LogLevel.Information, line.Level);
            Assert.AreEqual($"{CallImportLog.Prefix} call='{call.CallId}' outcome=Saved sessions=3 pages=2 users=4 external=0 unresolved=0 resolveMs={ResolveMsOf(line.Message)} elapsedMs={ElapsedMsOf(line.Message)}", line.Message);
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("synthetic-client-state") || e.Message.Contains("@contoso.com")), "No client state, and no UPNs in the log.");
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("Ignoring external user")));
        }

        private static string ResolveMsOf(string message) => System.Text.RegularExpressions.Regex.Match(message, @"resolveMs=(\d+)").Groups[1].Value;
        private static string ElapsedMsOf(string message) => System.Text.RegularExpressions.Regex.Match(message, @"elapsedMs=(\d+)").Groups[1].Value;

        [TestMethod]
        public void FailureCode_NamesTheTypeAndTheStatusOrErrorNumberNeverTheMessage()
        {
            Assert.AreEqual("ApiException/502", CallImportFailureClassifier.Code(new ApiException("secret-ish text") { ResponseStatusCode = 502 }));
            Assert.AreEqual("ODataError/429/TooManyRequests", CallImportFailureClassifier.Code(new ODataError { ResponseStatusCode = 429, Error = new MainError { Code = "TooManyRequests" } }));
            Assert.AreEqual("NoOrganiserIdentity", CallImportFailureClassifier.Code(new CallRecordPayloadException("c", CallRecordPayloadException.NoOrganiserIdentityReason, "x")));
            Assert.AreEqual("InvalidOperationException", CallImportFailureClassifier.Code(new InvalidOperationException("whatever")));
        }
    }
}
