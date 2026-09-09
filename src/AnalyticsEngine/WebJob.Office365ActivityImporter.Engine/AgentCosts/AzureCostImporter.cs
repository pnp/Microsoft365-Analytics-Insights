using Common.Entities.Config;
using Common.Entities.Entities.AgentCosts;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Imports daily Azure spend from Microsoft Cost Management into SQL.
    ///
    /// <para>Built for reporting on agent workloads billed directly to an Azure subscription, but it is a
    /// <b>general</b> cost import: which meters it brings back is configured, not compiled in. That is not
    /// laziness. Microsoft Cowork - the workload this was written for - is billed through Copilot Credits
    /// managed in the Microsoft 365 admin centre, and Microsoft documents no Azure meter for it at all, so
    /// there is no correct constant to ship. With no filter configured the import brings back every meter at
    /// the scope, which is exactly what an operator needs to discover what their own agent spend is called.</para>
    ///
    /// <para><b>No user attribution is possible.</b> Azure billing is resource-scoped; no Cost Management
    /// surface carries a user identity.</para>
    /// </summary>
    public class AzureCostImporter
    {
        /// <summary>
        /// How long after a billing period ends before its costs stop changing. Microsoft documents that
        /// charges can continue to accrue and change until about the fifth day after the period ends, so a
        /// usage day is reported as an estimate until then.
        /// </summary>
        public const int BillingPeriodFinalisationLagDays = 5;

        private readonly ILogger _logger;
        private readonly IAzureCostSource _source;
        private readonly IAgentCostStore _store;
        private readonly AzureCostImportSettings _settings;
        private readonly IClock _clock;

        public AzureCostImporter(
            ILogger logger,
            IAzureCostSource source,
            IAgentCostStore store,
            AzureCostImportSettings settings,
            IClock clock = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// Runs one import across every configured scope. Returns the log row it wrote; never throws, so a
        /// failure cannot take down the import sections that run after it.
        /// </summary>
        public async Task<AgentCostImportLog> ImportAsync()
        {
            var today = _clock.UtcNow.Date;
            var windowDays = _settings.TrailingWindowDays > 0
                ? _settings.TrailingWindowDays
                : AzureCostImportSettings.DefaultTrailingWindowDays;
            var from = today.AddDays(-(windowDays - 1));

            var log = new AgentCostImportLog
            {
                ImportName = AgentCostImportNames.AzureCostManagement,
                ImportedUtc = _clock.UtcNow,
                WindowFrom = from,
                WindowTo = today,
            };

            if (!_settings.IsConfigured)
            {
                // Declining is the correct behaviour, and saying so explicitly matters: the toggle can be on
                // while the scope setting is missing, and without this the operator would see an import that
                // "succeeds" and stores nothing.
                log.Error = "No Cost Management scope is configured. Set the 'AzureCostScopes' App Service application "
                    + "setting to one or more scopes (for example '/subscriptions/00000000-0000-0000-0000-000000000000'; "
                    + "separate several with ';') and grant the app registration's service principal the 'Cost Management "
                    + "Reader' role on each. Nothing was imported.";
                _logger.LogWarning("Azure cost import is enabled but no scope is configured, so it did nothing. " + log.Error);

                await SafeSaveLogAsync(log);
                return log;
            }

            var errors = new List<string>();

            foreach (var scope in _settings.Scopes)
            {
                try
                {
                    var rows = await _source.GetDailyCostsAsync(scope, from, today);
                    log.RowsRead += rows.Count;

                    var mapped = MapAndAggregate(rows, scope, today, _clock.UtcNow);
                    log.RowsSaved += await _store.UpsertAzureCostsAsync(mapped);

                    _logger.LogInformation($"Azure costs for scope '{scope}': read {rows.Count:N0} row(s) for "
                        + $"{from:yyyy-MM-dd}..{today:yyyy-MM-dd}, stored {mapped.Count:N0}.");

                    if (rows.Count == 0)
                    {
                        _logger.LogInformation($"Azure costs for scope '{scope}': the query matched no rows. "
                            + (HasFilter
                                ? $"The '{_settings.MeterFilterDimension}' filter is set to "
                                  + $"'{string.Join(", ", _settings.MeterFilterValues)}' - check those values against a cost "
                                  + "export from this scope, because a filter that matches nothing is indistinguishable from no spend."
                                : "No meter filter is configured, so this scope genuinely had no spend in the window."));
                    }
                }
                catch (AgentCostAuthorisationException ex)
                {
                    errors.Add(ex.Message);
                    _logger.LogError(ex, $"Azure cost import failed for scope '{scope}': " + ex.Message);
                }
                catch (Exception ex)
                {
                    // Per-scope, so one inaccessible subscription does not cost the customer the others.
                    errors.Add($"{scope}: {ex.Message}");
                    _logger.LogError(ex, $"Azure cost import failed for scope '{scope}': {ex.Message}. "
                        + "The remaining scopes and all other imports are unaffected.");
                }
            }

            if (errors.Count > 0)
            {
                log.Error = string.Join(" | ", errors);
            }

            await SafeSaveLogAsync(log);
            return log;
        }

        private bool HasFilter => _settings.MeterFilterValues != null && _settings.MeterFilterValues.Count > 0;

        /// <summary>
        /// Maps cost rows onto entities, combining any that land on the same identity.
        /// </summary>
        /// <remarks>
        /// Cost Management returns one row per grouping combination per day, so a collision should not happen.
        /// It is handled anyway because the alternative - two rows with the same upsert key - would mean the
        /// second silently replaced the first, understating real spend. Costs and quantities are summed, which
        /// is the correct combination for money.
        /// </remarks>
        internal IReadOnlyList<AzureCostDaily> MapAndAggregate(
            IEnumerable<AzureCostRow> rows, string scope, DateTime today, DateTime importedUtc)
        {
            var byHash = new Dictionary<string, AzureCostDaily>(StringComparer.Ordinal);

            foreach (var row in rows ?? Enumerable.Empty<AzureCostRow>())
            {
                if (row == null) continue;

                var usageDate = row.UsageDate.Date;

                var hash = AgentCostRowHasher.Hash(
                    usageDate.ToString("yyyy-MM-dd"),
                    scope,
                    row.SubscriptionId,
                    row.ResourceId,
                    row.ServiceName,
                    row.MeterCategory,
                    row.MeterSubCategory,
                    row.MeterName,
                    row.Currency);

                if (byHash.TryGetValue(hash, out var existing))
                {
                    existing.Cost += row.Cost;
                    if (row.Quantity.HasValue)
                    {
                        existing.Quantity = (existing.Quantity ?? 0m) + row.Quantity.Value;
                    }
                    continue;
                }

                byHash.Add(hash, new AzureCostDaily
                {
                    UsageDate = usageDate,
                    Scope = scope,
                    SubscriptionId = row.SubscriptionId,
                    ResourceId = Truncate(row.ResourceId, 850),
                    ResourceGroup = row.ResourceGroup,
                    ServiceName = row.ServiceName,
                    MeterCategory = row.MeterCategory,
                    MeterSubCategory = row.MeterSubCategory,
                    MeterName = row.MeterName,
                    Cost = row.Cost,
                    Currency = row.Currency,
                    Quantity = row.Quantity,
                    IsEstimated = IsStillProvisional(usageDate, today),
                    RowHash = hash,
                    ImportedUtc = importedUtc,
                });
            }

            return byHash.Values.ToList();
        }

        /// <summary>
        /// Whether a usage day's cost can still change: true until the billing period containing it has been
        /// closed, which Microsoft documents as up to
        /// <see cref="BillingPeriodFinalisationLagDays"/> days after the period ends.
        /// </summary>
        internal static bool IsStillProvisional(DateTime usageDate, DateTime today)
        {
            var periodEnd = new DateTime(usageDate.Year, usageDate.Month,
                DateTime.DaysInMonth(usageDate.Year, usageDate.Month));

            return today.Date <= periodEnd.AddDays(BillingPeriodFinalisationLagDays);
        }

        /// <summary>
        /// Trims a value to the width of its column. An Azure resource id can exceed the indexable Unicode
        /// width, and truncating is better than failing the whole batch - the id is descriptive here, while
        /// the cost is the reason the row exists.
        /// </summary>
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength);
        }

        private async Task SafeSaveLogAsync(AgentCostImportLog log)
        {
            try
            {
                await _store.SaveImportLogAsync(log);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Couldn't write the Azure cost import log row: {ex.Message}");
            }
        }
    }
}
