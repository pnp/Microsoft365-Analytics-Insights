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
    /// Imports billed Microsoft Copilot Studio consumption ("Copilot Credits") into SQL.
    ///
    /// <para>This is <b>what Microsoft charged</b>, per agent and per day. It is deliberately not reconciled
    /// with the per-conversation <c>CopilotCreditEstimation</c> this product derives from audit events: that
    /// one is an inference over audit data and the two will legitimately disagree.</para>
    ///
    /// <para><b>No user attribution is possible.</b> Microsoft bills Copilot Studio at the environment and
    /// agent level, and the API returns a distinct-user count rather than user identities. The importer stores
    /// that count and nothing more - it does not apportion spend across users, because any such figure would
    /// be an invention presented alongside real billing data.</para>
    /// </summary>
    public class CopilotStudioCreditImporter
    {
        private readonly ILogger _logger;
        private readonly ICopilotStudioCreditSource _source;
        private readonly IAgentCostStore _store;
        private readonly IClock _clock;
        private readonly int _trailingWindowDays;

        /// <summary>
        /// Stops a broken or looping continuation token from paging for ever. A tenant's daily consumption is
        /// a few thousand rows at most, and the page size is 5000, so this is far above any real response.
        /// </summary>
        private const int MaxPages = 500;

        public CopilotStudioCreditImporter(
            ILogger logger,
            ICopilotStudioCreditSource source,
            IAgentCostStore store,
            int trailingWindowDays = AppConfig.DefaultCopilotStudioCreditsTrailingWindowDays,
            IClock clock = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? SystemClock.Instance;
            _trailingWindowDays = trailingWindowDays > 0
                ? trailingWindowDays
                : AppConfig.DefaultCopilotStudioCreditsTrailingWindowDays;
        }

        /// <summary>
        /// Runs one import. Returns the log row it wrote, which carries the outcome; never throws, so a
        /// failure here cannot take down the import sections that run after it.
        /// </summary>
        public async Task<AgentCostImportOutcome> ImportAsync()
        {
            // Microsoft's consumption figures lag, and a partially-settled day is re-read on the next run
            // anyway, so the window ends today rather than trying to guess how far behind the API is.
            var today = _clock.UtcNow.Date;
            var from = today.AddDays(-(_trailingWindowDays - 1));

            var log = new AgentCostImportLog
            {
                ImportName = AgentCostImportNames.CopilotStudioCredits,
                ImportedUtc = _clock.UtcNow,
                WindowFrom = from,
                WindowTo = today,
            };

            try
            {
                var environmentNames = await _source.GetEnvironmentNamesAsync();

                var mapped = new List<CopilotStudioCreditDaily>();
                var rowsRead = 0;

                // One request PER DAY, not one request for the whole window.
                //
                // The endpoint reports consumption for the range it is given, and the only per-row date is
                // `asOfDate` - which is not part of the documented response model and appears only when the
                // undocumented `includeFields` parameter is honoured. Asking for a seven-day range and then
                // trusting that field to split the answer back into days would, if it were ever absent or
                // range-level, pile a whole week's spend onto a single date. Asking for one day at a time
                // makes the usage date a fact about the request instead of an inference about the response.
                //
                // The cost is a handful of extra calls on a once-a-day import, which is not a constraint.
                for (var day = from; day <= today; day = day.AddDays(1))
                {
                    var rows = await ReadAllPagesAsync(day, day);
                    rowsRead += rows.Count;
                    mapped.AddRange(MapAndAggregate(rows, day, day, environmentNames, _clock.UtcNow));
                }

                log.RowsRead = rowsRead;
                log.RowsSaved = await _store.UpsertCopilotStudioCreditsAsync(mapped);

                _logger.LogInformation(
                    $"Copilot Studio credits: read {log.RowsRead:N0} row(s) across {(today - from).Days + 1} day(s) "
                    + $"({from:yyyy-MM-dd}..{today:yyyy-MM-dd}), stored {log.RowsSaved:N0} after aggregating duplicate "
                    + "dimension slices.");
            }
            catch (AgentCostAuthorisationException ex)
            {
                // The remedy is a role assignment, so the message is the whole value of this log line.
                log.Error = ex.Message;
                _logger.LogError(ex, "Copilot Studio credit import failed: " + ex.Message);
                await SafeSaveLogAsync(log);
                return new AgentCostImportOutcome(log, isAuthorisationFailure: true);
            }
            catch (Exception ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, $"Copilot Studio credit import failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
            }

            await SafeSaveLogAsync(log);
            return new AgentCostImportOutcome(log);
        }

        private async Task<List<CopilotStudioCreditRow>> ReadAllPagesAsync(DateTime from, DateTime to)
        {
            var all = new List<CopilotStudioCreditRow>();
            string continuationToken = null;

            // Every continuation token already followed. A server that keeps handing back the same token
            // would otherwise append the same rows on each pass; those rows aggregate by dimension hash, so
            // the repeats would be SUMMED into inflated spend rather than surfacing as an error.
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;

            do
            {
                if (++pages > MaxPages)
                {
                    throw new AgentCostIncompleteReadException(
                        $"Copilot Studio credit paging exceeded the {MaxPages}-page safety limit, so the window "
                        + $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd} could not be read completely. Nothing was stored rather "
                        + "than a partial figure that would look like a drop in spend.");
                }

                var page = await _source.GetConsumptionPageAsync(from, to, continuationToken);
                all.AddRange(page.Rows);

                if (!page.HasMore) break;

                if (!seenTokens.Add(page.ContinuationToken))
                {
                    throw new AgentCostIncompleteReadException(
                        "The Power Platform licensing API returned a continuation token it had already returned, so "
                        + "paging could not complete. Nothing was stored for this window; it will be retried on the next "
                        + "cycle.");
                }

                continuationToken = page.ContinuationToken;
            }
            while (!string.IsNullOrEmpty(continuationToken));

            return all;
        }

        /// <summary>
        /// Maps API rows onto entities, and combines any that land on the same dimension slice.
        /// </summary>
        /// <remarks>
        /// Aggregation is defensive rather than expected: one row per (day, environment, agent, dimensions) is
        /// what the API should return. But the response envelope is only partly documented, and two rows that
        /// collided on the upsert key would otherwise mean the second silently replaced the first - losing
        /// real spend. Credits are summed; the distinct-user count is taken as the maximum rather than summed,
        /// because the same user can appear in both rows and adding them would overstate reach.
        /// </remarks>
        internal static IReadOnlyList<CopilotStudioCreditDaily> MapAndAggregate(
            IEnumerable<CopilotStudioCreditRow> rows,
            DateTime windowFrom,
            DateTime windowTo,
            IReadOnlyDictionary<string, string> environmentNames,
            DateTime importedUtc)
        {
            var byHash = new Dictionary<string, CopilotStudioCreditDaily>(StringComparer.Ordinal);

            foreach (var row in rows ?? Enumerable.Empty<CopilotStudioCreditRow>())
            {
                if (row == null) continue;

                var usageDate = ResolveUsageDate(row, windowFrom, windowTo);

                var hash = AgentCostRowHasher.Hash(
                    usageDate.ToString("yyyy-MM-dd"),
                    row.EnvironmentId,
                    row.ResourceId,
                    row.FeatureName,
                    row.ChannelId,
                    row.LlmModel,
                    row.ToolInvoked,
                    row.KnowledgeSources);

                if (byHash.TryGetValue(hash, out var existing))
                {
                    existing.BilledCredits += row.Consumed;

                    if (row.NonBillableQuantity.HasValue)
                    {
                        existing.NonBilledCredits = (existing.NonBilledCredits ?? 0m) + row.NonBillableQuantity.Value;
                    }

                    if (row.Users.HasValue)
                    {
                        existing.DistinctUsers = Math.Max(existing.DistinctUsers ?? 0, row.Users.Value);
                    }

                    if (row.LastRefreshedDate.HasValue
                        && (!existing.LastRefreshedUtc.HasValue || row.LastRefreshedDate.Value > existing.LastRefreshedUtc.Value))
                    {
                        existing.LastRefreshedUtc = row.LastRefreshedDate;
                    }

                    continue;
                }

                string environmentName = null;
                if (!string.IsNullOrEmpty(row.EnvironmentId) && environmentNames != null)
                {
                    environmentNames.TryGetValue(row.EnvironmentId, out environmentName);
                }

                byHash.Add(hash, new CopilotStudioCreditDaily
                {
                    UsageDate = usageDate,
                    EnvironmentId = row.EnvironmentId,
                    EnvironmentName = environmentName,
                    AgentId = row.ResourceId,
                    AgentName = row.ResourceName,
                    Harness = CopilotStudioHarnessClassifier.Classify(row.FeatureName),
                    FeatureName = row.FeatureName,
                    ChannelId = row.ChannelId,
                    LlmModel = row.LlmModel,
                    ToolInvoked = row.ToolInvoked,
                    KnowledgeSources = row.KnowledgeSources,
                    BilledCredits = row.Consumed,
                    NonBilledCredits = row.NonBillableQuantity,
                    DistinctUsers = row.Users,
                    LastRefreshedUtc = row.LastRefreshedDate,
                    DimensionHash = hash,
                    ImportedUtc = importedUtc,
                });
            }

            return byHash.Values.ToList();
        }

        /// <summary>
        /// The usage day for a row.
        /// </summary>
        /// <remarks>
        /// <para>Because the importer asks for exactly one day per request, the <b>requested</b> day is the
        /// authoritative answer and is used unconditionally. The API's own <c>asOfDate</c> is deliberately
        /// not preferred over it: that field is absent from the documented response model, so relying on it
        /// would make the usage date - which is part of the upsert key - depend on an undocumented field
        /// continuing to be returned.</para>
        /// <para>It is also never clamped or rewritten. The date being a fact about the request means it is
        /// stable across runs, so a re-read of the same day updates the row in place rather than inserting a
        /// second copy under a shifted key as the trailing window advances.</para>
        /// </remarks>
        private static DateTime ResolveUsageDate(CopilotStudioCreditRow row, DateTime windowFrom, DateTime windowTo)
        {
            // windowFrom == windowTo for every production call; the parameters remain so the mapping stays
            // testable with a wider window.
            return windowFrom == windowTo ? windowFrom.Date : (row.AsOfDate?.Date ?? windowTo.Date);
        }

        private async Task SafeSaveLogAsync(AgentCostImportLog log)
        {
            try
            {
                await _store.SaveImportLogAsync(log);
            }
            catch (Exception ex)
            {
                // The log row is a diagnostic. Failing to write it must not turn a successful import into a
                // failed one, nor mask the error it was recording.
                _logger.LogWarning($"Couldn't write the Copilot Studio credit import log row: {ex.Message}");
            }
        }

        /// <summary>
        /// Takes a snapshot of the tenant's overall Copilot Credits entitlement - entitled, consumed,
        /// available and overage status.
        ///
        /// Separate from <see cref="ImportAsync"/> because it answers a different question ("are we about to
        /// run out?" rather than "where did the spend go?") and because it is cheap: one call, one row. Never
        /// throws, for the same reason as the consumption import.
        /// </summary>
        public async Task<AgentCostImportOutcome> ImportCapacityAsync()
        {
            var log = new AgentCostImportLog
            {
                ImportName = AgentCostImportNames.CopilotStudioCapacity,
                ImportedUtc = _clock.UtcNow,
            };

            try
            {
                var snapshot = await _source.GetCapacityAsync();
                if (snapshot == null)
                {
                    // Not an error: a tenant with no Copilot Studio entitlement legitimately has nothing to
                    // report. Recorded as zero rows so it can be told apart from a failure.
                    _logger.LogInformation("Copilot Studio credit entitlement: the API returned no entitlement for this tenant.");
                }
                else
                {
                    await _store.SaveCapacitySnapshotAsync(new CopilotStudioCreditCapacity
                    {
                        SnapshotUtc = _clock.UtcNow,
                        ConsumptionAsOf = snapshot.ConsumedLastUpdatedOn,
                        Entitled = snapshot.Entitled,
                        Consumed = snapshot.Consumed,
                        ConsumptionType = snapshot.ConsumptionType,
                        Allocated = snapshot.Allocated,
                        Available = snapshot.Available,
                        PayAsYouGoConsumed = snapshot.PayAsYouGoConsumed,
                        Status = snapshot.Status,
                    });

                    log.RowsRead = 1;
                    log.RowsSaved = 1;

                    _logger.LogInformation($"Copilot Studio credit entitlement: consumed {snapshot.Consumed} of "
                        + $"{snapshot.Entitled} ({snapshot.Status}).");
                }
            }
            catch (AgentCostAuthorisationException ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, "Copilot Studio credit entitlement read failed: " + ex.Message);
                await SafeSaveLogAsync(log);
                return new AgentCostImportOutcome(log, isAuthorisationFailure: true);
            }
            catch (Exception ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, $"Copilot Studio credit entitlement read failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
            }

            await SafeSaveLogAsync(log);
            return new AgentCostImportOutcome(log);
        }

        /// <summary>
        /// Imports <b>per-user</b> billed Copilot Studio consumption, from the entitlement routes Microsoft
        /// added in July 2026.
        /// </summary>
        /// <remarks>
        /// <para>This is the only documented source of per-user Copilot Studio credit consumption. It is a
        /// separate endpoint from the per-agent read, not a breakdown of it, so the two are stored in
        /// separate tables and their totals should not be expected to reconcile exactly.</para>
        /// <para>Returns a log whose <c>RowsRead</c> is zero and whose error is empty when the tenant's API
        /// does not offer the route - an older or restricted API surface is a legitimate state, not a
        /// failure, and must not stop the per-agent figures being recorded as up to date.</para>
        /// </remarks>
        public async Task<AgentCostImportOutcome> ImportUserCreditsAsync()
        {
            var today = _clock.UtcNow.Date;
            var from = today.AddDays(-(_trailingWindowDays - 1));

            var log = new AgentCostImportLog
            {
                ImportName = AgentCostImportNames.CopilotStudioUserCredits,
                ImportedUtc = _clock.UtcNow,
                WindowFrom = from,
                WindowTo = today,
            };

            try
            {
                var environmentNames = await _source.GetEnvironmentNamesAsync();

                var mapped = new List<CopilotStudioCreditUserDaily>();
                var rowsRead = 0;
                var routeAvailable = true;

                // One request per day, for the same reason as the per-agent read: the documented parameters
                // describe a query RANGE, and whether these newer routes break a range down per day is not
                // established. Asking for a single day makes the usage date a fact about the request.
                for (var day = from; day <= today && routeAvailable; day = day.AddDays(1))
                {
                    string continuationToken = null;
                    var seenTokens = new HashSet<string>(StringComparer.Ordinal);
                    var pages = 0;

                    do
                    {
                        if (++pages > MaxPages)
                        {
                            throw new AgentCostIncompleteReadException(
                                $"Copilot Studio per-user credit paging for {day:yyyy-MM-dd} exceeded the {MaxPages}-page "
                                + "safety limit, so the day could not be read completely. Nothing was stored.");
                        }

                        var page = await _source.GetUserConsumptionPageAsync(day, day, continuationToken);
                        if (page == null)
                        {
                            routeAvailable = false;
                            break;
                        }

                        rowsRead += page.Rows.Count;
                        mapped.AddRange(MapUserRows(page.Rows, day, environmentNames, _clock.UtcNow));

                        if (!page.HasMore) break;

                        if (!seenTokens.Add(page.ContinuationToken))
                        {
                            throw new AgentCostIncompleteReadException(
                                "The Power Platform licensing API returned a per-user continuation token it had already "
                                + "returned, so paging could not complete. Nothing was stored for this window.");
                        }

                        continuationToken = page.ContinuationToken;
                    }
                    while (!string.IsNullOrEmpty(continuationToken));
                }

                if (!routeAvailable)
                {
                    _logger.LogInformation("Per-user Copilot Studio credit consumption is not available on this tenant's "
                        + "licensing API. Per-agent figures are unaffected.");
                    await SafeSaveLogAsync(log);
                    return new AgentCostImportOutcome(log);
                }

                log.RowsRead = rowsRead;
                log.RowsSaved = await _store.UpsertCopilotStudioUserCreditsAsync(mapped);

                _logger.LogInformation($"Copilot Studio per-user credits: read {log.RowsRead:N0} row(s) across "
                    + $"{(today - from).Days + 1} day(s), stored {log.RowsSaved:N0}.");
            }
            catch (AgentCostAuthorisationException ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, "Copilot Studio per-user credit import failed: " + ex.Message);
                await SafeSaveLogAsync(log);
                return new AgentCostImportOutcome(log, isAuthorisationFailure: true);
            }
            catch (Exception ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, $"Copilot Studio per-user credit import failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
            }

            await SafeSaveLogAsync(log);
            return new AgentCostImportOutcome(log);
        }

        /// <summary>
        /// Maps per-user API rows onto entities for one day, combining any that land on the same identity.
        /// </summary>
        internal static IReadOnlyList<CopilotStudioCreditUserDaily> MapUserRows(
            IEnumerable<CopilotStudioUserCreditRow> rows,
            DateTime usageDate,
            IReadOnlyDictionary<string, string> environmentNames,
            DateTime importedUtc)
        {
            var byHash = new Dictionary<string, CopilotStudioCreditUserDaily>(StringComparer.Ordinal);

            foreach (var row in rows ?? Enumerable.Empty<CopilotStudioUserCreditRow>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.UserId)) continue;

                var hash = AgentCostRowHasher.Hash(
                    usageDate.ToString("yyyy-MM-dd"), row.UserId, row.EnvironmentId);

                if (byHash.TryGetValue(hash, out var existing))
                {
                    existing.BilledCredits += row.Consumed;
                    continue;
                }

                string environmentName = null;
                if (!string.IsNullOrEmpty(row.EnvironmentId) && environmentNames != null)
                {
                    environmentNames.TryGetValue(row.EnvironmentId, out environmentName);
                }

                byHash.Add(hash, new CopilotStudioCreditUserDaily
                {
                    UsageDate = usageDate.Date,
                    UserId = row.UserId,
                    EnvironmentId = row.EnvironmentId,
                    EnvironmentName = environmentName,
                    BilledCredits = row.Consumed,
                    Unit = row.Unit,
                    DimensionHash = hash,
                    ImportedUtc = importedUtc,
                });
            }

            return byHash.Values.ToList();
        }
    }
}
