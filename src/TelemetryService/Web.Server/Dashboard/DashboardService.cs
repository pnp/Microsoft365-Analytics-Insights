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
        private readonly IClientAnnotationStore? _annotationStore;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);

        public DashboardService(
            ITelemetryQueryAdaptor queryAdaptor,
            ILogger<DashboardService> logger,
            IMemoryCache cache,
            int maxItems,
            TimeSpan cacheDuration,
            IClientAnnotationStore? annotationStore = null)
        {
            _queryAdaptor = queryAdaptor;
            _logger = logger;
            _cache = cache;
            _maxItems = maxItems;
            _cacheDuration = cacheDuration;
            _annotationStore = annotationStore;
        }

        public async Task<DashboardStats> GetStatsAsync()
        {
            var clients = await GetCurrentClientsAsync();
            return Aggregate(clients);
        }

        public async Task<IReadOnlyList<ClientSummary>> GetClientsAsync()
        {
            var clients = await GetCurrentClientsAsync();
            var annotations = await LoadAnnotationsAsync();
            var summaries = new List<ClientSummary>(clients.Count);

            foreach (var c in clients)
            {
                if (c == null) continue;
                var tables = c.TableStats ?? new List<AnonUsageStatsModel.TableStat>();
                var adoption = c.Adoption;
                var copilot = adoption?.Copilot;

                ClientAnnotation? annotation = null;
                if (!string.IsNullOrEmpty(c.AnonClientId))
                {
                    annotations.TryGetValue(c.AnonClientId, out annotation);
                }

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
                        .ToList(),
                    AnnotationDisplayName = annotation?.DisplayName,
                    AnnotationNotes = annotation?.Notes,
                    AdoptionGeneratedUtc = adoption?.GeneratedUtc,
                    AdoptionSuppressed = adoption?.Suppressed ?? false,
                    CopilotLicensedUsers = copilot?.LicensedUsers,
                    CopilotActiveUsers = copilot?.ActiveUsers,
                    CopilotAdoptionRatePct = copilot?.AdoptionRatePct,
                });
            }

            // Most recently updated first — useful for the dashboard table view.
            return summaries.OrderByDescending(s => s.Generated ?? System.DateTime.MinValue).ToList();
        }

        /// <summary>
        /// Maintainer annotations, keyed by anonymous client id.
        /// </summary>
        /// <remarks>
        /// Deliberately fails soft: an annotation is a convenience label, so a problem reading them
        /// must not take the whole client list down with it. The dashboard then simply shows the
        /// anonymous ids, which is what it did before annotations existed.
        /// </remarks>
        private async Task<Dictionary<string, ClientAnnotation>> LoadAnnotationsAsync()
        {
            var result = new Dictionary<string, ClientAnnotation>(System.StringComparer.OrdinalIgnoreCase);
            if (_annotationStore == null) return result;

            try
            {
                foreach (var annotation in await _annotationStore.LoadAllAsync())
                {
                    if (annotation?.id == null) continue;
                    result[annotation.id] = annotation;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not load client annotations ({Message}); clients will be shown by their anonymous id.",
                    ex.Message);
            }

            return result;
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

            stats.Adoption = AggregateAdoption(clients, nowUtc);

            return stats;
        }

        /// <summary>
        /// Rolls the per-client adoption blocks up across the install base.
        /// </summary>
        /// <remarks>
        /// Every ratio is a median OF THE CLIENTS' OWN RATES, not a rate recomputed from summed
        /// counts. Those are different questions, and the second one is almost always the wrong
        /// answer here: one 200,000-seat tenant would otherwise drown out fifty smaller ones and the
        /// figure would describe that tenant rather than the install base.
        ///
        /// Remember the inputs are bucketed client-side, so sums are approximate by construction.
        /// </remarks>
        internal static AdoptionInsights AggregateAdoption(
            IReadOnlyList<AnonUsageStatsModel> clients, System.DateTime nowUtc)
        {
            var insights = new AdoptionInsights();
            if (clients == null || clients.Count == 0) return insights;

            var licensedPerClient = new List<long>();
            var activePerClient = new List<long>();
            var adoptionRates = new List<double>();
            var habitRates = new List<double>();

            var skuAccumulators = new Dictionary<string, SkuPopularity>(System.StringComparer.OrdinalIgnoreCase);
            var sourceAccumulators = new Dictionary<string, FeatureAdoption>(System.StringComparer.Ordinal);
            var coverageAccumulators = new Dictionary<string, CoverageStatusTotal>(System.StringComparer.Ordinal);

            foreach (var c in clients)
            {
                var adoption = c?.Adoption;
                if (adoption == null) continue;

                insights.ClientsReporting++;
                BucketFreshness(insights.Freshness, adoption.GeneratedUtc, nowUtc);

                // Count SKUs even for a suppressed client: suppression is about the Copilot seat
                // population being too small to describe, and it says nothing about the tenant's
                // wider licence estate, which has its own per-SKU threshold.
                if (adoption.Licences?.Skus != null)
                {
                    foreach (var sku in adoption.Licences.Skus)
                    {
                        if (sku == null || string.IsNullOrWhiteSpace(sku.SkuPartNumber)) continue;

                        if (!skuAccumulators.TryGetValue(sku.SkuPartNumber, out var skuAcc))
                        {
                            skuAcc = new SkuPopularity { SkuPartNumber = sku.SkuPartNumber };
                            skuAccumulators[sku.SkuPartNumber] = skuAcc;
                        }
                        skuAcc.ClientCount++;
                        skuAcc.AssignedUsers += sku.AssignedUsers ?? 0;
                    }
                }

                if (adoption.Licences?.Coverage != null)
                {
                    foreach (var cov in adoption.Licences.Coverage)
                    {
                        if (cov == null || string.IsNullOrWhiteSpace(cov.Workload)) continue;

                        var status = string.IsNullOrWhiteSpace(cov.Status) ? UnknownLabel : cov.Status;
                        var key = cov.Workload + "/" + status;
                        if (!coverageAccumulators.TryGetValue(key, out var covAcc))
                        {
                            covAcc = new CoverageStatusTotal { Workload = cov.Workload, Status = status };
                            coverageAccumulators[key] = covAcc;
                        }
                        covAcc.ClientCount++;
                    }
                }

                if (adoption.Suppressed || adoption.Copilot == null)
                {
                    insights.ClientsSuppressed++;
                    continue;
                }

                var copilot = adoption.Copilot;
                insights.ClientsWithCopilotFigures++;

                insights.TotalLicensedUsers += copilot.LicensedUsers ?? 0;
                insights.TotalActiveUsers += copilot.ActiveUsers ?? 0;

                if (copilot.LicensedUsers.HasValue) licensedPerClient.Add(copilot.LicensedUsers.Value);
                if (copilot.ActiveUsers.HasValue) activePerClient.Add(copilot.ActiveUsers.Value);
                if (copilot.AdoptionRatePct.HasValue) adoptionRates.Add(copilot.AdoptionRatePct.Value);
                if (copilot.HabitRatePct.HasValue) habitRates.Add(copilot.HabitRatePct.Value);

                if ((copilot.CustomAgents ?? 0) > 0) insights.ClientsWithCustomAgents++;

                CountSource(sourceAccumulators, "Copilot audit", copilot.AuditAvailable);
                CountSource(sourceAccumulators, "Copilot usage report", copilot.CopilotUsageReportAvailable);
                CountSource(sourceAccumulators, "Microsoft 365 usage reports", copilot.M365UsageReportsAvailable);
                CountSource(sourceAccumulators, "User metadata", copilot.UserMetadataAvailable);
            }

            insights.MedianLicensedUsersPerClient = Median(licensedPerClient);
            insights.MedianActiveUsersPerClient = Median(activePerClient);
            insights.MedianAdoptionRatePct = Percentile(adoptionRates, 0.5);
            insights.LowerQuartileAdoptionRatePct = Percentile(adoptionRates, 0.25);
            insights.UpperQuartileAdoptionRatePct = Percentile(adoptionRates, 0.75);
            insights.MedianHabitRatePct = Percentile(habitRates, 0.5);

            insights.Skus = skuAccumulators.Values
                .OrderByDescending(s => s.ClientCount)
                .ThenByDescending(s => s.AssignedUsers)
                .ThenBy(s => s.SkuPartNumber, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            insights.DataSources = sourceAccumulators.Values
                .OrderByDescending(f => f.EnabledCount)
                .ThenBy(f => f.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            insights.Coverage = coverageAccumulators.Values
                .OrderBy(c => c.Workload, System.StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(c => c.ClientCount)
                .ThenBy(c => c.Status, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            return insights;
        }

        private static void CountSource(Dictionary<string, FeatureAdoption> accumulators, string name, bool available)
        {
            if (!accumulators.TryGetValue(name, out var acc))
            {
                acc = new FeatureAdoption { Name = name };
                accumulators[name] = acc;
            }

            if (available) acc.EnabledCount++;
            else acc.DisabledCount++;
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

        /// <summary>
        /// Linear-interpolated percentile, rounded to one decimal.
        /// </summary>
        /// <remarks>
        /// Used for the adoption-rate spread. The quartiles matter more than the median on their own:
        /// "half of installs are between 12% and 48%" tells the project team something actionable,
        /// whereas a lone median hides whether the install base is consistent or bimodal.
        /// </remarks>
        internal static double Percentile(List<double> values, double percentile)
        {
            if (values == null || values.Count == 0) return 0;

            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 1) return System.Math.Round(sorted[0], 1);

            var position = percentile * (sorted.Count - 1);
            var lower = (int)System.Math.Floor(position);
            var upper = (int)System.Math.Ceiling(position);

            if (lower == upper) return System.Math.Round(sorted[lower], 1);

            var weight = position - lower;
            return System.Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * weight, 1);
        }
    }
}
