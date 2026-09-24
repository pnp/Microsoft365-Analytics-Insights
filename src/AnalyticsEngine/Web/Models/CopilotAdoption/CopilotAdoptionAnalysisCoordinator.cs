using Common.Entities;
using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.CopilotAdoption
{
    internal interface ICopilotAdoptionAnalysisRunner
    {
        Task<CopilotAdoptionAnalysis> RunAsync(
            int windowDays,
            List<int> seatLicenceTypeIds,
            ICopilotAdoptionRunTelemetry telemetry);
    }

    internal sealed class CopilotAdoptionAnalysisRunner : ICopilotAdoptionAnalysisRunner
    {
        public Task<CopilotAdoptionAnalysis> RunAsync(
            int windowDays,
            List<int> seatLicenceTypeIds,
            ICopilotAdoptionRunTelemetry telemetry)
        {
            var options = CopilotAdoptionOptions.Default;
            options.WindowDays = windowDays;

            var service = new CopilotAdoptionService(
                options,
                // A factory that creates its OWN context, deliberately - not one resolved from a
                // per-request scope. This run outlives the request that started it, so a scoped
                // context would already be disposed by the time the analysis used it.
                //
                // ASP.NET Core note: `AddDbContext` registers scoped by default, so resolving the
                // context from the request's provider during a .NET migration would reintroduce
                // exactly that. Background work needs its own scope via IServiceScopeFactory.
                DefaultAnalyticsDbContextFactory.Instance,
                telemetry: telemetry);
            return service.AnalyseAsync(
                seatLicenceTypeIds.Count == 0 ? null : seatLicenceTypeIds,
                // CancellationToken.None is load-bearing: this run is SHARED between every caller
                // polling for the same result, so it must not be tied to any one request. A caller
                // giving up cancels only its own wait (see CopilotAdoptionAnalysisCoordinator
                // .TryGetAsync, where the request's token bounds Task.Delay and nothing else).
                //
                // ASP.NET Core note: passing HttpContext.RequestAborted here would look like good
                // hygiene and would be a serious bug - the first poller's 202 response would cancel
                // the analysis for everybody, reproducing the user-visible failure of issue #441
                // through a different mechanism that Task.Run does not protect against.
                CancellationToken.None);
        }
    }

    internal interface ICopilotAdoptionAnalysisCache
    {
        bool TryGet(string key, out CopilotAdoptionAnalysis analysis);

        void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl);
    }

    /// <summary>
    /// The completed-result cache, backed by the process-wide <see cref="MemoryCache.Default"/>.
    /// </summary>
    /// <remarks>
    /// .NET Core / .NET 10 note: the replacement, <c>IMemoryCache</c>, is not a drop-in.
    /// <see cref="MemoryCache.Default"/> is a single process-wide instance that trims itself under
    /// memory pressure; <c>IMemoryCache</c> is an ordinary DI singleton that does NOT evict on
    /// memory pressure unless a <c>SizeLimit</c> is configured and every entry declares a size.
    /// Porting this across without setting that up gives a cache that grows without bound - and the
    /// entries here are whole tenant analyses, which on a large tenant are not small. The absolute
    /// expiry below is what bounds it today, so keep an equivalent when it moves.
    /// </remarks>
    internal sealed class MemoryCopilotAdoptionAnalysisCache : ICopilotAdoptionAnalysisCache
    {
        public static readonly MemoryCopilotAdoptionAnalysisCache Instance =
            new MemoryCopilotAdoptionAnalysisCache();

        public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
        {
            analysis = MemoryCache.Default.Get(key) as CopilotAdoptionAnalysis;
            return analysis != null;
        }

        public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
        {
            MemoryCache.Default.Set(
                key,
                analysis,
                new CacheItemPolicy
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.Add(ttl),
                });
        }
    }

    /// <summary>
    /// Owns the completed-result cache and single in-flight analysis per scope.
    ///
    /// The ports are deliberately instance-scoped so tests can exercise the real wait/deduplication/
    /// publication policy without SQL, Application Insights, wall-clock 20-second waits or shared
    /// <see cref="MemoryCache.Default"/> state.
    /// </summary>
    internal sealed class CopilotAdoptionAnalysisCoordinator
    {
        private const string CacheKeyPrefix = "CopilotAdoption::Analysis::";

        /// <summary>
        /// How many analyses this web application instance runs at once, across every window and seat override.
        /// </summary>
        /// <remarks>
        /// <see cref="CopilotAdoptionService.MaxConcurrentSteps"/> bounds the queries inside ONE analysis to
        /// two, but nothing bounded the analyses themselves: each (window, seat override) starts its own, and
        /// the browser aborting its fetch does not stop the shared run. So clicking through 7, 28, 90 and 180
        /// days started four full analyses - up to eight heavy queries - against a database the importer is
        /// using, pushing every one of them towards the 90-second timeout. Two analyses caps that at four
        /// heavy queries while still letting the period someone has just picked start alongside one already
        /// running.
        /// <para>The gate lives in this AppDomain, like the result cache, so the limit is per instance. A scaled-out
        /// App Service plan gets it once per instance, and during an overlapped recycle the outgoing AppDomain can
        /// still be finishing its runs while the new one admits its own, so for that brief period the database can
        /// see twice the limit. Coordinating across instances would need an external lease, which this does not
        /// attempt.</para>
        /// </remarks>
        public const int DefaultMaxConcurrentAnalyses = 2;

        /// <summary>
        /// The longest a run waits for a slot. It then proceeds on the single overflow slot if that is free
        /// (<c>GateBypassed</c>), or is dropped without running (<c>GateTimedOut</c>). A hung analysis holds its
        /// slot for ever, so without the overflow two of them would stop every later analysis in the process
        /// from starting; without the cap on the overflow, every queued run would pile onto a database that is
        /// evidently not keeping up.
        /// </summary>
        /// <remarks>
        /// Three minutes, so a run admitted on the overflow slot can still finish while somebody is waiting for
        /// it. The page polls for ten minutes (<c>POLL_CEILING_MS</c> in <c>copilotAdoptionApi.ts</c>), and a run
        /// started by an export is still wanted for <see cref="Web.AnalyticsWeb.Controllers.CopilotAdoptionAPIController.ExportWaitBudget"/>
        /// plus <see cref="DefaultAbandonAfter"/> - 210 seconds. Waiting as long as the page polls admitted the
        /// run at the moment the page gave up, and let an export's run be abandoned before it was ever admitted.
        /// </remarks>
        public static readonly TimeSpan DefaultMaxQueueWait = TimeSpan.FromMinutes(3);

        /// <summary>
        /// How many runs may wait for a slot at once. Beyond this a run is turned away at once
        /// (<c>QueueFull</c>) and the caller's next poll tries again.
        /// </summary>
        /// <remarks>
        /// Every queued run holds its telemetry - including a registration on the shared heartbeat thread, so it
        /// sends a heartbeat every 30 seconds while it waits - plus an in-flight entry and a watcher, and a seat
        /// override is caller-supplied, so without a bound a burst of distinct overrides queued without limit. Eight
        /// covers every period with and without an override for one person, with the two running beside them.
        /// </remarks>
        public const int DefaultMaxQueuedAnalyses = 8;

        /// <summary>
        /// A queued run whose result nobody has asked for in this long - and that no request is waiting on -
        /// is dropped instead of started. The SPA re-polls every few seconds, and a waiting request counts as
        /// interest until the moment it stops waiting, so a live page is always inside this; a period the user
        /// clicked past stops being polled and is never run.
        /// </summary>
        public static readonly TimeSpan DefaultAbandonAfter = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How often a queued run re-checks whether anybody still wants it. A run nobody wants is released
        /// within about <see cref="DefaultAbandonAfter"/> plus this, rather than holding its telemetry and sending
        /// heartbeats until a slot frees - which, behind a hung analysis, is the full queue wait.
        /// </summary>
        public static readonly TimeSpan DefaultQueuePollInterval = TimeSpan.FromSeconds(15);

        public static readonly CopilotAdoptionAnalysisCoordinator Default =
            new CopilotAdoptionAnalysisCoordinator(
                new CopilotAdoptionAnalysisRunner(),
                MemoryCopilotAdoptionAnalysisCache.Instance,
                CopilotAdoptionTelemetryHost.Start,
                TimeSpan.FromMinutes(10));

        private readonly ConcurrentDictionary<string, Generation> _inFlight =
            new ConcurrentDictionary<string, Generation>(StringComparer.Ordinal);
        private readonly ICopilotAdoptionAnalysisRunner _runner;
        private readonly ICopilotAdoptionAnalysisCache _cache;
        private readonly Func<int, bool, ICopilotAdoptionAnalysisTelemetry> _telemetryFactory;
        private readonly Action<Exception, string> _reportUnqueuedFailure;
        private readonly TimeSpan _cacheTtl;
        private readonly SemaphoreSlim _gate;
        private readonly SemaphoreSlim _overflow = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _maxQueueWait;
        private readonly TimeSpan _abandonAfter;
        private readonly TimeSpan _queuePollInterval;
        private readonly int _maxQueuedAnalyses;
        private readonly Func<DateTime> _utcNow;
        private int _queuedRuns;

        public CopilotAdoptionAnalysisCoordinator(
            ICopilotAdoptionAnalysisRunner runner,
            ICopilotAdoptionAnalysisCache cache,
            Func<int, bool, ICopilotAdoptionAnalysisTelemetry> telemetryFactory,
            TimeSpan cacheTtl,
            Action<Exception, string> reportUnqueuedFailure = null,
            int maxConcurrentAnalyses = DefaultMaxConcurrentAnalyses,
            TimeSpan? maxQueueWait = null,
            TimeSpan? abandonAfter = null,
            Func<DateTime> utcNow = null,
            TimeSpan? queuePollInterval = null,
            int maxQueuedAnalyses = DefaultMaxQueuedAnalyses)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _telemetryFactory = telemetryFactory ?? throw new ArgumentNullException(nameof(telemetryFactory));
            _cacheTtl = cacheTtl;
            _reportUnqueuedFailure = reportUnqueuedFailure ?? WebExceptionTelemetry.Report;

            var slots = Math.Max(1, maxConcurrentAnalyses);
            _gate = new SemaphoreSlim(slots, slots);
            _maxQueueWait = maxQueueWait ?? DefaultMaxQueueWait;
            _abandonAfter = abandonAfter ?? DefaultAbandonAfter;
            _queuePollInterval = queuePollInterval ?? DefaultQueuePollInterval;
            _maxQueuedAnalyses = Math.Max(0, maxQueuedAnalyses);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task<CopilotAdoptionAnalysis> TryGetAsync(
            int windowDays,
            List<int> seatLicenceTypeIds,
            TimeSpan waitBudget,
            CancellationToken cancellationToken)
        {
            var task = Join(windowDays, seatLicenceTypeIds, out var interest);
            if (task.IsCompleted) return await task;

            // Joining already recorded this request, so a queued run cannot judge it absent in the gap
            // before this line. Waiting as well keeps a long wait - an export's 150 seconds - counted as
            // interest for all of its duration, and leaving it restarts the abandonment clock from then.
            interest?.EnterWait();
            try
            {
                var finished = await Task.WhenAny(task, Task.Delay(waitBudget, cancellationToken));
                return finished == task ? await task : null;
            }
            finally
            {
                interest?.ExitWait(_utcNow());
            }
        }

        /// <summary>
        /// The telemetry <c>RunId</c> of the analysis currently running (or queued) for this window and seat
        /// override, or null when there is none. Returned to the browser with a 202 so a HAR can be matched to
        /// the run's lifecycle events.
        /// </summary>
        internal string InFlightRunId(int windowDays, List<int> seatLicenceTypeIds)
        {
            return _inFlight.TryGetValue(
                CacheKey(windowDays, seatLicenceTypeIds ?? new List<int>()), out var generation)
                ? generation.RunId
                : null;
        }

        internal Task<CopilotAdoptionAnalysis> GetAsync(
            int windowDays,
            List<int> seatLicenceTypeIds)
        {
            return Join(windowDays, seatLicenceTypeIds, out _);
        }

        /// <summary>
        /// Returns the cached result, or joins - starting it if need be - the single in-flight analysis for
        /// this window and seat override, recording the request as interest in it.
        /// </summary>
        /// <param name="interest">
        /// The joined run's interest record, or null when the result came from the cache. The record lives
        /// on the run and dies with it, so nothing here grows with the number of distinct seat overrides a
        /// caller can invent.
        /// </param>
        private Task<CopilotAdoptionAnalysis> Join(
            int windowDays,
            List<int> seatLicenceTypeIds,
            out AnalysisInterest interest)
        {
            interest = null;
            var ids = seatLicenceTypeIds ?? new List<int>();
            var cacheKey = CacheKey(windowDays, ids);

            if (_cache.TryGet(cacheKey, out var cached))
            {
                return Task.FromResult(cached);
            }

            // More than one pass only when the run found in the table had been sealed as abandoned in the
            // instant before this request; one created by this pass cannot be sealed before it has even started.
            for (var pass = 0; pass < 3; pass++)
            {
                var candidate = NewGeneration(cacheKey, windowDays, ids);
                var effective = _inFlight.GetOrAdd(cacheKey, candidate);

                // A previous generation may have published between the first miss and GetOrAdd.
                if (ReferenceEquals(effective, candidate) && _cache.TryGet(cacheKey, out var justPublished))
                {
                    RemoveInFlight(cacheKey, candidate);
                    return Task.FromResult(justPublished);
                }

                // Recorded BEFORE the run can start, so it can never judge the request that started it absent.
                if (effective.Interest.TryTouch(_utcNow()))
                {
                    interest = effective.Interest;
                    return effective.Work.Value;
                }

                // Sealed: that run will publish nothing. Take it out - only if it is still there - so the next
                // pass starts a fresh run instead of handing this request a result that is never coming.
                RemoveInFlight(cacheKey, effective);
            }

            // Unreachable in practice. Null is "not ready yet", which every caller already answers with a 202.
            return Task.FromResult<CopilotAdoptionAnalysis>(null);
        }

        private Generation NewGeneration(string cacheKey, int windowDays, List<int> ids)
        {
            Generation generation = null;
            generation = new Generation(new Lazy<Task<CopilotAdoptionAnalysis>>(
                // Task.Run, deliberately, rather than calling RunAndPublishAsync directly.
                //
                // This run is SHARED and outlives the request that happened to start it: that request
                // gives up after CopilotAdoptionAPIController.FirstResponseBudget and answers 202, and a
                // later poll collects the result. But GetAsync is called ON a request thread, where
                // ASP.NET has installed a request-bound SynchronizationContext. Any await in the analysis
                // that does not say ConfigureAwait(false) would capture it and post its continuation back
                // - and once that request has ended the context never pumps again, so the continuation
                // simply never runs. The analysis then stops mid-flight with no exception, no timeout and
                // no recycle, while the in-flight entry below is never cleared (its finally never runs),
                // so every later poll joins the dead task and the page can never load again until the app
                // restarts. That is issue #441, seen in production as a run still "alive" nearly an hour
                // later with an idle database, an idle thread pool and nothing logged.
                //
                // Starting the run on the thread pool gives it no ambient SynchronizationContext at all,
                // which fixes this for every await in the analysis - including ones not yet written -
                // rather than relying on ~32 separate ConfigureAwait(false) calls staying correct.
                () => Task.Run(() => RunAndPublishAsync(cacheKey, generation, windowDays, ids)),
                LazyThreadSafetyMode.ExecutionAndPublication));
            return generation;
        }

        internal static string CacheKey(int windowDays, IEnumerable<int> seatLicenceTypeIds)
        {
            var ids = (seatLicenceTypeIds ?? Enumerable.Empty<int>())
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            return CacheKeyPrefix + windowDays + "::"
                   + (ids.Count == 0 ? "auto" : string.Join(",", ids));
        }

        private async Task<CopilotAdoptionAnalysis> RunAndPublishAsync(
            string cacheKey,
            Generation generation,
            int windowDays,
            List<int> seatLicenceTypeIds)
        {
            ICopilotAdoptionAnalysisTelemetry telemetry =
                NullCopilotAdoptionAnalysisTelemetry.Instance;
            var holdsSlot = false;
            var holdsOverflow = false;
            string runId = null;

            try
            {
                // Checked again here, on the run itself, before telemetry or admission. A request can join this
                // generation in the instant after the result it would compute was published - and the Join that
                // inserted the generation may then detach it from the in-flight table. Without this check the run
                // repeated a whole analysis whose result every caller could already read.
                if (_cache.TryGet(cacheKey, out var alreadyPublished))
                {
                    return alreadyPublished;
                }

                try
                {
                    telemetry = _telemetryFactory(
                        windowDays, seatLicenceTypeIds.Count > 0)
                        ?? NullCopilotAdoptionAnalysisTelemetry.Instance;
                }
                catch (Exception)
                {
                    // Telemetry is best-effort. A construction failure must not poison this in-flight
                    // generation or stop the analysis from running.
                }

                runId = string.IsNullOrEmpty(telemetry.RunId) ? null : telemetry.RunId;
                generation.RunId = runId;

                // Admission. Try for a slot without waiting first, so the common case - nothing else running -
                // emits no Queued stage at all and a Queued event always means real contention.
                var queueWatch = System.Diagnostics.Stopwatch.StartNew();
                var queued = !_gate.Wait(0);
                var admission = queued
                    ? await QueueAsync(generation, telemetry).ConfigureAwait(false)
                    : Admission.Slot;
                holdsSlot = admission == Admission.Slot;
                holdsOverflow = admission == Admission.Overflow;
                queueWatch.Stop();

                // Null is "nothing was produced", and nothing is published: a request that arrives later
                // starts a fresh run, exactly as for a cold cache.
                if (admission == Admission.Abandoned)
                {
                    telemetry.Checkpoint(
                        CopilotAdoptionTelemetryStages.Abandoned, queueWatch.ElapsedMilliseconds);
                    return null;
                }

                if (admission == Admission.TimedOut)
                {
                    telemetry.Checkpoint(
                        CopilotAdoptionTelemetryStages.GateTimedOut, queueWatch.ElapsedMilliseconds);
                    return null;
                }

                if (admission == Admission.QueueFull)
                {
                    telemetry.Checkpoint(CopilotAdoptionTelemetryStages.QueueFull);
                    return null;
                }

                telemetry.Checkpoint(
                    holdsSlot
                        ? CopilotAdoptionTelemetryStages.GateAcquired
                        : CopilotAdoptionTelemetryStages.GateBypassed,
                    queueWatch.ElapsedMilliseconds);

                // Only a run that had to queue can have been left behind: one admitted straight away was
                // created by the request that is waiting for it. Sealing it here means no request can join it
                // between this decision and its removal from the in-flight table.
                if (queued && generation.Interest.TrySealIfAbandoned(_utcNow(), _abandonAfter))
                {
                    telemetry.Checkpoint(
                        CopilotAdoptionTelemetryStages.Abandoned, queueWatch.ElapsedMilliseconds);
                    return null;
                }

                var serviceWatch = System.Diagnostics.Stopwatch.StartNew();
                var analysis = await _runner.RunAsync(
                    windowDays,
                    seatLicenceTypeIds,
                    telemetry).ConfigureAwait(false);
                var serviceDurationMs = serviceWatch.ElapsedMilliseconds;

                // Stamped before publication so the summary every caller reads carries the id of the run that
                // produced it - the join between a browser trace and the lifecycle events.
                if (runId != null && analysis?.Summary?.Diagnostics != null)
                {
                    analysis.Summary.Diagnostics.RunId = runId;
                }

                // Publish first. A blocked or broken telemetry channel must never keep the page polling
                // after the analysis has already produced a valid result.
                var cacheWatch = System.Diagnostics.Stopwatch.StartNew();
                _cache.Set(cacheKey, analysis, _cacheTtl);
                cacheWatch.Stop();

                telemetry.Checkpoint(
                    CopilotAdoptionTelemetryStages.ServiceReturned, serviceDurationMs);
                telemetry.Checkpoint(
                    CopilotAdoptionTelemetryStages.CachePublished, cacheWatch.ElapsedMilliseconds);
                telemetry.QueueCompletion(analysis);
                return analysis;
            }
            catch (Exception ex)
            {
                // QueueFailure returns false when the bounded failure sink rejected the event - it is
                // full, or the host is stopping - and the no-op telemetry used when telemetry
                // construction failed always returns false. In that case nothing else is guaranteed to
                // observe this failure: the request that started the run has normally already returned
                // 202, so if no poller is awaiting the shared task the exception is never observed by
                // anyone. Report it directly instead. The reporter is best-effort and swallows its own
                // failures.
                //
                // The accepted path is deliberately NOT marked as reported. Acceptance is not delivery:
                // the event has only been added to an in-memory queue whose worker can drop it. Marking
                // here would suppress reporting by waiting requests without anything having confirmed
                // the failure was reported. An accepted failure may therefore be reported by a waiting
                // request if it wins the atomic report claim before the sink worker writes it.
                if (!telemetry.QueueFailure(ex))
                {
                    try
                    {
                        _reportUnqueuedFailure(ex, "CopilotAdoption background analysis");
                    }
                    catch (Exception)
                    {
                        // Failure telemetry is best-effort and must never replace the analysis fault.
                    }
                }

                throw;
            }
            finally
            {
                if (holdsSlot) _gate.Release();
                if (holdsOverflow) _overflow.Release();

                RemoveInFlight(cacheKey, generation);
                telemetry.Dispose();
            }
        }

        private enum Admission
        {
            Slot,
            Overflow,
            TimedOut,
            Abandoned,
            QueueFull,
        }

        /// <summary>
        /// Waits for a slot, in arrival order, while watching whether anybody still wants the result.
        /// </summary>
        private async Task<Admission> QueueAsync(Generation generation, ICopilotAdoptionAnalysisTelemetry telemetry)
        {
            // Counted before anything is allocated for the wait, and never waited for: a caller turned away
            // gets "not ready yet" straight away and asks again on its next poll.
            if (Interlocked.Increment(ref _queuedRuns) > _maxQueuedAnalyses)
            {
                Interlocked.Decrement(ref _queuedRuns);
                return Admission.QueueFull;
            }

            try
            {
                telemetry.Checkpoint(CopilotAdoptionTelemetryStages.Queued);

                // One long wait rather than repeated short ones: SemaphoreSlim admits async waiters first-come
                // first-served, and re-waiting would send this run to the back of the queue every time.
                using (var abandoned = new CancellationTokenSource())
                {
                    var watcher = CancelWhenAbandonedAsync(generation.Interest, abandoned);
                    try
                    {
                        if (await _gate.WaitAsync(_maxQueueWait, abandoned.Token).ConfigureAwait(false))
                        {
                            return Admission.Slot;
                        }
                    }
                    catch (OperationCanceledException) when (abandoned.IsCancellationRequested)
                    {
                        return Admission.Abandoned;
                    }
                    finally
                    {
                        abandoned.Cancel();
                        await watcher.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _queuedRuns);
            }

            // No slot came free for this run within the whole wait - held by slow or hung analyses, or taken
            // by runs queued ahead of it. One run at a time may proceed regardless.
            return _overflow.Wait(0) ? Admission.Overflow : Admission.TimedOut;
        }

        private async Task CancelWhenAbandonedAsync(AnalysisInterest interest, CancellationTokenSource abandoned)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_queuePollInterval, abandoned.Token).ConfigureAwait(false);
                    if (interest.TrySealIfAbandoned(_utcNow(), _abandonAfter))
                    {
                        abandoned.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The wait ended first: a slot was granted or the queue wait ran out.
            }
        }

        private void RemoveInFlight(string cacheKey, Generation generation)
        {
            // Only this generation: a newer one for the same key may already have replaced it.
            ((ICollection<KeyValuePair<string, Generation>>)_inFlight).Remove(
                new KeyValuePair<string, Generation>(cacheKey, generation));
        }

        /// <summary>
        /// One in-flight analysis: its shared work, who still wants its result, and its telemetry id.
        /// </summary>
        private sealed class Generation
        {
            private string _runId;

            public Generation(Lazy<Task<CopilotAdoptionAnalysis>> work)
            {
                Work = work;
            }

            public Lazy<Task<CopilotAdoptionAnalysis>> Work { get; }

            public AnalysisInterest Interest { get; } = new AnalysisInterest();

            /// <summary>Written by the run once its telemetry exists; read by request threads for the 202.</summary>
            public string RunId
            {
                get => Volatile.Read(ref _runId);
                set => Volatile.Write(ref _runId, value);
            }
        }

        /// <summary>
        /// Who still wants one run's result - when it was last asked for and how many requests are waiting on
        /// it - and whether it has been sealed as abandoned. One lock makes "is anybody still interested?" and
        /// "record this request" mutually exclusive, so no request can join a run in the instant after it was
        /// judged abandoned.
        /// </summary>
        private sealed class AnalysisInterest
        {
            private readonly object _sync = new object();
            private DateTime _lastRequestedUtc;
            private int _waiters;
            private bool _sealed;

            /// <summary>Records a request. False when the run is sealed, in which case it must not be joined.</summary>
            public bool TryTouch(DateTime utcNow)
            {
                lock (_sync)
                {
                    if (_sealed) return false;
                    if (utcNow > _lastRequestedUtc) _lastRequestedUtc = utcNow;
                    return true;
                }
            }

            public void EnterWait()
            {
                lock (_sync)
                {
                    _waiters++;
                }
            }

            /// <summary>
            /// A request that stops waiting was interested until now, so the abandonment clock restarts here.
            /// Measured from when it joined instead, an export that waited its full 150 seconds would leave the
            /// run looking long forgotten the instant it gave up.
            /// </summary>
            public void ExitWait(DateTime utcNow)
            {
                lock (_sync)
                {
                    _waiters--;
                    if (utcNow > _lastRequestedUtc) _lastRequestedUtc = utcNow;
                }
            }

            /// <summary>
            /// Seals the run when no request is waiting and none has asked within <paramref name="abandonAfter"/>.
            /// True once sealed; a sealed run stays sealed.
            /// </summary>
            public bool TrySealIfAbandoned(DateTime utcNow, TimeSpan abandonAfter)
            {
                lock (_sync)
                {
                    if (!_sealed && _waiters == 0 && utcNow - _lastRequestedUtc > abandonAfter) _sealed = true;
                    return _sealed;
                }
            }
        }
    }
}
