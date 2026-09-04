using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Records when each individual usage report last completed successfully.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the phase-level <see cref="ISingleDateStore"/>, which answers a different question. That
    /// one timestamp has to mean two things at once: "don't re-run this once-a-day phase yet" AND "these
    /// stored days are proven complete, so they can be skipped". Withholding it after a failure is correct for
    /// the first meaning (issue #285 - a broken import must not look complete) but wrong for the second: it
    /// also emptied the finalized-date skip list for the reports that had succeeded, so one permanently
    /// failing report made all eleven re-download their full window every cycle (issue #311).
    /// </para>
    /// <para>
    /// A per-report stamp lets those two meanings separate. The phase timestamp keeps its throttling job
    /// unchanged; the skip list now asks each report when <b>it</b> last completed.
    /// </para>
    /// </remarks>
    public interface IReportCompletionStore
    {
        /// <summary>When this report last completed successfully, or null if it never has.</summary>
        Task<DateTime?> GetLastSuccessAsync(string reportKey);

        /// <summary>Record that this report has just completed successfully.</summary>
        Task SaveSuccessAsync(string reportKey);

        /// <summary>
        /// Forget this report's completion. Called before a report runs, so that a crash part-way through its
        /// save cannot leave a stamp claiming the window is complete.
        /// </summary>
        Task ClearAsync(string reportKey);
    }

    /// <summary>
    /// In-memory fallback used when Redis is not configured; lives only for the life of the WebJob process.
    /// Must be constructed ONCE outside the per-cycle loop, exactly like <see cref="InMemorySingleDateStore"/>.
    /// </summary>
    public class InMemoryReportCompletionStore : IReportCompletionStore
    {
        // Reports run concurrently under Task.WhenAll, so this must be thread-safe.
        private readonly ConcurrentDictionary<string, DateTime> _lastSuccess =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public Task<DateTime?> GetLastSuccessAsync(string reportKey)
        {
            return Task.FromResult(_lastSuccess.TryGetValue(reportKey, out var dt) ? dt : (DateTime?)null);
        }

        public Task SaveSuccessAsync(string reportKey)
        {
            _lastSuccess[reportKey] = DateTime.Now;
            return Task.CompletedTask;
        }

        public Task ClearAsync(string reportKey)
        {
            _lastSuccess.TryRemove(reportKey, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Redis-backed <see cref="IReportCompletionStore"/>, so per-report completion survives a WebJob restart
    /// (which is the whole point - an in-memory stamp would be lost on every restart and the skip list would
    /// be empty again).
    /// </summary>
    public class RedisReportCompletionStore : IReportCompletionStore
    {
        /// <summary>
        /// Prefix for the per-report keys. Deliberately distinct from the phase-level
        /// <c>UserActivityLastImported</c> key so the two cannot collide.
        /// </summary>
        internal const string KeyPrefix = "UserActivityReportLastImported:";

        private readonly ConcurrentDictionary<string, RedisSingleDateLoader> _loaders =
            new ConcurrentDictionary<string, RedisSingleDateLoader>(StringComparer.OrdinalIgnoreCase);

        private readonly string _redisConnectionString;
        private readonly string _tenantId;
        private readonly string _clientId;
        private readonly string _clientSecret;

        public RedisReportCompletionStore(string redisConnectionString, string tenantId = null, string clientId = null, string clientSecret = null)
        {
            _redisConnectionString = redisConnectionString;
            _tenantId = tenantId;
            _clientId = clientId;
            _clientSecret = clientSecret;
        }

        private RedisSingleDateLoader LoaderFor(string reportKey)
        {
            return _loaders.GetOrAdd(reportKey,
                k => new RedisSingleDateLoader(_redisConnectionString, KeyPrefix + k, _tenantId, _clientId, _clientSecret));
        }

        public Task<DateTime?> GetLastSuccessAsync(string reportKey) => LoaderFor(reportKey).GetLastDT();

        public Task SaveSuccessAsync(string reportKey) => LoaderFor(reportKey).SaveDT();

        public Task ClearAsync(string reportKey) => LoaderFor(reportKey).DeleteDt();
    }

    /// <summary>
    /// Builds the <see cref="IReportCompletionStore"/> for per-report completion stamps: Redis when
    /// configured (durable across restarts), otherwise in-memory for the life of this process. Mirrors
    /// <see cref="ActivityReportsLastImportedStoreFactory"/>.
    /// </summary>
    public static class ReportCompletionStoreFactory
    {
        public static IReportCompletionStore Create(AppConfig config, ILogger logger)
        {
            var redisConn = config?.ConnectionStrings?.RedisConnectionString;
            if (!string.IsNullOrEmpty(redisConn))
            {
                return new RedisReportCompletionStore(redisConn, config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
            }

            logger?.LogInformation(
                "No Redis connection string configured - per-report usage-report completion is tracked in memory only, "
                + "so the finalized-date skip list resets each time the WebJob process restarts.");
            return new InMemoryReportCompletionStore();
        }
    }
}
