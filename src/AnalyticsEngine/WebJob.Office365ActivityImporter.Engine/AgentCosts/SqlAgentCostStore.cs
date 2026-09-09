using Common.Entities;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Stores imported agent-cost data in SQL.
    ///
    /// Both fact writes are <b>upserts</b>, not inserts. Azure re-estimates an open billing period several
    /// times a day and Copilot Studio recalculates consumption as usage days settle, so both importers re-read
    /// a trailing window on every run. Appending would multiply every restated day by the number of runs that
    /// have seen it.
    /// </summary>
    public class SqlAgentCostStore : IAgentCostStore
    {
        private readonly IAnalyticsDbContextFactory _dbContextFactory;
        private readonly ILogger _logger;

        public SqlAgentCostStore(IAnalyticsDbContextFactory dbContextFactory, ILogger logger)
        {
            _dbContextFactory = dbContextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<int> UpsertCopilotStudioCreditsAsync(IReadOnlyList<CopilotStudioCreditDaily> rows)
        {
            if (rows == null || rows.Count == 0) return 0;

            using (var db = _dbContextFactory.Create())
            {
                // Scoped to the usage-date range being written so the unique index on
                // (usage_date, dimension_hash) can seek. A lookup on the hash alone would have to scan,
                // because the hash is not the leading key column.
                var from = rows.Min(r => r.UsageDate).Date;
                var to = rows.Max(r => r.UsageDate).Date;

                var stored = await db.CopilotStudioCreditDaily
                    .Where(r => r.UsageDate >= from && r.UsageDate <= to)
                    .ToListAsync();

                var byHash = BuildIndex(stored, r => r.DimensionHash);

                foreach (var row in rows)
                {
                    if (byHash.TryGetValue(row.DimensionHash, out var existing))
                    {
                        // Restated figures: overwrite the measures and the descriptive fields, but leave the
                        // identity (usage date + dimension hash) alone - it is what matched them.
                        existing.BilledCredits = row.BilledCredits;
                        existing.NonBilledCredits = row.NonBilledCredits;
                        existing.DistinctUsers = row.DistinctUsers;
                        existing.AgentName = row.AgentName;
                        existing.EnvironmentName = row.EnvironmentName;
                        existing.Harness = row.Harness;
                        existing.LastRefreshedUtc = row.LastRefreshedUtc;
                        existing.ImportedUtc = row.ImportedUtc;
                    }
                    else
                    {
                        db.CopilotStudioCreditDaily.Add(row);

                        // Indexed as well, so a duplicate within this same batch updates the pending row
                        // rather than adding a second one that would violate the unique index.
                        byHash[row.DimensionHash] = row;
                    }
                }

                await db.SaveChangesAsync();
                return rows.Count;
            }
        }

        public async Task<int> UpsertAzureCostsAsync(IReadOnlyList<AzureCostDaily> rows)
        {
            if (rows == null || rows.Count == 0) return 0;

            using (var db = _dbContextFactory.Create())
            {
                var from = rows.Min(r => r.UsageDate).Date;
                var to = rows.Max(r => r.UsageDate).Date;

                var stored = await db.AzureCostDaily
                    .Where(r => r.UsageDate >= from && r.UsageDate <= to)
                    .ToListAsync();

                var byHash = BuildIndex(stored, r => r.RowHash);

                foreach (var row in rows)
                {
                    if (byHash.TryGetValue(row.RowHash, out var existing))
                    {
                        existing.Cost = row.Cost;
                        existing.Quantity = row.Quantity;
                        existing.IsEstimated = row.IsEstimated;
                        existing.ImportedUtc = row.ImportedUtc;
                    }
                    else
                    {
                        db.AzureCostDaily.Add(row);
                        byHash[row.RowHash] = row;
                    }
                }

                await db.SaveChangesAsync();
                return rows.Count;
            }
        }

        public async Task SaveCapacitySnapshotAsync(CopilotStudioCreditCapacity snapshot)
        {
            if (snapshot == null) return;

            using (var db = _dbContextFactory.Create())
            {
                db.CopilotStudioCreditCapacity.Add(snapshot);
                await db.SaveChangesAsync();
            }
        }

        public async Task SaveImportLogAsync(AgentCostImportLog log)
        {
            if (log == null) return;

            using (var db = _dbContextFactory.Create())
            {
                // Truncate defensively: Error carries an API response fragment, and an over-length value
                // would throw on SaveChanges - losing the very diagnostic the row exists to record.
                if (log.Error != null && log.Error.Length > 1000)
                {
                    log.Error = log.Error.Substring(0, 1000);
                }

                db.AgentCostImportLogs.Add(log);
                await db.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Indexes already-stored rows by their hash. Ordinal comparison, because the hash is hex produced by
        /// this product rather than user text. A duplicate hash keeps the first row: the unique index makes
        /// that impossible in a healthy database, and if one somehow exists, updating an arbitrary one of the
        /// pair is no better than updating the first consistently.
        /// </summary>
        private static Dictionary<string, T> BuildIndex<T>(IEnumerable<T> rows, Func<T, string> hashSelector)
        {
            var index = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var hash = hashSelector(row);
                if (!string.IsNullOrEmpty(hash) && !index.ContainsKey(hash))
                {
                    index.Add(hash, row);
                }
            }
            return index;
        }
    }
}
