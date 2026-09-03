extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot adoption analysis deliberately OUTLIVES the HTTP request that starts it: the
    /// request gives up after <c>FirstResponseBudget</c> and answers 202, while the shared run
    /// carries on and a later poll collects the result.
    ///
    /// That only works if the run is not tied to the starting request. ASP.NET installs a
    /// request-bound <see cref="SynchronizationContext"/>, and an <c>await</c> that does not say
    /// <c>ConfigureAwait(false)</c> posts its continuation back to it. Once the request has ended
    /// that context never pumps again, so the continuation is simply never run - the analysis stops
    /// mid-flight with no exception, no timeout and no recycle, and because the coordinator only
    /// clears its in-flight entry in a <c>finally</c> that never executes, every later poll joins
    /// the same dead task and the page can never load again until the app restarts (issue #441).
    ///
    /// The failure is invisible in the obvious places - CPU and database are idle, the thread pool
    /// is completely free, nothing throws - which is exactly why it took production telemetry to
    /// find. These tests pin the invariant that makes it impossible.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionSynchronizationContextTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The run must not inherit the starting thread's context in the first place. This is the
        /// direct statement of the invariant; the test below shows what it costs when it is broken.
        /// </summary>
        [TestMethod]
        public async Task AnalysisRun_DoesNotCaptureTheStartingRequestsSynchronizationContext()
        {
            var runner = new ContextCapturingRunner();
            var coordinator = NewCoordinator(runner);

            using (var request = new RequestBoundSynchronizationContext())
            {
                var run = request.StartAndCapture(() => coordinator.GetAsync(28, new List<int>()));

                Assert.IsTrue(
                    runner.Entered.Wait(Timeout),
                    "the analysis should have been started");
                Assert.IsNotNull(
                    request.ObservedContextOnStartingThread,
                    "the fake request context must actually be installed, or this test proves nothing");

                runner.Release();
                await WithTimeout(run, "the analysis");

                Assert.IsNull(
                    runner.ObservedContext,
                    "the analysis must run with no SynchronizationContext, so a continuation can "
                    + "never be posted back to a request that has already finished");
            }
        }

        /// <summary>
        /// The regression test for the hang itself: end the starting request while the analysis is
        /// still going, then let the analysis finish. It must still complete and publish.
        /// </summary>
        [TestMethod]
        public async Task AnalysisRun_CompletesEvenAfterTheStartingRequestHasEnded()
        {
            var runner = new ContextCapturingRunner();
            var cache = new InMemoryAnalysisCache();
            var coordinator = NewCoordinator(runner, cache);

            using (var request = new RequestBoundSynchronizationContext())
            {
                var run = request.StartAndCapture(() => coordinator.GetAsync(28, new List<int>()));
                Assert.IsTrue(runner.Entered.Wait(Timeout), "the analysis should have been started");

                // The 202 has gone back and ASP.NET has torn the request down. Anything posted to
                // its context from here on is dropped on the floor.
                request.CompleteRequest();

                // Only now does the analysis finish its work, so its continuation is scheduled
                // strictly after the request ended - the exact ordering seen in production.
                runner.Release();

                var analysis = await WithTimeout(run, "the analysis");

                Assert.IsNotNull(analysis);
                Assert.AreEqual(
                    1,
                    cache.SetCount,
                    "the completed analysis must be published so the next poll can serve it");
                Assert.AreEqual(
                    0,
                    request.PostsAfterCompletion,
                    "nothing belonging to the analysis may be posted to the finished request");
            }
        }

        /// <summary>
        /// The consequence that made this permanent rather than merely slow: a run that survives must
        /// also clear itself from the in-flight table. The check deliberately happens with an EMPTY
        /// cache, because a populated one answers the follow-up poll before the in-flight table is
        /// ever consulted - so a cached result would make this pass even with the cleanup removed.
        /// </summary>
        [TestMethod]
        public async Task AnalysisRun_LeavesNoStaleGeneration_AfterTheStartingRequestHasEnded()
        {
            var runner = new ContextCapturingRunner();
            var cache = new InMemoryAnalysisCache();
            var coordinator = NewCoordinator(runner, cache);

            using (var request = new RequestBoundSynchronizationContext())
            {
                var run = request.StartAndCapture(() => coordinator.GetAsync(28, new List<int>()));
                Assert.IsTrue(runner.Entered.Wait(Timeout), "the analysis should have been started");
                request.CompleteRequest();
                runner.Release();
                await WithTimeout(run, "the analysis");
            }

            // The published entry reaching its TTL is the ordinary case, not an exotic one - it is a
            // 10-minute expiry against an analysis that can run for minutes. Once it has gone, the
            // next poll has to be able to start a FRESH run, and it can only do that if the finished
            // generation took itself out of the in-flight table.
            cache.Evict();

            var second = await WithTimeout(
                coordinator.GetAsync(28, new List<int>()), "the follow-up analysis");

            Assert.IsNotNull(second);
            Assert.AreEqual(
                2,
                runner.CallCount,
                "once the cached result had expired the coordinator had to start a new analysis; "
                + "still seeing a single call means the finished generation was left behind in the "
                + "in-flight table, where it would serve its stale result for ever");
        }

        private static async Task<CopilotAdoptionAnalysis> WithTimeout(
            Task<CopilotAdoptionAnalysis> task, string what)
        {
            var finished = await Task.WhenAny(task, Task.Delay(Timeout));
            Assert.AreSame(
                task,
                finished,
                what + " never completed. It was stranded on the SynchronizationContext of the "
                + "request that started it - see issue #441.");
            return await task;
        }

        private static AdoptionCoordinator NewCoordinator(
            AdoptionRunner runner, AdoptionCache cache = null)
        {
            return new AdoptionCoordinator(
                runner,
                cache ?? new InMemoryAnalysisCache(),
                (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10));
        }

        /// <summary>
        /// Stands in for ASP.NET's request-bound context: continuations are queued to a single
        /// pump, and once the request has ended anything posted is DROPPED rather than run. That
        /// dropping is the whole point - it is what turns a captured context into a permanent hang.
        /// </summary>
        private sealed class RequestBoundSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> _queue =
                new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();
            private readonly Thread _pump;
            private int _postsAfterCompletion;

            public RequestBoundSynchronizationContext()
            {
                _pump = new Thread(Pump)
                {
                    IsBackground = true,
                    Name = "FakeAspNetRequestPump",
                };
                _pump.Start();
            }

            /// <summary>What <c>SynchronizationContext.Current</c> was on the starting thread.</summary>
            public SynchronizationContext ObservedContextOnStartingThread { get; private set; }

            public int PostsAfterCompletion => Volatile.Read(ref _postsAfterCompletion);

            public override void Post(SendOrPostCallback d, object state)
            {
                try
                {
                    _queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state));
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding has been called: the request is over and this continuation is
                    // never going to run, which is precisely ASP.NET's behaviour.
                    Interlocked.Increment(ref _postsAfterCompletion);
                }
            }

            public override void Send(SendOrPostCallback d, object state) => d(state);

            /// <summary>Runs <paramref name="start"/> ON the pump thread, so it starts under this context.</summary>
            public Task<CopilotAdoptionAnalysis> StartAndCapture(
                Func<Task<CopilotAdoptionAnalysis>> start)
            {
                var handoff = new TaskCompletionSource<Task<CopilotAdoptionAnalysis>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                Post(
                    _ =>
                    {
                        ObservedContextOnStartingThread = Current;
                        try
                        {
                            handoff.SetResult(start());
                        }
                        catch (Exception ex)
                        {
                            handoff.SetException(ex);
                        }
                    },
                    null);

                Assert.IsTrue(
                    handoff.Task.Wait(Timeout),
                    "the analysis was never started on the request thread");
                return handoff.Task.Result;
            }

            /// <summary>Ends the "request". Later posts are dropped, exactly as ASP.NET drops them.</summary>
            public void CompleteRequest() => _queue.CompleteAdding();

            private void Pump()
            {
                SetSynchronizationContext(this);
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    item.Key(item.Value);
                }
            }

            public void Dispose()
            {
                if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
                _pump.Join(Timeout);
                _queue.Dispose();
            }
        }

        /// <summary>
        /// Mirrors <c>CopilotAdoptionService</c>: it awaits WITHOUT <c>ConfigureAwait(false)</c>,
        /// which is what the real service does on every await that matters. If the coordinator lets
        /// the caller's context reach here, the continuation goes back to that context.
        /// </summary>
        private sealed class ContextCapturingRunner : AdoptionRunner
        {
            private readonly TaskCompletionSource<bool> _gate =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _callCount;

            public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

            public SynchronizationContext ObservedContext { get; private set; }

            public int CallCount => Volatile.Read(ref _callCount);

            public async Task<CopilotAdoptionAnalysis> RunAsync(
                int windowDays,
                List<int> seatLicenceTypeIds,
                ICopilotAdoptionRunTelemetry telemetry)
            {
                Interlocked.Increment(ref _callCount);
                ObservedContext = SynchronizationContext.Current;
                Entered.Set();

                await _gate.Task;

                return new CopilotAdoptionAnalysis();
            }

            public void Release() => _gate.TrySetResult(true);
        }

        private sealed class InMemoryAnalysisCache : AdoptionCache
        {
            private readonly object _gate = new object();
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _items =
                new Dictionary<string, CopilotAdoptionAnalysis>(StringComparer.Ordinal);
            private int _setCount;

            public int SetCount
            {
                get { lock (_gate) { return _setCount; } }
            }

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                lock (_gate)
                {
                    return _items.TryGetValue(key, out analysis);
                }
            }

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
                lock (_gate)
                {
                    _items[key] = analysis;
                    _setCount++;
                }
            }

            /// <summary>Drops everything, standing in for the cache entry reaching its TTL.</summary>
            public void Evict()
            {
                lock (_gate)
                {
                    _items.Clear();
                }
            }
        }
    }
}
