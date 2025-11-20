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
    /// Only Messages and AccessedResources are available in audit logs, so we can only
    /// estimate message-level costs (Classic/Generative/TenantGraph answers).
    /// 
    /// Agent Actions, AI Tool Usages, and Flow Actions are not included in audit logs,
    /// so those costs cannot be calculated from this data source.
    /// 
    /// Reference: https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management
    /// </summary>
    public class CopilotCreditEstimation
    {
        #region Billing Constants
        // Based on Microsoft Copilot Studio billing documentation (as of March 2025)
        // https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management#copilot-credits-and-events-scenarios
        
        /// <summary>
        /// Classic answers are manually authored, predefined responses. Cost: 1 credit per answer.
        /// Note: Currently not distinguishable from audit logs - all answers estimated as Generative or TenantGraph.
        /// </summary>
        private const int CLASSIC_ANSWER_CREDITS = 1;
        
        /// <summary>
        /// Generative answers use AI models (GPT) to create dynamic responses. Cost: 2 credits per answer.
        /// </summary>
        private const int GENERATIVE_ANSWER_CREDITS = 2;
        
        /// <summary>
        /// Tenant graph grounding provides RAG over Microsoft Graph data (SharePoint, OneDrive, Email, Teams).
        /// Cost: 10 credits per grounded message (not per resource accessed).
        /// This is an optional capability that can be enabled per agent.
        /// </summary>
        private const int TENANT_GRAPH_GROUNDING_CREDITS = 10;
        
        // The following constants are for reference only - these costs cannot be calculated from audit logs
        
        /// <summary>
        /// Agent actions (triggers, deep reasoning, topic transitions, tool invocations) cost 5 credits each.
        /// Note: Not available in audit logs - cannot be calculated.
        /// </summary>
        private const int AGENT_ACTION_CREDITS = 5;
        
        /// <summary>
        /// Agent flow actions cost 13 credits per 100 actions (charged in increments).
        /// Note: Not available in audit logs - cannot be calculated.
        /// </summary>
        private const int AGENT_FLOW_CREDITS_PER_100_ACTIONS = 13;
        
        // AI Tools billing (per 10 responses, rounded up)
        /// <summary>
        /// Basic AI tools: 1 credit per 10 responses.
        /// Note: Not available in audit logs - cannot be calculated.
        /// </summary>
        private const int AI_TOOLS_BASIC_PER_10 = 1;
        
        /// <summary>
        /// Standard AI tools: 15 credits per 10 responses.
        /// Note: Not available in audit logs - cannot be calculated.
        /// </summary>
        private const int AI_TOOLS_STANDARD_PER_10 = 15;
        
        /// <summary>
        /// Premium AI tools: 100 credits per 10 responses.
        /// Note: Not available in audit logs - cannot be calculated.
        /// </summary>
        private const int AI_TOOLS_PREMIUM_PER_10 = 100;
        
        #endregion

        #region Properties

        [JsonProperty("GenerativeAnswers")]
        public int GenerativeAnswers { get; set; }

        [JsonProperty("TenantGraphGroundedAnswers")]
        public int TenantGraphGroundedAnswers { get; set; }


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

        #endregion


        /// <summary>
        /// Analyzes a Copilot audit event JSON and calculates the total Copilot Credits consumed.
        /// 
        /// Billing Logic (based on available audit log data):
        /// 1. Messages: Each response message is billed based on accessed resources
        ///    - If tenant resources (SharePoint, OneDrive, etc.) accessed = 10 credits (Tenant Graph Grounding)
        ///    - Otherwise = 2 credits (Generative Answer)
        /// 2. AccessedResources: Used to detect tenant graph grounding, but does not multiply costs
        /// 
        /// Limitations:
        /// - Agent Actions, AI Tool Usages, and Flow Actions are NOT included in audit logs
        /// - Classic vs Generative answer types cannot be distinguished from audit logs
        /// - This provides a minimum cost estimate based on available data
        /// 
        /// Note: The number of resources accessed does NOT multiply costs. Tenant graph grounding
        /// costs 10 credits per message regardless of how many SharePoint files, emails, etc. are accessed.
        /// </summary>
        /// <param name="json">JSON string containing the Copilot audit event data</param>
        /// <returns>CreditReport with detailed billing breakdown based on available audit log data</returns>
        public static CopilotCreditEstimation Analyze(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CopilotCreditEstimation
                {
                    TotalCredits = 0,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>()
                };
            }

            var auditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(json);
            return Analyze(auditEvent);
        }

        /// <summary>
        /// Analyzes a Copilot audit event object and calculates the total Copilot Credits consumed.
        /// 
        /// Billing Logic (based on available audit log data):
        /// 1. Messages: Each response message is billed based on accessed resources
        ///    - If tenant resources (SharePoint, OneDrive, etc.) accessed = 10 credits (Tenant Graph Grounding)
        ///    - Otherwise = 2 credits (Generative Answer)
        /// 2. AccessedResources: Used to detect tenant graph grounding, but does not multiply costs
        /// 
        /// Limitations:
        /// - Agent Actions, AI Tool Usages, and Flow Actions are NOT included in audit logs
        /// - Classic vs Generative answer types cannot be distinguished from audit logs
        /// - This provides a minimum cost estimate based on available data
        /// 
        /// Note: The number of resources accessed does NOT multiply costs. Tenant graph grounding
        /// costs 10 credits per message regardless of how many SharePoint files, emails, etc. are accessed.
        /// </summary>
        /// <param name="auditEvent">The Copilot audit event object to analyze</param>
        /// <returns>CreditReport with detailed billing breakdown based on available audit log data</returns>
        public static CopilotCreditEstimation Analyze(CopilotAuditEvent auditEvent)
        {
            if (auditEvent == null)
            {
                return new CopilotCreditEstimation
                {
                    TotalCredits = 0,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>()
                };
            }

            var report = new CopilotCreditEstimation
            {
                ResourceTypeBreakdown = new Dictionary<string, int>(),
                CreditBreakdown = new Dictionary<string, int>()
            };

            int totalCredits = 0;

            // STEP 1: Detect tenant graph usage
            // If any Microsoft Graph resources (SharePoint, Email, Teams, etc.) were accessed,
            // this indicates tenant graph grounding was used for the conversation.
            // This affects billing: regular generative answers (2 credits) become tenant graph grounded (10 credits).
            bool hasTenantGraphResources = HasTenantGraphResources(auditEvent.AccessedResources);

            // STEP 2: Count and bill response messages
            // Only non-prompt messages (isPrompt=false) are billable.
            // Each message is billed once regardless of how many resources it accessed.
            // 
            // Note: Audit logs don't include explicit message types, so we infer:
            // - Messages with tenant resources = Tenant Graph Grounding (10 credits)
            // - Messages without tenant resources = Generative Answer (2 credits)
            // - Classic answers cannot be distinguished from audit logs
            if (auditEvent.Messages != null)
            {
                foreach (var message in auditEvent.Messages.Where(m => !m.IsPrompt))
                {

                    // Type not specified - infer based on accessed resources
                    // This is the current behavior as audit logs don't yet include explicit message types
                    if (hasTenantGraphResources)
                    {
                        // Microsoft Graph resources were accessed - bill as tenant graph grounding (10 credits)
                        report.TenantGraphGroundedAnswers++;
                        totalCredits += TENANT_GRAPH_GROUNDING_CREDITS;
                        AddToBreakdown(report.CreditBreakdown, "Tenant Graph Grounding", TENANT_GRAPH_GROUNDING_CREDITS);
                    }
                    else
                    {
                        // Only web search or no resources - bill as standard generative answer (2 credits)
                        report.GenerativeAnswers++;
                        totalCredits += GENERATIVE_ANSWER_CREDITS;
                        AddToBreakdown(report.CreditBreakdown, "Generative Answers", GENERATIVE_ANSWER_CREDITS);
                    }

                }
            }

            // STEP 3: Build resource breakdown (for reference/analytics only)
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
                    if (siteUrlLower.Contains("sharepoint.com") ||
                        siteUrlLower.Contains("onedrive.") ||
                        siteUrlLower.Contains("outlook.office") ||
                        siteUrlLower.Contains("teams.microsoft.com"))
                    {
                        return true;
                    }
                }
            }

            // No tenant resources found - likely web search only
            return false;
        }
    }

}
