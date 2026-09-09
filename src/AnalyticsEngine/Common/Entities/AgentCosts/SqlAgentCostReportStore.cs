using Common.Entities.Entities.AgentCosts;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.AgentCosts
{
    /// <summary>
    /// EF-backed <see cref="IAgentCostReportStore"/>.
    ///
    /// Every query is bounded by the usage-date window first, which the unique indexes on
    /// <c>(usage_date, dimension_hash)</c> and <c>(usage_date, row_hash)</c> let SQL Server seek. Aggregation
    /// happens in SQL rather than in memory: on a busy tenant the granular table gains thousands of rows a
    /// day, and materialising a year of them to sum in C# would be the one thing that makes this page slow.
    /// </summary>
    public class SqlAgentCostReportStore : IAgentCostReportStore
    {
        /// <summary>
        /// Hard ceiling on a page of granular rows. The detail grid is meant for drilling into a filtered
        /// slice, not for exporting the whole table through the browser.
        /// </summary>
        public const int MaxPageSize = 500;

        private readonly IAnalyticsDbContextFactory _dbContextFactory;

        public SqlAgentCostReportStore(IAnalyticsDbContextFactory dbContextFactory = null)
        {
            _dbContextFactory = dbContextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
        }

        public async Task<AgentCostAvailability> GetAvailabilityAsync(bool copilotStudioCreditsEnabled, bool azureCostsEnabled)
        {
            var result = new AgentCostAvailability
            {
                CopilotStudioCreditsEnabled = copilotStudioCreditsEnabled,
                AzureCostsEnabled = azureCostsEnabled,
            };

            using (var db = _dbContextFactory.Create())
            {
                result.HasCopilotStudioCreditData = await db.CopilotStudioCreditDaily.AnyAsync();
                result.HasAzureCostData = await db.AzureCostDaily.AnyAsync();

                if (result.HasCopilotStudioCreditData)
                {
                    result.EarliestUsageDate = await db.CopilotStudioCreditDaily.MinAsync(r => (DateTime?)r.UsageDate);
                    result.LatestUsageDate = await db.CopilotStudioCreditDaily.MaxAsync(r => (DateTime?)r.UsageDate);
                }

                var creditLog = await LatestLogAsync(db, AgentCostImportNames.CopilotStudioCredits);
                if (creditLog != null)
                {
                    result.CopilotStudioCreditsLastImportUtc = creditLog.ImportedUtc;
                    result.CopilotStudioCreditsLastError = creditLog.Error;
                }

                var azureLog = await LatestLogAsync(db, AgentCostImportNames.AzureCostManagement);
                if (azureLog != null)
                {
                    result.AzureCostsLastImportUtc = azureLog.ImportedUtc;
                    result.AzureCostsLastError = azureLog.Error;
                }
            }

            AddMessages(result);
            return result;
        }

        /// <summary>
        /// The plain-English notes shown above the report. Separate and pure so the wording - which is the
        /// part an admin actually reads - can be asserted in a test.
        /// </summary>
        internal static void AddMessages(AgentCostAvailability result)
        {
            if (!result.CopilotStudioCreditsEnabled && !result.AzureCostsEnabled)
            {
                result.Messages.Add("Neither agent cost import is switched on. Ask whoever installed the product to tick "
                    + "\"Copilot Studio credits\" and/or \"Azure costs\" in the installer.");
            }

            if (result.CopilotStudioCreditsEnabled && !result.HasCopilotStudioCreditData)
            {
                result.Messages.Add(string.IsNullOrEmpty(result.CopilotStudioCreditsLastError)
                    ? "The Copilot Studio credit import is switched on but has not stored anything yet. It runs once a "
                      + "day, so allow a cycle before expecting figures."
                    : "The Copilot Studio credit import is switched on but is failing. Most often this means the app "
                      + "registration has not been given a Power Platform role: " + result.CopilotStudioCreditsLastError);
            }

            if (result.AzureCostsEnabled && !result.HasAzureCostData)
            {
                result.Messages.Add(string.IsNullOrEmpty(result.AzureCostsLastError)
                    ? "The Azure cost import is switched on but has not stored anything yet. Check that a scope is set "
                      + "and that the meter filter matches something in your own cost data - a filter that matches "
                      + "nothing looks exactly like having no spend."
                    : "The Azure cost import is switched on but is failing: " + result.AzureCostsLastError);
            }

            // The single most important caveat on this page, and the reason there is no "cost per user" column.
            result.Messages.Add("Copilot Studio credits are billed by Microsoft per agent and per environment, never per "
                + "person, so this report cannot show who spent what. The user counts below are how many different "
                + "people used an agent - they cannot be added together, because the same person appears under every "
                + "agent they used.");

            result.Messages.Add("Azure costs are estimates until Microsoft closes the billing period, which can take a "
                + "few days after month end. Figures marked as estimates can still change.");
        }

        private static async Task<AgentCostImportLog> LatestLogAsync(AnalyticsEntitiesContext db, string importName)
        {
            return await db.AgentCostImportLogs
                .Where(l => l.ImportName == importName)
                .OrderByDescending(l => l.ImportedUtc)
                .FirstOrDefaultAsync();
        }

        public async Task<AgentCostSummary> GetSummaryAsync(AgentCostQuery query)
        {
            var summary = new AgentCostSummary();

            using (var db = _dbContextFactory.Create())
            {
                var credits = Filter(db.CopilotStudioCreditDaily, query);

                if (await credits.AnyAsync())
                {
                    // One round trip for the additive measures. The nullable Sum overloads matter: EF
                    // translates Sum over an empty set to SQL NULL, and the non-nullable overload throws
                    // rather than returning zero.
                    summary.BilledCredits = await credits.SumAsync(r => (decimal?)r.BilledCredits) ?? 0m;
                    summary.NonBilledCredits = await credits.SumAsync(r => r.NonBilledCredits) ?? 0m;
                    summary.DistinctAgents = await credits.Select(r => r.AgentId).Distinct().CountAsync();
                    summary.DistinctEnvironments = await credits.Select(r => r.EnvironmentId).Distinct().CountAsync();
                    summary.DaysWithUsage = await credits.Select(r => r.UsageDate).Distinct().CountAsync();

                    // MAX, never SUM - the counts overlap across slices. See the model docs.
                    summary.PeakDistinctUsersOnASlice = await credits.MaxAsync(r => r.DistinctUsers);

                    summary.UnclassifiedHarnessCredits = await credits
                        .Where(r => r.Harness == CopilotStudioHarness.Unknown || r.Harness == CopilotStudioHarness.NotAssessed)
                        .SumAsync(r => (decimal?)r.BilledCredits) ?? 0m;
                }

                // Latest capacity snapshot regardless of the window: it is a point-in-time tenant total, and
                // an admin wants today's headroom, not headroom as at the end of an arbitrary report window.
                var capacity = await db.CopilotStudioCreditCapacity
                    .OrderByDescending(c => c.SnapshotUtc)
                    .FirstOrDefaultAsync();

                if (capacity != null)
                {
                    summary.Capacity = new CopilotCapacitySnapshot
                    {
                        SnapshotUtc = capacity.SnapshotUtc,
                        ConsumptionAsOf = capacity.ConsumptionAsOf,
                        Entitled = capacity.Entitled,
                        Consumed = capacity.Consumed,
                        ConsumptionType = capacity.ConsumptionType,
                        Allocated = capacity.Allocated,
                        Available = capacity.Available,
                        PayAsYouGoConsumed = capacity.PayAsYouGoConsumed,
                        Status = capacity.Status,
                    };
                }

                // Grouped by currency and never added up: a tenant billed in more than one currency has no
                // meaningful single total without an exchange rate we do not hold.
                var azure = await db.AzureCostDaily
                    .Where(r => r.UsageDate >= query.FromUtc && r.UsageDate <= query.ToUtc)
                    .GroupBy(r => r.Currency)
                    .Select(g => new
                    {
                        Currency = g.Key,
                        Cost = g.Sum(r => (decimal?)r.Cost),
                        AnyEstimated = g.Any(r => r.IsEstimated),
                    })
                    .ToListAsync();

                summary.AzureCost = azure
                    .Select(a => new AzureCostByCurrency
                    {
                        Currency = a.Currency,
                        Cost = a.Cost ?? 0m,
                        IncludesEstimates = a.AnyEstimated,
                    })
                    .OrderByDescending(a => a.Cost)
                    .ToList();
            }

            return summary;
        }

        public async Task<List<AgentCostDailyPoint>> GetDailyTrendAsync(AgentCostQuery query)
        {
            using (var db = _dbContextFactory.Create())
            {
                var rows = await Filter(db.CopilotStudioCreditDaily, query)
                    .GroupBy(r => r.UsageDate)
                    .Select(g => new
                    {
                        Date = g.Key,
                        Billed = g.Sum(r => (decimal?)r.BilledCredits),
                        NonBilled = g.Sum(r => r.NonBilledCredits),
                    })
                    .ToListAsync();

                return rows
                    .Select(r => new AgentCostDailyPoint
                    {
                        Date = r.Date,
                        BilledCredits = r.Billed ?? 0m,
                        NonBilledCredits = r.NonBilled ?? 0m,
                    })
                    .OrderBy(r => r.Date)
                    .ToList();
            }
        }

        public async Task<List<AgentCostBreakdownRow>> GetBreakdownAsync(AgentCostQuery query, string dimension, int top)
        {
            if (!AgentCostDimensions.IsValid(dimension))
            {
                throw new ArgumentException($"'{dimension}' is not a supported breakdown dimension.", nameof(dimension));
            }

            top = top <= 0 ? 20 : Math.Min(top, 200);

            using (var db = _dbContextFactory.Create())
            {
                var filtered = Filter(db.CopilotStudioCreditDaily, query);

                // Projected to (key, label) first so the grouping below is one expression per dimension
                // rather than eight near-identical GroupBy blocks. The label is only meaningful for the two
                // dimensions that have a display name; elsewhere it repeats the key.
                IQueryable<KeyedRow> keyed;
                switch (dimension.ToLowerInvariant())
                {
                    case AgentCostDimensions.Agent:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.AgentId, Label = r.AgentName, Row = r });
                        break;
                    case AgentCostDimensions.Environment:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.EnvironmentId, Label = r.EnvironmentName, Row = r });
                        break;
                    case AgentCostDimensions.Harness:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.Harness, Label = r.Harness, Row = r });
                        break;
                    case AgentCostDimensions.Feature:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.FeatureName, Label = r.FeatureName, Row = r });
                        break;
                    case AgentCostDimensions.Model:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.LlmModel, Label = r.LlmModel, Row = r });
                        break;
                    case AgentCostDimensions.Tool:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.ToolInvoked, Label = r.ToolInvoked, Row = r });
                        break;
                    case AgentCostDimensions.KnowledgeSource:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.KnowledgeSources, Label = r.KnowledgeSources, Row = r });
                        break;
                    default:
                        keyed = filtered.Select(r => new KeyedRow { Key = r.ChannelId, Label = r.ChannelId, Row = r });
                        break;
                }

                var grouped = await keyed
                    .GroupBy(k => k.Key)
                    .Select(g => new
                    {
                        Key = g.Key,
                        Label = g.Max(k => k.Label),
                        Billed = g.Sum(k => (decimal?)k.Row.BilledCredits),
                        NonBilled = g.Sum(k => k.Row.NonBilledCredits),
                        ActiveDays = g.Select(k => k.Row.UsageDate).Distinct().Count(),
                        PeakUsers = g.Max(k => k.Row.DistinctUsers),
                    })
                    .OrderByDescending(g => g.Billed)
                    .Take(top)
                    .ToListAsync();

                return grouped
                    .Select(g => new AgentCostBreakdownRow
                    {
                        Key = g.Key,
                        Label = string.IsNullOrWhiteSpace(g.Label) ? g.Key : g.Label,
                        BilledCredits = g.Billed ?? 0m,
                        NonBilledCredits = g.NonBilled ?? 0m,
                        ActiveDays = g.ActiveDays,
                        PeakDistinctUsers = g.PeakUsers,
                    })
                    .ToList();
            }
        }

        public async Task<AgentCostDetailPage> GetDetailAsync(AgentCostQuery query)
        {
            var page = Math.Max(1, query.Page);
            var pageSize = query.PageSize <= 0 ? 50 : Math.Min(query.PageSize, MaxPageSize);

            using (var db = _dbContextFactory.Create())
            {
                var filtered = Filter(db.CopilotStudioCreditDaily, query);

                var total = await filtered.CountAsync();
                var ascending = string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);

                IOrderedQueryable<CopilotStudioCreditDaily> ordered;
                switch ((query.Sort ?? "credits").ToLowerInvariant())
                {
                    case "date":
                        ordered = ascending ? filtered.OrderBy(r => r.UsageDate) : filtered.OrderByDescending(r => r.UsageDate);
                        break;
                    case "agent":
                        ordered = ascending ? filtered.OrderBy(r => r.AgentName) : filtered.OrderByDescending(r => r.AgentName);
                        break;
                    case "feature":
                        ordered = ascending ? filtered.OrderBy(r => r.FeatureName) : filtered.OrderByDescending(r => r.FeatureName);
                        break;
                    case "users":
                        ordered = ascending ? filtered.OrderBy(r => r.DistinctUsers) : filtered.OrderByDescending(r => r.DistinctUsers);
                        break;
                    default:
                        ordered = ascending ? filtered.OrderBy(r => r.BilledCredits) : filtered.OrderByDescending(r => r.BilledCredits);
                        break;
                }

                // Id is the tie-breaker on every sort. Without it SQL Server may return a different ordering
                // for equal keys on each call, which makes paging drop and repeat rows.
                var rows = await ordered.ThenBy(r => r.ID)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(r => new AgentCostDetailRow
                    {
                        UsageDate = r.UsageDate,
                        EnvironmentId = r.EnvironmentId,
                        EnvironmentName = r.EnvironmentName,
                        AgentId = r.AgentId,
                        AgentName = r.AgentName,
                        Harness = r.Harness,
                        FeatureName = r.FeatureName,
                        ChannelId = r.ChannelId,
                        LlmModel = r.LlmModel,
                        ToolInvoked = r.ToolInvoked,
                        KnowledgeSources = r.KnowledgeSources,
                        BilledCredits = r.BilledCredits,
                        NonBilledCredits = r.NonBilledCredits,
                        DistinctUsers = r.DistinctUsers,
                    })
                    .ToListAsync();

                return new AgentCostDetailPage
                {
                    Rows = rows,
                    TotalRows = total,
                    Page = page,
                    PageSize = pageSize,
                };
            }
        }

        public async Task<List<AzureCostBreakdownRow>> GetAzureBreakdownAsync(AgentCostQuery query, string dimension, int top)
        {
            if (!AzureCostDimensions.IsValid(dimension))
            {
                throw new ArgumentException($"'{dimension}' is not a supported Azure breakdown dimension.", nameof(dimension));
            }

            top = top <= 0 ? 20 : Math.Min(top, 200);

            using (var db = _dbContextFactory.Create())
            {
                var filtered = db.AzureCostDaily.Where(r => r.UsageDate >= query.FromUtc && r.UsageDate <= query.ToUtc);

                IQueryable<AzureKeyedRow> keyed;
                switch (dimension.ToLowerInvariant())
                {
                    case AzureCostDimensions.Service:
                        keyed = filtered.Select(r => new AzureKeyedRow { Key = r.ServiceName, Row = r });
                        break;
                    case AzureCostDimensions.MeterCategory:
                        keyed = filtered.Select(r => new AzureKeyedRow { Key = r.MeterCategory, Row = r });
                        break;
                    case AzureCostDimensions.Resource:
                        keyed = filtered.Select(r => new AzureKeyedRow { Key = r.ResourceId, Row = r });
                        break;
                    case AzureCostDimensions.ResourceGroup:
                        keyed = filtered.Select(r => new AzureKeyedRow { Key = r.ResourceGroup, Row = r });
                        break;
                    case AzureCostDimensions.Subscription:
                        keyed = filtered.Select(r => new AzureKeyedRow { Key = r.SubscriptionId, Row = r });
                        break;
                    default:
                        keyed = filtered.Select(r => new AzureKeyedRow { Key = r.MeterName, Row = r });
                        break;
                }

                // Currency is part of the grouping key, not an aggregate: costs in different currencies are
                // different quantities and adding them would produce a number that means nothing.
                var grouped = await keyed
                    .GroupBy(k => new { k.Key, k.Row.Currency })
                    .Select(g => new
                    {
                        g.Key.Key,
                        g.Key.Currency,
                        Cost = g.Sum(k => (decimal?)k.Row.Cost),
                        Quantity = g.Sum(k => k.Row.Quantity),
                        AnyEstimated = g.Any(k => k.Row.IsEstimated),
                    })
                    .OrderByDescending(g => g.Cost)
                    .Take(top)
                    .ToListAsync();

                return grouped
                    .Select(g => new AzureCostBreakdownRow
                    {
                        Key = g.Key,
                        Label = g.Key,
                        Currency = g.Currency,
                        Cost = g.Cost ?? 0m,
                        Quantity = g.Quantity,
                        IncludesEstimates = g.AnyEstimated,
                    })
                    .ToList();
            }
        }

        public async Task<AgentCostFilterOptions> GetFilterOptionsAsync(AgentCostQuery query)
        {
            using (var db = _dbContextFactory.Create())
            {
                // Deliberately NOT narrowed by the dimension filters themselves - only by the date window.
                // Otherwise selecting an agent would remove every other agent from the picker, and the user
                // could not change their mind without clearing the filter first.
                var inWindow = db.CopilotStudioCreditDaily
                    .Where(r => r.UsageDate >= query.FromUtc && r.UsageDate <= query.ToUtc);

                var agents = await inWindow
                    .Where(r => r.AgentId != null)
                    .GroupBy(r => r.AgentId)
                    .Select(g => new { Id = g.Key, Label = g.Max(r => r.AgentName) })
                    .Take(500)
                    .ToListAsync();

                var environments = await inWindow
                    .Where(r => r.EnvironmentId != null)
                    .GroupBy(r => r.EnvironmentId)
                    .Select(g => new { Id = g.Key, Label = g.Max(r => r.EnvironmentName) })
                    .Take(500)
                    .ToListAsync();

                var harnesses = await inWindow.Where(r => r.Harness != null).Select(r => r.Harness).Distinct().ToListAsync();
                var features = await inWindow.Where(r => r.FeatureName != null).Select(r => r.FeatureName).Distinct().Take(200).ToListAsync();
                var models = await inWindow.Where(r => r.LlmModel != null).Select(r => r.LlmModel).Distinct().Take(200).ToListAsync();

                return new AgentCostFilterOptions
                {
                    Agents = agents
                        .Select(a => new AgentCostFilterOption { Id = a.Id, Label = string.IsNullOrWhiteSpace(a.Label) ? a.Id : a.Label })
                        .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
                        .ToList(),
                    Environments = environments
                        .Select(e => new AgentCostFilterOption { Id = e.Id, Label = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label })
                        .OrderBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase)
                        .ToList(),
                    Harnesses = harnesses.OrderBy(h => h, StringComparer.Ordinal).ToList(),
                    Features = features.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    Models = models.OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).ToList(),
                };
            }
        }

        /// <summary>
        /// Applies the date window and any dimension filters.
        /// </summary>
        /// <remarks>
        /// No <c>ToLower()</c> anywhere: the database collation is already case-insensitive, and wrapping an
        /// indexed column in <c>LOWER()</c> makes the predicate non-SARGable so the date-window seek is lost.
        /// </remarks>
        private static IQueryable<CopilotStudioCreditDaily> Filter(IQueryable<CopilotStudioCreditDaily> source, AgentCostQuery query)
        {
            var filtered = source.Where(r => r.UsageDate >= query.FromUtc && r.UsageDate <= query.ToUtc);

            if (!string.IsNullOrWhiteSpace(query.AgentId)) filtered = filtered.Where(r => r.AgentId == query.AgentId);
            if (!string.IsNullOrWhiteSpace(query.EnvironmentId)) filtered = filtered.Where(r => r.EnvironmentId == query.EnvironmentId);
            if (!string.IsNullOrWhiteSpace(query.Harness)) filtered = filtered.Where(r => r.Harness == query.Harness);
            if (!string.IsNullOrWhiteSpace(query.FeatureName)) filtered = filtered.Where(r => r.FeatureName == query.FeatureName);
            if (!string.IsNullOrWhiteSpace(query.LlmModel)) filtered = filtered.Where(r => r.LlmModel == query.LlmModel);

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var search = query.Search.Trim();
                filtered = filtered.Where(r => r.AgentName.Contains(search) || r.AgentId.Contains(search));
            }

            return filtered;
        }

        /// <summary>Projection shape for the generic credit breakdown. Named so EF can translate it.</summary>
        private class KeyedRow
        {
            public string Key { get; set; }
            public string Label { get; set; }
            public CopilotStudioCreditDaily Row { get; set; }
        }

        /// <summary>Projection shape for the generic Azure breakdown.</summary>
        private class AzureKeyedRow
        {
            public string Key { get; set; }
            public AzureCostDaily Row { get; set; }
        }
    }
}
