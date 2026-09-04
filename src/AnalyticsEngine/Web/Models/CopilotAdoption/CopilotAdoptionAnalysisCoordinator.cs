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

        public static readonly CopilotAdoptionAnalysisCoordinator Default =
            new CopilotAdoptionAnalysisCoordinator(
                new CopilotAdoptionAnalysisRunner(),
                MemoryCopilotAdoptionAnalysisCache.Instance,
                CopilotAdoptionTelemetryHost.Start,
                TimeSpan.FromMinutes(10));

        private readonly ConcurrentDictionary<string, Lazy<Task<CopilotAdoptionAnalysis>>> _inFlight =
            new ConcurrentDictionary<string, Lazy<Task<CopilotAdoptionAnalysis>>>(StringComparer.Ordinal);
        private readonly ICopilotAdoptionAnalysisRunner _runner;
        private readonly ICopilotAdoptionAnalysisCache _cache;
        private readonly Func<int, bool, ICopilotAdoptionAnalysisTelemetry> _telemetryFactory;
        private readonly Action<Exception, string> _reportUnqueuedFailure;
        private readonly TimeSpan _cacheTtl;

        public CopilotAdoptionAnalysisCoordinator(
            ICopilotAdoptionAnalysisRunner runner,
            ICopilotAdoptionAnalysisCache cache,
            Func<int, bool, ICopilotAdoptionAnalysisTelemetry> telemetryFactory,
            TimeSpan cacheTtl,
            Action<Exception, string> reportUnqueuedFailure = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _telemetryFactory = telemetryFactory ?? throw new ArgumentNullException(nameof(telemetryFactory));
            _cacheTtl = cacheTtl;
            _reportUnqueuedFailure = reportUnqueuedFailure ?? WebExceptionTelemetry.Report;
        }

        public async Task<CopilotAdoptionAnalysis> TryGetAsync(
            int windowDays,
            List<int> seatLicenceTypeIds,
            TimeSpan waitBudget,
            CancellationToken cancellationToken)
        {
            var task = GetAsync(windowDays, seatLicenceTypeIds);
            if (task.IsCompleted) return await task;

            var finished = await Task.WhenAny(task, Task.Delay(waitBudget, cancellationToken));
            return finished == task ? await task : null;
        }

        internal Task<CopilotAdoptionAnalysis> GetAsync(
            int windowDays,
            List<int> seatLicenceTypeIds)
        {
            var ids = seatLicenceTypeIds ?? new List<int>();
            var cacheKey = CacheKey(windowDays, ids);

            if (_cache.TryGet(cacheKey, out var cached))
            {
                return Task.FromResult(cached);
            }

            Lazy<Task<CopilotAdoptionAnalysis>> candidate = null;
            candidate = new Lazy<Task<CopilotAdoptionAnalysis>>(
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
                () => Task.Run(() => RunAndPublishAsync(cacheKey, candidate, windowDays, ids)),
                LazyThreadSafetyMode.ExecutionAndPublication);

            var effective = _inFlight.GetOrAdd(cacheKey, candidate);
            if (ReferenceEquals(effective, candidate))
            {
                // A previous generation may have published between the first miss and GetOrAdd.
                if (_cache.TryGet(cacheKey, out var justPublished))
                {
                    RemoveInFlight(cacheKey, candidate);
                    return Task.FromResult(justPublished);
                }
            }

            return effective.Value;
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
            Lazy<Task<CopilotAdoptionAnalysis>> generation,
            int windowDays,
            List<int> seatLicenceTypeIds)
        {
            ICopilotAdoptionAnalysisTelemetry telemetry =
                NullCopilotAdoptionAnalysisTelemetry.Instance;
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
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

                var analysis = await _runner.RunAsync(
                    windowDays,
                    seatLicenceTypeIds,
                    telemetry).ConfigureAwait(false);
                var serviceDurationMs = watch.ElapsedMilliseconds;

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
                // anyone and the failure is invisible. Report it directly instead. WebExceptionTelemetry
                // .Report never throws and marks the exception reported, so a poller that does await the
                // faulted task will not report it a second time.
                //
                // The accepted path is deliberately NOT marked as reported. QueueFailure returning true
                // means the event was ENQUEUED, not written: QueuedCopilotAdoptionEventSink's worker
                // drops an item permanently if the writer cannot be constructed or the write throws.
                // Marking here would suppress every waiting request's report and leave no exception
                // telemetry at all. That trades a duplicate for silence, and silence is the failure this
                // release exists to remove - so a request awaiting the shared run may still report it.
                // Removing the duplicate needs delivery acknowledgement from the sink; see issue #454.
                if (!telemetry.QueueFailure(ex))
                {
                    _reportUnqueuedFailure(ex, "CopilotAdoption background analysis");
                }

                throw;
            }
            finally
            {
                RemoveInFlight(cacheKey, generation);
                telemetry.Dispose();
            }
        }

        private void RemoveInFlight(
            string cacheKey,
            Lazy<Task<CopilotAdoptionAnalysis>> generation)
        {
            var entries = _inFlight
                as ICollection<KeyValuePair<string, Lazy<Task<CopilotAdoptionAnalysis>>>>;
            entries.Remove(
                new KeyValuePair<string, Lazy<Task<CopilotAdoptionAnalysis>>>(
                    cacheKey,
                    generation));
        }
    }
}
