extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisTelemetry;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionEvent = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionLifecycleEvent;
using AdoptionFailure = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionFailureEvent;
using AdoptionHeartbeatFactory = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionHeartbeatFactory;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using AdoptionRunTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionRunTelemetry;
using AdoptionSink = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionEventSink;
using AdoptionCompletion = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionCompletionEvent;
using AdoptionCorrelation = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionExceptionCorrelation;
using AdoptionQueuedSink = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.QueuedCopilotAdoptionEventSink;
using AdoptionWriter = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionTelemetryWriter;
using SharedHeartbeatFactory = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.DedicatedThreadHeartbeatFactory;
using AppInsightsAdoptionWriter = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.AppInsightsCopilotAdoptionTelemetryWriter;
using CopilotAdoptionAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotAdoptionAPIController;
using WebExceptionTelemetry = AnalyticsWeb::Web.AnalyticsWeb.WebExceptionTelemetry;

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
                // Failure classification. Type names and numeric error codes only - reviewed as carrying no
                // tenant data; see CopilotAdoptionFailure.
                "FailureKind",
                "ExceptionChain",
                "SqlErrorNumber",
                "Win32ErrorCode",
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
                CopilotAdoptionFailure.From(new TimeoutException()));
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
            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, failedQuery.FailureKind);
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
        public void FailureWriter_EmitsExceptionTelemetryWithRunId()
        {
            var channel = new RecordingTelemetryChannel();
            var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };
            var logger = new AnalyticsLogger(new TelemetryClient(configuration), "CopilotAdoptionTest");
            var writer = new AppInsightsAdoptionWriter(logger);
            var runId = "00000000000000000000000000000004";

            writer.WriteFailure(new AdoptionFailure
            {
                RunId = runId,
                WindowDays = 28,
                Exception = new InvalidOperationException("synthetic failure"),
            });

            var exception = channel.Sent.OfType<ExceptionTelemetry>().Single();
            Assert.AreEqual(runId, exception.Context.Operation.Id);
            Assert.AreEqual(runId, exception.Properties["RunId"]);
            Assert.AreEqual("CopilotAdoptionTest", exception.Context.Operation.Name);
            Assert.IsTrue(
                channel.Sent.OfType<TraceTelemetry>()
                    .Any(trace => trace.Message.Contains("RunId " + runId)),
                "The searchable fallback trace must carry the same RunId as exception telemetry.");
        }

        [TestMethod]
        public void FailedRun_AttachesRunIdSoWebExceptionFallbackStillReportsWhenQueueRejects()
        {
            var channel = new RecordingTelemetryChannel();
            var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };
            var logger = new AnalyticsLogger(new TelemetryClient(configuration), "WebApiTest");
            var sink = new RejectingSink();
            var heartbeat = new ManualHeartbeatFactory();
            var telemetry = NewTelemetry(sink, heartbeat);
            var exception = new InvalidOperationException("synthetic rejected failure");

            Assert.IsFalse(telemetry.QueueFailure(exception), "The fake must reject the queued failure.");
            Assert.IsTrue(AdoptionCorrelation.TryGetRunId(exception, out var runId));

            WebExceptionTelemetry.Report(exception, "WebApi /api/copilotadoption", _ => logger);
            WebExceptionTelemetry.Report(exception, "WebApi /api/copilotadoption", _ => logger);

            var exceptionTelemetry = channel.Sent.OfType<ExceptionTelemetry>().ToList();
            Assert.AreEqual(
                1,
                exceptionTelemetry.Count,
                "A rejected queue item must still be reported by the Web API fallback, but only once.");
            Assert.AreEqual(runId, exceptionTelemetry[0].Context.Operation.Id);
            Assert.AreEqual(runId, exceptionTelemetry[0].Properties["RunId"]);
            Assert.IsTrue(
                channel.Sent.OfType<TraceTelemetry>()
                    .Any(trace => trace.Message.Contains("RunId " + runId)),
                "The fallback trace must be joinable to the lifecycle failure by RunId.");
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
        public async Task AnalysisFailure_RejectedByTheSink_IsStillReportedByTheFallback()
        {
            // The run that fails has normally outlived the request that started it (that request
            // returned 202 at the first-response budget). If the bounded failure sink also rejects the
            // event, nothing else observes the exception, so the failure would be completely invisible.
            var boom = new InvalidOperationException("analysis failed");
            var reported = new List<Tuple<Exception, string>>();
            var coordinator = new AdoptionCoordinator(
                new SequencedRunner(Task.FromException<CopilotAdoptionAnalysis>(boom)),
                new InMemoryAnalysisCache(),
                (window, hasOverride) => new RejectingTelemetry(),
                TimeSpan.FromMinutes(10),
                (ex, context) => reported.Add(Tuple.Create(ex, context)));

            try
            {
                await coordinator.GetAsync(28, new List<int>());
                Assert.Fail("the fake analysis should fail");
            }
            catch (InvalidOperationException)
            {
            }

            Assert.AreEqual(
                1,
                reported.Count,
                "A failure the sink refused must fall back to direct reporting, or it is never seen.");
            Assert.AreSame(boom, reported[0].Item1);
        }

        [TestMethod]
        public async Task AnalysisFailure_AcceptedByTheSink_IsNotReportedTwice()
        {
            var reported = new List<Tuple<Exception, string>>();
            var coordinator = new AdoptionCoordinator(
                new SequencedRunner(
                    Task.FromException<CopilotAdoptionAnalysis>(
                        new InvalidOperationException("analysis failed"))),
                new InMemoryAnalysisCache(),
                (window, hasOverride) => new AcceptingTelemetry(),
                TimeSpan.FromMinutes(10),
                (ex, context) => reported.Add(Tuple.Create(ex, context)));

            try
            {
                await coordinator.GetAsync(28, new List<int>());
                Assert.Fail("the fake analysis should fail");
            }
            catch (InvalidOperationException)
            {
            }

            Assert.AreEqual(
                0,
                reported.Count,
                "The lifecycle sink already has it; reporting again would double-count the failure.");
        }

        [TestMethod]
        public async Task AnalysisFailure_AcceptedByTheSink_IsStillVisibleToAWaitingRequest()
        {
            // Delivery, not acceptance, is the dedup boundary. Once the sink writer has actually
            // reported the exception, waiting requests must stand down: exactly one exception
            // telemetry item is the invariant, not "one from the sink plus one from a waiter".
            var channel = new RecordingTelemetryChannel();
            var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };
            var sinkLogger = new AnalyticsLogger(new TelemetryClient(configuration), "CopilotAdoptionTest");
            var waiterLogger = new AnalyticsLogger(new TelemetryClient(configuration), "WebApiTest");
            var sink = new AdoptionQueuedSink(() => new AppInsightsAdoptionWriter(sinkLogger));
            var heartbeat = new ManualHeartbeatFactory();

            var boom = new InvalidOperationException("analysis failed");
            var coordinator = new AdoptionCoordinator(
                new SequencedRunner(Task.FromException<CopilotAdoptionAnalysis>(boom)),
                new InMemoryAnalysisCache(),
                (window, hasOverride) => NewTelemetry(sink, heartbeat, window, hasOverride),
                TimeSpan.FromMinutes(10));

            Exception observed = null;
            try
            {
                await coordinator.GetAsync(28, new List<int>());
                Assert.Fail("the fake analysis should fail");
            }
            catch (InvalidOperationException ex)
            {
                observed = ex;
            }

            Assert.AreSame(boom, observed, "every waiter observes the one shared instance");
            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => channel.Sent.OfType<ExceptionTelemetry>().Any(),
                    TimeSpan.FromSeconds(2)),
                "the queued sink should report the accepted failure");

            // Exactly what a request awaiting the shared run does next.
            WebExceptionTelemetry.Report(observed, "WebApi /api/copilotadoption", _ => waiterLogger);
            WebExceptionTelemetry.Report(observed, "WebApi /api/copilotadoption", _ => waiterLogger);

            Assert.AreEqual(
                1,
                channel.Sent.OfType<ExceptionTelemetry>().Count(),
                "A delivered sink failure must be reported exactly once, not once per waiting request.");

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        [TestMethod]
        public void QueuedSink_AcceptedFailureDroppedByWorker_IsReportedByFallback()
        {
            var boom = new InvalidOperationException("analysis failed");
            var reported = new ConcurrentQueue<Tuple<Exception, string>>();
            var sink = new AdoptionQueuedSink(
                () => new ThrowingFailureWriter(),
                (ex, context) => reported.Enqueue(Tuple.Create(ex, context)));
            var failure = new AdoptionFailure
            {
                RunId = "00000000000000000000000000000005",
                WindowDays = 28,
                Exception = boom,
            };

            Assert.IsTrue(
                sink.TrackFailure(
                    failure,
                    LifecycleEvent(CopilotAdoptionTelemetryStages.Failed, failure.RunId)),
                "the queue must accept the failure before the worker drops it");

            Assert.IsTrue(
                SpinWait.SpinUntil(() => reported.Count == 1, TimeSpan.FromSeconds(2)),
                "a failure dropped after queue acceptance must be redeemed by fallback reporting");
            Assert.AreSame(boom, reported.Single().Item1);
            Assert.AreEqual(1, sink.DroppedEvents);

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        [TestMethod]
        public void ConcurrentWaitingRequests_ReportSharedFailureOnce()
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var channel = new BlockingExceptionTelemetryChannel();
                var configuration = new TelemetryConfiguration
                {
                    TelemetryChannel = channel,
                    ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
                };
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), "WebApiTest");
                var boom = new InvalidOperationException("analysis failed " + iteration);

                var first = Task.Run(
                    () => WebExceptionTelemetry.Report(
                        boom,
                        "WebApi /api/copilotadoption",
                        _ => logger));
                Assert.IsTrue(
                    channel.FirstExceptionSendEntered.Wait(TimeSpan.FromSeconds(2)),
                    "the first waiter should reach the telemetry send and pause before marking reported");

                var second = Task.Run(
                    () => WebExceptionTelemetry.Report(
                        boom,
                        "WebApi /api/copilotadoption",
                        _ => logger));

                channel.ReleaseFirstExceptionSend.Set();
                Assert.IsTrue(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(2)));
                Assert.AreEqual(
                    1,
                    channel.Sent.OfType<ExceptionTelemetry>().Count(),
                    "two concurrent waiters must share one atomic report claim");
            }
        }

        [TestMethod]
        public async Task FailureTelemetryFallbackFailure_DoesNotReplaceAnalysisFailure()
        {
            var boom = new InvalidOperationException("analysis failed");
            var coordinator = new AdoptionCoordinator(
                new SequencedRunner(Task.FromException<CopilotAdoptionAnalysis>(boom)),
                new InMemoryAnalysisCache(),
                (window, hasOverride) => new RejectingTelemetry(),
                TimeSpan.FromMinutes(10),
                (ex, context) => throw new ApplicationException("telemetry failed"));

            try
            {
                await coordinator.GetAsync(28, new List<int>());
                Assert.Fail("the fake analysis should fail");
            }
            catch (InvalidOperationException ex)
            {
                Assert.AreSame(boom, ex, "telemetry failure must not replace the analysis exception");
            }
        }

        private class RejectingTelemetry : StubAnalysisTelemetry
        {
            public override bool QueueFailure(Exception exception) => false;
        }

        private class AcceptingTelemetry : StubAnalysisTelemetry
        {
            public override bool QueueFailure(Exception exception) => true;
        }

        /// <summary>
        /// Does nothing except let a test decide what <c>QueueFailure</c> answers, which is the only
        /// thing the fallback-reporting decision depends on.
        /// </summary>
        private abstract class StubAnalysisTelemetry : AdoptionAnalysisTelemetry
        {
            public string RunId => "00000000000000000000000000000001";

            public long StepStarted(string step) => 0;

            public void StepCompleted(
                long operationId,
                string step,
                long durationMs,
                bool failed,
                CopilotAdoptionFailure failure = null)
            {
            }

            public long QueryStarted(string step, string query) => 0;

            public void QueryCompleted(
                long operationId,
                string step,
                string query,
                long durationMs,
                bool failed,
                CopilotAdoptionFailure failure = null)
            {
            }

            public void Checkpoint(string stage, long durationMs = 0)
            {
            }

            public void QueueCompletion(CopilotAdoptionAnalysis analysis)
            {
            }

            public abstract bool QueueFailure(Exception exception);

            public void HostStopping(string reason)
            {
            }

            public void Dispose()
            {
            }
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

        #region Failure classification

        [DataTestMethod]
        [DataRow(-2, CopilotAdoptionFailureKinds.Timeout)]
        [DataRow(1222, CopilotAdoptionFailureKinds.Timeout)]
        [DataRow(1205, CopilotAdoptionFailureKinds.Deadlock)]
        [DataRow(10928, CopilotAdoptionFailureKinds.Throttled)]
        [DataRow(40501, CopilotAdoptionFailureKinds.Throttled)]
        [DataRow(40613, CopilotAdoptionFailureKinds.Unavailable)]
        [DataRow(-1, CopilotAdoptionFailureKinds.Connection)]
        [DataRow(18456, CopilotAdoptionFailureKinds.Permission)]
        [DataRow(229, CopilotAdoptionFailureKinds.Permission)]
        [DataRow(208, CopilotAdoptionFailureKinds.SchemaMismatch)]
        [DataRow(207, CopilotAdoptionFailureKinds.SchemaMismatch)]
        [DataRow(8134, CopilotAdoptionFailureKinds.SqlError)]
        public void FailureClassification_SeparatesTheSqlErrorsAnOperatorActsOnDifferently(int number, string expected)
        {
            // Each kind has a different fix: scale the database, stop the importer during the window, apply
            // the migration, grant the login. Collapsed into "SqlException" they are indistinguishable.
            Assert.AreEqual(expected, CopilotAdoptionFailure.ClassifySqlError(number));
        }

        [TestMethod]
        public void FailureClassification_AWrappedWaitTimeoutIsATimeoutNotAConnectionFailure()
        {
            // The shape SqlClient produces for a command timeout, minus the SqlException that cannot be
            // constructed in a unit test (the SQL integration tests cover the real one): the innermost
            // exception is Win32Exception 258, which is all ExceptionType used to record.
            var failure = CopilotAdoptionFailure.From(
                new InvalidOperationException("outer", new System.ComponentModel.Win32Exception(258)));

            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, failure.FailureKind);
            Assert.AreEqual("Win32Exception", failure.ExceptionType, "ExceptionType keeps its old meaning.");
            Assert.AreEqual(258, failure.Win32ErrorCode);
            Assert.IsNull(failure.SqlErrorNumber);
            Assert.AreEqual("InvalidOperationException>Win32Exception", failure.ExceptionChain);

            var network = CopilotAdoptionFailure.From(new System.ComponentModel.Win32Exception(53));
            Assert.AreEqual(CopilotAdoptionFailureKinds.Connection, network.FailureKind,
                "Any other Win32 error on its own is a network failure, not a timeout.");
        }

        [TestMethod]
        public void FailureClassification_OnlyARequestedCancellationIsACancellation()
        {
            // EF6 / SqlClient can surface an async command timeout as a TaskCanceledException nobody asked for.
            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout,
                CopilotAdoptionFailure.From(new TaskCanceledException(), cancellationRequested: false).FailureKind);
            Assert.AreEqual(CopilotAdoptionFailureKinds.Cancelled,
                CopilotAdoptionFailure.From(new TaskCanceledException(), cancellationRequested: true).FailureKind);

            Assert.AreEqual(CopilotAdoptionFailureKinds.Other,
                CopilotAdoptionFailure.From(new InvalidOperationException("sensitive-value")).FailureKind);
            Assert.IsNull(CopilotAdoptionFailure.From(null));
        }

        [TestMethod]
        public void QueryFailedEvent_CarriesTheClassificationButNoMessage()
        {
            var sink = new RecordingSink();
            var telemetry = NewTelemetry(sink, new ManualHeartbeatFactory());
            var query = telemetry.QueryStarted(CopilotAdoptionSteps.LicensedUsers, CopilotAdoptionQueries.LicensedUserDetail);

            telemetry.QueryCompleted(
                query,
                CopilotAdoptionSteps.LicensedUsers,
                CopilotAdoptionQueries.LicensedUserDetail,
                90012,
                true,
                CopilotAdoptionFailure.From(
                    new InvalidOperationException("sensitive-value", new System.ComponentModel.Win32Exception(258))));
            telemetry.Dispose();

            var failed = sink.Events.Single(item => item.Stage == CopilotAdoptionTelemetryStages.QueryFailed);
            var dimensions = failed.Dimensions();

            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, dimensions["FailureKind"]);
            Assert.AreEqual("258", dimensions["Win32ErrorCode"]);
            Assert.AreEqual("InvalidOperationException>Win32Exception", dimensions["ExceptionChain"]);
            Assert.AreEqual("Win32Exception", dimensions["ExceptionType"]);
            Assert.IsFalse(dimensions.ContainsKey("SqlErrorNumber"), "Absent codes are omitted, not written as blanks.");
            Assert.IsFalse(string.Join("|", dimensions.Values).Contains("sensitive-value"),
                "Exception messages can quote data values and must never reach telemetry.");
        }

        [TestMethod]
        public async Task Service_AFailedQueryReachesTelemetryClassifiedNotJustTyped()
        {
            // End to end through CopilotAdoptionService.SafeAsync: the failure object, not a bare type name,
            // must reach the run telemetry. The throwing factory reproduces SqlClient's timeout shape (a
            // Win32Exception 258 inside another exception) without a database.
            var recorder = new RecordingRunTelemetry();
            var service = new CopilotAdoptionService(
                CopilotAdoptionOptions.Default,
                new global::UnitTests.FakeLoaderClasses.ThrowingAnalyticsDbContextFactory(
                    () => new InvalidOperationException("sensitive-value", new System.ComponentModel.Win32Exception(258))),
                telemetry: recorder);

            var analysis = await service.AnalyseAsync();

            Assert.IsTrue(analysis.Summary.FiguresIncomplete);
            var licenceTypes = recorder.FailedQueries.Single(item => item.Query == CopilotAdoptionQueries.LicenceTypes);
            Assert.IsNotNull(licenceTypes.Failure, "the classification must be passed through, not dropped");
            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, licenceTypes.Failure.FailureKind);
            Assert.AreEqual(258, licenceTypes.Failure.Win32ErrorCode);
        }

        [TestMethod]
        public async Task Service_ASequentialStepThatDegradesSaysWhyAndTheRunStillReportsItsTotal()
        {
            // The licence-types step and the probes run outside the concurrent phase, so they have no StepOutput
            // to carry a failure: their StepFailed event named no FailureKind, and a run that stopped at its first
            // query reported a total of 0 ms.
            var recorder = new RecordingRunTelemetry();
            var service = new CopilotAdoptionService(
                CopilotAdoptionOptions.Default,
                new global::UnitTests.FakeLoaderClasses.ThrowingAnalyticsDbContextFactory(() =>
                {
                    Thread.Sleep(30);
                    return new InvalidOperationException("sensitive-value", new System.ComponentModel.Win32Exception(258));
                }),
                telemetry: recorder);

            var analysis = await service.AnalyseAsync();

            var step = recorder.CompletedSteps.Single(item => item.Step == CopilotAdoptionSteps.LicenceTypes);
            Assert.IsTrue(step.Failed);
            Assert.IsNotNull(step.Failure, "the step event must say why it failed, not only that it did");
            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, step.Failure.FailureKind);
            Assert.IsTrue(analysis.Summary.Diagnostics.TotalMs >= 25,
                $"the early return must still record the run's total, not {analysis.Summary.Diagnostics.TotalMs} ms");
        }

        #endregion

        #region Completion event

        [TestMethod]
        public void CompletionEvent_ReportsFailuresNotCaveats()
        {
            var sink = new RecordingSink();
            var telemetry = NewTelemetry(sink, new ManualHeartbeatFactory());

            var timedOut = telemetry.QueryStarted(CopilotAdoptionSteps.LicensedUsers, CopilotAdoptionQueries.LicensedUserDetail);
            telemetry.QueryCompleted(timedOut, CopilotAdoptionSteps.LicensedUsers, CopilotAdoptionQueries.LicensedUserDetail,
                90000, true, CopilotAdoptionFailure.From(new System.ComponentModel.Win32Exception(258)));
            var missing = telemetry.QueryStarted(CopilotAdoptionSteps.CoworkReadiness, CopilotAdoptionQueries.CoworkReadiness);
            telemetry.QueryCompleted(missing, CopilotAdoptionSteps.CoworkReadiness, CopilotAdoptionQueries.CoworkReadiness,
                40, true, CopilotAdoptionFailure.From(new InvalidOperationException()));

            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.MarkFiguresIncomplete("licensed users");
            analysis.Summary.Diagnostics.Record(CopilotAdoptionSteps.LicensedUsers, 90100, failed: true);
            analysis.Summary.Diagnostics.Record(CopilotAdoptionSteps.WeeklyTrend, 1200, failed: false);
            analysis.Summary.Diagnostics.Record(CopilotAdoptionSteps.CoworkReadiness, 60, failed: true);

            telemetry.QueueCompletion(analysis);
            telemetry.Dispose();

            var completion = sink.Completions.Single();
            Assert.IsTrue(completion.FiguresIncomplete);
            Assert.AreEqual(1, completion.IncompleteReasonCount);
            Assert.AreEqual(2, completion.FailedQueryCount);
            Assert.AreEqual(1, completion.TimedOutQueryCount, "Only the classified timeout counts as one.");
            Assert.IsTrue(completion.TimedOut);
            Assert.AreEqual(
                CopilotAdoptionSteps.LicensedUsers + "," + CopilotAdoptionSteps.CoworkReadiness,
                completion.FailedSteps);
        }

        [TestMethod]
        public void CompletionEvent_ASlowStepWithoutATimeoutIsNotReportedAsTimedOut()
        {
            // The old heuristic flagged any step of 90 s or more: the six sequential probes, or the CPU-only
            // scoring step, taking that long in total without a single query timing out.
            var sink = new RecordingSink();
            var telemetry = NewTelemetry(sink, new ManualHeartbeatFactory());

            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.Diagnostics.Record(CopilotAdoptionSteps.DataSourceProbes, 120000, failed: false);
            analysis.Summary.Diagnostics.Record(CopilotAdoptionSteps.Scoring, 95000, failed: false);

            telemetry.QueueCompletion(analysis);
            telemetry.Dispose();

            var completion = sink.Completions.Single();
            Assert.IsFalse(completion.TimedOut);
            Assert.AreEqual(0, completion.FailedQueryCount);
            Assert.AreEqual(string.Empty, completion.FailedSteps);
        }

        [TestMethod]
        public void CompletionWriter_OutcomeIsDegradedOnlyWhenSomethingFailed()
        {
            var channel = new RecordingTelemetryChannel();
            var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };
            var writer = new AppInsightsAdoptionWriter(
                new AnalyticsLogger(new TelemetryClient(configuration), "CopilotAdoptionTest"));

            // Three caveats and nothing failed: the Cowork eligibility note alone is on every Cowork tenant,
            // which is why a caveat count could never be the degradation signal.
            writer.WriteCompletion(new AdoptionCompletion
            {
                RunId = "00000000000000000000000000000005",
                WindowDays = 28,
                Steps = new Dictionary<string, long>(),
                WarningCount = 3,
                FailedSteps = string.Empty,
            });

            writer.WriteCompletion(new AdoptionCompletion
            {
                RunId = "00000000000000000000000000000006",
                WindowDays = 28,
                Steps = new Dictionary<string, long>(),
                WarningCount = 1,
                FiguresIncomplete = true,
                IncompleteReasonCount = 1,
                FailedQueryCount = 1,
                TimedOutQueryCount = 1,
                TimedOut = true,
                FailedSteps = CopilotAdoptionSteps.LicensedUsers,
            });

            var events = channel.Sent.OfType<EventTelemetry>()
                .Where(item => item.Name == "CopilotAdoptionAnalysis")
                .ToList();

            Assert.AreEqual("Complete", events[0].Properties["Outcome"]);
            Assert.AreEqual("false", events[0].Properties["FiguresIncomplete"]);
            Assert.AreEqual(0d, events[0].Metrics["FailedQueryCount"]);

            Assert.AreEqual("Degraded", events[1].Properties["Outcome"]);
            Assert.AreEqual(CopilotAdoptionSteps.LicensedUsers, events[1].Properties["FailedSteps"]);
            Assert.AreEqual(1d, events[1].Metrics["TimedOutQueryCount"]);
            Assert.AreEqual("true", events[1].Properties["TimedOut"]);
        }

        #endregion

        [TestMethod]
        public void QueuedSink_FlushesAfterAHeartbeatButAtMostOncePerInterval()
        {
            // A heartbeat is the evidence an abrupt recycle leaves behind, so it is not left for the channel's
            // 30-second send interval. But a flush is a synchronous send on the sink's one worker thread, so with
            // several runs heartbeating at once only the first heartbeat in an interval may force one.
            var writer = new BlockingWriter();
            writer.ReleaseCompletion.Set();
            var sink = new AdoptionQueuedSink(() => writer);

            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.QueryStarted, "00000000000000000000000000000007"));
            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.Heartbeat, "00000000000000000000000000000007"));
            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.Heartbeat, "00000000000000000000000000000008"));
            // Never flushes. The worker is strictly in order, so once this is written the decision about the
            // second heartbeat has already been made and FlushCount can no longer change under the assertion.
            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.StepStarted, "00000000000000000000000000000007"));

            Assert.IsTrue(
                SpinWait.SpinUntil(() => writer.Events.Count >= 4, TimeSpan.FromSeconds(2)),
                "all four events must be written");
            Assert.AreEqual(1, writer.FlushCount,
                "the first heartbeat must be flushed; the second, inside the interval, must not force another send");

            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.HostStopping, "00000000000000000000000000000007"));
            Assert.IsTrue(
                SpinWait.SpinUntil(() => writer.FlushCount >= 2, TimeSpan.FromSeconds(2)),
                "a stopping host is flushed regardless of the heartbeat interval");

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        [TestMethod]
        public void QueuedSink_ARunDroppedAtTheGateIsFlushedImmediately()
        {
            // GateTimedOut and Abandoned end a run that produces no completion event, so nothing else would send them.
            var writer = new BlockingWriter();
            writer.ReleaseCompletion.Set();
            var sink = new AdoptionQueuedSink(() => writer);

            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.GateTimedOut, "00000000000000000000000000000009"));

            Assert.IsTrue(
                SpinWait.SpinUntil(() => writer.FlushCount >= 1, TimeSpan.FromSeconds(2)),
                "a run dropped at the gate must be flushed, not left in the channel buffer");

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        [TestMethod]
        public void QueuedSink_ARunTurnedAwayByAFullQueueDoesNotForceASend()
        {
            // Unlike the other early endings, which the queue bounds, a rejection happens once per poll per request
            // as fast as callers ask. A forced send for each would bring back the worker stall the heartbeat
            // throttle exists to prevent.
            var writer = new BlockingWriter();
            writer.ReleaseCompletion.Set();
            var sink = new AdoptionQueuedSink(() => writer);

            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.QueueFull, "00000000000000000000000000000010"));
            // Never flushes; once it has been written the decision about QueueFull has already been made.
            sink.Track(LifecycleEvent(
                CopilotAdoptionTelemetryStages.StepStarted, "00000000000000000000000000000010"));

            Assert.IsTrue(
                SpinWait.SpinUntil(() => writer.Events.Count >= 2, TimeSpan.FromSeconds(2)),
                "both events must be written");
            Assert.AreEqual(0, writer.FlushCount, "a QueueFull rejection must be left to the channel's own send");

            sink.Shutdown(TimeSpan.FromSeconds(2));
        }

        #region Admission gate

        [TestMethod]
        public async Task Gate_QueuesAnalysesBeyondTheLimitAndAdmitsThemInTurn()
        {
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var coordinator = NewGatedCoordinator(runner, sink, maxConcurrentAnalyses: 1);

            var first = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1), "the first analysis must start straight away");

            var second = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(
                SpinWait.SpinUntil(() => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                    TimeSpan.FromSeconds(2)),
                "a second window must queue rather than start a second full analysis");
            Thread.Sleep(50);
            Assert.AreEqual(1, runner.CallCount, "the queued analysis must not have started");

            runner.Complete(28, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await first);

            Assert.IsTrue(runner.WaitForCalls(2), "the slot must pass to the queued analysis");
            runner.Complete(90, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await second);

            var queuedRunId = sink.Events.Single(item => item.Stage == CopilotAdoptionTelemetryStages.Queued).RunId;
            var admitted = sink.Events.Single(item =>
                item.Stage == CopilotAdoptionTelemetryStages.GateAcquired && item.RunId == queuedRunId);
            Assert.IsTrue(admitted.DurationMs >= 40, "GateAcquired must say how long the run waited");
            Assert.IsTrue(admitted.Measurements().ContainsKey("DurationMs"));
        }

        [TestMethod]
        public async Task Gate_AQueuedRunNobodyStillWantsIsAbandonedNotRun()
        {
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var clock = new FakeUtcClock(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var coordinator = NewGatedCoordinator(runner, sink, maxConcurrentAnalyses: 1, utcNow: () => clock.UtcNow);

            var first = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            // A period the user clicked past: requested once, then never polled again.
            var abandoned = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                TimeSpan.FromSeconds(2)));
            Assert.IsNotNull(coordinator.InFlightRunId(90, new List<int>()), "a queued run already has a RunId");

            clock.Advance(AdoptionCoordinator.DefaultAbandonAfter + TimeSpan.FromSeconds(1));
            runner.Complete(28, new CopilotAdoptionAnalysis());
            await first;

            // Bounded: were abandonment to regress, the run would be waiting on a fake runner call that this test
            // never completes, and an unbounded await would hang the test run instead of failing it.
            Assert.AreSame(abandoned, await Task.WhenAny(abandoned, Task.Delay(TimeSpan.FromSeconds(5))),
                "an abandoned run must finish without ever reaching the database");
            Assert.IsNull(await abandoned, "an abandoned run produces nothing");
            Assert.AreEqual(1, runner.CallCount, "it must never have queried the database");
            Assert.IsTrue(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Abandoned));
            Assert.IsNull(coordinator.InFlightRunId(90, new List<int>()));

            // Asked for again, it runs normally.
            var retried = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(2));
            runner.Complete(90, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await retried);
        }

        [TestMethod]
        public async Task Gate_NeverAbandonsARunARequestIsStillWaitingOn()
        {
            // An export waits up to 150 seconds without re-asking. It must count as interest throughout.
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var clock = new FakeUtcClock(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var coordinator = NewGatedCoordinator(runner, sink, maxConcurrentAnalyses: 1, utcNow: () => clock.UtcNow);

            var first = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var export = coordinator.TryGetAsync(90, new List<int>(), TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.IsTrue(SpinWait.SpinUntil(
                () => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                TimeSpan.FromSeconds(2)));

            clock.Advance(AdoptionCoordinator.DefaultAbandonAfter + TimeSpan.FromSeconds(1));
            runner.Complete(28, new CopilotAdoptionAnalysis());
            await first;

            Assert.IsTrue(runner.WaitForCalls(2), "a run somebody is waiting for must still start");
            var analysis = new CopilotAdoptionAnalysis();
            runner.Complete(90, analysis);
            Assert.AreSame(analysis, await export);
            Assert.IsFalse(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Abandoned));
        }

        [TestMethod]
        public async Task Gate_ARunStuckInItsSlotCannotBlockEveryOtherAnalysis()
        {
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var coordinator = NewGatedCoordinator(
                runner, sink, maxConcurrentAnalyses: 1, maxQueueWait: TimeSpan.FromMilliseconds(100));

            var hung = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var next = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(2), "after the maximum queue wait the run must proceed anyway");
            Assert.IsTrue(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.GateBypassed));

            runner.Complete(90, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await next);
            runner.Complete(28, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await hung);
        }

        [TestMethod]
        public async Task Gate_OnlyOneRunMayBypassAFullGateAndAnyOtherIsDropped()
        {
            // Every run that waited out the queue used to proceed, so a database that had stopped keeping up got
            // one extra analysis per timed-out waiter - an unbounded pile-up. One overflow slot bounds it.
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var coordinator = NewGatedCoordinator(
                runner, sink, maxConcurrentAnalyses: 1, maxQueueWait: TimeSpan.FromMilliseconds(100));

            var hung = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var bypassing = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(2), "the first run to wait out the queue proceeds on the overflow slot");

            var dropped = coordinator.GetAsync(180, new List<int>());
            Assert.AreSame(dropped, await Task.WhenAny(dropped, Task.Delay(TimeSpan.FromSeconds(5))),
                "with the overflow slot taken as well, a further run must be dropped, not left waiting on the database");
            Assert.IsNull(await dropped, "a dropped run produces nothing");
            Assert.AreEqual(2, runner.CallCount, "the dropped run must never have reached the database");
            var timedOut = sink.Events.Single(item => item.Stage == CopilotAdoptionTelemetryStages.GateTimedOut);
            Assert.IsTrue(timedOut.Measurements().ContainsKey("DurationMs"), "GateTimedOut must say how long it waited");
            Assert.IsNull(coordinator.InFlightRunId(180, new List<int>()), "a dropped run leaves nothing in flight");

            runner.Complete(90, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await bypassing);

            var afterRelease = coordinator.GetAsync(180, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(3), "the overflow slot must be released when its run finishes");
            runner.Complete(180, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await afterRelease);

            runner.Complete(28, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await hung);
        }

        [TestMethod]
        public async Task Gate_AWaiterCountsAsInterestUntilTheMomentItGivesUp()
        {
            // An export waits 150 seconds and then answers 503. Measured from when it JOINED, its interest was
            // already stale the instant it left, so the queued run was thrown away and the user's retry could
            // never find it running.
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var clock = new FakeUtcClock(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var coordinator = NewGatedCoordinator(runner, sink, maxConcurrentAnalyses: 1, utcNow: () => clock.UtcNow);

            var first = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            // TryGetAsync has joined and registered the wait before it returns, so the clock can move straight
            // away: the export's real two-second budget cannot run out before the fake minute has passed, which
            // is what made a slow machine fail this test with a 300 ms budget.
            var export = coordinator.TryGetAsync(90, new List<int>(), TimeSpan.FromSeconds(2), CancellationToken.None);
            clock.Advance(AdoptionCoordinator.DefaultAbandonAfter + TimeSpan.FromSeconds(1));
            Assert.IsTrue(SpinWait.SpinUntil(
                () => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                TimeSpan.FromSeconds(2)));

            // The export's whole wait passes, then it gives up.
            Assert.IsNull(await export, "the export's budget ran out before the run could start");

            runner.Complete(28, new CopilotAdoptionAnalysis());
            await first;

            Assert.IsTrue(runner.WaitForCalls(2), "the run was wanted until a moment ago, so it must still start");
            var analysis = new CopilotAdoptionAnalysis();
            runner.Complete(90, analysis);
            Assert.AreSame(analysis, await coordinator.GetAsync(90, new List<int>()));
            Assert.IsFalse(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Abandoned));
        }

        [TestMethod]
        public async Task Gate_ARequestArrivingAsARunIsAbandonedStartsAFreshRunRatherThanJoiningIt()
        {
            // Between a run deciding it was abandoned and leaving the in-flight table, a request could still join
            // it - and be handed the null of a run that was never going to happen.
            var sink = new RecordingSink { BlockStage = CopilotAdoptionTelemetryStages.Abandoned };
            var runner = new PerCallRunner();
            var clock = new FakeUtcClock(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var coordinator = NewGatedCoordinator(runner, sink, maxConcurrentAnalyses: 1, utcNow: () => clock.UtcNow);

            var first = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var forgotten = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                TimeSpan.FromSeconds(2)));

            clock.Advance(AdoptionCoordinator.DefaultAbandonAfter + TimeSpan.FromSeconds(1));
            runner.Complete(28, new CopilotAdoptionAnalysis());
            await first;

            // The forgotten run has taken the slot and judged itself abandoned, but is still in the table.
            Assert.IsTrue(sink.StageEntered.Wait(TimeSpan.FromSeconds(5)), "the queued run must be abandoned");
            var arriving = coordinator.GetAsync(90, new List<int>());
            sink.ReleaseStage.Set();

            Assert.IsNull(await forgotten, "the abandoned run itself still produces nothing");
            Assert.IsTrue(runner.WaitForCalls(2), "the late request must start a run of its own");
            var analysis = new CopilotAdoptionAnalysis();
            runner.Complete(90, analysis);
            Assert.AreSame(analysis, await arriving, "and receive that run's result, not the abandoned run's null");
        }

        [TestMethod]
        public async Task Gate_AQueuedRunNobodyWantsIsReleasedWithoutWaitingForASlot()
        {
            // Behind a hung analysis, a run nobody wanted stayed queued - holding its telemetry and sending
            // heartbeats - for the whole queue wait, because it only checked whether it was wanted once it got a slot.
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var clock = new FakeUtcClock(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
            var coordinator = NewGatedCoordinator(
                runner, sink, maxConcurrentAnalyses: 1, utcNow: () => clock.UtcNow,
                queuePollInterval: TimeSpan.FromMilliseconds(20));

            var hung = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var forgotten = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                TimeSpan.FromSeconds(2)));
            Thread.Sleep(100);
            Assert.IsFalse(forgotten.IsCompleted, "a run asked for moments ago must keep its place in the queue");

            clock.Advance(AdoptionCoordinator.DefaultAbandonAfter + TimeSpan.FromSeconds(1));

            Assert.AreSame(forgotten, await Task.WhenAny(forgotten, Task.Delay(TimeSpan.FromSeconds(5))),
                "it must be released while the slot is still held");
            Assert.IsNull(await forgotten);
            Assert.AreEqual(1, runner.CallCount);
            var queuedRunId = sink.Events.Single(item => item.Stage == CopilotAdoptionTelemetryStages.Queued).RunId;
            Assert.IsTrue(sink.Events.Any(item =>
                item.Stage == CopilotAdoptionTelemetryStages.Abandoned && item.RunId == queuedRunId));
            Assert.IsFalse(sink.Events.Any(item =>
                item.Stage == CopilotAdoptionTelemetryStages.GateAcquired && item.RunId == queuedRunId),
                "it was released from the queue, not admitted");

            runner.Complete(28, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await hung);

            // The slot it never took is still usable.
            var next = coordinator.GetAsync(180, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(2));
            runner.Complete(180, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await next);
        }

        [TestMethod]
        public async Task Gate_BeyondTheQueueLimitARunIsTurnedAwayAtOnceRatherThanQueued()
        {
            // Every queued run holds its telemetry and a heartbeat registration, and a seat override is
            // caller-supplied, so a burst of distinct overrides used to queue without limit.
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var coordinator = NewGatedCoordinator(runner, sink, maxConcurrentAnalyses: 1, maxQueuedAnalyses: 1);

            var first = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var queued = coordinator.GetAsync(90, new List<int>());
            Assert.IsTrue(SpinWait.SpinUntil(
                () => sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued),
                TimeSpan.FromSeconds(2)));

            var rejected = coordinator.GetAsync(180, new List<int>());
            Assert.AreSame(rejected, await Task.WhenAny(rejected, Task.Delay(TimeSpan.FromSeconds(5))),
                "with the queue full, a further run must be turned away at once, not left waiting");
            Assert.IsNull(await rejected, "a run turned away produces nothing");
            var rejectedRunId = sink.Events.Single(item => item.Stage == CopilotAdoptionTelemetryStages.QueueFull).RunId;
            Assert.IsFalse(
                sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Queued && item.RunId == rejectedRunId),
                "a run turned away never joined the queue");
            Assert.IsNull(coordinator.InFlightRunId(180, new List<int>()), "nothing is left in flight for it");

            runner.Complete(28, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await first);
            Assert.IsTrue(runner.WaitForCalls(2), "the run that did queue still gets the slot");
            runner.Complete(90, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await queued);

            // Asked for again once there is room, it runs - and the rejection itself never reached the database.
            var retried = coordinator.GetAsync(180, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(3));
            runner.Complete(180, new CopilotAdoptionAnalysis());
            Assert.IsNotNull(await retried);
            Assert.AreEqual(3, runner.CallCount);
        }

        [TestMethod]
        public void Gate_TheQueueWaitLeavesTimeForAnExportOrThePageToCollectTheResult()
        {
            // A run reaches the overflow slot only after the whole queue wait. Waiting as long as the page polls -
            // ten minutes, POLL_CEILING_MS in copilotAdoptionApi.ts - admitted it just as the page gave up. And a run
            // started by an export is only wanted until the export's budget plus the abandonment window.
            Assert.IsTrue(
                AdoptionCoordinator.DefaultMaxQueueWait
                    < CopilotAdoptionAPIController.ExportWaitBudget + AdoptionCoordinator.DefaultAbandonAfter,
                "a run started by an export would be abandoned before it could reach the overflow slot");
            Assert.IsTrue(
                AdoptionCoordinator.DefaultMaxQueueWait <= TimeSpan.FromMinutes(5),
                "a run admitted on the overflow slot needs at least half of the page's ten minutes to finish");
        }

        [TestMethod]
        public async Task CompletedAnalysis_CarriesTheRunIdOfTheRunThatProducedIt()
        {
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var coordinator = NewGatedCoordinator(runner, sink);

            var pending = coordinator.GetAsync(28, new List<int>());
            Assert.IsTrue(runner.WaitForCalls(1));

            var runId = sink.Events.First(item => item.Stage == CopilotAdoptionTelemetryStages.Started).RunId;
            Assert.AreEqual(runId, coordinator.InFlightRunId(28, new List<int>()),
                "a 202 must be able to name the run it is waiting on");

            runner.Complete(28, new CopilotAdoptionAnalysis());
            var analysis = await pending;

            Assert.AreEqual(runId, analysis.Summary.Diagnostics.RunId,
                "the summary JSON must name the run so a browser trace can be matched to telemetry");
            Assert.IsNull(coordinator.InFlightRunId(28, new List<int>()), "nothing is in flight once it completes");
        }

        [TestMethod]
        public void StillBuildingBody_NamesTheRunWhenThereIsOne()
        {
            var body = CopilotAdoptionAPIController.StillBuildingBody("00000000000000000000000000000008");
            Assert.AreEqual("building", body["status"], "the SPA detects this exact value");
            Assert.AreEqual("00000000000000000000000000000008", body["runId"]);
            Assert.IsTrue(body.ContainsKey("retryAfterSeconds"));

            Assert.IsFalse(
                CopilotAdoptionAPIController.StillBuildingBody(null).ContainsKey("runId"),
                "with telemetry off there is no run id to give, and none is invented");
        }

        [TestMethod]
        public async Task Coordinator_ARunThatStartsAfterItsResultWasPublishedReturnsItWithoutRunning()
        {
            // A request can join a freshly inserted generation in the instant after the result it would compute was
            // published, and the Join that inserted it may then detach it. The run itself has to check first, or it
            // repeats a whole analysis whose result every caller can already read.
            var published = new CopilotAdoptionAnalysis();
            var sink = new RecordingSink();
            var runner = new PerCallRunner();
            var coordinator = new AdoptionCoordinator(
                runner,
                new LatePublishingCache(published, missesBeforeHit: 2), // both of Join's own checks miss
                (window, hasOverride) => NewTelemetry(sink, new ManualHeartbeatFactory(), window, hasOverride),
                TimeSpan.FromMinutes(10));

            var pending = coordinator.GetAsync(28, new List<int>());

            Assert.AreSame(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))),
                "the run must return the published result, not start an analysis");
            Assert.AreSame(published, await pending);
            Assert.AreEqual(0, runner.CallCount, "it must never have queried the database");
            Assert.IsFalse(sink.Events.Any(), "nothing was started, so there is no run to report");
            Assert.IsNull(coordinator.InFlightRunId(28, new List<int>()));
        }

        #endregion

        #region Shared heartbeat thread

        [TestMethod]
        public void Heartbeats_EveryRunSharesOneDedicatedThread()
        {
            // A thread per run meant every run - even one the gate turned away - created an OS thread just to be
            // observed. They now share one, and it is still a dedicated thread rather than a pool thread, so
            // heartbeats keep arriving while the pool is starved.
            var factory = new SharedHeartbeatFactory();
            var threads = new ConcurrentDictionary<int, bool>();
            var fromPool = 0;
            var counts = new int[3];
            var registrations = Enumerable.Range(0, 3)
                .Select(index => factory.Start(() =>
                {
                    threads[Thread.CurrentThread.ManagedThreadId] = true;
                    if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref fromPool);
                    Interlocked.Increment(ref counts[index]);
                }, TimeSpan.FromMilliseconds(20)))
                .ToList();

            try
            {
                Assert.IsTrue(
                    SpinWait.SpinUntil(
                        () => Enumerable.Range(0, 3).All(index => Volatile.Read(ref counts[index]) >= 2),
                        TimeSpan.FromSeconds(5)),
                    "every registered run must get its heartbeats");
                Assert.AreEqual(1, threads.Count, "every run's heartbeats must come from the one thread");
                Assert.AreEqual(0, Volatile.Read(ref fromPool), "and it must be a dedicated thread, not a pool thread");
            }
            finally
            {
                foreach (var registration in registrations) registration.Dispose();
            }
        }

        [TestMethod]
        public void Heartbeats_AFinishedOrFailingRunDoesNotStopTheOthers()
        {
            var factory = new SharedHeartbeatFactory();
            var finished = 0;
            var healthy = 0;
            var throwing = factory.Start(
                () => throw new InvalidOperationException("synthetic heartbeat failure"), TimeSpan.FromMilliseconds(20));
            var toFinish = factory.Start(() => Interlocked.Increment(ref finished), TimeSpan.FromMilliseconds(20));
            var keeps = factory.Start(() => Interlocked.Increment(ref healthy), TimeSpan.FromMilliseconds(20));

            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref finished) >= 2, TimeSpan.FromSeconds(5)));
                toFinish.Dispose();
                var finishedAtDispose = Volatile.Read(ref finished);
                var healthyAtDispose = Volatile.Read(ref healthy);

                Assert.IsTrue(
                    SpinWait.SpinUntil(() => Volatile.Read(ref healthy) >= healthyAtDispose + 3, TimeSpan.FromSeconds(5)),
                    "a run whose heartbeat throws, or one that has finished, must not stop the others");
                Assert.IsTrue(Volatile.Read(ref finished) <= finishedAtDispose + 1,
                    "a finished run gets at most the one call that was already under way when it was disposed");
            }
            finally
            {
                throwing.Dispose();
                keeps.Dispose();
            }
        }

        [TestMethod]
        public void Heartbeats_ARunRegisteredAfterEveryOtherFinishedStillGetsThem()
        {
            // The thread exits once nothing is registered, and the next run must get a new one. The first worker is
            // captured and waited for, so the replacement path is exercised every time rather than only when the old
            // worker happens to have exited already.
            var workers = new ConcurrentQueue<Thread>();
            var factory = new SharedHeartbeatFactory(run =>
            {
                var worker = new Thread(run) { IsBackground = true };
                workers.Enqueue(worker);
                return worker;
            });

            var first = 0;
            var registration = factory.Start(() => Interlocked.Increment(ref first), TimeSpan.FromMilliseconds(20));
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref first) >= 1, TimeSpan.FromSeconds(5)));
            registration.Dispose();

            Assert.IsTrue(workers.TryPeek(out var firstWorker));
            Assert.IsTrue(firstWorker.Join(TimeSpan.FromSeconds(5)), "with nothing left to serve, the worker must exit");

            var second = 0;
            using (factory.Start(() => Interlocked.Increment(ref second), TimeSpan.FromMilliseconds(20)))
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref second) >= 2, TimeSpan.FromSeconds(5)),
                    "a run registered after the thread went idle must still get heartbeats");
            }

            Assert.AreEqual(2, workers.Count, "the second run must have been given a new worker");
        }

        [TestMethod]
        public void Heartbeats_TheThreadExitsAsSoonAsTheLastRunFinishes()
        {
            // Removing the last run did not wake the thread, so it held on until that run's next due time: 30 seconds
            // in production, but indefinitely for a long interval. It must notice at once.
            var workers = new ConcurrentQueue<Thread>();
            var factory = new SharedHeartbeatFactory(run =>
            {
                var created = new Thread(run) { IsBackground = true };
                workers.Enqueue(created);
                return created;
            });

            // Park the thread in a long wait: a short run proves it is looping, then leaves, so all that remains is a
            // run due in an hour.
            var beats = 0;
            var shortRun = factory.Start(() => Interlocked.Increment(ref beats), TimeSpan.FromMilliseconds(20));
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref beats) >= 2, TimeSpan.FromSeconds(5)));
            var longRun = factory.Start(() => { }, TimeSpan.FromHours(1));
            shortRun.Dispose();
            Thread.Sleep(500);

            Assert.IsTrue(workers.TryPeek(out var worker));
            Assert.IsTrue(worker.IsAlive, "the thread is still serving the long run");
            longRun.Dispose();

            Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)),
                "the thread must exit once its last run is removed, not an hour later");
        }

        [TestMethod]
        public void RunTelemetry_HostShutdownAfterARunHasFinishedEmitsNothingForIt()
        {
            // Host shutdown works from a snapshot of active runs, so it can reach one that has just finished. An
            // "Interrupted" event after its CachePublished would make a completed run read as cut off.
            var sink = new RecordingSink();
            var telemetry = NewTelemetry(sink, new ManualHeartbeatFactory());

            telemetry.Dispose();
            telemetry.HostStopping("synthetic shutdown");

            Assert.IsFalse(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.HostStopping));
        }

        [TestMethod]
        public void RunTelemetry_HostShutdownOnceTheResultIsPublishedDoesNotMarkTheRunInterrupted()
        {
            // Between publishing its result and being disposed, a run is still in the snapshot host shutdown works
            // from. A HostStopping there followed CachePublished, so a completed run read as interrupted.
            var sink = new RecordingSink();
            var published = NewTelemetry(sink, new ManualHeartbeatFactory());
            published.Checkpoint(CopilotAdoptionTelemetryStages.CachePublished);
            published.HostStopping("synthetic shutdown");

            Assert.IsFalse(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.HostStopping),
                "a run whose result was already published was not interrupted");

            var running = NewTelemetry(sink, new ManualHeartbeatFactory());
            running.HostStopping("synthetic shutdown");

            Assert.AreEqual(1, sink.Events.Count(item => item.Stage == CopilotAdoptionTelemetryStages.HostStopping),
                "a run still in progress must still report the shutdown");
        }

        [TestMethod]
        public void Heartbeats_AThreadThatFailsToStartLeavesNothingBehind()
        {
            // Thread.Start throws when the process cannot create a thread. The factory used to record the thread as
            // running before starting it, and to register the run first, so a failed start left a dead thread on
            // record - no later run in the AppDomain ever got a heartbeat - and a registration nobody would dispose.
            var attempts = 0;
            var factory = new SharedHeartbeatFactory(run =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    // A thread that has already run: starting it again throws, standing in for a failed start.
                    var spent = new Thread(() => { }) { IsBackground = true };
                    spent.Start();
                    spent.Join();
                    return spent;
                }

                return new Thread(run) { IsBackground = true };
            });

            var failedRunBeats = 0;
            Assert.ThrowsException<ThreadStateException>(() =>
                factory.Start(() => Interlocked.Increment(ref failedRunBeats), TimeSpan.FromMilliseconds(20)));

            var beats = 0;
            using (factory.Start(() => Interlocked.Increment(ref beats), TimeSpan.FromMilliseconds(20)))
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref beats) >= 2, TimeSpan.FromSeconds(5)),
                    "a run registered after a failed start must still get heartbeats");
            }

            Assert.AreEqual(0, Volatile.Read(ref failedRunBeats),
                "the run whose start failed was never registered, so nothing calls its heartbeat");
        }

        [TestMethod]
        public void RunTelemetry_NoHeartbeatIsEmittedOnceItHasBeenDisposed()
        {
            // The per-run thread used to be joined on dispose. With a shared thread, a heartbeat already due can
            // still be delivered after the run has finished; it must not produce an event.
            var sink = new RecordingSink();
            var heartbeat = new CapturingHeartbeatFactory();
            var telemetry = NewTelemetry(sink, heartbeat);

            telemetry.Dispose();
            heartbeat.Callback(); // what the shared thread may still do with a registration it had already read

            Assert.IsFalse(sink.Events.Any(item => item.Stage == CopilotAdoptionTelemetryStages.Heartbeat));
        }

        #endregion

        private static AdoptionCoordinator NewGatedCoordinator(
            AdoptionRunner runner,
            RecordingSink sink,
            int maxConcurrentAnalyses = AdoptionCoordinator.DefaultMaxConcurrentAnalyses,
            TimeSpan? maxQueueWait = null,
            Func<DateTime> utcNow = null,
            TimeSpan? queuePollInterval = null,
            int maxQueuedAnalyses = AdoptionCoordinator.DefaultMaxQueuedAnalyses)
        {
            var heartbeat = new ManualHeartbeatFactory();
            return new AdoptionCoordinator(
                runner,
                new InMemoryAnalysisCache(),
                (window, hasOverride) => NewTelemetry(sink, heartbeat, window, hasOverride),
                TimeSpan.FromMinutes(10),
                reportUnqueuedFailure: null,
                maxConcurrentAnalyses: maxConcurrentAnalyses,
                maxQueueWait: maxQueueWait,
                utcNow: utcNow,
                queuePollInterval: queuePollInterval,
                maxQueuedAnalyses: maxQueuedAnalyses);
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

            /// <summary>A lifecycle stage whose Track call blocks until <see cref="ReleaseStage"/> is set.</summary>
            public string BlockStage { get; set; }
            public ManualResetEventSlim StageEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim ReleaseStage { get; } =
                new ManualResetEventSlim(false);
            public int DroppedEvents => 0;

            public List<AdoptionCompletion> Completions
            {
                get
                {
                    lock (_gate)
                    {
                        return _completions.ToList();
                    }
                }
            }

            private readonly List<AdoptionCompletion> _completions = new List<AdoptionCompletion>();

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

                if (BlockStage != null && telemetryEvent.Stage == BlockStage)
                {
                    StageEntered.Set();
                    ReleaseStage.Wait(TimeSpan.FromSeconds(5));
                }
            }

            public void TrackCompletion(
                AdoptionCompletion completion,
                AdoptionEvent submittedEvent)
            {
                lock (_gate)
                {
                    _completions.Add(completion);
                }
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

        private sealed class RejectingSink : AdoptionSink
        {
            public int DroppedEvents => 1;

            public void Track(AdoptionEvent telemetryEvent)
            {
            }

            public void TrackCompletion(
                AdoptionCompletion completion,
                AdoptionEvent submittedEvent)
            {
            }

            public bool TrackFailure(
                AdoptionFailure failure,
                AdoptionEvent failureEvent)
            {
                return false;
            }

            public void Shutdown(TimeSpan timeout)
            {
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

        private sealed class BlockingExceptionTelemetryChannel : ITelemetryChannel
        {
            private readonly object _gate = new object();
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();
            private int _blockedFirstException;

            public ManualResetEventSlim FirstExceptionSendEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim ReleaseFirstExceptionSend { get; } =
                new ManualResetEventSlim(false);

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
                if (item is ExceptionTelemetry
                    && Interlocked.CompareExchange(ref _blockedFirstException, 1, 0) == 0)
                {
                    FirstExceptionSendEntered.Set();
                    ReleaseFirstExceptionSend.Wait(TimeSpan.FromSeconds(2));
                }

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

        /// <summary>
        /// Keeps the heartbeat callback so a test can call it after the run is disposed, as the shared heartbeat
        /// thread can for a registration it had already read.
        /// </summary>
        private sealed class CapturingHeartbeatFactory : AdoptionHeartbeatFactory
        {
            public Action Callback { get; private set; }

            public IDisposable Start(Action heartbeat, TimeSpan interval)
            {
                Callback = heartbeat;
                return new CallbackDisposable(() => { });
            }
        }

        /// <summary>
        /// Misses a set number of times, then returns the published analysis: a result that appeared while a
        /// request was joining.
        /// </summary>
        private sealed class LatePublishingCache : AdoptionCache
        {
            private readonly CopilotAdoptionAnalysis _published;
            private int _missesLeft;

            public LatePublishingCache(CopilotAdoptionAnalysis published, int missesBeforeHit)
            {
                _published = published;
                _missesLeft = missesBeforeHit;
            }

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                if (Interlocked.Decrement(ref _missesLeft) >= 0)
                {
                    analysis = null;
                    return false;
                }

                analysis = _published;
                return true;
            }

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
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
            private int _flushes;

            public ManualResetEventSlim CompletionEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim ReleaseCompletion { get; } =
                new ManualResetEventSlim(false);

            public int FlushCount => Volatile.Read(ref _flushes);

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
                Interlocked.Increment(ref _flushes);
            }
        }

        /// <summary>
        /// A runner whose every call can be completed individually, keyed by window, so a test can hold one
        /// analysis in its admission slot while another queues behind it.
        /// </summary>
        private sealed class PerCallRunner : AdoptionRunner
        {
            private readonly ConcurrentDictionary<int, TaskCompletionSource<CopilotAdoptionAnalysis>> _pending =
                new ConcurrentDictionary<int, TaskCompletionSource<CopilotAdoptionAnalysis>>();
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public Task<CopilotAdoptionAnalysis> RunAsync(
                int windowDays,
                List<int> seatLicenceTypeIds,
                ICopilotAdoptionRunTelemetry telemetry)
            {
                var completion = new TaskCompletionSource<CopilotAdoptionAnalysis>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[windowDays] = completion;
                Interlocked.Increment(ref _callCount);
                return completion.Task;
            }

            public bool WaitForCalls(int count) =>
                SpinWait.SpinUntil(() => CallCount >= count, TimeSpan.FromSeconds(5));

            public void Complete(int windowDays, CopilotAdoptionAnalysis analysis) =>
                _pending[windowDays].SetResult(analysis);
        }

        private sealed class FakeUtcClock
        {
            private long _ticks;

            public FakeUtcClock(DateTime startUtc)
            {
                _ticks = startUtc.Ticks;
            }

            public DateTime UtcNow => new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

            public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        }

        /// <summary>Records what the analysis reports to its run telemetry, for end-to-end service tests.</summary>
        private sealed class RecordingRunTelemetry : ICopilotAdoptionRunTelemetry
        {
            private readonly ConcurrentQueue<(string Query, CopilotAdoptionFailure Failure)> _failedQueries =
                new ConcurrentQueue<(string Query, CopilotAdoptionFailure Failure)>();
            private readonly ConcurrentQueue<(string Step, bool Failed, CopilotAdoptionFailure Failure)> _steps =
                new ConcurrentQueue<(string Step, bool Failed, CopilotAdoptionFailure Failure)>();

            public List<(string Query, CopilotAdoptionFailure Failure)> FailedQueries => _failedQueries.ToList();

            public List<(string Step, bool Failed, CopilotAdoptionFailure Failure)> CompletedSteps => _steps.ToList();

            public long StepStarted(string step) => 0;

            public void StepCompleted(long operationId, string step, long durationMs, bool failed,
                CopilotAdoptionFailure failure = null)
            {
                _steps.Enqueue((step, failed, failure));
            }

            public long QueryStarted(string step, string query) => 0;

            public void QueryCompleted(long operationId, string step, string query, long durationMs, bool failed,
                CopilotAdoptionFailure failure = null)
            {
                if (failed) _failedQueries.Enqueue((query, failure));
            }

            public void Checkpoint(string stage, long durationMs = 0)
            {
            }
        }

        private sealed class ThrowingFailureWriter : AdoptionWriter
        {
            public void Write(AdoptionEvent telemetryEvent)
            {
            }

            public void WriteCompletion(AdoptionCompletion completion)
            {
            }

            public void WriteFailure(AdoptionFailure failure)
            {
                throw new InvalidOperationException("synthetic telemetry failure");
            }

            public void Flush()
            {
            }
        }
    }
}
