using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    /// <summary>
    /// How a policy affected Copilot's access to one resource.
    /// </summary>
    public enum CopilotDlpOutcome
    {
        /// <summary>No policy detail on the resource - nothing to report.</summary>
        NotPolicyRelated = 0,

        /// <summary>
        /// A policy matched but did not stop Copilot using the resource - an "Audit only" rule, or a
        /// rule whose only actions are notify/report. Worth reporting (it is what a policy would block
        /// if it were switched to Enforce) but it is NOT a block.
        /// </summary>
        PolicyAudited = 1,

        /// <summary>A policy actually stopped Copilot using the resource.</summary>
        PolicyBlocked = 2,
    }

    /// <summary>
    /// Decides whether a Microsoft Purview DLP policy actually BLOCKED Copilot from using a resource,
    /// or merely matched and audited it.
    ///
    /// Pure and dependency-free so the classification - which is the entire meaning of the DLP report -
    /// can be asserted without a database, an HTTP client or an audit feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this lives here at all: DLP policies scoped to the "Microsoft 365 Copilot and Copilot Chat"
    /// location do not emit standalone <c>DlpRuleMatch</c> records on the <c>DLP.All</c> feed. Microsoft
    /// documents the DLP feed's workloads as Exchange Online, Endpoint and SharePoint/OneDrive only, and
    /// a DLP record carries the human <c>UserId</c> with no agent identity. The Copilot block signal is
    /// instead embedded in the <c>CopilotInteraction</c> record's
    /// <c>AccessedResources[].Status</c> / <c>AccessedResources[].PolicyDetails</c>, alongside
    /// <c>AgentId</c> / <c>AgentName</c> - so this is the ONLY place a DLP block can be attributed to a
    /// specific Copilot agent.
    /// https://learn.microsoft.com/en-us/purview/audit-copilot
    /// </para>
    /// <para>
    /// The classification is deliberately conservative in both directions. A bare
    /// <c>Status = "failure"</c> with no policy detail is an access failure of some other kind (a deleted
    /// file, a permissions problem) and is NOT counted as DLP. A rule that lists <c>BlockAccess</c> while
    /// running in <c>RuleMode = "Audit only"</c> blocked nothing and is counted as audited, not blocked.
    /// Over-counting here would tell an admin their users are being blocked when they are not.
    /// </para>
    /// </remarks>
    public static class CopilotDlpRules
    {
        /// <summary>
        /// <c>AccessedResources[].Status</c> value meaning Copilot could not use the resource.
        /// </summary>
        public const string STATUS_FAILURE = "failure";

        /// <summary>
        /// <c>RuleMode</c> value meaning the rule's actions were actually applied. The other documented
        /// values ("Audit only", "Audit with Notify") report without enforcing.
        /// </summary>
        public const string RULE_MODE_ENFORCE = "Enforce";

        /// <summary>
        /// Rule actions that stop the user getting the content. The Management Activity API schema does
        /// not enumerate the permitted <c>Actions</c> values, so this list is deliberately short and
        /// literal: an unrecognised action is treated as non-blocking, which under-reports rather than
        /// inventing blocks that did not happen.
        /// </summary>
        private static readonly HashSet<string> BlockingActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BlockAccess",
            "Block",
        };

        /// <summary>
        /// Ranking used to pick the single most meaningful action to store against a match when a rule
        /// lists several. Highest wins.
        /// </summary>
        private static readonly Dictionary<string, int> ActionRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "BlockAccess", 100 },
            { "Block", 100 },
            { "EncryptContent", 50 },
            { "GenerateIncidentReport", 20 },
            { "NotifyUser", 10 },
        };

        /// <summary>True when the resource carries any policy detail at all.</summary>
        public static bool HasPolicyDetail(AccessedResource resource)
        {
            return resource?.PolicyDetails != null && resource.PolicyDetails.Any(p => p != null);
        }

        /// <summary>
        /// True when Copilot's access to the resource failed. Note this is NOT sufficient on its own to
        /// call something a DLP block - see <see cref="Classify"/>.
        /// </summary>
        public static bool IsAccessFailure(AccessedResource resource)
        {
            return string.Equals(resource?.Status, STATUS_FAILURE, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the rule was enforcing rather than auditing.</summary>
        public static bool IsEnforcing(AccessedResourcePolicyRule rule)
        {
            return string.Equals(rule?.RuleMode, RULE_MODE_ENFORCE, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when any of the rule's actions denies the content to the user.</summary>
        public static bool HasBlockingAction(AccessedResourcePolicyRule rule)
        {
            return rule?.Actions != null
                && rule.Actions.Any(a => !string.IsNullOrWhiteSpace(a) && BlockingActions.Contains(a.Trim()));
        }

        /// <summary>
        /// True when this single rule blocked the resource: it was enforcing AND took a blocking action.
        /// </summary>
        public static bool RuleBlocked(AccessedResourcePolicyRule rule)
        {
            return IsEnforcing(rule) && HasBlockingAction(rule);
        }

        /// <summary>
        /// How the resource was affected by policy.
        /// </summary>
        /// <remarks>
        /// A resource counts as blocked when it carries policy detail AND either the service told us the
        /// access failed, or one of the matched rules was enforcing a blocking action. The two are
        /// deliberately OR'd: <c>Status</c> is the service's own verdict and is authoritative when
        /// present, while the rule inspection covers records that carry policy detail without a status.
        /// </remarks>
        public static CopilotDlpOutcome Classify(AccessedResource resource)
        {
            if (!HasPolicyDetail(resource))
            {
                return CopilotDlpOutcome.NotPolicyRelated;
            }

            if (IsAccessFailure(resource))
            {
                return CopilotDlpOutcome.PolicyBlocked;
            }

            var blockedByRule = resource.PolicyDetails
                .Where(p => p?.Rules != null)
                .SelectMany(p => p.Rules)
                .Any(RuleBlocked);

            return blockedByRule ? CopilotDlpOutcome.PolicyBlocked : CopilotDlpOutcome.PolicyAudited;
        }

        /// <summary>
        /// The most meaningful action of a rule, for storing one action per match. Returns null when the
        /// rule lists no actions.
        /// </summary>
        public static string PrimaryAction(AccessedResourcePolicyRule rule)
        {
            if (rule?.Actions == null)
            {
                return null;
            }

            var named = rule.Actions
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToList();

            if (named.Count == 0)
            {
                return null;
            }

            // Rank-then-name so the choice is deterministic for equally-ranked (including unknown) actions
            // rather than depending on the order Microsoft happened to serialise them in.
            return named
                .OrderByDescending(a => ActionRank.TryGetValue(a, out var rank) ? rank : 0)
                .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        /// <summary>
        /// Flattens one Copilot interaction into the policy matches worth persisting: one record per
        /// (accessed resource x policy x rule). A policy carrying no rule detail still yields one record
        /// so the policy is not lost.
        /// </summary>
        /// <remarks>
        /// Resources with no policy detail produce nothing, which is what keeps this table sparse: the
        /// overwhelming majority of accessed resources are not policy-affected, and they are already
        /// recorded in full in <c>copilot_event_accessed_resources</c>.
        /// </remarks>
        public static List<CopilotDlpMatch> ExtractMatches(CopilotAuditLogContent auditRecord)
        {
            var results = new List<CopilotDlpMatch>();
            var resources = auditRecord?.CopilotEventData?.AccessedResources;
            if (resources == null)
            {
                return results;
            }

            foreach (var resource in resources)
            {
                if (!HasPolicyDetail(resource))
                {
                    continue;
                }

                var outcome = Classify(resource);

                foreach (var policy in resource.PolicyDetails.Where(p => p != null))
                {
                    var rules = policy.Rules?.Where(r => r != null).ToList();

                    if (rules == null || rules.Count == 0)
                    {
                        results.Add(new CopilotDlpMatch
                        {
                            ResourceName = resource.Name,
                            ResourceType = resource.Type,
                            SensitivityLabelId = resource.SensitivityLabelId,
                            PolicyId = policy.PolicyId,
                            PolicyName = policy.PolicyName,
                            IsBlocked = outcome == CopilotDlpOutcome.PolicyBlocked,
                        });
                        continue;
                    }

                    foreach (var rule in rules)
                    {
                        results.Add(new CopilotDlpMatch
                        {
                            ResourceName = resource.Name,
                            ResourceType = resource.Type,
                            SensitivityLabelId = resource.SensitivityLabelId,
                            PolicyId = policy.PolicyId,
                            PolicyName = policy.PolicyName,
                            RuleId = rule.RuleId,
                            RuleName = rule.RuleName,
                            Severity = rule.Severity,
                            RuleMode = rule.RuleMode,
                            Action = PrimaryAction(rule),

                            // Per-rule verdict, so a policy with one enforcing and one auditing rule
                            // reports each rule honestly. The resource-level Status is authoritative
                            // when it says the access failed, because that is the service telling us
                            // the user did not get the content whatever the rule metadata says.
                            IsBlocked = IsAccessFailure(resource) || RuleBlocked(rule),
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Names of the DLP evaluation stages encoded in <c>CopilotEventData.DLPEvaluationDeferred</c>.
        /// Index = bit position.
        /// </summary>
        private static readonly string[] DeferredStageNames = { "Prompt", "Response", "Grounding", "WebGrounding" };

        /// <summary>
        /// Decodes the <c>DLPEvaluationDeferred</c> bitmask into stage names (1 = Prompt, 2 = Response,
        /// 4 = Grounding, 8 = WebGrounding). Returns an empty list for null / zero.
        /// </summary>
        /// <remarks>
        /// A deferred evaluation is neither a block nor an all-clear: DLP could not be evaluated for that
        /// stage, so the interaction's DLP outcome is unknown. It is surfaced as a coverage caveat rather
        /// than mixed into the block counts.
        /// </remarks>
        public static IReadOnlyList<string> DecodeDeferredStages(int? bitmask)
        {
            var stages = new List<string>();
            if (!bitmask.HasValue || bitmask.Value == 0)
            {
                return stages;
            }

            for (var bit = 0; bit < DeferredStageNames.Length; bit++)
            {
                if ((bitmask.Value & (1 << bit)) != 0)
                {
                    stages.Add(DeferredStageNames[bit]);
                }
            }

            return stages;
        }
    }

    /// <summary>
    /// One (accessed resource x policy x rule) policy match extracted from a Copilot interaction, ready
    /// to be staged. Flat and primitive-only so it can be asserted in tests without a database.
    /// </summary>
    public class CopilotDlpMatch
    {
        public string ResourceName { get; set; }
        public string ResourceType { get; set; }
        public string SensitivityLabelId { get; set; }
        public string PolicyId { get; set; }
        public string PolicyName { get; set; }
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public string Severity { get; set; }
        public string RuleMode { get; set; }
        public string Action { get; set; }

        /// <summary>True when the user did not get the content because of this rule.</summary>
        public bool IsBlocked { get; set; }
    }
}
