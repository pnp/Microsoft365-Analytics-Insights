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
                   || stage == CopilotAdoptionTelemetryStages.CompletionTelemetryReturned;
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
    }

    internal sealed class CopilotAdoptionFailureEvent
    {
        public string RunId { get; set; }
        public int WindowDays { get; set; }
        public Exception Exception { get; set; }
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
                completion.RunId);
        }

        public void WriteFailure(CopilotAdoptionFailureEvent failure)
        {
            _logger.TrackException(failure.Exception);
            _logger.LogError(
                $"Copilot adoption analysis failed ({failure.Exception.GetBaseException().GetType().Name}).");
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

        private readonly BlockingCollection<SinkItem> _queue =
            new BlockingCollection<SinkItem>(new ConcurrentQueue<SinkItem>(), Capacity);
        private readonly Func<ICopilotAdoptionTelemetryWriter> _writerFactory;
        private readonly Thread _worker;
        private int _droppedEvents;
        private int _stopping;

        public QueuedCopilotAdoptionEventSink(Func<ICopilotAdoptionTelemetryWriter> writerFactory)
        {
            _writerFactory = writerFactory ?? throw new ArgumentNullException(nameof(writerFactory));
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
                            continue;
                        }
                    }

                    try
                    {
                        item.Write(writer);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _droppedEvents);
                        Console.WriteLine(
                            $"Copilot adoption telemetry write failed ({ex.GetBaseException().GetType().Name}).");
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

        private sealed class SinkItem
        {
            private readonly CopilotAdoptionLifecycleEvent _lifecycle;
            private readonly CopilotAdoptionCompletionEvent _completion;
            private readonly CopilotAdoptionFailureEvent _failure;
            private readonly CopilotAdoptionLifecycleEvent _submitted;

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

            public void Write(ICopilotAdoptionTelemetryWriter writer)
            {
                if (_lifecycle != null) writer.Write(_lifecycle);
                if (_completion != null)
                {
                    var watch = Stopwatch.StartNew();
                    writer.WriteCompletion(_completion);
                    watch.Stop();
                    _submitted.OccurredUtc = DateTimeOffset.UtcNow;
                    _submitted.DurationMs = watch.ElapsedMilliseconds;
                    writer.Write(_submitted);
                    writer.Flush();
                }
                if (_failure != null)
                {
                    writer.WriteFailure(_failure);
                    writer.Flush();
                }
            }
        }
    }

    internal interface ICopilotAdoptionHeartbeatFactory
    {
        IDisposable Start(Action heartbeat, TimeSpan interval);
    }

    internal sealed class DedicatedThreadHeartbeatFactory : ICopilotAdoptionHeartbeatFactory
    {
        public static readonly DedicatedThreadHeartbeatFactory Instance =
            new DedicatedThreadHeartbeatFactory();

        public IDisposable Start(Action heartbeat, TimeSpan interval) =>
            new DedicatedThreadHeartbeat(heartbeat, interval);

        private sealed class DedicatedThreadHeartbeat : IDisposable
        {
            private readonly Action _heartbeat;
            private readonly TimeSpan _interval;
            private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
            private readonly Thread _thread;
            private int _disposed;

            public DedicatedThreadHeartbeat(Action heartbeat, TimeSpan interval)
            {
                _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
                _interval = interval;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "CopilotAdoptionHeartbeat",
                };
                _thread.Start();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _stop.Set();
                if (Thread.CurrentThread != _thread
                    && _thread.Join(TimeSpan.FromSeconds(1)))
                {
                    _stop.Dispose();
                }
            }

            private void Run()
            {
                while (!_stop.Wait(_interval))
                {
                    _heartbeat();
                }
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
            string exceptionType = null)
        {
        }

        public long QueryStarted(string step, string query) => 0;

        public void QueryCompleted(
            long operationId,
            string step,
            string query,
            long durationMs,
            bool failed,
            string exceptionType = null)
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
        private readonly Action<CopilotAdoptionRunTelemetry> _onDispose;
        private readonly int _appDomainId;
        private readonly long _heartbeatIntervalMs;
        private readonly IDisposable _heartbeat;
        private long _sequence;
        private long _operationId;
        private long _lastHeartbeatMs;
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
            string exceptionType = null)
        {
            _active.TryRemove(operationId, out _);
            Emit(
                failed ? CopilotAdoptionTelemetryStages.StepFailed : CopilotAdoptionTelemetryStages.StepCompleted,
                step,
                outcome: failed ? "Failed" : "Succeeded",
                exceptionType: exceptionType,
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
            string exceptionType = null)
        {
            _active.TryRemove(operationId, out _);
            Emit(
                failed ? CopilotAdoptionTelemetryStages.QueryFailed : CopilotAdoptionTelemetryStages.QueryCompleted,
                step,
                query,
                failed ? "Failed" : "Succeeded",
                exceptionType,
                operationId,
                durationMs,
                includeRuntime: true);
        }

        public void Checkpoint(string stage, long durationMs = 0)
        {
            Emit(stage, durationMs: durationMs, includeRuntime: IsTerminal(stage));
        }

        public void QueueCompletion(CopilotAdoptionAnalysis analysis)
        {
            var diagnostics = analysis?.Summary?.Diagnostics;
            if (diagnostics == null) return;

            var steps = diagnostics.Steps.ToDictionary(
                step => step.Step,
                step => step.DurationMs,
                StringComparer.Ordinal);
            var timeoutMs = CopilotAdoptionService.QueryTimeoutSecs * 1000L;

            var completion = new CopilotAdoptionCompletionEvent
            {
                RunId = RunId,
                WindowDays = WindowDays,
                TotalMs = diagnostics.TotalMs,
                Steps = steps,
                WarningCount = analysis.Summary.Warnings.Count,
                TimedOut = diagnostics.Steps.Any(step => step.DurationMs >= timeoutMs),
                SlowestStep = diagnostics.SlowestStep?.Step,
            };

            var submitted = CreateEvent(
                CopilotAdoptionTelemetryStages.CompletionTelemetryReturned,
                outcome: "Succeeded",
                includeRuntime: true);
            TryTrackCompletion(completion, submitted);
        }

        public bool QueueFailure(Exception exception)
        {
            var failureEvent = CreateEvent(
                CopilotAdoptionTelemetryStages.Failed,
                outcome: "Failed",
                exceptionType: exception?.GetBaseException().GetType().Name,
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

        public void HostStopping(string reason)
        {
            Emit(
                CopilotAdoptionTelemetryStages.HostStopping,
                outcome: "Interrupted",
                activeOperations: ActiveOperations(),
                shutdownReason: reason,
                includeRuntime: true);
        }

        public void EmitHeartbeat()
        {
            Heartbeat();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _heartbeat.Dispose();
            _onDispose?.Invoke(this);
        }

        private void Heartbeat()
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
            string exceptionType = null,
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
                exceptionType,
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
            string exceptionType = null,
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
                    ExceptionType = exceptionType,
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
                   || stage == CopilotAdoptionTelemetryStages.Failed;
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
