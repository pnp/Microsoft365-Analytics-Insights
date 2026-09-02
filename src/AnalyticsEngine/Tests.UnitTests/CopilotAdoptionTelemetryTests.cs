extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionEvent = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionLifecycleEvent;
using AdoptionFailure = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionFailureEvent;
using AdoptionHeartbeatFactory = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionHeartbeatFactory;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using AdoptionRunTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionRunTelemetry;
using AdoptionSink = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionEventSink;
using AdoptionCompletion = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionCompletionEvent;
using AdoptionQueuedSink = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.QueuedCopilotAdoptionEventSink;
using AdoptionWriter = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionTelemetryWriter;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotAdoptionTelemetryTests
    {
        private static readonly HashSet<string> AllowedDimensions =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "SchemaVersion",
                "Stage",
                "RunId",
                "InstanceId",
                "WindowDays",
                "HasSeatOverride",
                "SynchronizationContext",
                "Step",
                "Query",
                "Outcome",
                "ExceptionType",
                "ActiveOperations",
                "ShutdownReason",
            };

        private static readonly HashSet<string> AllowedMeasurements =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Sequence",
                "ElapsedMs",
                "AppDomainId",
                "AppDomainUptimeMs",
                "DroppedEvents",
                "OperationId",
                "DurationMs",
                "HeartbeatDriftMs",
                "ProcessWorkingSetBytes",
                "ManagedHeapBytes",
                "Gen0Collections",
                "Gen1Collections",
                "Gen2Collections",
                "ThreadPoolAvailableWorkers",
                "ThreadPoolAvailableCompletionPorts",
            };

        [TestMethod]
        public void LifecycleEvents_AreCorrelatedSequencedAndPrivacyAllowListed()
        {
            var sink = new RecordingSink();
            var heartbeat = new ManualHeartbeatFactory();
            var telemetry = NewTelemetry(sink, heartbeat);

            var step = telemetry.StepStarted(CopilotAdoptionSteps.LicensedUsers);
            var firstQuery = telemetry.QueryStarted(
                CopilotAdoptionSteps.LicensedUsers,
                CopilotAdoptionQueries.CoworkAgentLookup);
            var secondQuery = telemetry.QueryStarted(
                CopilotAdoptionSteps.WeeklyTrend,
                CopilotAdoptionQueries.CoworkAgentLookup);

            heartbeat.Trigger();
            var bothActive = sink.Events.Last(
                item => item.Stage == CopilotAdoptionTelemetryStages.Heartbeat);
            StringAssert.Contains(bothActive.ActiveOperations, firstQuery + ":");
            StringAssert.Contains(bothActive.ActiveOperations, secondQuery + ":");

            telemetry.QueryCompleted(
                firstQuery,
                CopilotAdoptionSteps.LicensedUsers,
                CopilotAdoptionQueries.CoworkAgentLookup,
                10,
                false);
            heartbeat.Trigger();
            var oneActive = sink.Events.Last(
                item => item.Stage == CopilotAdoptionTelemetryStages.Heartbeat);
            Assert.IsFalse(oneActive.ActiveOperations.Contains(firstQuery + ":"));
            StringAssert.Contains(oneActive.ActiveOperations, secondQuery + ":");

            telemetry.QueryCompleted(
                secondQuery,
                CopilotAdoptionSteps.WeeklyTrend,
                CopilotAdoptionQueries.CoworkAgentLookup,
                20,
                true,
                nameof(TimeoutException));
            telemetry.StepCompleted(
                step,
                CopilotAdoptionSteps.LicensedUsers,
                30,
                false);

            var countBeforeDispose = sink.Events.Count;
            telemetry.Dispose();
            heartbeat.Trigger();
            Assert.AreEqual(
                countBeforeDispose,
                sink.Events.Count,
                "disposing a completed run must stop its heartbeat");

            var events = sink.Events;
            Assert.IsTrue(events.Count > 0);
            Assert.AreEqual(1, events.Select(item => item.RunId).Distinct().Count());
            Assert.AreEqual("00000000000000000000000000000001", events[0].InstanceId);
            CollectionAssert.AreEqual(
                Enumerable.Range(1, events.Count).Select(value => (long)value).ToArray(),
                events.Select(item => item.Sequence).ToArray(),
                "sequence numbers must make missing or reordered telemetry visible");

            foreach (var telemetryEvent in events)
            {
                Assert.IsTrue(
                    telemetryEvent.Dimensions().Keys.All(AllowedDimensions.Contains),
                    "an unreviewed custom dimension was added");
                Assert.IsTrue(
                    telemetryEvent.Measurements().Keys.All(AllowedMeasurements.Contains),
                    "an unreviewed custom measurement was added");
            }

            var failedQuery = events.Single(
                item => item.Stage == CopilotAdoptionTelemetryStages.QueryFailed);
            Assert.AreEqual(nameof(TimeoutException), failedQuery.ExceptionType);
        }

        [TestMethod]
        public async Task ConcurrentColdRequests_ShareOneRunThenReadThePublishedCache()
        {
            var order = new ConcurrentQueue<string>();
            var sink = new RecordingSink(order);
            var heartbeat = new ManualHeartbeatFactory();
            var runner = new ControllableRunner();
            var cache = new InMemoryAnalysisCache(order);
            var coordinator = NewCoordinator(runner, cache, sink, heartbeat);
            var ids = new List<int>();

            var firstPolls = Enumerable.Range(0, 3)
                .Select(_ => coordinator.TryGetAsync(
                    28,
                    ids,
                    TimeSpan.FromMilliseconds(40),
                    CancellationToken.None))
                .ToArray();

            await Task.WhenAll(firstPolls);

            Assert.AreEqual(1, runner.CallCount, "three cold endpoints must start one analysis");
            Assert.IsTrue(firstPolls.All(task => task.Result == null));

            var analysis = new CopilotAdoptionAnalysis();
            runner.Complete(analysis);
            var completed = await coordinator.TryGetAsync(
                28,
                ids,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            var cached = await coordinator.GetAsync(28, ids);

            Assert.AreSame(analysis, completed);
            Assert.AreSame(analysis, cached);
            Assert.AreEqual(1, runner.CallCount);
            Assert.AreEqual(1, cache.SetCount);
            Assert.AreEqual(
                1,
                sink.Events.Count(
                    item => item.Stage == CopilotAdoptionTelemetryStages.CachePublished));
            var published = sink.Events.Single(
                item => item.Stage == CopilotAdoptionTelemetryStages.CachePublished);
            Assert.IsTrue(
                published.DurationMs < published.ElapsedMs,
                "cache DurationMs must time MemoryCache.Set, not repeat total analysis elapsed time");

            var ordered = order.ToArray();
            Assert.IsTrue(
                Array.IndexOf(ordered, "CacheSet")
                < Array.IndexOf(ordered, CopilotAdoptionTelemetryStages.CachePublished),
                "the result must be published before any terminal telemetry");
        }

        [TestMethod]
        public async Task CachePublication_IsNotBlockedByCompletionTelemetry()
        {
            var sink = new RecordingSink { BlockCompletion = true };
            var heartbeat = new ManualHeartbeatFactory();
            var runner = new ControllableRunner();
            var cache = new InMemoryAnalysisCache();
            var coordinator = NewCoordinator(runner, cache, sink, heartbeat);
            var analysis = new CopilotAdoptionAnalysis();

            var first = coordinator.GetAsync(28, new List<int>());
            runner.Complete(analysis);

            Assert.IsTrue(
                sink.CompletionEntered.Wait(TimeSpan.FromSeconds(2)),
                "the fake must block the completion telemetry path");
            Assert.AreEqual(1, cache.SetCount, "cache publication must happen first");

            var second = await coordinator.GetAsync(28, new List<int>());
            Assert.AreSame(
                analysis,
                second,
                "a new poll must read the cache while completion telemetry is blocked");

            sink.ReleaseCompletion.Set();
            Assert.AreSame(analysis, await first);
        }

        [TestMethod]
        public async Task FailedRun_IsEvictedAndTheNextRequestRetries()
        {
            var sink = new RecordingSink();
            var heartbeat = new ManualHeartbeatFactory();
            var runner = new SequencedRunner(
                Task.FromException<CopilotAdoptionAnalysis>(
                    new InvalidOperationException("sensitive-value")),
                Task.FromResult(new CopilotAdoptionAnalysis()));
            var cache = new InMemoryAnalysisCache();
            var coordinator = NewCoordinator(runner, cache, sink, heartbeat);

            try
            {
                await coordinator.GetAsync(28, new List<int>());
                Assert.Fail("the first run should fail");
            }
            catch (InvalidOperationException)
            {
            }

            var recovered = await coordinator.GetAsync(28, new List<int>());

            Assert.IsNotNull(recovered);
            Assert.AreEqual(2, runner.CallCount, "a faulted generation must not stay in-flight");
            Assert.AreEqual(1, cache.SetCount);
            var failure = sink.Events.Single(
                item => item.Stage == CopilotAdoptionTelemetryStages.Failed);
            Assert.AreEqual(nameof(InvalidOperationException), failure.ExceptionType);
            Assert.IsFalse(
                string.Join("|", failure.Dimensions().Values).Contains("sensitive-value"));
        }

        [TestMethod]
        public async Task TelemetryFactoryFailure_DoesNotStopOrPoisonTheAnalysis()
        {
            var runner = new SequencedRunner(
                Task.FromException<CopilotAdoptionAnalysis>(
                    new InvalidOperationException("first analysis failed")),
                Task.FromResult(new CopilotAdoptionAnalysis()));
            var cache = new InMemoryAnalysisCache();
            var coordinator = new AdoptionCoordinator(
                runner,
                cache,
                (window, hasOverride) => throw new InvalidOperationException(
                    "telemetry construction failed"),
                TimeSpan.FromMinutes(10));

            try
            {
                await coordinator.GetAsync(28, new List<int>());
                Assert.Fail("the first fake analysis should fail");
            }
            catch (InvalidOperationException)
            {
            }

            var recovered = await coordinator.GetAsync(28, new List<int>());

            Assert.IsNotNull(recovered);
            Assert.AreEqual(
                2,
                runner.CallCount,
                "telemetry construction must not leave a faulted generation in-flight");
            Assert.AreEqual(1, cache.SetCount);
        }

        [TestMethod]
        public void QueuedSink_DoesNotBlockTheCallerWhenTheWriterBlocks()
        {
            var writer = new BlockingWriter();
            var sink = new AdoptionQueuedSink(() => writer);
            var completion = new AdoptionCompletion
            {
                RunId = "00000000000000000000000000000002",
                WindowDays = 28,
                Steps = new Dictionary<string, long>(),
            };
            var submitted = LifecycleEvent(
                CopilotAdoptionTelemetryStages.CompletionTelemetryReturned,
                completion.RunId);

            var watch = Stopwatch.StartNew();
            sink.TrackCompletion(completion, submitted);
            watch.Stop();

            Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(250));
            Assert.IsTrue(writer.CompletionEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(
                writer.Events.Any(
                    item => item.Stage
                            == CopilotAdoptionTelemetryStages.CompletionTelemetryReturned),
                "the boundary event must only be submitted after legacy completion telemetry returns");

            Thread.Sleep(30);
            writer.ReleaseCompletion.Set();
            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => writer.Events.Any(
                        item => item.Stage
                                == CopilotAdoptionTelemetryStages.CompletionTelemetryReturned),
                    TimeSpan.FromSeconds(2)));
            var returned = writer.Events.Single(
                item => item.Stage
                        == CopilotAdoptionTelemetryStages.CompletionTelemetryReturned);
            Assert.IsTrue(returned.DurationMs >= 20);

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        [TestMethod]
        public void QueuedSink_RetriesAfterTransientWriterConstructionFailure()
        {
            var writer = new BlockingWriter();
            writer.ReleaseCompletion.Set();
            var attempts = 0;
            var sink = new AdoptionQueuedSink(() =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("transient setup failure");
                }
                return writer;
            });

            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.Started,
                "00000000000000000000000000000003"));
            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.Heartbeat,
                "00000000000000000000000000000003"));

            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => writer.Events.Any(
                        item => item.Stage == CopilotAdoptionTelemetryStages.Heartbeat),
                    TimeSpan.FromSeconds(2)));
            Assert.AreEqual(2, attempts);
            Assert.AreEqual(1, sink.DroppedEvents);

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        private static AdoptionCoordinator NewCoordinator(
            AdoptionRunner runner,
            AdoptionCache cache,
            RecordingSink sink,
            ManualHeartbeatFactory heartbeat)
        {
            return new AdoptionCoordinator(
                runner,
                cache,
                (window, hasOverride) => NewTelemetry(
                    sink,
                    heartbeat,
                    window,
                    hasOverride),
                TimeSpan.FromMinutes(10));
        }

        private static AdoptionRunTelemetry NewTelemetry(
            AdoptionSink sink,
            AdoptionHeartbeatFactory heartbeat,
            int windowDays = 28,
            bool hasOverride = false)
        {
            return new AdoptionRunTelemetry(
                sink,
                windowDays,
                hasOverride,
                "00000000000000000000000000000001",
                1,
                Stopwatch.StartNew(),
                heartbeat,
                TimeSpan.FromSeconds(30));
        }

        private static AdoptionEvent LifecycleEvent(string stage, string runId)
        {
            return new AdoptionEvent
            {
                OccurredUtc = DateTimeOffset.UtcNow,
                Stage = stage,
                RunId = runId,
                InstanceId = "00000000000000000000000000000001",
                WindowDays = 28,
                Sequence = 1,
                Gen0Collections = -1,
                Gen1Collections = -1,
                Gen2Collections = -1,
                ThreadPoolAvailableWorkers = -1,
                ThreadPoolAvailableCompletionPorts = -1,
            };
        }

        private sealed class RecordingSink : AdoptionSink
        {
            private readonly object _gate = new object();
            private readonly ConcurrentQueue<string> _order;
            private readonly List<AdoptionEvent> _events = new List<AdoptionEvent>();

            public RecordingSink(ConcurrentQueue<string> order = null)
            {
                _order = order;
            }

            public bool BlockCompletion { get; set; }
            public ManualResetEventSlim CompletionEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim ReleaseCompletion { get; } =
                new ManualResetEventSlim(false);
            public int DroppedEvents => 0;

            public List<AdoptionEvent> Events
            {
                get
                {
                    lock (_gate)
                    {
                        return _events.ToList();
                    }
                }
            }

            public void Track(AdoptionEvent telemetryEvent)
            {
                lock (_gate)
                {
                    _events.Add(telemetryEvent);
                }
                _order?.Enqueue(telemetryEvent.Stage);
            }

            public void TrackCompletion(
                AdoptionCompletion completion,
                AdoptionEvent submittedEvent)
            {
                CompletionEntered.Set();
                if (BlockCompletion)
                {
                    ReleaseCompletion.Wait(TimeSpan.FromSeconds(5));
                }
                Track(submittedEvent);
            }

            public bool TrackFailure(
                AdoptionFailure failure,
                AdoptionEvent failureEvent)
            {
                Track(failureEvent);
                return true;
            }

            public void Shutdown(TimeSpan timeout)
            {
            }
        }

        private sealed class ManualHeartbeatFactory : AdoptionHeartbeatFactory
        {
            private Action _heartbeat;
            private bool _disposed;

            public IDisposable Start(Action heartbeat, TimeSpan interval)
            {
                _heartbeat = heartbeat;
                _disposed = false;
                return new CallbackDisposable(() => _disposed = true);
            }

            public void Trigger()
            {
                if (!_disposed) _heartbeat?.Invoke();
            }
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private readonly Action _dispose;

            public CallbackDisposable(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                _dispose();
            }
        }

        private sealed class InMemoryAnalysisCache : AdoptionCache
        {
            private readonly object _gate = new object();
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _items =
                new Dictionary<string, CopilotAdoptionAnalysis>(StringComparer.Ordinal);
            private readonly ConcurrentQueue<string> _order;

            public InMemoryAnalysisCache(ConcurrentQueue<string> order = null)
            {
                _order = order;
            }

            public int SetCount { get; private set; }

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
                    SetCount++;
                }
                _order?.Enqueue("CacheSet");
            }
        }

        private sealed class ControllableRunner : AdoptionRunner
        {
            private readonly TaskCompletionSource<CopilotAdoptionAnalysis> _completion =
                new TaskCompletionSource<CopilotAdoptionAnalysis>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public Task<CopilotAdoptionAnalysis> RunAsync(
                int windowDays,
                List<int> seatLicenceTypeIds,
                ICopilotAdoptionRunTelemetry telemetry)
            {
                Interlocked.Increment(ref _callCount);
                return _completion.Task;
            }

            public void Complete(CopilotAdoptionAnalysis analysis)
            {
                _completion.SetResult(analysis);
            }
        }

        private sealed class SequencedRunner : AdoptionRunner
        {
            private readonly ConcurrentQueue<Task<CopilotAdoptionAnalysis>> _responses;
            private int _callCount;

            public SequencedRunner(params Task<CopilotAdoptionAnalysis>[] responses)
            {
                _responses = new ConcurrentQueue<Task<CopilotAdoptionAnalysis>>(responses);
            }

            public int CallCount => Volatile.Read(ref _callCount);

            public Task<CopilotAdoptionAnalysis> RunAsync(
                int windowDays,
                List<int> seatLicenceTypeIds,
                ICopilotAdoptionRunTelemetry telemetry)
            {
                Interlocked.Increment(ref _callCount);
                if (!_responses.TryDequeue(out var response))
                {
                    throw new InvalidOperationException("No fake response configured.");
                }
                return response;
            }
        }

        private sealed class BlockingWriter : AdoptionWriter
        {
            private readonly object _gate = new object();
            private readonly List<AdoptionEvent> _events = new List<AdoptionEvent>();

            public ManualResetEventSlim CompletionEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim ReleaseCompletion { get; } =
                new ManualResetEventSlim(false);

            public List<AdoptionEvent> Events
            {
                get
                {
                    lock (_gate)
                    {
                        return _events.ToList();
                    }
                }
            }

            public void Write(AdoptionEvent telemetryEvent)
            {
                lock (_gate)
                {
                    _events.Add(telemetryEvent);
                }
            }

            public void WriteCompletion(AdoptionCompletion completion)
            {
                CompletionEntered.Set();
                ReleaseCompletion.Wait(TimeSpan.FromSeconds(5));
            }

            public void WriteFailure(AdoptionFailure failure)
            {
            }

            public void Flush()
            {
            }
        }
    }
}
