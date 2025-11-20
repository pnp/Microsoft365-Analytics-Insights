using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// Represents a Microsoft Copilot audit event from the Office 365 Management API.
    /// Contains information about Copilot interactions including messages, accessed resources, and actions.
    /// </summary>
    public class CopilotAuditEvent
    {
        [JsonProperty("AISystemPlugin")]
        public List<AISystemPlugin> AISystemPlugin { get; set; }
        
        [JsonProperty("AccessedResources")]
        public List<AccessedResource> AccessedResources { get; set; }
        
        [JsonProperty("Messages")]
        public List<Message> Messages { get; set; }
        
        // Additional properties to support comprehensive billing calculation
        [JsonProperty("AnswerType")]
        public string AnswerType { get; set; } // "Classic", "Generative", "TenantGraph"
        
        [JsonProperty("AgentActions")]
        public List<AgentAction> AgentActions { get; set; }
        
        [JsonProperty("AIToolUsages")]
        public List<AIToolUsage> AIToolUsages { get; set; }
        
        [JsonProperty("FlowActions")]
        public AgentFlowUsage FlowActions { get; set; }
    }

    /// <summary>
    /// Represents an AI system plugin used during a Copilot interaction (e.g., BingWebSearch).
    /// Each plugin invocation is billed as an Agent Action.
    /// </summary>
    public class AISystemPlugin
    {
        [JsonProperty("Id")]
        public string Id { get; set; }
        
        [JsonProperty("Name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Represents a single message in a Copilot conversation.
    /// Messages can be either prompts (user input) or responses (Copilot output).
    /// Only response messages (isPrompt=false) are billable.
    /// </summary>
    public class Message
    {
        [JsonProperty("Id")]
        public string Id { get; set; }
        
        [JsonProperty("isPrompt")]
        public bool isPrompt { get; set; }
        
        /// <summary>
        /// Type of response: "Classic" (1 credit), "Generative" (2 credits), or "TenantGraph" (10 credits).
        /// If not specified, will be inferred from accessed resources.
        /// </summary>
        [JsonProperty("Type")]
        public string Type { get; set; }
    }

    /// <summary>
    /// Represents an agent action such as triggers, deep reasoning, topic transitions, etc.
    /// Each action costs 5 Copilot Credits regardless of type.
    /// </summary>
    public class AgentAction
    {
        [JsonProperty("Id")]
        public string Id { get; set; }
        
        [JsonProperty("Type")]
        public string Type { get; set; } // "Trigger", "DeepReasoning", "TopicTransition", "KnowledgeSearch", "AIToolPrompt"
    }

    /// <summary>
    /// Represents AI tool usage (prompts) with tiered billing.
    /// Billed per 10 responses: Basic=1, Standard=15, Premium=100 credits.
    /// </summary>
    public class AIToolUsage
    {
        [JsonProperty("ToolId")]
        public string ToolId { get; set; }
        
        [JsonProperty("Tier")]
        public string Tier { get; set; } // "Basic", "Standard", "Premium"
        
        [JsonProperty("ResponseCount")]
        public int ResponseCount { get; set; }
    }

    /// <summary>
    /// Represents agent flow actions (predefined sequences).
    /// Billed at 13 credits per 100 actions.
    /// </summary>
    public class AgentFlowUsage
    {
        [JsonProperty("ActionCount")]
        public int ActionCount { get; set; }
    }

    /// <summary>
    /// Detailed billing report for a Copilot audit event.
    /// Calculates Copilot Credits consumed based on Microsoft Copilot Studio billing policies.
    /// Reference: https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management
    /// </summary>
    public class CreditReport
    {
        #region Billing Constants
        // Based on Microsoft Copilot Studio billing documentation (as of March 2025)
        // https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-messages-management#copilot-credits-and-events-scenarios
        
        /// <summary>
        /// Classic answers are manually authored, predefined responses. Cost: 1 credit per answer.
        /// </summary>
        private const int CLASSIC_ANSWER_CREDITS = 1;
        
        /// <summary>
        /// Generative answers use AI models (GPT) to create dynamic responses. Cost: 2 credits per answer.
        /// </summary>
        private const int GENERATIVE_ANSWER_CREDITS = 2;
        
        /// <summary>
        /// Agent actions include triggers, deep reasoning, topic transitions, and tool invocations.
        /// Each action costs 5 credits regardless of complexity.
        /// </summary>
        private const int AGENT_ACTION_CREDITS = 5;
        
        /// <summary>
        /// Tenant graph grounding provides RAG over Microsoft Graph data (SharePoint, OneDrive, Email, Teams).
        /// Cost: 10 credits per grounded message (not per resource accessed).
        /// This is an optional capability that can be enabled per agent.
        /// </summary>
        private const int TENANT_GRAPH_GROUNDING_CREDITS = 10;
        
        /// <summary>
        /// Agent flow actions are predefined sequences that execute without requiring reasoning at each step.
        /// Cost: 13 credits per 100 actions (charged in increments).
        /// </summary>
        private const int AGENT_FLOW_CREDITS_PER_100_ACTIONS = 13;
        
        // AI Tools billing (per 10 responses, rounded up)
        /// <summary>
        /// Basic AI tools use lightweight language models. Cost: 1 credit per 10 responses.
        /// </summary>
        private const int AI_TOOLS_BASIC_PER_10 = 1;
        
        /// <summary>
        /// Standard AI tools use standard language models. Cost: 15 credits per 10 responses.
        /// </summary>
        private const int AI_TOOLS_STANDARD_PER_10 = 15;
        
        /// <summary>
        /// Premium AI tools use advanced models including deep reasoning. Cost: 100 credits per 10 responses.
        /// </summary>
        private const int AI_TOOLS_PREMIUM_PER_10 = 100;
        
        #endregion

        #region Properties
        
        // Legacy properties (kept for backwards compatibility but deprecated)
        [Obsolete("Use specific answer type counts instead")]
        [JsonProperty("AgentActions")]
        public int AgentActions { get; set; }
        
        [Obsolete("Use specific answer type counts instead")]
        [JsonProperty("GenerativeTurns")]
        public int GenerativeTurns { get; set; }
        
        // New detailed properties
        [JsonProperty("ClassicAnswers")]
        public int ClassicAnswers { get; set; }
        
        [JsonProperty("GenerativeAnswers")]
        public int GenerativeAnswers { get; set; }
        
        [JsonProperty("TenantGraphGroundedAnswers")]
        public int TenantGraphGroundedAnswers { get; set; }
        
        [JsonProperty("AgentActionCount")]
        public int AgentActionCount { get; set; }
        
        [JsonProperty("FlowActions")]
        public int FlowActions { get; set; }
        
        // AI Tools breakdown
        [JsonProperty("BasicAIToolResponses")]
        public int BasicAIToolResponses { get; set; }
        
        [JsonProperty("StandardAIToolResponses")]
        public int StandardAIToolResponses { get; set; }
        
        [JsonProperty("PremiumAIToolResponses")]
        public int PremiumAIToolResponses { get; set; }

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
        /// Analyzes a Copilot audit event JSON and calculates the total Copilot Credits consumed.
        /// 
        /// Billing Logic:
        /// 1. Messages: Each response message is billed based on its type (Classic=1, Generative=2, TenantGraph=10)
        /// 2. Agent Actions: Each action (plugin, tool invocation) costs 5 credits
        /// 3. AI Tools: Billed per 10 responses based on tier (Basic=1, Standard=15, Premium=100)
        /// 4. Flow Actions: Billed per 100 actions at 13 credits per 100
        /// 5. Tenant Graph Grounding: Inferred from accessed resources if not explicitly specified
        /// 
        /// Note: The number of resources accessed does NOT multiply costs. Tenant graph grounding
        /// costs 10 credits per message regardless of how many SharePoint files, emails, etc. are accessed.
        /// </summary>
        /// <param name="json">JSON string containing the Copilot audit event data</param>
        /// <returns>CreditReport with detailed billing breakdown</returns>
        public static CreditReport Analyze(string json)
        {
            var auditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(json);
            if (auditEvent == null)
            {
                return new CreditReport
                {
                    TotalCredits = 0,
                    ResourceTypeBreakdown = new Dictionary<string, int>(),
                    CreditBreakdown = new Dictionary<string, int>()
                };
            }

            var report = new CreditReport
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
            if (auditEvent.Messages != null)
            {
                foreach (var message in auditEvent.Messages.Where(m => !m.isPrompt))
                {
                    // Check if message Type is explicitly set in the audit log
                    if (!string.IsNullOrEmpty(message.Type))
                    {
                        // Use explicit type if available (future audit log enhancement)
                        switch (message.Type.ToLower())
                        {
                            case "classic":
                                report.ClassicAnswers++;
                                totalCredits += CLASSIC_ANSWER_CREDITS;
                                AddToBreakdown(report.CreditBreakdown, "Classic Answers", CLASSIC_ANSWER_CREDITS);
                                break;
                            case "generative":
                                report.GenerativeAnswers++;
                                totalCredits += GENERATIVE_ANSWER_CREDITS;
                                AddToBreakdown(report.CreditBreakdown, "Generative Answers", GENERATIVE_ANSWER_CREDITS);
                                break;
                            case "tenantgraph":
                                report.TenantGraphGroundedAnswers++;
                                totalCredits += TENANT_GRAPH_GROUNDING_CREDITS;
                                AddToBreakdown(report.CreditBreakdown, "Tenant Graph Grounding", TENANT_GRAPH_GROUNDING_CREDITS);
                                break;
                        }
                    }
                    else
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
            }

            // STEP 3: Count agent actions
            // Agent actions include triggers, topic transitions, knowledge searches, and tool invocations.
            // Each action costs 5 credits regardless of type or complexity.
            if (auditEvent.AgentActions != null)
            {
                // Use explicit AgentActions if available (future audit log enhancement)
                report.AgentActionCount = auditEvent.AgentActions.Count;
                int agentActionCredits = report.AgentActionCount * AGENT_ACTION_CREDITS;
                totalCredits += agentActionCredits;
                report.CreditBreakdown["Agent Actions"] = agentActionCredits;
            }
            else if (auditEvent.AISystemPlugin != null)
            {
                // Fallback: Use AISystemPlugin as proxy for agent actions
                // Current audit logs use this field (e.g., BingWebSearch plugin)
                // Each plugin invocation = 1 agent action = 5 credits
                report.AgentActionCount = auditEvent.AISystemPlugin.Count;
                int agentActionCredits = report.AgentActionCount * AGENT_ACTION_CREDITS;
                totalCredits += agentActionCredits;
                report.CreditBreakdown["Agent Actions"] = agentActionCredits;
            }

            // STEP 4: Count AI Tool usages
            // AI tools are prompt-based intelligent processing with tiered pricing.
            // Billed per 10 responses (rounded up): Basic=1, Standard=15, Premium=100 credits
            if (auditEvent.AIToolUsages != null)
            {
                foreach (var toolUsage in auditEvent.AIToolUsages)
                {
                    int credits = 0;
                    switch (toolUsage.Tier?.ToLower())
                    {
                        case "basic":
                            report.BasicAIToolResponses += toolUsage.ResponseCount;
                            // Round up: 1-10 responses = 1 credit, 11-20 = 2 credits, etc.
                            credits = (int)Math.Ceiling(toolUsage.ResponseCount / 10.0) * AI_TOOLS_BASIC_PER_10;
                            AddToBreakdown(report.CreditBreakdown, "AI Tools (Basic)", credits);
                            break;
                        case "standard":
                            report.StandardAIToolResponses += toolUsage.ResponseCount;
                            credits = (int)Math.Ceiling(toolUsage.ResponseCount / 10.0) * AI_TOOLS_STANDARD_PER_10;
                            AddToBreakdown(report.CreditBreakdown, "AI Tools (Standard)", credits);
                            break;
                        case "premium":
                            // Premium tier includes deep reasoning capabilities
                            report.PremiumAIToolResponses += toolUsage.ResponseCount;
                            credits = (int)Math.Ceiling(toolUsage.ResponseCount / 10.0) * AI_TOOLS_PREMIUM_PER_10;
                            AddToBreakdown(report.CreditBreakdown, "AI Tools (Premium)", credits);
                            break;
                    }
                    totalCredits += credits;
                }
            }

            // STEP 5: Count agent flow actions
            // Agent flows are predefined sequences that execute without AI reasoning at each step.
            // More efficient than agent actions but still consume credits.
            // Billed at 13 credits per 100 actions (rounded up)
            if (auditEvent.FlowActions != null && auditEvent.FlowActions.ActionCount > 0)
            {
                report.FlowActions = auditEvent.FlowActions.ActionCount;
                // Round up: 1-100 actions = 13 credits, 101-200 = 26 credits, etc.
                int flowCredits = (int)Math.Ceiling(auditEvent.FlowActions.ActionCount / 100.0) * AGENT_FLOW_CREDITS_PER_100_ACTIONS;
                totalCredits += flowCredits;
                report.CreditBreakdown["Agent Flow Actions"] = flowCredits;
            }

            // STEP 6: Build resource breakdown (for reference/analytics only)
            // This shows what types of resources were accessed but does NOT affect billing.
            // Resources are billed at the message level, not per resource.
            report.ResourceTypeBreakdown = auditEvent.AccessedResources?
                .GroupBy(r => string.IsNullOrEmpty(r.Type) ? "WebPage" : r.Type)
                .ToDictionary(g => g.Key, g => g.Count()) ?? new Dictionary<string, int>();

            // Set legacy properties for backwards compatibility
            #pragma warning disable CS0618 // Type or member is obsolete
            report.AgentActions = report.AgentActionCount;
            report.GenerativeTurns = report.GenerativeAnswers;
            #pragma warning restore CS0618 // Type or member is obsolete

            report.TotalCredits = totalCredits;

            return report;
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
