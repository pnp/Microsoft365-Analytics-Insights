using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{

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
        /// Version of the cost estimation model.
        /// </summary>
        private const string COST_ESTIMATION_VERSION = "1.0.0.0";

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


        /// <summary>
        /// Analyzes a Copilot audit event JSON and calculates the total Copilot Credits consumed.
        /// This is an overload that deserializes the JSON string before analysis.
        /// See <see cref="Analyze(CopilotAuditEvent)"/> for detailed billing logic.
        /// </summary>
        /// <param name="json">JSON string containing the Copilot audit event data</param>
        /// <returns>CreditReport with detailed billing breakdown</returns>
        public static CopilotCreditEstimation Analyze(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CopilotCreditEstimation
                {
                    CostModelVersion = COST_ESTIMATION_VERSION,
                    TotalCredits = 0,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>(),
                    ModelsUsed = new List<string>()
                };
            }

            var auditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(json);
            return Analyze(auditEvent);
        }

        /// <summary>
        /// Analyzes a Copilot audit event object and calculates the total Copilot Credits consumed.
        /// 
        /// Billing Logic (based on Microsoft documentation, effective March 25, 2025):
        /// 1. Generative Answers: 2 credits per response message
        /// 2. Tenant Graph Grounding: +10 credits per message (additive with generative)
        /// 3. Deep Reasoning (DEEP_LEO model): 5 credits per agent action
        /// 
        /// Example from documentation ("Sales performance agent"):
        /// - Scenario: 4 generative answers, all grounded in the tenant graph.
        /// - Calculation: 4 messages * (2 for generative answer + 10 for tenant graph) = 48 credits.
        /// - This model correctly calculates this as 4 * (GENERATIVE_ANSWER_CREDITS + TENANT_GRAPH_GROUNDING_CREDITS).
        /// - Reference: https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management#sales-performance-agent
        /// 
        /// Formula per message with tenant graph grounding:
        ///   Total = 2 (generative) + 10 (tenant graph) = 12 credits
        /// 
        /// Formula per message with tenant graph + deep reasoning:
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
        /// <returns>CreditReport with detailed billing breakdown</returns>
        public static CopilotCreditEstimation Analyze(CopilotAuditEvent auditEvent)
        {
            if (auditEvent == null)
            {
                return new CopilotCreditEstimation
                {
                    CostModelVersion = COST_ESTIMATION_VERSION,
                    TotalCredits = 0,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>(),
                    ModelsUsed = new List<string>()
                };
            }

            var report = new CopilotCreditEstimation
            {
                CostModelVersion = COST_ESTIMATION_VERSION,
                ResourceTypeBreakdown = new Dictionary<string, int>(),
                CreditBreakdown = new Dictionary<string, int>(),
                ModelsUsed = new List<string>()
            };

            int totalCredits = 0;

            // STEP 1: Detect tenant graph usage
            // If any Microsoft Graph resources (SharePoint, OneDrive, Teams files, etc.) were accessed,
            // this indicates tenant graph grounding was used for the conversation.
            // Per Microsoft docs: "tenant graph grounding for messages" costs 10 credits
            // PLUS the base generative answer cost of 2 credits = 12 credits total per message
            bool hasTenantGraphResources = HasTenantGraphResources(auditEvent.AccessedResources);

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
            report.ResourceTypeBreakdown = auditEvent.AccessedResources?
                .GroupBy(r => string.IsNullOrEmpty(r.Type) ? "WebPage" : r.Type)
                .ToDictionary(g => g.Key, g => g.Count()) ?? new Dictionary<string, int>();

            report.TotalCredits = totalCredits;

            return report;
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
        /// Determines if the accessed resources indicate tenant graph grounding was used.
        /// 
        /// Tenant graph grounding provides RAG over Microsoft Graph data including:
        /// - SharePoint sites, files, and documents
        /// - OneDrive files and folders
        /// - Outlook emails and calendar events
        /// - Teams messages, channels, and meetings
        /// - Other Microsoft 365 data synced to Graph
        /// 
        /// Detection Logic:
        /// 1. Check resource Type field for known Microsoft Graph entity types
        /// 2. Check SiteUrl field for Microsoft 365 service URLs
        /// 3. Returns true if ANY tenant resource is found (does not count individual resources)
        /// 
        /// Important: This is an inference-based approach as current audit logs don't explicitly
        /// flag tenant graph grounding. Future audit log schema updates may include explicit fields.
        /// </summary>
        /// <param name="accessedResources">List of resources accessed during the Copilot interaction</param>
        /// <returns>True if any Microsoft Graph tenant resources were accessed, false for web-only or no resources</returns>
        private static bool HasTenantGraphResources(List<AccessedResource> accessedResources)
        {
            if (accessedResources == null || accessedResources.Count == 0)
            {
                return false;
            }

            // Resource types that indicate tenant graph grounding
            // These are Microsoft Graph entities that represent tenant data
            var tenantGraphResourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // SharePoint & OneDrive
                "Site", "Web", "List", "Folder", "File",
                "docx", "xlsx", "pptx", "pdf", "txt", "doc", "xls", "ppt",
                
                // Email & Calendar
                "EmailMessage", "Email", "Message", "MailFolder", "Calendar", "Event",
                
                // Teams
                "Team", "Channel", "Chat", "TeamsMessage", "TeamsMeeting",
                
                // Other Microsoft Graph entities
                "User", "Group", "Contact", "Task", "Planner", "OneNote",
                "Drive", "DriveItem"
            };

            // Check if any accessed resource is a tenant graph resource
            foreach (var resource in accessedResources)
            {
                // Check resource type field
                if (!string.IsNullOrEmpty(resource.Type) && tenantGraphResourceTypes.Contains(resource.Type))
                {
                    return true;
                }

                // Check if SiteUrl contains Microsoft 365 service indicators
                if (!string.IsNullOrEmpty(resource.SiteUrl))
                {
                    var siteUrlLower = resource.SiteUrl.ToLower();
                    
                    // Teams file URLs contain specific patterns
                    if (siteUrlLower.Contains("sharepoint.com") ||
                        siteUrlLower.Contains("onedrive.") ||
                        siteUrlLower.Contains("outlook.office") ||
                        siteUrlLower.Contains("teams.microsoft.com") ||
                        siteUrlLower.Contains("asyncgw.teams.microsoft.com"))  // Teams async gateway for file access
                    {
                        return true;
                    }
                }
            }

            // No tenant resources found - likely web search only
            return false;
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
