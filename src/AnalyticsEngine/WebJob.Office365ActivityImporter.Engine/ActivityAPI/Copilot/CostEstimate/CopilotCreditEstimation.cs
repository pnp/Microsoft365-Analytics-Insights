using Common.Entities.Copilot;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{

    /// <summary>
    /// How <see cref="CopilotCreditEstimation"/> reached its tenant-graph grounding decision for a
    /// conversation. Stored as a string on the estimate rather than an enum so an older reader of the
    /// persisted JSON cannot silently coerce a value it does not know into the first enum member.
    /// </summary>
    public static class CopilotTenantGroundingBasis
    {
        /// <summary>Not a billable custom agent, or there was nothing to analyse - no decision was made.</summary>
        public const string NotAssessed = "NotAssessed";

        /// <summary>The conversation listed no accessed resources at all.</summary>
        public const string NoResources = "NoResources";

        /// <summary>
        /// Every accessed resource was positively identified as grounding from outside the tenant.
        /// This is the only case that genuinely means "web search only".
        /// </summary>
        public const string ExternalOnly = "ExternalOnly";

        /// <summary>At least one resource carried positive evidence of being a tenant resource.</summary>
        public const string TenantResource = "TenantResource";

        /// <summary>
        /// No resource carried positive tenant evidence, but at least one could not be classified
        /// either way, so the charge was applied on the assumption that it was tenant content. See
        /// <see cref="CopilotCreditEstimation.UnclassifiedResources"/>.
        /// </summary>
        public const string UnclassifiedResource = "UnclassifiedResource";
    }

    /// <summary>
    /// Detailed billing report for a Copilot audit event.
    /// Calculates Copilot Credits consumed based on Microsoft Copilot Studio billing policies.
    /// 
    /// Note: This implementation provides estimates based on available audit log data.
    /// Only Messages, AccessedResources, and ModelTransparencyDetails are available in audit logs.
    /// 
    /// Agent Actions, AI Tool Usages, and Flow Actions are not explicitly listed in audit logs,
    /// but some can be inferred (e.g., deep reasoning from DEEP_LEO model).
    /// 
    /// Reference: https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management
    /// </summary>
    public class CopilotCreditEstimation
    {
        #region Billing Constants

        /// <summary>
        /// Version of the cost estimation model. Stamped into every stored estimate, so an estimate
        /// produced by an older importer can be told apart from a current one.
        ///
        /// History:
        /// 1.0.0.1 - initial model.
        /// 1.1.0.0 - tenant-graph grounding is decided from positive evidence of a tenant resource
        ///           rather than from an allowlist of AccessedResources[].Type values, and a resource
        ///           this product cannot classify no longer silently produces the cheaper answer
        ///           (#469). Estimates for the same conversation can legitimately differ across this
        ///           boundary.
        /// </summary>
        private const string COST_ESTIMATION_VERSION = "1.1.0.0";

        // Based on Microsoft Copilot Studio billing documentation (as of March 2025)
        // https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management#copilot-credits-and-events-scenarios

        /// <summary>
        /// Generative answers use AI models (GPT) to create dynamic responses. Cost: 2 credits per answer.
        /// </summary>
        private const int GENERATIVE_ANSWER_CREDITS = 2;

        /// <summary>
        /// Tenant graph grounding provides RAG over Microsoft Graph data (SharePoint, OneDrive, Email, Teams).
        /// Cost: 10 credits per grounded message (additive with generative answer cost).
        /// This is an optional capability that can be enabled per agent.
        /// </summary>
        private const int TENANT_GRAPH_GROUNDING_CREDITS = 10;

        /// <summary>
        /// Agent actions (triggers, deep reasoning, topic transitions, tool invocations) cost 5 credits each.
        /// Deep reasoning can be detected from DEEP_LEO model in ModelTransparencyDetails.
        /// </summary>
        private const int AGENT_ACTION_CREDITS = 5;

        #endregion

        #region Properties

        /// <summary>
        /// The version of the cost estimation model used to generate this report.
        /// </summary>
        [JsonProperty("CostModelVersion")]
        public string CostModelVersion { get; set; }

        [JsonProperty("GenerativeAnswers")]
        public int GenerativeAnswers { get; set; }

        [JsonProperty("TenantGraphGroundedAnswers")]
        public int TenantGraphGroundedAnswers { get; set; }

        /// <summary>
        /// Why the tenant-graph grounding charge was or was not applied - one of the
        /// <see cref="CopilotTenantGroundingBasis"/> constants.
        ///
        /// Recorded because the decision is an inference, not something the audit log states. An
        /// estimate that rests on a SharePoint URL is worth more than one that rests on a resource
        /// type nobody recognises, and without this the two are indistinguishable after the fact.
        /// </summary>
        [JsonProperty("TenantGraphGroundingBasis")]
        public string TenantGraphGroundingBasis { get; set; }

        /// <summary>
        /// How many accessed resources carried no evidence either way - neither positive evidence of a
        /// tenant resource nor a recognised marker for grounding from outside the tenant.
        ///
        /// These are charged as tenant grounding, because Microsoft documents AccessedResources as the
        /// resources Copilot accessed to answer and gives no way to tell an unrecognised entry apart.
        /// The count exists so that assumption is visible rather than buried.
        /// </summary>
        [JsonProperty("UnclassifiedResources")]
        public int UnclassifiedResources { get; set; }

        [JsonProperty("DeepReasoningActions")]
        public int DeepReasoningActions { get; set; }

        [JsonProperty("TotalCredits")]
        public int TotalCredits { get; set; }

        /// <summary>
        /// Breakdown of accessed resource types (for reference only, does not affect billing).
        /// </summary>
        [JsonProperty("ResourceTypeBreakdown")]
        public Dictionary<string, int> ResourceTypeBreakdown { get; set; }

        /// <summary>
        /// Detailed breakdown showing how many credits were consumed by each billing category.
        /// </summary>
        [JsonProperty("CreditBreakdown")]
        public Dictionary<string, int> CreditBreakdown { get; set; }

        /// <summary>
        /// List of AI models detected in the conversation (e.g., DEEP_LEO).
        /// </summary>
        [JsonProperty("ModelsUsed")]
        public List<string> ModelsUsed { get; set; }

        #endregion

        public static CopilotCreditEstimation NoCost = new CopilotCreditEstimation
        {
            CostModelVersion = COST_ESTIMATION_VERSION,
            TotalCredits = 0,
            TenantGraphGroundingBasis = CopilotTenantGroundingBasis.NotAssessed,
            ResourceTypeBreakdown = new Dictionary<string, int>(),
            CreditBreakdown = new Dictionary<string, int>(),
            ModelsUsed = new List<string>()
        };

        /// <summary>
        /// Analyzes a Copilot audit event JSON and calculates the total Copilot Credits consumed.
        /// This is an overload that deserializes the JSON string before analysis.
        /// See <see cref="Analyze(CopilotAuditEvent, bool)"/> for detailed billing logic.
        /// </summary>
        /// <param name="json">JSON string containing the Copilot audit event data</param>
        /// <param name="isCustomAgent">True if this is a custom Copilot Studio agent (billable), false for standard M365 Copilot (not billable via credits)</param>
        /// <returns>CreditReport with detailed billing breakdown</returns>
        public static CopilotCreditEstimation Analyze(string json, bool isCustomAgent)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CopilotCreditEstimation
                {
                    CostModelVersion = COST_ESTIMATION_VERSION,
                    TotalCredits = 0,
                    TenantGraphGroundingBasis = CopilotTenantGroundingBasis.NotAssessed,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>(),
                    ModelsUsed = new List<string>()
                };
            }

            var auditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(json);
            return Analyze(auditEvent, isCustomAgent);
        }

        /// <summary>
        /// Analyzes a Copilot audit event object and calculates the total Copilot Credits consumed.
        /// 
        /// Billing Logic (based on Microsoft documentation, effective March 25, 2025):
        /// - Only custom agents (Copilot Studio agents) incur Copilot Credit charges.
        /// - Standard Microsoft 365 Copilot agents (Word, Excel, Teams, etc.) are not charged via Copilot Credits.
        /// 
        /// For custom agents:
        /// 1. Generative Answers: 2 credits per response message
        /// 2. Tenant Graph Grounding: +10 credits per message (additive with generative)
        /// 3. Deep Reasoning (DEEP_LEO model): 5 credits per agent action
        /// 
        /// Example from documentation ("Sales performance agent"):
        /// - Scenario: 4 generative answers, all grounded in the tenant graph (custom agent).
        /// - Calculation: 4 messages * (2 for generative answer + 10 for tenant graph) = 48 credits.
        /// - This model correctly calculates this as 4 * (GENERATIVE_ANSWER_CREDITS + TENANT_GRAPH_GROUNDING_CREDITS).
        /// - Reference: https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management#sales-performance-agent
        /// 
        /// Formula per message with tenant graph grounding (custom agent only):
        ///   Total = 2 (generative) + 10 (tenant graph) = 12 credits
        /// 
        /// Formula per message with tenant graph + deep reasoning (custom agent only):
        ///   Total = 2 (generative) + 10 (tenant graph) + 5 (deep reasoning) = 17 credits
        /// 
        /// Important: Deep reasoning is billed as an Agent Action (5 credits) when detected
        /// via the DEEP_LEO model in ModelTransparencyDetails. This is separate from and
        /// additive to message-level costs.
        /// 
        /// Limitations:
        /// - AI Tool Usages (premium tier billing) may be underestimated.
        /// - Flow Actions are NOT included in audit logs and cannot be calculated.
        /// - Classic vs. Generative answer types cannot be fully distinguished; all responses are billed as Generative.
        /// </summary>
        /// <param name="auditEvent">The Copilot audit event object to analyze</param>
        /// <param name="isCustomAgent">True if this is a custom Copilot Studio agent (billable), false for standard M365 Copilot (not billable via credits)</param>
        /// <returns>CreditReport with detailed billing breakdown</returns>
        public static CopilotCreditEstimation Analyze(CopilotAuditEvent auditEvent, bool isCustomAgent)
        {
            if (auditEvent == null)
            {
                return new CopilotCreditEstimation
                {
                    CostModelVersion = COST_ESTIMATION_VERSION,
                    TotalCredits = 0,
                    TenantGraphGroundingBasis = CopilotTenantGroundingBasis.NotAssessed,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>(),
                    ModelsUsed = new List<string>()
                };
            }

            var report = new CopilotCreditEstimation
            {
                CostModelVersion = COST_ESTIMATION_VERSION,
                TenantGraphGroundingBasis = CopilotTenantGroundingBasis.NotAssessed,
                ResourceTypeBreakdown = new Dictionary<string, int>(),
                CreditBreakdown = new Dictionary<string, int>(),
                ModelsUsed = new List<string>()
            };

            // Only custom agents incur Copilot Credit charges
            // Standard Microsoft 365 Copilot (Word, Excel, Teams, etc.) is not charged via Copilot Credits
            if (!isCustomAgent)
            {
                // Build resource breakdown for analytics but don't charge any credits
                report.ResourceTypeBreakdown = BuildResourceTypeBreakdown(auditEvent.AccessedResources);

                // Track models used for analytics even if not charging
                if (HasDeepReasoning(auditEvent.ModelTransparencyDetails))
                {
                    report.ModelsUsed.Add("DEEP_LEO");
                }

                report.TotalCredits = 0;
                return report;
            }

            int totalCredits = 0;

            // STEP 1: Detect tenant graph usage
            // Per Microsoft docs: "tenant graph grounding for messages" costs 10 credits
            // PLUS the base generative answer cost of 2 credits = 12 credits total per message.
            // See AssessTenantGrounding for how the decision is reached and what it assumes.
            var grounding = AssessTenantGrounding(auditEvent.AccessedResources);
            bool hasTenantGraphResources = grounding.IsTenantGrounded;
            report.TenantGraphGroundingBasis = grounding.Basis;
            report.UnclassifiedResources = grounding.UnclassifiedResources;

            // STEP 2: Detect deep reasoning usage
            // Deep reasoning is indicated by the DEEP_LEO model in ModelTransparencyDetails.
            // Per Microsoft docs (March 25, 2025): "deep reasoning is available in AI prompts and
            // agent flows. Charges for deep reasoning in AI prompts use the Text and generative 
            // AI tools (premium) rate, and charges for agent flows use the Flow actions rate."
            // 
            // For audit log purposes, we bill as an Agent Action: 5 credits per conversation
            // that used DEEP_LEO, as this represents the deep reasoning invocation.
            bool hasDeepReasoning = HasDeepReasoning(auditEvent.ModelTransparencyDetails);
            if (hasDeepReasoning)
            {
                report.ModelsUsed.Add("DEEP_LEO");
            }

            // STEP 3: Count and bill response messages
            // Only non-prompt messages (isPrompt=false) are billable.
            // 
            // Billing formula per message:
            // - Generative Answer: 2 credits (always for AI-generated responses)
            // - Tenant Graph Grounding: +10 credits (if tenant resources accessed)
            // - Total per message: 2 or 12 credits
            if (auditEvent.Messages != null)
            {
                foreach (var message in auditEvent.Messages.Where(m => !m.IsPrompt))
                {
                    // Every AI-generated response is a generative answer (2 credits)
                    report.GenerativeAnswers++;
                    totalCredits += GENERATIVE_ANSWER_CREDITS;
                    AddToBreakdown(report.CreditBreakdown, "Generative Answers", GENERATIVE_ANSWER_CREDITS);

                    // If tenant resources were accessed, add tenant graph grounding cost (10 credits)
                    if (hasTenantGraphResources)
                    {
                        report.TenantGraphGroundedAnswers++;
                        totalCredits += TENANT_GRAPH_GROUNDING_CREDITS;
                        AddToBreakdown(report.CreditBreakdown, "Tenant Graph Grounding", TENANT_GRAPH_GROUNDING_CREDITS);
                    }
                }
            }

            // STEP 4: Bill deep reasoning as an Agent Action
            // Deep reasoning (DEEP_LEO) is billed once per conversation as an Agent Action.
            // This represents the invocation of the advanced reasoning capability.
            // Cost: 5 credits per agent action
            if (hasDeepReasoning)
            {
                report.DeepReasoningActions = 1;  // One deep reasoning action per conversation
                totalCredits += AGENT_ACTION_CREDITS;
                AddToBreakdown(report.CreditBreakdown, "Agent Actions (Deep Reasoning)", AGENT_ACTION_CREDITS);
            }

            // STEP 5: Build resource breakdown (for reference/analytics only)
            // This shows what types of resources were accessed but does NOT affect billing.
            // Resources are billed at the message level, not per resource.
            report.ResourceTypeBreakdown = BuildResourceTypeBreakdown(auditEvent.AccessedResources);

            report.TotalCredits = totalCredits;

            return report;
        }

        /// <summary>
        /// Counts accessed resources by their raw <c>Type</c> value, for reference only.
        ///
        /// A resource with no type is counted as <c>(unknown)</c> - the same label the Copilot
        /// Adoption reporting uses - and deliberately NOT as a web page. Microsoft publishes no list
        /// of possible values for that field and documents it as carrying either a file extension or
        /// a description of a non-SharePoint resource, so a missing one says nothing about where the
        /// resource came from.
        /// </summary>
        private static Dictionary<string, int> BuildResourceTypeBreakdown(List<AccessedResource> accessedResources)
        {
            if (accessedResources == null) return new Dictionary<string, int>();

            return accessedResources
                .GroupBy(r => string.IsNullOrWhiteSpace(r?.Type) ? CopilotAccessedResourceTaxonomy.UnknownTypeLabel : r.Type)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Helper method to safely add credits to a specific category in the breakdown.
        /// Handles the case where the key doesn't exist in the dictionary.
        /// </summary>
        /// <param name="breakdown">The credit breakdown dictionary</param>
        /// <param name="category">The billing category name</param>
        /// <param name="creditsToAdd">The number of credits to add</param>
        private static void AddToBreakdown(Dictionary<string, int> breakdown, string category, int creditsToAdd)
        {
            if (breakdown.ContainsKey(category))
            {
                breakdown[category] += creditsToAdd;
            }
            else
            {
                breakdown[category] = creditsToAdd;
            }
        }

        /// <summary>
        /// The outcome of the tenant-graph grounding decision for one conversation.
        /// </summary>
        private struct TenantGroundingAssessment
        {
            public bool IsTenantGrounded { get; set; }
            public int UnclassifiedResources { get; set; }
            public string Basis { get; set; }
        }

        /// <summary>
        /// Decides whether a conversation should carry the tenant-graph grounding charge, and records
        /// how that decision was reached.
        ///
        /// The question this answers is "did this conversation ground on tenant data?". The audit log
        /// does not state that, so it has to be inferred - but it is inferred from POSITIVE EVIDENCE
        /// that a resource belongs to the tenant, not from a list of resource-type names:
        ///
        /// 1. A SharePoint list-item id. Microsoft documents ListItemUniqueId as the "unique identifier
        ///    for a SharePoint item", so a real one only exists for an item in the tenant's own
        ///    SharePoint or OneDrive.
        /// 2. A sensitivity label id. Labels are applied by the tenant's own Purview policy; content
        ///    from the public web does not carry one.
        /// 3. A SiteUrl on a Microsoft 365 tenant-content host, across the worldwide and sovereign
        ///    clouds - see CopilotAccessedResourceTaxonomy.IsTenantContentUrl.
        /// 4. Failing all of those, a Type value that names tenant content.
        ///
        /// Why not an allowlist of Type values: Microsoft publishes no enumeration for that field
        /// (https://learn.microsoft.com/en-us/purview/audit-copilot#common-properties-in-copilot-audit-logs
        /// describes it as carrying "values like the filetype extension ... or ... the type of resource
        /// (for non-SharePoint resources)"), and one of the commonest values in practice - CITATION -
        /// describes how the resource was used rather than what it is. An allowlist therefore silently
        /// misclassifies whatever Microsoft adds next, which is exactly what happened in #469.
        ///
        /// A resource is only dismissed on positive evidence that it came from outside the tenant: a
        /// type that marks external grounding, or a SiteUrl that resolves to a host which is not the
        /// tenant's. That is a real finding and is quite different from a resource that says nothing.
        ///
        /// An unclassifiable resource - one with neither kind of evidence - is charged rather than
        /// waved through. Microsoft documents AccessedResources as the resources Copilot accessed in
        /// order to answer, so an entry with nothing to place it outside the tenant is more likely
        /// tenant content than not, and the previous behaviour of quietly choosing the cheaper answer
        /// understated the estimate by 10 credits per response message. The count is reported so the
        /// assumption is visible: see <see cref="UnclassifiedResources"/> and
        /// <see cref="TenantGraphGroundingBasis"/>.
        ///
        /// Deliberately NOT used: AISystemPlugin.Id == "BingWebSearch". Microsoft documents that as
        /// the way to tell that Copilot referenced the public web, but their own schema example shows
        /// it alongside an accessed Microsoft 365 document, so it cannot rule tenant grounding out.
        /// </summary>
        /// <param name="accessedResources">List of resources accessed during the Copilot interaction</param>
        private static TenantGroundingAssessment AssessTenantGrounding(List<AccessedResource> accessedResources)
        {
            if (accessedResources == null || accessedResources.Count == 0)
            {
                return new TenantGroundingAssessment
                {
                    IsTenantGrounded = false,
                    UnclassifiedResources = 0,
                    Basis = CopilotTenantGroundingBasis.NoResources,
                };
            }

            var hasTenantResource = false;
            var unclassified = 0;

            foreach (var resource in accessedResources)
            {
                if (HasTenantResourceEvidence(resource))
                {
                    hasTenantResource = true;
                    continue;
                }

                // The only things that let a resource be dismissed are positive markers that it came
                // from outside the tenant: a type that says so, or a SiteUrl that resolves to a host
                // which is not the tenant's. Everything else is unclassified.
                if (HasExternalGroundingEvidence(resource))
                {
                    continue;
                }

                unclassified++;
            }

            if (hasTenantResource)
            {
                return new TenantGroundingAssessment
                {
                    IsTenantGrounded = true,
                    UnclassifiedResources = unclassified,
                    Basis = CopilotTenantGroundingBasis.TenantResource,
                };
            }

            if (unclassified > 0)
            {
                return new TenantGroundingAssessment
                {
                    IsTenantGrounded = true,
                    UnclassifiedResources = unclassified,
                    Basis = CopilotTenantGroundingBasis.UnclassifiedResource,
                };
            }

            return new TenantGroundingAssessment
            {
                IsTenantGrounded = false,
                UnclassifiedResources = 0,
                Basis = CopilotTenantGroundingBasis.ExternalOnly,
            };
        }

        /// <summary>
        /// Whether one accessed resource carries positive evidence of belonging to the tenant.
        ///
        /// <c>Id</c> and <c>Name</c> are deliberately not treated as evidence: a public web result also
        /// has an identifier and a human-readable name, so their presence distinguishes nothing.
        /// </summary>
        private static bool HasTenantResourceEvidence(AccessedResource resource)
        {
            if (resource == null) return false;

            if (IsMeaningfulIdentifier(resource.ListItemUniqueId)) return true;
            if (IsMeaningfulIdentifier(resource.SensitivityLabelId)) return true;
            if (CopilotAccessedResourceTaxonomy.IsTenantContentUrl(resource.SiteUrl)) return true;

            return CopilotAccessedResourceTaxonomy.Classify(resource.Type) == CopilotResourceTypeKind.TenantContent;
        }

        /// <summary>
        /// Whether one accessed resource carries positive evidence of having come from OUTSIDE the
        /// tenant - either a type that says so, or a SiteUrl on a host that is not the tenant's.
        ///
        /// Only called once tenant evidence has been ruled out, so the two can never both apply.
        /// "The record says where this lives and it is not in the tenant" is a genuine finding; it is
        /// not the same as a resource that says nothing at all, which stays unclassified.
        /// </summary>
        private static bool HasExternalGroundingEvidence(AccessedResource resource)
        {
            if (resource == null) return false;

            if (CopilotAccessedResourceTaxonomy.Classify(resource.Type) == CopilotResourceTypeKind.ExternalGrounding)
            {
                return true;
            }

            return CopilotAccessedResourceTaxonomy.IsExternalWebUrl(resource.SiteUrl);
        }

        /// <summary>
        /// Whether an identifier field actually identifies something. The audit payload carries an
        /// all-zero GUID as a placeholder where a resource has no such identifier, and treating that
        /// as evidence would make the check true for every resource in the record.
        /// </summary>
        private static bool IsMeaningfulIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            Guid parsed;
            if (Guid.TryParse(value.Trim(), out parsed) && parsed == Guid.Empty) return false;

            return true;
        }

        /// <summary>
        /// Determines if deep reasoning (DEEP_LEO model) was used in the conversation.
        /// 
        /// Deep reasoning is Microsoft's advanced AI capability that provides more thorough
        /// analysis and problem-solving. It's indicated by the "DEEP_LEO" model name in
        /// the ModelTransparencyDetails field.
        /// 
        /// Billing: Deep reasoning is charged as an Agent Action (5 credits) per Microsoft
        /// documentation (effective March 25, 2025). The charge is per conversation, not
        /// per message, as it represents the invocation of the advanced reasoning capability.
        /// 
        /// Reference: "Starting on March 25, 2025, deep reasoning is available in AI prompts 
        /// and agent flows. Charges for deep reasoning in AI prompts use the Text and 
        /// generative AI tools (premium) rate, and charges for agent flows use the Flow 
        /// actions rate."
        /// </summary>
        /// <param name="modelDetails">List of model transparency details from the audit log</param>
        /// <returns>True if DEEP_LEO model was used, false otherwise</returns>
        private static bool HasDeepReasoning(List<ModelTransparencyDetail> modelDetails)
        {
            if (modelDetails == null || modelDetails.Count == 0)
            {
                return false;
            }

            // Check if any model is DEEP_LEO (case-insensitive)
            return modelDetails.Any(m =>
                !string.IsNullOrEmpty(m.ModelName) &&
                m.ModelName.Equals("DEEP_LEO", StringComparison.OrdinalIgnoreCase));
        }
    }

}
