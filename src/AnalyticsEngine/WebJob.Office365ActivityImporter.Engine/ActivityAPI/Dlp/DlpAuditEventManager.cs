using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    /// <summary>
    /// Saves Data Loss Prevention metadata to SQL via staging tables + merge scripts, from both DLP
    /// sources: the policy detail embedded in a Copilot interaction (which names the agent) and the
    /// standalone DLP.All feed (which does not). Mirrors <c>PowerPlatformAuditEventManager</c>.
    /// </summary>
    /// <remarks>
    /// Both sources are classified by the same <see cref="CopilotDlpRules"/>, so "blocked" means exactly
    /// the same thing on the report wherever the row came from.
    /// </remarks>
    public class DlpAuditEventManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IDlpStagingWriter _stagingWriter;

        private int _totalCopilotMatchCount;
        private int _totalCopilotBlockedCount;
        private int _totalRuleMatchCount;
        private int _deferredEvaluationCount;

        /// <summary>
        /// Production entry point: builds the SQL staging writer from a connection string.
        /// </summary>
        public DlpAuditEventManager(string connectionString, ILogger logger)
            : this(new SqlDlpStagingWriter(connectionString, logger), logger)
        {
        }

        /// <summary>
        /// Testable entry point: the adaptation rules run against any <see cref="IDlpStagingWriter"/>, so
        /// match extraction and block classification can be asserted with no database.
        /// </summary>
        internal DlpAuditEventManager(IDlpStagingWriter stagingWriter, ILogger logger)
        {
            _stagingWriter = stagingWriter ?? throw new ArgumentNullException(nameof(stagingWriter));
            _logger = logger;
        }

        /// <summary>Policy matches staged from Copilot interactions since the last commit.</summary>
        public int StagedCopilotMatchCount => _totalCopilotMatchCount;

        /// <summary>Of those, how many actually denied the user the content.</summary>
        public int StagedCopilotBlockedCount => _totalCopilotBlockedCount;

        /// <summary>Rule matches staged from the DLP.All feed since the last commit.</summary>
        public int StagedRuleMatchCount => _totalRuleMatchCount;

        /// <summary>
        /// Interactions seen since the last commit whose DLP evaluation Microsoft could not complete.
        /// </summary>
        /// <remarks>
        /// Counted rather than stored. A deferred evaluation is neither a block nor an all-clear - it
        /// means the DLP verdict for that interaction is simply unknown - so it must not be mixed into
        /// the block counts. Surfacing it as a per-cycle number tells an operator when the report is
        /// under-counting because Purview was timing out, without adding a column to the largest
        /// Copilot table for a signal that is normally zero.
        /// </remarks>
        public int DeferredEvaluationCount => _deferredEvaluationCount;

        /// <summary>
        /// Stages every DLP policy match carried by a Copilot interaction. Interactions with no
        /// policy-affected resource - the overwhelming majority - stage nothing at all, which is what
        /// keeps <c>copilot_dlp_events</c> sparse.
        /// </summary>
        public Task SaveCopilotDlpMatchesToSqlStaging(CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger?.LogWarning("DlpAuditEventManager received a null Copilot auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            var deferredStages = CopilotDlpRules.DecodeDeferredStages(auditRecord.CopilotEventData?.DlpEvaluationDeferred);
            if (deferredStages.Count > 0)
            {
                // Not a block and not an all-clear: Purview could not evaluate these stages, so this
                // interaction's DLP outcome is unknown and the report under-counts by one.
                _deferredEvaluationCount++;
                _logger?.LogDebug(
                    $"DLP: Copilot event '{baseOfficeEvent.Id}' had deferred DLP evaluation for {string.Join(", ", deferredStages)}" +
                    $"{(string.IsNullOrEmpty(auditRecord.CopilotEventData?.DlpEvaluationDeferredReason) ? string.Empty : $" (reason: {auditRecord.CopilotEventData.DlpEvaluationDeferredReason})")}.");
            }

            foreach (var match in CopilotDlpRules.ExtractMatches(auditRecord))            {
                // A match with neither a policy nor a rule identity would land a row that says only
                // "something happened", which cannot be grouped, named or acted on in the report.
                if (string.IsNullOrEmpty(match.PolicyId) && string.IsNullOrEmpty(match.PolicyName)
                    && string.IsNullOrEmpty(match.RuleId) && string.IsNullOrEmpty(match.RuleName))
                {
                    _logger?.LogDebug(
                        $"DLP: skipping a policy match on Copilot event '{baseOfficeEvent.Id}' that names neither a policy nor a rule.");
                    continue;
                }

                _stagingWriter.StageCopilotDlp(new CopilotDlpLogTempEntity
                {
                    EventId = baseOfficeEvent.Id,
                    PolicyId = match.PolicyId,
                    PolicyName = match.PolicyName,
                    RuleId = match.RuleId,
                    RuleName = match.RuleName,
                    Severity = match.Severity,
                    RuleMode = match.RuleMode,
                    ActionName = match.Action,
                    ResourceName = match.ResourceName,
                    ResourceType = match.ResourceType,
                    SensitivityLabelId = match.SensitivityLabelId,
                    IsBlocked = match.IsBlocked,
                });

                _totalCopilotMatchCount++;
                if (match.IsBlocked)
                {
                    _totalCopilotBlockedCount++;
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Stages every rule match on a record from the standalone DLP.All feed.
        /// </summary>
        public Task SaveSingleDlpEventToSqlStaging(DlpAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger?.LogWarning("DlpAuditEventManager received a null DLP auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            var policies = auditRecord.PolicyDetails?.Where(p => p != null).ToList();
            if (policies == null || policies.Count == 0)
            {
                // A DLP record with no policy detail carries nothing this report can use. The base
                // audit_events row is still imported by the caller, so nothing is lost.
                _logger?.LogDebug($"DLP: event '{baseOfficeEvent.Id}' ({auditRecord.Operation}) carries no PolicyDetails - no rule-match rows staged.");
                return Task.CompletedTask;
            }

            foreach (var policy in policies)
            {
                var rules = policy.Rules?.Where(r => r != null).ToList();

                if (rules == null || rules.Count == 0)
                {
                    StageRuleMatch(baseOfficeEvent, policy, null, isBlocked: false);
                    continue;
                }

                foreach (var rule in rules)
                {
                    StageRuleMatch(baseOfficeEvent, policy, rule, CopilotDlpRules.RuleBlocked(rule));
                }
            }

            return Task.CompletedTask;
        }

        private void StageRuleMatch(CommonAuditEvent baseOfficeEvent, AccessedResourcePolicyDetail policy, AccessedResourcePolicyRule rule, bool isBlocked)
        {
            if (string.IsNullOrEmpty(policy.PolicyId) && string.IsNullOrEmpty(policy.PolicyName)
                && string.IsNullOrEmpty(rule?.RuleId) && string.IsNullOrEmpty(rule?.RuleName))
            {
                return;
            }

            _stagingWriter.StageDlpRuleMatch(new DlpRuleMatchLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                PolicyId = policy.PolicyId,
                PolicyName = policy.PolicyName,
                RuleId = rule?.RuleId,
                RuleName = rule?.RuleName,
                Severity = rule?.Severity,
                RuleMode = rule?.RuleMode,
                ActionName = rule == null ? null : CopilotDlpRules.PrimaryAction(rule),
                IsBlocked = isBlocked,
            });

            _totalRuleMatchCount++;
        }

        /// <summary>
        /// Flush the staging tables and run the merge scripts.
        /// </summary>
        /// <remarks>
        /// Must run AFTER the Copilot merge: <c>copilot_dlp_events.copilot_chat_id</c> is a foreign key
        /// to <c>copilot_chats</c>, whose rows that merge creates. <c>SaveSession.CommitAllChanges</c>
        /// orders the two accordingly.
        /// </remarks>
        public async Task CommitAllChanges()
        {
            if (_totalCopilotMatchCount > 0 || _totalRuleMatchCount > 0 || _deferredEvaluationCount > 0)
            {
                _logger?.LogInformation(
                    $"DLP commit: {_totalCopilotMatchCount} Copilot policy match(es) of which {_totalCopilotBlockedCount} blocked, " +
                    $"{_totalRuleMatchCount} DLP.All rule match(es), " +
                    $"{_deferredEvaluationCount} interaction(s) with a deferred (unknown) DLP evaluation.");
            }

            await _stagingWriter.CommitAllChanges();

            _totalCopilotMatchCount = 0;
            _totalCopilotBlockedCount = 0;
            _totalRuleMatchCount = 0;
            _deferredEvaluationCount = 0;
        }

        public void Dispose()
        {
        }
    }
}
