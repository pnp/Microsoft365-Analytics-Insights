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
        public async Task<AgentCostImportLog> ImportAsync()
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
                var rows = await ReadAllPagesAsync(from, today);
                log.RowsRead = rows.Count;

                var mapped = MapAndAggregate(rows, from, today, environmentNames, _clock.UtcNow);
                log.RowsSaved = await _store.UpsertCopilotStudioCreditsAsync(mapped);

                _logger.LogInformation(
                    $"Copilot Studio credits: read {log.RowsRead:N0} row(s) for {from:yyyy-MM-dd}..{today:yyyy-MM-dd}, "
                    + $"stored {log.RowsSaved:N0} after aggregating duplicate dimension slices.");
            }
            catch (AgentCostAuthorisationException ex)
            {
                // The remedy is a role assignment, so the message is the whole value of this log line.
                log.Error = ex.Message;
                _logger.LogError(ex, "Copilot Studio credit import failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, $"Copilot Studio credit import failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
            }

            await SafeSaveLogAsync(log);
            return log;
        }

        private async Task<List<CopilotStudioCreditRow>> ReadAllPagesAsync(DateTime from, DateTime to)
        {
            var all = new List<CopilotStudioCreditRow>();
            string continuationToken = null;
            var pages = 0;

            do
            {
                if (++pages > MaxPages)
                {
                    _logger.LogWarning($"Stopped reading Copilot Studio credit consumption after {MaxPages} pages. "
                        + "This is a safety limit; the imported window may be incomplete.");
                    break;
                }

                var page = await _source.GetConsumptionPageAsync(from, to, continuationToken);
                all.AddRange(page.Rows);

                // Guard against a server that keeps handing back the same token: that would page for ever
                // while appending the same rows.
                if (page.HasMore && string.Equals(page.ContinuationToken, continuationToken, StringComparison.Ordinal))
                {
                    _logger.LogWarning("The Power Platform licensing API returned the same continuation token twice; "
                        + "stopping to avoid an endless loop. The imported window may be incomplete.");
                    break;
                }

                continuationToken = page.HasMore ? page.ContinuationToken : null;
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
        /// The usage day for a row. Uses the API's own <c>asOfDate</c> when present, clamped to the window
        /// that was requested; otherwise falls back to the end of the window.
        /// </summary>
        /// <remarks>
        /// Clamping matters because the usage date is part of the upsert key. A row dated outside the window
        /// would be written but never revisited by a later run over that window, so a restatement of it could
        /// never be applied - it would be stranded at its first value.
        /// </remarks>
        private static DateTime ResolveUsageDate(CopilotStudioCreditRow row, DateTime windowFrom, DateTime windowTo)
        {
            if (!row.AsOfDate.HasValue) return windowTo.Date;

            var asOf = row.AsOfDate.Value.Date;
            if (asOf < windowFrom.Date) return windowFrom.Date;
            if (asOf > windowTo.Date) return windowTo.Date;
            return asOf;
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
        public async Task<AgentCostImportLog> ImportCapacityAsync()
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
            }
            catch (Exception ex)
            {
                log.Error = ex.Message;
                _logger.LogError(ex, $"Copilot Studio credit entitlement read failed: {ex.Message}. "
                    + "No other import is affected; this one will be retried on the next cycle.");
            }

            await SafeSaveLogAsync(log);
            return log;
        }
    }
}
