using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using DataUtils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Web.AnalyticsWeb.Models.CopilotAdoption
{
    internal sealed class CopilotAdoptionLifecycleEvent
    {
        public const string SchemaVersion = "1";

        public DateTimeOffset OccurredUtc { get; set; }
        public string Stage { get; set; }
        public string RunId { get; set; }
        public string InstanceId { get; set; }
        public int WindowDays { get; set; }
        public bool HasSeatOverride { get; set; }
        public string Step { get; set; }
        public string Query { get; set; }
        public string Outcome { get; set; }
        public string ExceptionType { get; set; }

        /// <summary>A <see cref="CopilotAdoptionFailureKinds"/> value: Timeout, Deadlock, Throttled, SchemaMismatch...</summary>
        public string FailureKind { get; set; }

        /// <summary>Exception type names, outermost to innermost. Type names only - never a message.</summary>
        public string ExceptionChain { get; set; }

        /// <summary>SQL Server error number, e.g. "-2" (timeout), "1205" (deadlock), "208" (invalid object).</summary>
        public string SqlErrorNumber { get; set; }

        /// <summary>Win32 native error code, e.g. "258" (wait operation timed out).</summary>
        public string Win32ErrorCode { get; set; }

        public string ActiveOperations { get; set; }
        public string SynchronizationContext { get; set; }
        public string ShutdownReason { get; set; }
        public long Sequence { get; set; }
        public long OperationId { get; set; }
        public long ElapsedMs { get; set; }
        public long DurationMs { get; set; }
        public long HeartbeatDriftMs { get; set; }
        public long ProcessWorkingSetBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public int ThreadPoolAvailableWorkers { get; set; }
        public int ThreadPoolAvailableCompletionPorts { get; set; }
        public int AppDomainId { get; set; }
        public long AppDomainUptimeMs { get; set; }
        public int DroppedEvents { get; set; }

        public Dictionary<string, string> Dimensions()
        {
            var result = new Dictionary<string, string>
            {
                { "SchemaVersion", SchemaVersion },
                { "Stage", Stage },
                { "RunId", RunId },
                { "InstanceId", InstanceId },
                { "WindowDays", WindowDays.ToString(CultureInfo.InvariantCulture) },
                { "HasSeatOverride", HasSeatOverride ? "true" : "false" },
                { "SynchronizationContext", SynchronizationContext ?? "None" },
            };

            AddIfPresent(result, "Step", Step);
            AddIfPresent(result, "Query", Query);
            AddIfPresent(result, "Outcome", Outcome);
            AddIfPresent(result, "ExceptionType", ExceptionType);
            AddIfPresent(result, "FailureKind", FailureKind);
            AddIfPresent(result, "ExceptionChain", ExceptionChain);
            AddIfPresent(result, "SqlErrorNumber", SqlErrorNumber);
            AddIfPresent(result, "Win32ErrorCode", Win32ErrorCode);
            AddIfPresent(result, "ActiveOperations", ActiveOperations);
            AddIfPresent(result, "ShutdownReason", ShutdownReason);
            return result;
        }

        public Dictionary<string, double> Measurements()
        {
            var result = new Dictionary<string, double>
            {
                { "Sequence", Sequence },
                { "ElapsedMs", ElapsedMs },
                { "AppDomainId", AppDomainId },
                { "AppDomainUptimeMs", AppDomainUptimeMs },
                { "DroppedEvents", DroppedEvents },
            };

            if (OperationId > 0) result.Add("OperationId", OperationId);
            if (HasDuration(Stage)) result.Add("DurationMs", DurationMs);
            if (HeartbeatDriftMs > 0) result.Add("HeartbeatDriftMs", HeartbeatDriftMs);
            if (ProcessWorkingSetBytes > 0) result.Add("ProcessWorkingSetBytes", ProcessWorkingSetBytes);
            if (ManagedHeapBytes > 0) result.Add("ManagedHeapBytes", ManagedHeapBytes);
            if (Gen0Collections >= 0) result.Add("Gen0Collections", Gen0Collections);
            if (Gen1Collections >= 0) result.Add("Gen1Collections", Gen1Collections);
            if (Gen2Collections >= 0) result.Add("Gen2Collections", Gen2Collections);
            if (ThreadPoolAvailableWorkers >= 0)
            {
                result.Add("ThreadPoolAvailableWorkers", ThreadPoolAvailableWorkers);
            }
            if (ThreadPoolAvailableCompletionPorts >= 0)
            {
                result.Add("ThreadPoolAvailableCompletionPorts", ThreadPoolAvailableCompletionPorts);
            }

            return result;
        }

        private static void AddIfPresent(IDictionary<string, string> target, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) target.Add(key, value);
        }

        private static bool HasDuration(string stage)
        {
            return stage == CopilotAdoptionTelemetryStages.QueryCompleted
                   || stage == CopilotAdoptionTelemetryStages.QueryFailed
                   || stage == CopilotAdoptionTelemetryStages.StepCompleted
                   || stage == CopilotAdoptionTelemetryStages.StepFailed
                   || stage == CopilotAdoptionTelemetryStages.ScoringCompleted
                   || stage == CopilotAdoptionTelemetryStages.ServiceReturned
                   || stage == CopilotAdoptionTelemetryStages.CachePublished
                   || stage == CopilotAdoptionTelemetryStages.CompletionTelemetryReturned
                   || stage == CopilotAdoptionTelemetryStages.GateAcquired
                   || stage == CopilotAdoptionTelemetryStages.GateBypassed
                   || stage == CopilotAdoptionTelemetryStages.GateTimedOut
                   || stage == CopilotAdoptionTelemetryStages.Abandoned;
        }
    }

    internal sealed class CopilotAdoptionCompletionEvent
    {
        public string RunId { get; set; }
        public int WindowDays { get; set; }
        public long TotalMs { get; set; }
        public IDictionary<string, long> Steps { get; set; }
        public int WarningCount { get; set; }
        public bool TimedOut { get; set; }
        public string SlowestStep { get; set; }

        /// <summary><c>Summary.FiguresIncomplete</c>: at least one dataset failed to load.</summary>
        public bool FiguresIncomplete { get; set; }

        public int IncompleteReasonCount { get; set; }

        /// <summary>How many queries failed in this run (any kind).</summary>
        public int FailedQueryCount { get; set; }

        /// <summary>How many of those failures were classified as timeouts.</summary>
        public int TimedOutQueryCount { get; set; }

        /// <summary>Comma-separated names of the steps that failed - compile-time constants only.</summary>
        public string FailedSteps { get; set; }
    }

    internal sealed class CopilotAdoptionFailureEvent
    {
        public string RunId { get; set; }
        public int WindowDays { get; set; }
        public Exception Exception { get; set; }
    }

    internal static class CopilotAdoptionExceptionCorrelation
    {
        public const string RunIdDataKey = "CopilotAdoption.RunId";

        public static void SetRunId(Exception exception, string runId)
        {
            if (exception == null || string.IsNullOrEmpty(runId)) return;

            for (var current = exception; current != null; current = current.InnerException)
            {
                try
                {
                    current.Data[RunIdDataKey] = runId;
                }
                catch (Exception)
                {
                    // Some exception types expose a fixed IDictionary. The telemetry path must not fail.
                }
            }
        }

        public static bool TryGetRunId(Exception exception, out string runId)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                var value = current.Data?[RunIdDataKey] as string;
                if (!string.IsNullOrEmpty(value))
                {
                    runId = value;
                    return true;
                }
            }

            runId = null;
            return false;
        }
    }

    internal interface ICopilotAdoptionEventSink
    {
        int DroppedEvents { get; }

        void Track(CopilotAdoptionLifecycleEvent telemetryEvent);

        void TrackCompletion(
            CopilotAdoptionCompletionEvent completion,
            CopilotAdoptionLifecycleEvent submittedEvent);

        bool TrackFailure(
            CopilotAdoptionFailureEvent failure,
            CopilotAdoptionLifecycleEvent failureEvent);

        void Shutdown(TimeSpan timeout);
    }

    internal interface ICopilotAdoptionTelemetryWriter
    {
        void Write(CopilotAdoptionLifecycleEvent telemetryEvent);

        void WriteCompletion(CopilotAdoptionCompletionEvent completion);

        void WriteFailure(CopilotAdoptionFailureEvent failure);

        void Flush();
    }

    internal sealed class AppInsightsCopilotAdoptionTelemetryWriter : ICopilotAdoptionTelemetryWriter
    {
        private readonly AnalyticsLogger _logger;

        public AppInsightsCopilotAdoptionTelemetryWriter(string connectionString)
        {
            _logger = new AnalyticsLogger(
                connectionString, nameof(Controllers.CopilotAdoptionAPIController));
        }

        internal AppInsightsCopilotAdoptionTelemetryWriter(AnalyticsLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Write(CopilotAdoptionLifecycleEvent telemetryEvent)
        {
            _logger.TrackEvent(
                AnalyticsLogger.AnalyticsEvent.CopilotAdoptionLifecycle,
                telemetryEvent.Dimensions(),
                telemetryEvent.Measurements(),
                telemetryEvent.RunId,
                telemetryEvent.OccurredUtc);
        }

        public void WriteCompletion(CopilotAdoptionCompletionEvent completion)
        {
            _logger.TrackCopilotAdoptionAnalysis(
                completion.WindowDays,
                completion.TotalMs,
                completion.Steps,
                completion.WarningCount,
                completion.TimedOut,
                completion.SlowestStep,
                completion.RunId,
                completion.FiguresIncomplete,
                completion.IncompleteReasonCount,
                completion.FailedQueryCount,
                completion.TimedOutQueryCount,
                completion.FailedSteps);
        }

        public void WriteFailure(CopilotAdoptionFailureEvent failure)
        {
            var properties = new Dictionary<string, string>
            {
                { "RunId", failure.RunId },
            };
            _logger.TrackException(failure.Exception, properties, failure.RunId);
            _logger.LogError(
                $"Copilot adoption analysis failed ({failure.Exception.GetBaseException().GetType().Name}, RunId {failure.RunId}).");
        }

        public void Flush()
        {
            _logger.Flush();
        }
    }

    /// <summary>
    /// Bounded, non-blocking hand-off to Application Insights.
    ///
    /// The worker owns all SDK calls. A blocked telemetry channel can stop this thread, but it can never
    /// stop the analysis task or delay cache publication.
    /// </summary>
    internal sealed class QueuedCopilotAdoptionEventSink : ICopilotAdoptionEventSink
    {
        private const int Capacity = 1024;

        /// <summary>
        /// The least time between the flushes heartbeats may force. On the SDK's default in-memory channel a
        /// flush is a synchronous send on this single worker thread, so one per heartbeat - every 30 seconds for
        /// every active or queued run - would let a slow ingestion endpoint back the queue up, and the events
        /// waiting behind it are the terminal ones that matter most.
        /// </summary>
        internal static readonly TimeSpan DefaultHeartbeatFlushInterval = TimeSpan.FromSeconds(25);

        private readonly BlockingCollection<SinkItem> _queue =
            new BlockingCollection<SinkItem>(new ConcurrentQueue<SinkItem>(), Capacity);
        private readonly Func<ICopilotAdoptionTelemetryWriter> _writerFactory;
        private readonly Action<Exception, string> _reportDroppedFailure;
        private readonly FlushPolicy _flush;
        private readonly Thread _worker;
        private int _droppedEvents;
        private int _stopping;

        public QueuedCopilotAdoptionEventSink(
            Func<ICopilotAdoptionTelemetryWriter> writerFactory,
            Action<Exception, string> reportDroppedFailure = null,
            TimeSpan? heartbeatFlushInterval = null)
        {
            _writerFactory = writerFactory ?? throw new ArgumentNullException(nameof(writerFactory));
            _reportDroppedFailure = reportDroppedFailure ?? WebExceptionTelemetry.Report;
            _flush = new FlushPolicy(heartbeatFlushInterval ?? DefaultHeartbeatFlushInterval);
            _worker = new Thread(Drain)
            {
                IsBackground = true,
                Name = "CopilotAdoptionTelemetry",
            };
            _worker.Start();
        }

        public int DroppedEvents => Volatile.Read(ref _droppedEvents);

        public void Track(CopilotAdoptionLifecycleEvent telemetryEvent)
        {
            TryAdd(SinkItem.Lifecycle(telemetryEvent));
        }

        public void TrackCompletion(
            CopilotAdoptionCompletionEvent completion,
            CopilotAdoptionLifecycleEvent submittedEvent)
        {
            TryAdd(SinkItem.Completion(completion, submittedEvent));
        }

        public bool TrackFailure(
            CopilotAdoptionFailureEvent failure,
            CopilotAdoptionLifecycleEvent failureEvent)
        {
            return TryAdd(SinkItem.Failure(failure, failureEvent));
        }

        public void Shutdown(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

            _queue.CompleteAdding();
            if (Thread.CurrentThread != _worker)
            {
                _worker.Join(timeout);
            }
        }

        private bool TryAdd(SinkItem item)
        {
            try
            {
                if (item != null
                    && Volatile.Read(ref _stopping) == 0
                    && _queue.TryAdd(item))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding raced this enqueue during AppDomain shutdown.
            }

            Interlocked.Increment(ref _droppedEvents);
            return false;
        }

        private void Drain()
        {
            ICopilotAdoptionTelemetryWriter writer = null;
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    if (writer == null)
                    {
                        try
                        {
                            writer = _writerFactory();
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _droppedEvents);
                            Console.WriteLine(
                                $"Copilot adoption telemetry writer could not start ({ex.GetBaseException().GetType().Name}).");
                            item.ReportDroppedFailure(_reportDroppedFailure);
                            continue;
                        }
                    }

                    try
                    {
                        item.Write(writer, _flush);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _droppedEvents);
                        Console.WriteLine(
                            $"Copilot adoption telemetry write failed ({ex.GetBaseException().GetType().Name}).");
                        item.ReportDroppedFailure(_reportDroppedFailure);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Copilot adoption telemetry worker stopped ({ex.GetBaseException().GetType().Name}).");
            }
            finally
            {
                try
                {
                    writer?.Flush();
                    Thread.Sleep(1000);
                }
                catch (Exception)
                {
                    // The process is already stopping; telemetry must never obstruct shutdown.
                }
            }
        }

        /// <summary>
        /// When the worker flushes the channel. Only the worker thread touches it, so it needs no locking.
        /// </summary>
        private sealed class FlushPolicy
        {
            private readonly TimeSpan _heartbeatInterval;
            private readonly Stopwatch _sinceLastFlush = new Stopwatch();

            public FlushPolicy(TimeSpan heartbeatInterval)
            {
                _heartbeatInterval = heartbeatInterval;
            }

            public void Now(ICopilotAdoptionTelemetryWriter writer)
            {
                writer.Flush();
                _sinceLastFlush.Restart();
            }

            /// <summary>
            /// Flushes after a lifecycle event that must not wait in the buffer: the end of a run that produced
            /// no completion event, a host stopping, or a heartbeat - unless another flush went out recently,
            /// which already sent every heartbeat buffered before it.
            /// </summary>
            /// <remarks>
            /// <c>QueueFull</c> is deliberately not flushed. The other early endings are bounded by the queue,
            /// but a rejection happens once per poll per request as fast as callers ask, so a send for each would
            /// bring back the worker stall that <see cref="DefaultHeartbeatFlushInterval"/> prevents.
            /// </remarks>
            public void After(string stage, ICopilotAdoptionTelemetryWriter writer)
            {
                if (stage == CopilotAdoptionTelemetryStages.Heartbeat)
                {
                    if (!_sinceLastFlush.IsRunning || _sinceLastFlush.Elapsed >= _heartbeatInterval) Now(writer);
                    return;
                }

                if (stage == CopilotAdoptionTelemetryStages.HostStopping
                    || stage == CopilotAdoptionTelemetryStages.Abandoned
                    || stage == CopilotAdoptionTelemetryStages.GateTimedOut)
                {
                    Now(writer);
                }
            }
        }

        private sealed class SinkItem
        {
            private readonly CopilotAdoptionLifecycleEvent _lifecycle;
            private readonly CopilotAdoptionCompletionEvent _completion;
            private readonly CopilotAdoptionFailureEvent _failure;
            private readonly CopilotAdoptionLifecycleEvent _submitted;
            private int _failureClaimed;

            private SinkItem(
                CopilotAdoptionLifecycleEvent lifecycle,
                CopilotAdoptionCompletionEvent completion,
                CopilotAdoptionFailureEvent failure,
                CopilotAdoptionLifecycleEvent submitted)
            {
                _lifecycle = lifecycle;
                _completion = completion;
                _failure = failure;
                _submitted = submitted;
            }

            public static SinkItem Lifecycle(CopilotAdoptionLifecycleEvent telemetryEvent) =>
                new SinkItem(telemetryEvent, null, null, null);

            public static SinkItem Completion(
                CopilotAdoptionCompletionEvent completion,
                CopilotAdoptionLifecycleEvent submitted) =>
                new SinkItem(null, completion, null, submitted);

            public static SinkItem Failure(
                CopilotAdoptionFailureEvent failure,
                CopilotAdoptionLifecycleEvent failureEvent) =>
                new SinkItem(failureEvent, null, failure, null);

            public void Write(ICopilotAdoptionTelemetryWriter writer, FlushPolicy flush)
            {
                if (_lifecycle != null)
                {
                    writer.Write(_lifecycle);

                    // A heartbeat is the evidence left behind when a process dies mid-run, and the channel
                    // otherwise buffers for up to 30 seconds - so an abrupt recycle used to take the last
                    // heartbeat or two with it, exactly when they matter. Sent on this dedicated thread, never
                    // on the analysis, and throttled across every run (see DefaultHeartbeatFlushInterval).
                    flush.After(_lifecycle.Stage, writer);
                }
                if (_completion != null)
                {
                    var watch = Stopwatch.StartNew();
                    writer.WriteCompletion(_completion);
                    watch.Stop();
                    _submitted.OccurredUtc = DateTimeOffset.UtcNow;
                    _submitted.DurationMs = watch.ElapsedMilliseconds;
                    writer.Write(_submitted);
                    flush.Now(writer);
                }
                if (_failure != null)
                {
                    if (WebExceptionTelemetry.TryClaim(_failure.Exception))
                    {
                        Interlocked.Exchange(ref _failureClaimed, 1);
                        try
                        {
                            writer.WriteFailure(_failure);
                            flush.Now(writer);
                            WebExceptionTelemetry.MarkReported(_failure.Exception);
                            Interlocked.Exchange(ref _failureClaimed, 0);
                        }
                        catch (Exception)
                        {
                            ReleaseFailureClaim();
                            throw;
                        }
                    }
                    else
                    {
                        flush.Now(writer);
                    }
                }
            }

            public void ReportDroppedFailure(Action<Exception, string> reporter)
            {
                if (_failure?.Exception == null || reporter == null) return;

                ReleaseFailureClaim();

                try
                {
                    reporter(
                        _failure.Exception,
                        "CopilotAdoption background analysis telemetry sink");
                }
                catch (Exception)
                {
                    // The telemetry worker must keep draining even when fallback telemetry fails.
                }
            }

            private void ReleaseFailureClaim()
            {
                if (Interlocked.Exchange(ref _failureClaimed, 0) == 1)
                {
                    WebExceptionTelemetry.ReleaseClaim(_failure.Exception);
                }
            }
        }
    }

    internal interface ICopilotAdoptionHeartbeatFactory
    {
        IDisposable Start(Action heartbeat, TimeSpan interval);
    }

    /// <summary>
    /// Runs every Copilot Adoption heartbeat on one shared, dedicated background thread.
    /// </summary>
    /// <remarks>
    /// <para>A dedicated thread rather than the thread pool or a timer, because a heartbeat has to arrive while
    /// the pool is starved - that is one of the states it exists to reveal.</para>
    /// <para>One thread for every run rather than one each. A thread per run meant that every run - including
    /// one queued behind the admission gate, or turned away by it - created an OS thread just to be observed, so
    /// the number of analyses being asked for turned directly into thread creation. Heartbeat callbacks only queue
    /// an event, so sharing the thread does not hold one run's heartbeat up behind another's. The thread exists
    /// only while at least one run is registered.</para>
    /// </remarks>
    internal sealed class DedicatedThreadHeartbeatFactory : ICopilotAdoptionHeartbeatFactory
    {
        public static readonly DedicatedThreadHeartbeatFactory Instance =
            new DedicatedThreadHeartbeatFactory();

        private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(int.MaxValue);

        private readonly object _sync = new object();
        private readonly List<Registration> _registrations = new List<Registration>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Func<ThreadStart, Thread> _createThread;
        private Thread _thread;

        public DedicatedThreadHeartbeatFactory()
            : this(null)
        {
        }

        /// <param name="createThread">How the shared thread is created (a test seam); it is started here.</param>
        internal DedicatedThreadHeartbeatFactory(Func<ThreadStart, Thread> createThread)
        {
            _createThread = createThread ?? (run => new Thread(run)
            {
                IsBackground = true,
                Name = "CopilotAdoptionHeartbeat",
            });
        }

        public IDisposable Start(Action heartbeat, TimeSpan interval)
        {
            if (heartbeat == null) throw new ArgumentNullException(nameof(heartbeat));
            if (interval < MinimumInterval) interval = MinimumInterval;

            Registration registration;
            lock (_sync)
            {
                // Under the same lock the thread uses to decide it has nothing left to do, so a run can never be
                // registered with no thread to serve it. Started before it is recorded and before the run is
                // registered: a thread that cannot be started - Start throws when the process cannot create one -
                // must leave behind neither a dead thread recorded as running, which would silence every later
                // run's heartbeat, nor a registration nobody will ever dispose.
                if (_thread == null)
                {
                    var thread = _createThread(Run);
                    thread.Start();
                    _thread = thread;
                }

                registration = new Registration(this, heartbeat, interval, _clock.Elapsed + interval);
                _registrations.Add(registration);
            }

            _wake.Set();
            return registration;
        }

        private void Remove(Registration registration)
        {
            lock (_sync)
            {
                _registrations.Remove(registration);
            }

            // So the thread notices at once when it has nothing left to serve and exits, rather than holding on until
            // the removed run's next due time - which, for a long interval, could be a very long time.
            _wake.Set();
        }

        private void Run()
        {
            try
            {
                var due = new List<Registration>();
                while (true)
                {
                    try
                    {
                        var wait = Timeout.InfiniteTimeSpan;
                        lock (_sync)
                        {
                            if (_registrations.Count == 0)
                            {
                                _thread = null;
                                return;
                            }

                            var now = _clock.Elapsed;
                            foreach (var registration in _registrations)
                            {
                                if (registration.NextDue <= now)
                                {
                                    due.Add(registration);
                                    registration.NextDue = now + registration.Interval;
                                }

                                var untilDue = registration.NextDue - now;
                                if (wait == Timeout.InfiniteTimeSpan || untilDue < wait) wait = untilDue;
                            }
                        }

                        // Outside the lock, so a slow callback cannot block a run starting or finishing. A run
                        // disposed since its registration was read may be called once more; its heartbeat checks.
                        foreach (var registration in due)
                        {
                            try
                            {
                                registration.Heartbeat();
                            }
                            catch (Exception)
                            {
                                // One run's heartbeat must never stop every other run's.
                            }
                        }

                        due.Clear();

                        // WaitOne takes at most Int32.MaxValue milliseconds; a longer interval just re-scans early.
                        _wake.WaitOne(wait > MaximumWait ? MaximumWait : wait);
                    }
                    catch (Exception ex) when (!(ex is ThreadAbortException))
                    {
                        // Nothing above is expected to throw; if it ever does, keep serving the other runs.
                        due.Clear();
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }
                }
            }
            finally
            {
                // However the loop ends - normally, or aborted as the AppDomain unloads - it must not stay recorded
                // as the thread serving new registrations, or no later run would get a heartbeat.
                lock (_sync)
                {
                    if (ReferenceEquals(_thread, Thread.CurrentThread)) _thread = null;
                }
            }
        }

        private sealed class Registration : IDisposable
        {
            private readonly DedicatedThreadHeartbeatFactory _owner;
            private int _disposed;

            public Registration(
                DedicatedThreadHeartbeatFactory owner,
                Action heartbeat,
                TimeSpan interval,
                TimeSpan nextDue)
            {
                _owner = owner;
                Heartbeat = heartbeat;
                Interval = interval;
                NextDue = nextDue;
            }

            public Action Heartbeat { get; }

            public TimeSpan Interval { get; }

            /// <summary>Read and written only under the owner's lock.</summary>
            public TimeSpan NextDue { get; set; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _owner.Remove(this);
            }
        }
    }

    internal interface ICopilotAdoptionAnalysisTelemetry :
        ICopilotAdoptionRunTelemetry,
        IDisposable
    {
        string RunId { get; }

        void QueueCompletion(CopilotAdoptionAnalysis analysis);

        bool QueueFailure(Exception exception);

        void HostStopping(string reason);
    }

    internal sealed class NullCopilotAdoptionAnalysisTelemetry :
        ICopilotAdoptionAnalysisTelemetry
    {
        public static readonly NullCopilotAdoptionAnalysisTelemetry Instance =
            new NullCopilotAdoptionAnalysisTelemetry();

        public string RunId => string.Empty;

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

        public bool QueueFailure(Exception exception) => false;

        public void HostStopping(string reason)
        {
        }

        public void Dispose()
        {
        }
    }

    internal sealed class CopilotAdoptionRunTelemetry : ICopilotAdoptionAnalysisTelemetry
    {
        public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

        private readonly ICopilotAdoptionEventSink _sink;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Stopwatch _appDomainWatch;
        private readonly ConcurrentDictionary<long, ActiveOperation> _active =
            new ConcurrentDictionary<long, ActiveOperation>();
        private readonly object _emitGate = new object();

        /// <summary>
        /// Serialises <see cref="Dispose"/> and the run's terminal events with the two emitters called from outside
        /// the run, which can still be called after it has ended: the shared heartbeat thread, and host shutdown
        /// working from its snapshot of active runs. No heartbeat may be emitted once Dispose has returned, and no
        /// HostStopping once the run has ended.
        /// </summary>
        private readonly object _disposalGate = new object();
        private readonly Action<CopilotAdoptionRunTelemetry> _onDispose;
        private readonly int _appDomainId;
        private readonly long _heartbeatIntervalMs;
        private readonly IDisposable _heartbeat;
        private long _sequence;
        private long _operationId;
        private long _lastHeartbeatMs;
        private int _failedQueries;
        private int _timedOutQueries;
        private int _ended;
        private int _disposed;

        public CopilotAdoptionRunTelemetry(
            ICopilotAdoptionEventSink sink,
            int windowDays,
            bool hasSeatOverride,
            string instanceId,
            int appDomainId,
            Stopwatch appDomainWatch,
            ICopilotAdoptionHeartbeatFactory heartbeatFactory,
            TimeSpan heartbeatInterval,
            Action<CopilotAdoptionRunTelemetry> onDispose = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            WindowDays = windowDays;
            HasSeatOverride = hasSeatOverride;
            InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
            _appDomainId = appDomainId;
            _appDomainWatch = appDomainWatch ?? throw new ArgumentNullException(nameof(appDomainWatch));
            _heartbeatIntervalMs = Math.Max(1, (long)heartbeatInterval.TotalMilliseconds);
            _onDispose = onDispose;
            RunId = Guid.NewGuid().ToString("N");

            Emit(CopilotAdoptionTelemetryStages.Started, includeRuntime: true);
            _heartbeat = (heartbeatFactory ?? DedicatedThreadHeartbeatFactory.Instance)
                .Start(Heartbeat, heartbeatInterval);
        }

        public string RunId { get; }
        public string InstanceId { get; }
        public int WindowDays { get; }
        public bool HasSeatOverride { get; }

        public long StepStarted(string step)
        {
            var id = NextOperationId();
            _active[id] = new ActiveOperation("Step", step, null);
            Emit(CopilotAdoptionTelemetryStages.StepStarted, step: step, operationId: id);
            return id;
        }

        public void StepCompleted(
            long operationId,
            string step,
            long durationMs,
            bool failed,
            CopilotAdoptionFailure failure = null)
        {
            _active.TryRemove(operationId, out _);
            Emit(
                failed ? CopilotAdoptionTelemetryStages.StepFailed : CopilotAdoptionTelemetryStages.StepCompleted,
                step,
                outcome: failed ? "Failed" : "Succeeded",
                failure: failure,
                operationId: operationId,
                durationMs: durationMs);
        }

        public long QueryStarted(string step, string query)
        {
            var id = NextOperationId();
            _active[id] = new ActiveOperation("Query", step, query);
            Emit(
                CopilotAdoptionTelemetryStages.QueryStarted,
                step,
                query,
                operationId: id);
            return id;
        }

        public void QueryCompleted(
            long operationId,
            string step,
            string query,
            long durationMs,
            bool failed,
            CopilotAdoptionFailure failure = null)
        {
            _active.TryRemove(operationId, out _);

            if (failed)
            {
                Interlocked.Increment(ref _failedQueries);
                if (failure?.FailureKind == CopilotAdoptionFailureKinds.Timeout)
                {
                    Interlocked.Increment(ref _timedOutQueries);
                }
            }

            Emit(
                failed ? CopilotAdoptionTelemetryStages.QueryFailed : CopilotAdoptionTelemetryStages.QueryCompleted,
                step,
                query,
                failed ? "Failed" : "Succeeded",
                failure,
                operationId,
                durationMs,
                includeRuntime: true);
        }

        public void Checkpoint(string stage, long durationMs = 0)
        {
            if (!IsTerminal(stage))
            {
                Emit(stage, durationMs: durationMs);
                return;
            }

            // Under the disposal gate, and recorded, so host shutdown either lands before the run's end or not at all:
            // an "Interrupted" event after the result was published would make a completed run read as cut off.
            lock (_disposalGate)
            {
                Interlocked.Exchange(ref _ended, 1);
                Emit(stage, durationMs: durationMs, includeRuntime: true);
            }
        }

        public void QueueCompletion(CopilotAdoptionAnalysis analysis)
        {
            var diagnostics = analysis?.Summary?.Diagnostics;
            if (diagnostics == null) return;

            var steps = diagnostics.Steps.ToDictionary(
                step => step.Step,
                step => step.DurationMs,
                StringComparer.Ordinal);

            var completion = new CopilotAdoptionCompletionEvent
            {
                RunId = RunId,
                WindowDays = WindowDays,
                TotalMs = diagnostics.TotalMs,
                Steps = steps,
                WarningCount = analysis.Summary.Warnings.Count,

                // From classified query failures. It used to be "any step took 90 s or more", which fired for
                // the six sequential probes or the CPU-only scoring step taking that long in total without a
                // single timeout, and said nothing about which failures were timeouts at all.
                TimedOut = Volatile.Read(ref _timedOutQueries) > 0,
                SlowestStep = diagnostics.SlowestStep?.Step,
                FiguresIncomplete = analysis.Summary.FiguresIncomplete,
                IncompleteReasonCount = analysis.Summary.IncompleteReasons?.Count ?? 0,
                FailedQueryCount = Volatile.Read(ref _failedQueries),
                TimedOutQueryCount = Volatile.Read(ref _timedOutQueries),
                FailedSteps = string.Join(",", diagnostics.Steps.Where(step => step.Failed).Select(step => step.Step)),
            };

            var submitted = CreateEvent(
                CopilotAdoptionTelemetryStages.CompletionTelemetryReturned,
                outcome: "Succeeded",
                includeRuntime: true);
            TryTrackCompletion(completion, submitted);
        }

        public bool QueueFailure(Exception exception)
        {
            CopilotAdoptionExceptionCorrelation.SetRunId(exception, RunId);

            // A failure ends the run just as a checkpoint does; see Checkpoint.
            lock (_disposalGate)
            {
                Interlocked.Exchange(ref _ended, 1);
                var failureEvent = CreateEvent(
                    CopilotAdoptionTelemetryStages.Failed,
                    outcome: "Failed",
                    failure: CopilotAdoptionFailure.From(exception),
                    activeOperations: ActiveOperations(),
                    includeRuntime: true);
                return TryTrackFailure(
                    new CopilotAdoptionFailureEvent
                    {
                        RunId = RunId,
                        WindowDays = WindowDays,
                        Exception = exception,
                    },
                    failureEvent);
            }
        }

        public void HostStopping(string reason)
        {
            // Under the disposal gate: shutdown works from a snapshot of active runs, so it can reach a run that has
            // just finished, and an "Interrupted" event after CachePublished would make a completed run read as cut off.
            lock (_disposalGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _ended) != 0) return;

                Emit(
                    CopilotAdoptionTelemetryStages.HostStopping,
                    outcome: "Interrupted",
                    activeOperations: ActiveOperations(),
                    shutdownReason: reason,
                    includeRuntime: true);
            }
        }

        public void EmitHeartbeat()
        {
            Heartbeat();
        }

        public void Dispose()
        {
            // Under the disposal gate, so a heartbeat or host-shutdown event already being emitted finishes before
            // this returns and none starts afterwards - the guarantee a per-run heartbeat thread used to give by being
            // joined. The shared heartbeat thread can still call Heartbeat once more; it will see the flag and do nothing.
            lock (_disposalGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            }

            _heartbeat.Dispose();
            _onDispose?.Invoke(this);
        }

        private void Heartbeat()
        {
            lock (_disposalGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;

                var elapsed = _watch.ElapsedMilliseconds;
                var previous = Interlocked.Exchange(ref _lastHeartbeatMs, elapsed);
                var drift = previous == 0
                    ? Math.Max(0, elapsed - _heartbeatIntervalMs)
                    : Math.Max(0, elapsed - previous - _heartbeatIntervalMs);

                Emit(
                    CopilotAdoptionTelemetryStages.Heartbeat,
                    activeOperations: ActiveOperations(),
                    heartbeatDriftMs: drift,
                    includeRuntime: true);
            }
        }

        private long NextOperationId() => Interlocked.Increment(ref _operationId);

        private string ActiveOperations()
        {
            return string.Join(
                ",",
                _active
                    .OrderBy(item => item.Key)
                    .Select(item => item.Value.Display(item.Key)));
        }

        private void Emit(
            string stage,
            string step = null,
            string query = null,
            string outcome = null,
            CopilotAdoptionFailure failure = null,
            long operationId = 0,
            long durationMs = 0,
            string activeOperations = null,
            string shutdownReason = null,
            long heartbeatDriftMs = 0,
            bool includeRuntime = false)
        {
            TryTrack(CreateEvent(
                stage,
                step,
                query,
                outcome,
                failure,
                operationId,
                durationMs,
                activeOperations,
                shutdownReason,
                heartbeatDriftMs,
                includeRuntime));
        }

        private void TryTrack(CopilotAdoptionLifecycleEvent telemetryEvent)
        {
            try
            {
                _sink.Track(telemetryEvent);
            }
            catch (Exception)
            {
                // Observability must never alter analysis behavior.
            }
        }

        private void TryTrackCompletion(
            CopilotAdoptionCompletionEvent completion,
            CopilotAdoptionLifecycleEvent submitted)
        {
            try
            {
                _sink.TrackCompletion(completion, submitted);
            }
            catch (Exception)
            {
                // Observability must never alter analysis behavior.
            }
        }

        private bool TryTrackFailure(
            CopilotAdoptionFailureEvent failure,
            CopilotAdoptionLifecycleEvent failureEvent)
        {
            try
            {
                return _sink.TrackFailure(failure, failureEvent);
            }
            catch (Exception)
            {
                // Observability must never alter analysis behavior.
                return false;
            }
        }

        private CopilotAdoptionLifecycleEvent CreateEvent(
            string stage,
            string step = null,
            string query = null,
            string outcome = null,
            CopilotAdoptionFailure failure = null,
            long operationId = 0,
            long durationMs = 0,
            string activeOperations = null,
            string shutdownReason = null,
            long heartbeatDriftMs = 0,
            bool includeRuntime = false)
        {
            lock (_emitGate)
            {
                var telemetryEvent = new CopilotAdoptionLifecycleEvent
                {
                    OccurredUtc = DateTimeOffset.UtcNow,
                    Stage = stage,
                    RunId = RunId,
                    InstanceId = InstanceId,
                    WindowDays = WindowDays,
                    HasSeatOverride = HasSeatOverride,
                    Step = step,
                    Query = query,
                    Outcome = outcome,
                    ExceptionType = failure?.ExceptionType,
                    FailureKind = failure?.FailureKind,
                    ExceptionChain = failure?.ExceptionChain,
                    SqlErrorNumber = failure?.SqlErrorNumber?.ToString(CultureInfo.InvariantCulture),
                    Win32ErrorCode = failure?.Win32ErrorCode?.ToString(CultureInfo.InvariantCulture),
                    ActiveOperations = activeOperations,
                    SynchronizationContext =
                        SynchronizationContext.Current?.GetType().Name ?? "None",
                    ShutdownReason = shutdownReason,
                    Sequence = Interlocked.Increment(ref _sequence),
                    OperationId = operationId,
                    ElapsedMs = _watch.ElapsedMilliseconds,
                    DurationMs = durationMs,
                    HeartbeatDriftMs = heartbeatDriftMs,
                    AppDomainId = _appDomainId,
                    AppDomainUptimeMs = _appDomainWatch.ElapsedMilliseconds,
                    DroppedEvents = _sink.DroppedEvents,
                    Gen0Collections = -1,
                    Gen1Collections = -1,
                    Gen2Collections = -1,
                    ThreadPoolAvailableWorkers = -1,
                    ThreadPoolAvailableCompletionPorts = -1,
                };

                if (includeRuntime) PopulateRuntime(telemetryEvent);
                return telemetryEvent;
            }
        }

        private static bool IsTerminal(string stage)
        {
            return stage == CopilotAdoptionTelemetryStages.ServiceReturned
                   || stage == CopilotAdoptionTelemetryStages.CachePublished
                   || stage == CopilotAdoptionTelemetryStages.Failed
                   || stage == CopilotAdoptionTelemetryStages.GateTimedOut
                   || stage == CopilotAdoptionTelemetryStages.QueueFull
                   || stage == CopilotAdoptionTelemetryStages.Abandoned;
        }

        private static void PopulateRuntime(CopilotAdoptionLifecycleEvent telemetryEvent)
        {
            telemetryEvent.ManagedHeapBytes = GC.GetTotalMemory(false);
            telemetryEvent.Gen0Collections = GC.CollectionCount(0);
            telemetryEvent.Gen1Collections = GC.CollectionCount(1);
            telemetryEvent.Gen2Collections = GC.CollectionCount(2);
            ThreadPool.GetAvailableThreads(
                out var availableWorkers,
                out var availableCompletionPorts);
            telemetryEvent.ThreadPoolAvailableWorkers = availableWorkers;
            telemetryEvent.ThreadPoolAvailableCompletionPorts = availableCompletionPorts;

            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    telemetryEvent.ProcessWorkingSetBytes = process.WorkingSet64;
                }
            }
            catch (Exception)
            {
                // Runtime counters are supporting evidence only; their absence must not affect the run.
            }
        }

        private sealed class ActiveOperation
        {
            public ActiveOperation(string kind, string step, string query)
            {
                Kind = kind;
                Step = step;
                Query = query;
            }

            public string Kind { get; }
            public string Step { get; }
            public string Query { get; }

            public string Display(long id)
            {
                return string.IsNullOrEmpty(Query)
                    ? $"{id}:{Kind}:{Step}"
                    : $"{id}:{Kind}:{Step}:{Query}";
            }
        }
    }

    internal static class CopilotAdoptionTelemetryHost
    {
        private static readonly string InstanceId = Guid.NewGuid().ToString("N");

        // Identifies the AppDomain, so a run that stops because the worker was recycled can be told
        // apart from one that hung. Diagnosing issue #441 turned on exactly this: the id stayed
        // constant across the whole stall, which is what ruled out a recycle and pointed at a
        // stranded continuation instead.
        //
        // .NET Core / .NET 10 note: this still COMPILES but stops meaning anything - there are no
        // AppDomains, and AppDomain.CurrentDomain.Id is always 1. Nothing will fail; the field just
        // silently becomes a constant and the recycle-versus-hang signal is lost. On a migration this
        // needs replacing with something that actually changes per process lifetime (for example a
        // process start time or a per-process GUID) rather than being ported across as-is.
        private static readonly int AppDomainId = AppDomain.CurrentDomain.Id;
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();
        private static readonly ConcurrentDictionary<string, CopilotAdoptionRunTelemetry> ActiveRuns =
            new ConcurrentDictionary<string, CopilotAdoptionRunTelemetry>(StringComparer.Ordinal);
        private static readonly object SinkGate = new object();
        private static ICopilotAdoptionEventSink _sink;

        public static ICopilotAdoptionAnalysisTelemetry Start(
            int windowDays,
            bool hasSeatOverride)
        {
            try
            {
                var sink = GetSink();
                if (sink == null) return NullCopilotAdoptionAnalysisTelemetry.Instance;

                CopilotAdoptionRunTelemetry run = null;
                run = new CopilotAdoptionRunTelemetry(
                    sink,
                    windowDays,
                    hasSeatOverride,
                    InstanceId,
                    AppDomainId,
                    Uptime,
                    DedicatedThreadHeartbeatFactory.Instance,
                    CopilotAdoptionRunTelemetry.DefaultHeartbeatInterval,
                    completed => ActiveRuns.TryRemove(completed.RunId, out _));
                ActiveRuns.TryAdd(run.RunId, run);
                return run;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Copilot adoption telemetry could not start ({ex.GetBaseException().GetType().Name}).");
                return NullCopilotAdoptionAnalysisTelemetry.Instance;
            }
        }

        public static void Shutdown(string reason)
        {
            foreach (var run in ActiveRuns.Values)
            {
                run.HostStopping(reason);
                run.Dispose();
            }

            ICopilotAdoptionEventSink sink;
            lock (SinkGate)
            {
                sink = _sink;
            }
            sink?.Shutdown(TimeSpan.FromSeconds(2));
        }

        private static ICopilotAdoptionEventSink GetSink()
        {
            if (_sink != null) return _sink;

            lock (SinkGate)
            {
                if (_sink != null) return _sink;

                try
                {
                    _sink = new QueuedCopilotAdoptionEventSink(
                        () => new AppInsightsCopilotAdoptionTelemetryWriter(
                            new AppConfig().AppInsightsConnectionString));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Copilot adoption telemetry sink could not start ({ex.GetBaseException().GetType().Name}).");
                }

                return _sink;
            }
        }
    }
}
