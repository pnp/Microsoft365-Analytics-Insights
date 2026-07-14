using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Stores a single "last run" timestamp so a periodic import can be throttled to run at most once per
    /// window (e.g. once a day). Backed by Redis when configured, otherwise an in-memory fallback that lives
    /// only for the life of the (long-running) WebJob process.
    /// </summary>
    public interface ISingleDateStore
    {
        Task<DateTime?> GetLastDT();
        Task SaveDT();
        Task DeleteDt();
    }

    /// <summary>
    /// In-memory fallback for <see cref="ISingleDateStore"/>, used when Redis is not configured. The stored
    /// date lives only for the lifetime of this instance.
    ///
    /// IMPORTANT: callers must hold a single instance for the lifetime of the WebJob process (construct it
    /// ONCE, outside the per-cycle import loop). A fresh instance per cycle would always return null from
    /// <see cref="GetLastDT"/> and defeat the throttle, re-running the multi-hour usage-report phase every
    /// cycle. Mirrors <see cref="StatsUploader.InMemoryStatsDatesLoader"/>.
    /// </summary>
    public class InMemorySingleDateStore : ISingleDateStore
    {
        private readonly object _lock = new object();
        private DateTime? _lastDt;

        public Task<DateTime?> GetLastDT()
        {
            lock (_lock)
            {
                return Task.FromResult(_lastDt);
            }
        }

        public Task SaveDT()
        {
            lock (_lock)
            {
                _lastDt = DateTime.Now;
            }
            return Task.CompletedTask;
        }

        public Task DeleteDt()
        {
            lock (_lock)
            {
                _lastDt = null;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Builds the <see cref="ISingleDateStore"/> used to throttle the activity/usage-report phase to at most
    /// once a day: a Redis-backed store (durable across process restarts) when Redis is configured, otherwise
    /// an in-memory store that persists only for the life of this process. Mirrors the stats-upload throttle so
    /// the usage-report phase behaves the same as other once-a-day imports even without Redis configured.
    /// </summary>
    public static class ActivityReportsLastImportedStoreFactory
    {
        public static ISingleDateStore Create(AppConfig config, ILogger logger)
        {
            var redisConn = config?.ConnectionStrings?.RedisConnectionString;
            if (!string.IsNullOrEmpty(redisConn))
            {
                return new UserActivityLastImportedRedisSingleDateLoader(redisConn, config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
            }

            logger?.LogInformation("No Redis connection string configured - using in-memory throttle for activity/usage reports (the once-a-day window resets each time the WebJob process restarts).");
            return new InMemorySingleDateStore();
        }
    }
}
