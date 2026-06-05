using Microsoft.Extensions.Caching.Memory;
using UsageReporting;

namespace Web.Dashboard
{
    /// <summary>
    /// Builds the read-only dashboard view-models from the telemetry store.
    /// Pure aggregation — no Cosmos types leak out so it's trivially unit-testable.
    /// </summary>
    /// <remarks>
    /// Caching: the underlying Cosmos read (<see cref="ITelemetryQueryAdaptor.LoadAllCurrentAsync"/>)
    /// is cached for <c>DashboardCacheSeconds</c> (default 60s). Both the stats and the per-client
    /// views are derived from the same cached list so a dashboard refresh costs one Cosmos scan
    /// per cache window, not one per endpoint. A semaphore enforces single-flight so multiple
    /// concurrent requests after cache expiry collapse into one Cosmos read.
    /// </remarks>
    public class DashboardService
    {
        private const string CacheKey = "dashboard:current-clients";

        private readonly ITelemetryQueryAdaptor _queryAdaptor;
        private readonly ILogger<DashboardService> _logger;
        private readonly IMemoryCache _cache;
        private readonly int _maxItems;
        private readonly TimeSpan _cacheDuration;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);

        public DashboardService(
            ITelemetryQueryAdaptor queryAdaptor,
            ILogger<DashboardService> logger,
            IMemoryCache cache,
            int maxItems,
            TimeSpan cacheDuration)
        {
            _queryAdaptor = queryAdaptor;
            _logger = logger;
            _cache = cache;
            _maxItems = maxItems;
            _cacheDuration = cacheDuration;
        }

        public async Task<DashboardStats> GetStatsAsync()
        {
            var clients = await GetCurrentClientsAsync();
            return Aggregate(clients);
        }

        public async Task<IReadOnlyList<ClientSummary>> GetClientsAsync()
        {
            var clients = await GetCurrentClientsAsync();
            var summaries = new List<ClientSummary>(clients.Count);

            foreach (var c in clients)
            {
                if (c == null) continue;
                var tables = c.TableStats ?? new List<AnonUsageStatsModel.TableStat>();
                summaries.Add(new ClientSummary
                {
                    AnonClientId = c.AnonClientId ?? string.Empty,
                    Generated = c.Generated,
                    BuildVersionLabel = c.BuildVersionLabel,
                    ConfiguredImportsEnabledDescription = c.ConfiguredImportsEnabledDescription,
                    ConfiguredSolutionsEnabledDescription = c.ConfiguredSolutionsEnabledDescription,
                    DataPointsFromAITotal = c.DataPointsFromAITotal,
                    Rows = tables.Sum(t => t?.Rows ?? 0L),
                    TotalSpaceMB = tables.Sum(t => t?.TotalSpaceMB ?? 0m),
                    TableCount = tables.Count
                });
            }

            // Most recently updated first — useful for the dashboard table view.
            return summaries.OrderByDescending(s => s.Generated ?? System.DateTime.MinValue).ToList();
        }

        private async Task<IReadOnlyList<AnonUsageStatsModel>> GetCurrentClientsAsync()
        {
            // Caching disabled — every call hits Cosmos directly.
            if (_cacheDuration <= TimeSpan.Zero)
            {
                return await _queryAdaptor.LoadAllCurrentAsync(_maxItems);
            }

            if (_cache.TryGetValue<IReadOnlyList<AnonUsageStatsModel>>(CacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            // Single-flight: only one task does the Cosmos scan; everyone else waits and then
            // re-checks the cache. Avoids a thundering-herd refresh when the cache entry expires
            // while several requests are in flight.
            await _refreshGate.WaitAsync();
            try
            {
                if (_cache.TryGetValue<IReadOnlyList<AnonUsageStatsModel>>(CacheKey, out cached) && cached != null)
                {
                    return cached;
                }

                _logger.LogDebug("Dashboard cache miss — reloading current telemetry from store.");
                var fresh = await _queryAdaptor.LoadAllCurrentAsync(_maxItems);
                _cache.Set(CacheKey, fresh, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _cacheDuration
                });
                return fresh;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        internal static DashboardStats Aggregate(IReadOnlyList<AnonUsageStatsModel> clients)
        {
            var stats = new DashboardStats();
            if (clients == null || clients.Count == 0)
            {
                return stats;
            }

            var tableAccumulators = new Dictionary<string, TableTotal>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var c in clients)
            {
                if (c == null) continue;
                stats.ClientCount++;

                if (c.Generated.HasValue)
                {
                    if (!stats.LastUpdated.HasValue || c.Generated.Value > stats.LastUpdated.Value)
                    {
                        stats.LastUpdated = c.Generated;
                    }
                }

                var tables = c.TableStats;
                if (tables == null) continue;

                foreach (var t in tables)
                {
                    if (t == null || string.IsNullOrEmpty(t.TableName)) continue;

                    stats.TotalRows += t.Rows;
                    stats.TotalSpaceMB += t.TotalSpaceMB;

                    if (!tableAccumulators.TryGetValue(t.TableName, out var acc))
                    {
                        acc = new TableTotal { TableName = t.TableName };
                        tableAccumulators[t.TableName] = acc;
                    }
                    acc.Rows += t.Rows;
                    acc.TotalSpaceMB += t.TotalSpaceMB;
                    acc.ClientCount++;
                }
            }

            stats.TableTotals = tableAccumulators.Values
                .OrderByDescending(t => t.Rows)
                .ToList();

            return stats;
        }
    }
}
