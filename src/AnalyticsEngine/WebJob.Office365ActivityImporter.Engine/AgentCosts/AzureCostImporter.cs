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
        public async Task<AgentCostImportOutcome> ImportAsync()
        {
            var today = _clock.UtcNow.Date;
            var windowDays = _settings.TrailingWindowDays > 0
                ? _settings.TrailingWindowDays
                : AzureCostImportSettings.DefaultTrailingWindowDays;

            // The window is the LATER of "the configured trailing days" and "everything that still needs a
            // refresh", never just the former.
            //
            // Those two are not the same span, and assuming they were left rows stale for ever: Azure keeps
            // amending an open billing period until a few days after it ends, so a charge dated the 1st is
            // still mutable a month later - long after a 5-day trailing window has stopped re-reading it. The
            // refresh window also reaches a few days PAST the close, so a closing period is read at least
            // once while final rather than keeping its last estimate for ever.
            var provisionalFrom = EarliestRefreshDate(today);
            var trailingFrom = today.AddDays(-(windowDays - 1));
            var from = provisionalFrom < trailingFrom ? provisionalFrom : trailingFrom;

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

                // Treated like an authorisation failure for cadence purposes: a missing setting cannot fix
                // itself, so retrying every cycle would only repeat the same warning indefinitely.
                return new AgentCostImportOutcome(log, isAuthorisationFailure: true);
            }

            var errors = new List<string>();
            var sawAuthorisationFailure = false;
            var sawTransientFailure = false;

            if (_settings.GroupByWasTruncated)
            {
                _logger.LogWarning($"AzureCostGroupBy names {_settings.GroupBy.Count} dimensions, but Cost Management "
                    + $"accepts at most {AzureCostImportSettings.MaxGroupByDimensions} per query. Using "
                    + $"'{string.Join(", ", _settings.ResolvedGroupBy)}' and ignoring the rest - a request with more "
                    + "would be rejected outright.");
            }

            foreach (var scope in _settings.Scopes)
            {
                try
                {
                    var rows = await _source.GetDailyCostsAsync(scope, from, today);
                    log.RowsRead += rows.Count;

                    var mapped = MapAndAggregate(rows, scope, today, _clock.UtcNow);

                    // Replace, not upsert: the query answer is a complete snapshot of this scope and window,
                    // so rows it no longer returns are no longer true. Called even when the result is empty,
                    // because "the filter now matches nothing" has to clear what was there before.
                    log.RowsSaved += await _store.ReplaceAzureCostsAsync(mapped, scope, from, today);

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
                    sawAuthorisationFailure = true;
                    _logger.LogError(ex, $"Azure cost import failed for scope '{scope}': " + ex.Message);
                }
                catch (Exception ex)
                {
                    // Per-scope, so one inaccessible subscription does not cost the customer the others.
                    errors.Add($"{scope}: {ex.Message}");
                    sawTransientFailure = true;
                    _logger.LogError(ex, $"Azure cost import failed for scope '{scope}': {ex.Message}. "
                        + "The remaining scopes and all other imports are unaffected.");
                }
            }

            if (errors.Count > 0)
            {
                log.Error = string.Join(" | ", errors);
            }

            await SafeSaveLogAsync(log);

            // Back off only when every failing scope was REFUSED. Comparing the error count to the scope
            // count is not the same test: with one scope refused and another merely timing out, both fail,
            // the counts match, and the timeout would be suppressed for a full interval despite being
            // exactly the kind of failure a prompt retry fixes.
            var allFailuresWereRefusals = sawAuthorisationFailure && !sawTransientFailure;
            return new AgentCostImportOutcome(log, allFailuresWereRefusals);
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
                    // Recovered from the resource id when the query could not group by them. Cost Management
                    // allows only two group-by clauses, so the subscription and resource group are not
                    // returned as their own columns - but an Azure resource id contains both.
                    SubscriptionId = row.SubscriptionId ?? SubscriptionIdFromResourceId(row.ResourceId),
                    ResourceId = Truncate(row.ResourceId, 850),
                    ResourceGroup = row.ResourceGroup ?? ResourceGroupFromResourceId(row.ResourceId),
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
        /// The earliest usage day whose cost Azure might still restate, given today's date. This is the start
        /// of the current billing period, or of the previous one while it is still inside the finalisation
        /// lag.
        /// </summary>
        /// <remarks>
        /// Kept in step with <see cref="IsStillProvisional"/> by construction: it returns the earliest date
        /// for which that method is still true, so a row can never be labelled an estimate while sitting
        /// outside the window that would refresh it.
        /// </remarks>
        internal static DateTime EarliestStillProvisionalDate(DateTime today)
        {
            var startOfThisPeriod = new DateTime(today.Year, today.Month, 1);

            // Within the lag, last period is still open too.
            var startOfLastPeriod = startOfThisPeriod.AddMonths(-1);
            return IsStillProvisional(startOfLastPeriod, today) ? startOfLastPeriod : startOfThisPeriod;
        }

        /// <summary>
        /// How many days past a billing period's close it stays in the refresh window, so it is read at
        /// least once while final. Three, against a daily cadence, tolerates two consecutive missed runs.
        /// </summary>
        public const int PostCloseRefreshDays = 3;

        /// <summary>
        /// The earliest usage day the import should re-read, given today's date.
        /// </summary>
        /// <remarks>
        /// <para>This is deliberately <b>wider</b> than <see cref="EarliestStillProvisionalDate"/>. A period
        /// stops being provisional <see cref="BillingPeriodFinalisationLagDays"/> days after it ends - and if
        /// the window contracted the moment that happened, the period would never once be read while final.
        /// Its rows would keep whatever estimate they last received, stay flagged as estimates for ever, and
        /// any adjustment Azure made at close would be missed entirely.</para>
        /// <para>Keeping the previous period in the window for a few days past its close guarantees at least
        /// one read of it as final, at which point <see cref="IsStillProvisional"/> returns false and the
        /// rows are rewritten with <c>is_estimated = 0</c>.</para>
        /// </remarks>
        internal static DateTime EarliestRefreshDate(DateTime today)
        {
            var startOfThisPeriod = new DateTime(today.Year, today.Month, 1);
            var startOfLastPeriod = startOfThisPeriod.AddMonths(-1);

            return today.Day <= BillingPeriodFinalisationLagDays + PostCloseRefreshDays
                ? startOfLastPeriod
                : startOfThisPeriod;
        }

        /// <summary>
        /// Pulls the subscription id out of an Azure resource id
        /// (<c>/subscriptions/{id}/resourceGroups/{rg}/providers/...</c>), or null when it is not there.
        /// </summary>
        internal static string SubscriptionIdFromResourceId(string resourceId)
            => SegmentAfter(resourceId, "subscriptions");

        /// <summary>Pulls the resource group out of an Azure resource id, or null when it is not there.</summary>
        internal static string ResourceGroupFromResourceId(string resourceId)
            => SegmentAfter(resourceId, "resourcegroups");

        /// <summary>
        /// The path segment following <paramref name="marker"/>, matched case-insensitively because Azure
        /// resource ids are not case-consistent (<c>resourceGroups</c> and <c>resourcegroups</c> both occur).
        /// </summary>
        private static string SegmentAfter(string resourceId, string marker)
        {
            if (string.IsNullOrWhiteSpace(resourceId)) return null;

            var segments = resourceId.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], marker, StringComparison.OrdinalIgnoreCase))
                {
                    return segments[i + 1];
                }
            }
            return null;
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
