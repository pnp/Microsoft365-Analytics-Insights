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
                    TableCount = tables.Count,
                    EnabledImports = ParseSettings(c.ConfiguredImportsEnabledDescription)
                        .Where(kv => kv.Value)
                        .Select(kv => kv.Key)
                        .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)
                        .ToList()
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
            => Aggregate(clients, System.DateTime.UtcNow);

        /// <param name="nowUtc">Injected so the freshness buckets are deterministic in tests.</param>
        internal static DashboardStats Aggregate(IReadOnlyList<AnonUsageStatsModel> clients, System.DateTime nowUtc)
        {
            var stats = new DashboardStats();
            if (clients == null || clients.Count == 0)
            {
                return stats;
            }

            // Keyed on schema + table: the same table name can legitimately exist in more than one
            // schema (e.g. an app table and a profiling one), and merging them would be wrong.
            var tableAccumulators = new Dictionary<string, TableTotal>(System.StringComparer.OrdinalIgnoreCase);
            var versionAccumulators = new Dictionary<string, VersionAdoption>(System.StringComparer.OrdinalIgnoreCase);
            var featureAccumulators = new Dictionary<string, FeatureAdoption>(System.StringComparer.OrdinalIgnoreCase);

            var rowsPerClient = new List<long>(clients.Count);
            var spacePerClient = new List<decimal>(clients.Count);
            var tablesPerClient = new List<int>(clients.Count);

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

                BucketFreshness(stats.Freshness, c.Generated, nowUtc);

                if (c.DataPointsFromAITotal.HasValue)
                {
                    stats.ClientsReportingAi++;
                    stats.AiDataPointsTotal += c.DataPointsFromAITotal.Value;
                }

                // Build version adoption. Clients that never sent one are grouped under "(unknown)"
                // rather than dropped, so the counts always add up to ClientCount.
                var version = string.IsNullOrWhiteSpace(c.BuildVersionLabel) ? UnknownLabel : c.BuildVersionLabel.Trim();
                if (!versionAccumulators.TryGetValue(version, out var versionAcc))
                {
                    versionAcc = new VersionAdoption { BuildVersionLabel = version };
                    versionAccumulators[version] = versionAcc;
                }
                versionAcc.ClientCount++;
                if (c.Generated.HasValue && (!versionAcc.LastSeen.HasValue || c.Generated.Value > versionAcc.LastSeen.Value))
                {
                    versionAcc.LastSeen = c.Generated;
                }

                foreach (var setting in ParseSettings(c.ConfiguredImportsEnabledDescription))
                {
                    if (!featureAccumulators.TryGetValue(setting.Key, out var featureAcc))
                    {
                        featureAcc = new FeatureAdoption { Name = setting.Key };
                        featureAccumulators[setting.Key] = featureAcc;
                    }
                    if (setting.Value) featureAcc.EnabledCount++;
                    else featureAcc.DisabledCount++;
                }

                var tables = c.TableStats;
                if (tables == null)
                {
                    rowsPerClient.Add(0);
                    spacePerClient.Add(0m);
                    tablesPerClient.Add(0);
                    continue;
                }

                long clientRows = 0;
                decimal clientSpace = 0m;
                var clientTableCount = 0;

                foreach (var t in tables)
                {
                    if (t == null || string.IsNullOrEmpty(t.TableName)) continue;

                    clientRows += t.Rows;
                    clientSpace += t.TotalSpaceMB;
                    clientTableCount++;

                    stats.TotalRows += t.Rows;
                    stats.TotalSpaceMB += t.TotalSpaceMB;

                    var schema = string.IsNullOrWhiteSpace(t.SchemaName) ? null : t.SchemaName.Trim();
                    var display = schema == null ? t.TableName : $"{schema}.{t.TableName}";

                    if (!tableAccumulators.TryGetValue(display, out var acc))
                    {
                        acc = new TableTotal
                        {
                            TableName = t.TableName,
                            SchemaName = schema,
                            DisplayName = display
                        };
                        tableAccumulators[display] = acc;
                    }
                    acc.Rows += t.Rows;
                    acc.TotalSpaceMB += t.TotalSpaceMB;
                    acc.ClientCount++;
                }

                rowsPerClient.Add(clientRows);
                spacePerClient.Add(clientSpace);
                tablesPerClient.Add(clientTableCount);
            }

            stats.TableTotals = tableAccumulators.Values
                .OrderByDescending(t => t.Rows)
                .ToList();

            stats.DistinctTableCount = stats.TableTotals.Count;

            stats.SchemaTotals = stats.TableTotals
                .GroupBy(t => t.SchemaName ?? UnknownLabel, System.StringComparer.OrdinalIgnoreCase)
                .Select(g => new SchemaTotal
                {
                    SchemaName = g.Key,
                    Rows = g.Sum(t => t.Rows),
                    TotalSpaceMB = g.Sum(t => t.TotalSpaceMB),
                    TableCount = g.Count()
                })
                .OrderByDescending(s => s.Rows)
                .ToList();

            stats.Versions = versionAccumulators.Values
                .OrderByDescending(v => v.ClientCount)
                // "(unknown)" is a real bucket but not a real version, so keep it last on ties -
                // otherwise it can sort ahead of an actual build and read as the most common one.
                .ThenBy(v => v.BuildVersionLabel == UnknownLabel ? 1 : 0)
                .ThenBy(v => v.BuildVersionLabel, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            stats.ImportFeatures = featureAccumulators.Values
                .OrderByDescending(f => f.EnabledCount)
                .ThenBy(f => f.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            stats.SizeDistribution = new SizeDistribution
            {
                AvgRowsPerClient = rowsPerClient.Count == 0 ? 0 : (long)rowsPerClient.Average(),
                MedianRowsPerClient = Median(rowsPerClient),
                MaxRowsPerClient = rowsPerClient.Count == 0 ? 0 : rowsPerClient.Max(),
                AvgSpaceMBPerClient = spacePerClient.Count == 0 ? 0m : decimal.Round(spacePerClient.Average(), 2),
                MedianSpaceMBPerClient = decimal.Round(Median(spacePerClient), 2),
                MaxSpaceMBPerClient = spacePerClient.Count == 0 ? 0m : spacePerClient.Max(),
                AvgTablesPerClient = tablesPerClient.Count == 0 ? 0 : (int)System.Math.Round(tablesPerClient.Average())
            };

            return stats;
        }

        private const string UnknownLabel = "(unknown)";

        private static void BucketFreshness(FreshnessBuckets buckets, System.DateTime? generated, System.DateTime nowUtc)
        {
            if (!generated.HasValue)
            {
                buckets.Stale++;
                return;
            }

            // Reports are stamped in UTC, but be tolerant of a Local/Unspecified Kind sneaking through.
            var generatedUtc = generated.Value.Kind == System.DateTimeKind.Local
                ? generated.Value.ToUniversalTime()
                : generated.Value;

            var age = nowUtc - generatedUtc;
            if (age < System.TimeSpan.FromHours(24)) buckets.Last24Hours++;
            else if (age < System.TimeSpan.FromDays(7)) buckets.Last7Days++;
            else if (age < System.TimeSpan.FromDays(30)) buckets.Last30Days++;
            else buckets.Stale++;
        }

        /// <summary>
        /// Parses an <c>ImportTaskSettings.ToSettingsString()</c> payload — <c>Name=True;Name=False</c>.
        /// Defensive by design: the string is produced by whatever build the client happens to run, so
        /// unknown, malformed or non-boolean entries are skipped rather than throwing.
        /// </summary>
        internal static Dictionary<string, bool> ParseSettings(string? settings)
        {
            var result = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(settings)) return result;

            foreach (var token in settings.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = token.Split('=', 2);
                if (parts.Length != 2) continue;

                var name = parts[0].Trim();
                if (name.Length == 0) continue;

                if (bool.TryParse(parts[1].Trim(), out var enabled))
                {
                    result[name] = enabled;
                }
            }

            return result;
        }

        private static long Median(List<long> values)
        {
            if (values == null || values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 != 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        private static decimal Median(List<decimal> values)
        {
            if (values == null || values.Count == 0) return 0m;
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 != 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
        }
    }
}
