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
                DefaultAnalyticsDbContextFactory.Instance,
                telemetry: telemetry);
            return service.AnalyseAsync(
                seatLicenceTypeIds.Count == 0 ? null : seatLicenceTypeIds,
                CancellationToken.None);
        }
    }

    internal interface ICopilotAdoptionAnalysisCache
    {
        bool TryGet(string key, out CopilotAdoptionAnalysis analysis);

        void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl);
    }

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
        private readonly TimeSpan _cacheTtl;

        public CopilotAdoptionAnalysisCoordinator(
            ICopilotAdoptionAnalysisRunner runner,
            ICopilotAdoptionAnalysisCache cache,
            Func<int, bool, ICopilotAdoptionAnalysisTelemetry> telemetryFactory,
            TimeSpan cacheTtl)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _telemetryFactory = telemetryFactory ?? throw new ArgumentNullException(nameof(telemetryFactory));
            _cacheTtl = cacheTtl;
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
                telemetry.QueueFailure(ex);
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
