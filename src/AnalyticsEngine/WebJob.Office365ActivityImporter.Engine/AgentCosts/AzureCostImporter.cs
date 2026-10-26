using Common.Entities.Config;
using Common.Entities.Entities.AgentCosts;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Imports daily Azure spend from Microsoft Cost Management into SQL.
    ///
    /// <para>Built for reporting on agent workloads billed directly to an Azure subscription, but it is a
    /// <b>general</b> cost import: which meters it brings back is configured, not compiled in. That is not
    /// laziness. Microsoft documents that pay-as-you-go Copilot Credits - Copilot Cowork, the workload this was
    /// written for, as well as Copilot Studio and Work IQ API - appear under the one
    /// <c>Microsoft Copilot Studio</c> service rather than a service per experience, so no compiled-in
    /// constant could pick Cowork out; separating it takes its own subscription or resource group, or a service
    /// tag where the billing data carries one (a <c>TagKey:</c> grouping). With no filter configured the import
    /// brings back every meter at the scope, which is exactly what an operator needs to discover what their own
    /// agent spend is called.</para>
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

        /// <summary>
        /// Per-scope record of the latest billing period read in full while final. See <see cref="FinalisedPeriodKey"/>.
        /// </summary>
        private readonly IImportLastRunStore _periodState;

        /// <param name="periodState">
        /// Where each scope's "read while final" marker is kept: the process-lifetime ImportSchedule store in
        /// production. Without one, every run reads the latest closed period again - wasteful, but never wrong.
        /// </param>
        public AzureCostImporter(
            ILogger logger,
            IAzureCostSource source,
            IAgentCostStore store,
            AzureCostImportSettings settings,
            IClock clock = null,
            IImportLastRunStore periodState = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? SystemClock.Instance;
            _periodState = periodState ?? new InMemoryImportLastRunStore();
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
            // refresh window also reaches PAST the close, so a closing period is read at least once while final
            // rather than keeping its last estimate for ever - per scope, until that read has actually happened.
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
            var sawConfigurationFailure = false;
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

                    await ReadClosedPeriodAsFinalIfDueAsync(scope, from, today, log);
                }
                catch (AgentCostAuthorisationException ex)
                {
                    errors.Add(ex.Message);
                    sawAuthorisationFailure = true;
                    _logger.LogError(ex, $"Azure cost import failed for scope '{scope}': " + ex.Message);
                }
                catch (AgentCostConfigurationException ex)
                {
                    errors.Add(ex.Message);
                    sawConfigurationFailure = true;
                    _logger.LogError(ex, $"Azure cost import failed for scope '{scope}': {ex.Message} "
                        + "The remaining scopes and all other imports are unaffected.");
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

            // Back off only when every failing scope was REFUSED or REJECTED. Comparing the error count to the scope
            // count is not the same test: with one scope refused and another merely timing out, both fail, the counts
            // match, and the timeout would be suppressed for a full interval despite being exactly the kind of failure
            // a prompt retry fixes. A rejected configuration is reported as such only when nothing was refused, so a
            // refusal keeps its own (permission) guidance.
            var noTransientFailure = !sawTransientFailure;
            return new AgentCostImportOutcome(log,
                isAuthorisationFailure: sawAuthorisationFailure && noTransientFailure,
                isConfigurationFailure: sawConfigurationFailure && !sawAuthorisationFailure && noTransientFailure);
        }

        private bool HasFilter => _settings.MeterFilterValues != null && _settings.MeterFilterValues.Count > 0;

        /// <summary>
        /// Reads the latest closed billing period once more, as final, if this scope has not yet done so - then records
        /// that it has. See <see cref="PeriodAwaitingFinalRead"/>. Throws like the main read, so a failure fails the
        /// scope (and is retried) without touching what the main read already stored.
        /// </summary>
        private async Task ReadClosedPeriodAsFinalIfDueAsync(string scope, DateTime windowFrom, DateTime today, AgentCostImportLog log)
        {
            // Fail-open store: if the marker cannot be read, the period is simply read again.
            var finalisedThrough = await _periodState.GetLastRunUtc(FinalisedPeriodKey(scope));
            var due = PeriodAwaitingFinalRead(today, finalisedThrough?.Date);
            if (!due.HasValue) return;

            var periodStart = due.Value;
            if (periodStart < windowFrom)
            {
                var periodEnd = periodStart.AddMonths(1).AddDays(-1);
                _logger.LogInformation($"Azure costs for scope '{scope}': the billing period "
                    + $"{periodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture)} has closed but has not been read since it "
                    + "became final, so it is read again now and its rows stored as final rather than as estimates.");

                var rows = await _source.GetDailyCostsAsync(scope, periodStart, periodEnd);
                log.RowsRead += rows.Count;
                var mapped = MapAndAggregate(rows, scope, today, _clock.UtcNow);
                log.RowsSaved += await _store.ReplaceAzureCostsAsync(mapped, scope, periodStart, periodEnd);
                if (log.WindowFrom == null || periodStart < log.WindowFrom) log.WindowFrom = periodStart;
            }

            // Otherwise the main window already covered the whole period, after it became final.
            await _periodState.SetLastRunUtc(FinalisedPeriodKey(scope), DateTime.SpecifyKind(periodStart, DateTimeKind.Utc));
        }

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
                    // Invariant: part of the row's identity, so it must not change with the host's calendar.
                    usageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    scope,
                    row.SubscriptionId,
                    row.ResourceId,
                    row.ServiceName,
                    row.MeterCategory,
                    row.MeterSubCategory,
                    row.MeterName,
                    row.Currency,
                    // Part of the key, not payload: with a tag grouping the same resource and meter comes back
                    // once per tag value, so leaving it out would collapse those rows together and sum them -
                    // destroying the very split the tag grouping was configured to get.
                    row.TagKey,
                    row.TagValue);

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
                    TagKey = Truncate(row.TagKey, 256),
                    TagValue = Truncate(row.TagValue, 512),
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
        /// <para>Keeping the previous period in the window for a few days past its close gives every run in those
        /// days a read of it as final, at which point <see cref="IsStillProvisional"/> returns false and the rows are
        /// rewritten with <c>is_estimated = 0</c>. It is not a guarantee on its own - no run may succeed in those days
        /// - which is what <see cref="PeriodAwaitingFinalRead"/> adds.</para>
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
        /// Row key prefix (ImportSchedule partition) of each scope's marker: the first day of the latest billing period
        /// that scope has been read in full while final. Deleting a row re-reads that scope's last closed period once.
        /// </summary>
        public const string FinalisedPeriodKeyPrefix = "AzureCostFinalisedPeriod_";

        internal static string FinalisedPeriodKey(string scope) => FinalisedPeriodKeyPrefix + scope;

        /// <summary>
        /// The first day of the billing period this scope still has to read while final, or null when there is none.
        /// </summary>
        /// <remarks>
        /// <para>The calendar rule in <see cref="EarliestRefreshDate(DateTime)"/> keeps a closed period in the window
        /// for <see cref="PostCloseRefreshDays"/> days after it becomes final, which is enough only if a run succeeds
        /// in those days. If none did, the period left the window anyway and its rows kept their last estimate - and
        /// <c>is_estimated = 1</c> - for ever.</para>
        /// <para>So the latest final period stays due until this scope has actually read it while final. Only that one
        /// period is caught up, never older ones, and it is read as a query of its own rather than by widening the
        /// window: a month is no more than the window already spans, so a catch-up cannot push a large unfiltered
        /// scope past Cost Management's paging limit. With no record at all (a new scope, or the first run of this
        /// version) it is read once.</para>
        /// </remarks>
        internal static DateTime? PeriodAwaitingFinalRead(DateTime today, DateTime? finalisedPeriodStart)
        {
            var latestFinal = LatestFinalPeriodStart(today);
            return finalisedPeriodStart.HasValue && finalisedPeriodStart.Value.Date >= latestFinal
                ? (DateTime?)null
                : latestFinal;
        }

        /// <summary>
        /// The first day of the most recent billing period that is final (past its finalisation lag) on
        /// <paramref name="today"/>: last month once the lag has passed, otherwise the month before.
        /// </summary>
        internal static DateTime LatestFinalPeriodStart(DateTime today)
        {
            var startOfLastPeriod = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            return IsStillProvisional(startOfLastPeriod, today) ? startOfLastPeriod.AddMonths(-1) : startOfLastPeriod;
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
