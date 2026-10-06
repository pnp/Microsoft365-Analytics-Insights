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
        /// <summary>
        /// No policy detail on the resource - nothing to report. This includes a policy evaluation that
        /// names no policy, such as <c>PolicyOutcomes: ["None"]</c> (issue #659), whatever the resource's
        /// <c>Status</c>.
        /// </summary>
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
    /// <para>
    /// <c>PolicyDetails</c> also arrives as undocumented policy-<i>evaluation</i> entries
    /// (<c>PolicyType</c> / <c>PolicyOutcomes</c> / <c>AuditLog</c>, issue #659), which say that a policy
    /// engine looked at the resource, not that a policy matched. Every one seen so far had
    /// <c>PolicyOutcomes: ["None"]</c> and no policy named in its <c>AuditLog</c>. Such an entry is reported
    /// only through the documented detail it carries (<c>PolicyId</c> / <c>PolicyName</c> / <c>Rules</c>,
    /// on the entry or in its decoded <c>AuditLog.PolicyDetails</c>), and only an enforcing rule with a
    /// blocking action makes that detail a block: the resource's <c>Status</c> is not taken as the verdict
    /// for it, and no <c>PolicyOutcomes</c> value counts as a block on its own, because the meaning of
    /// those values is not documented. An entry with no such detail is not policy-related, whatever its
    /// outcomes and whatever the resource's <c>Status</c>.
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

        /// <summary>
        /// True when the resource carries policy detail the DLP report can use: a <c>PolicyDetails</c>
        /// entry in the documented shape, or documented detail inside a policy-evaluation entry. An
        /// evaluation entry that names no policy (e.g. <c>PolicyOutcomes: ["None"]</c> with an empty
        /// <c>AuditLog</c>) is not policy detail.
        /// </summary>
        public static bool HasPolicyDetail(AccessedResource resource)
        {
            return ReportablePolicies(resource).Count > 0;
        }

        /// <summary>
        /// True for an entry of the undocumented policy-evaluation shape (issue #659), which carries
        /// <c>PolicyType</c>, <c>PolicyOutcomes</c> or <c>AuditLog</c>. The documented shape carries none of them.
        /// </summary>
        public static bool IsPolicyEvaluation(AccessedResourcePolicyDetail entry)
        {
            return entry != null
                && (entry.PolicyType != null || entry.PolicyOutcomes != null || entry.AuditLog != null);
        }

        /// <summary>
        /// True when the entry names a policy or carries rules - the documented <c>PolicyId</c> /
        /// <c>PolicyName</c> / <c>Rules</c> detail.
        /// </summary>
        public static bool HasDocumentedDetail(AccessedResourcePolicyDetail entry)
        {
            return entry != null
                && (!string.IsNullOrWhiteSpace(entry.PolicyId)
                    || !string.IsNullOrWhiteSpace(entry.PolicyName)
                    || (entry.Rules != null && entry.Rules.Any(r => r != null)));
        }

        /// <summary>
        /// A policy the DLP report can name, and whether the resource's <c>Status</c> may decide its verdict.
        /// </summary>
        private sealed class ReportablePolicy
        {
            public AccessedResourcePolicyDetail Policy { get; set; }

            /// <summary>
            /// True for the documented shape, where a failed access with policy detail is a block. False for
            /// detail found through a policy-evaluation entry, where only an enforcing blocking rule is.
            /// </summary>
            public bool StatusDecides { get; set; }
        }

        /// <summary>
        /// The policies on a resource that the DLP report can use. Documented entries are taken as they are,
        /// exactly as before #659 (including one that names nothing, which the manager later declines to
        /// store). A policy-evaluation entry contributes only its documented detail: its own
        /// <c>PolicyId</c> / <c>PolicyName</c> / <c>Rules</c> if it has any, and the documented elements of
        /// its decoded <c>AuditLog.PolicyDetails</c>.
        /// </summary>
        private static List<ReportablePolicy> ReportablePolicies(AccessedResource resource)
        {
            var result = new List<ReportablePolicy>();
            if (resource?.PolicyDetails == null)
            {
                return result;
            }

            foreach (var entry in resource.PolicyDetails)
            {
                if (entry == null)
                {
                    continue;
                }

                if (!IsPolicyEvaluation(entry))
                {
                    result.Add(new ReportablePolicy { Policy = entry, StatusDecides = true });
                    continue;
                }

                if (HasDocumentedDetail(entry))
                {
                    result.Add(new ReportablePolicy { Policy = entry, StatusDecides = false });
                }

                var logged = entry.AuditLog?.PolicyDetails;
                if (logged == null)
                {
                    continue;
                }

                foreach (var policy in logged)
                {
                    if (HasDocumentedDetail(policy))
                    {
                        result.Add(new ReportablePolicy { Policy = policy, StatusDecides = false });
                    }
                }
            }

            return result;
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
        /// <c>Status</c> is only taken as the verdict for documented entries; detail found through a
        /// policy-evaluation entry is a block only when one of its rules is (see the class remarks).
        /// </remarks>
        public static CopilotDlpOutcome Classify(AccessedResource resource)
        {
            return Classify(resource, ReportablePolicies(resource));
        }

        private static CopilotDlpOutcome Classify(AccessedResource resource, List<ReportablePolicy> policies)
        {
            if (policies.Count == 0)
            {
                return CopilotDlpOutcome.NotPolicyRelated;
            }

            if (IsAccessFailure(resource) && policies.Any(p => p.StatusDecides))
            {
                return CopilotDlpOutcome.PolicyBlocked;
            }

            var blockedByRule = policies
                .Where(p => p.Policy.Rules != null)
                .SelectMany(p => p.Policy.Rules)
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
                var policies = ReportablePolicies(resource);
                if (policies.Count == 0)
                {
                    continue;
                }

                var outcome = Classify(resource, policies);

                foreach (var reportable in policies)
                {
                    var policy = reportable.Policy;
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
                            // the user did not get the content whatever the rule metadata says - for a
                            // documented entry. Detail from a policy-evaluation entry is judged by its
                            // rule alone (see the class remarks).
                            IsBlocked = (reportable.StatusDecides && IsAccessFailure(resource)) || RuleBlocked(rule),
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
