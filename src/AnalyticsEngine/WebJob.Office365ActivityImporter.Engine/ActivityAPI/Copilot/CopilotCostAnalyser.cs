using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{
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

    public class AISystemPlugin
    {
        [JsonProperty("Id")]
        public string Id { get; set; }
        
        [JsonProperty("Name")]
        public string Name { get; set; }
    }

    public class Message
    {
        [JsonProperty("Id")]
        public string Id { get; set; }
        
        [JsonProperty("isPrompt")]
        public bool isPrompt { get; set; }
        
        [JsonProperty("Type")]
        public string Type { get; set; } // "Classic", "Generative", "TenantGraph"
    }

    public class AgentAction
    {
        [JsonProperty("Id")]
        public string Id { get; set; }
        
        [JsonProperty("Type")]
        public string Type { get; set; } // "Trigger", "DeepReasoning", "TopicTransition", "KnowledgeSearch", "AIToolPrompt"
    }

    public class AIToolUsage
    {
        [JsonProperty("ToolId")]
        public string ToolId { get; set; }
        
        [JsonProperty("Tier")]
        public string Tier { get; set; } // "Basic", "Standard", "Premium"
        
        [JsonProperty("ResponseCount")]
        public int ResponseCount { get; set; }
    }

    public class AgentFlowUsage
    {
        [JsonProperty("ActionCount")]
        public int ActionCount { get; set; }
    }

    public class CreditReport
    {
        // Billing constants based on Microsoft Copilot Studio documentation
        private const int CLASSIC_ANSWER_CREDITS = 1;
        private const int GENERATIVE_ANSWER_CREDITS = 2;
        private const int AGENT_ACTION_CREDITS = 5;
        private const int TENANT_GRAPH_GROUNDING_CREDITS = 10;
        private const int AGENT_FLOW_CREDITS_PER_100_ACTIONS = 13;
        
        // AI Tools credits (per 10 responses)
        private const int AI_TOOLS_BASIC_PER_10 = 1;
        private const int AI_TOOLS_STANDARD_PER_10 = 15;
        private const int AI_TOOLS_PREMIUM_PER_10 = 100; // Includes deep reasoning

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
        
        [JsonProperty("ResourceTypeBreakdown")]
        public Dictionary<string, int> ResourceTypeBreakdown { get; set; }
        
        [JsonProperty("CreditBreakdown")]
        public Dictionary<string, int> CreditBreakdown { get; set; }

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

            // Count answer types from Messages
            if (auditEvent.Messages != null)
            {
                foreach (var message in auditEvent.Messages.Where(m => !m.isPrompt))
                {
                    switch (message.Type?.ToLower())
                    {
                        case "classic":
                            report.ClassicAnswers++;
                            totalCredits += CLASSIC_ANSWER_CREDITS;
                            report.CreditBreakdown["Classic Answers"] = report.CreditBreakdown.GetValueOrDefault("Classic Answers", 0) + CLASSIC_ANSWER_CREDITS;
                            break;
                        case "generative":
                            report.GenerativeAnswers++;
                            totalCredits += GENERATIVE_ANSWER_CREDITS;
                            report.CreditBreakdown["Generative Answers"] = report.CreditBreakdown.GetValueOrDefault("Generative Answers", 0) + GENERATIVE_ANSWER_CREDITS;
                            break;
                        case "tenantgraph":
                            report.TenantGraphGroundedAnswers++;
                            totalCredits += TENANT_GRAPH_GROUNDING_CREDITS;
                            report.CreditBreakdown["Tenant Graph Grounding"] = report.CreditBreakdown.GetValueOrDefault("Tenant Graph Grounding", 0) + TENANT_GRAPH_GROUNDING_CREDITS;
                            break;
                        default:
                            // If type is not specified, assume generative for non-prompts
                            report.GenerativeAnswers++;
                            totalCredits += GENERATIVE_ANSWER_CREDITS;
                            report.CreditBreakdown["Generative Answers"] = report.CreditBreakdown.GetValueOrDefault("Generative Answers", 0) + GENERATIVE_ANSWER_CREDITS;
                            break;
                    }
                }
            }

            // Count agent actions
            if (auditEvent.AgentActions != null)
            {
                report.AgentActionCount = auditEvent.AgentActions.Count;
                int agentActionCredits = report.AgentActionCount * AGENT_ACTION_CREDITS;
                totalCredits += agentActionCredits;
                report.CreditBreakdown["Agent Actions"] = agentActionCredits;
            }
            // Fallback to legacy plugin count if AgentActions not available
            else if (auditEvent.AISystemPlugin != null)
            {
                report.AgentActionCount = auditEvent.AISystemPlugin.Count;
                int agentActionCredits = report.AgentActionCount * AGENT_ACTION_CREDITS;
                totalCredits += agentActionCredits;
                report.CreditBreakdown["Agent Actions"] = agentActionCredits;
            }

            // Count AI Tool usages
            if (auditEvent.AIToolUsages != null)
            {
                foreach (var toolUsage in auditEvent.AIToolUsages)
                {
                    int credits = 0;
                    switch (toolUsage.Tier?.ToLower())
                    {
                        case "basic":
                            report.BasicAIToolResponses += toolUsage.ResponseCount;
                            credits = (int)Math.Ceiling(toolUsage.ResponseCount / 10.0) * AI_TOOLS_BASIC_PER_10;
                            report.CreditBreakdown["AI Tools (Basic)"] = report.CreditBreakdown.GetValueOrDefault("AI Tools (Basic)", 0) + credits;
                            break;
                        case "standard":
                            report.StandardAIToolResponses += toolUsage.ResponseCount;
                            credits = (int)Math.Ceiling(toolUsage.ResponseCount / 10.0) * AI_TOOLS_STANDARD_PER_10;
                            report.CreditBreakdown["AI Tools (Standard)"] = report.CreditBreakdown.GetValueOrDefault("AI Tools (Standard)", 0) + credits;
                            break;
                        case "premium":
                            report.PremiumAIToolResponses += toolUsage.ResponseCount;
                            credits = (int)Math.Ceiling(toolUsage.ResponseCount / 10.0) * AI_TOOLS_PREMIUM_PER_10;
                            report.CreditBreakdown["AI Tools (Premium)"] = report.CreditBreakdown.GetValueOrDefault("AI Tools (Premium)", 0) + credits;
                            break;
                    }
                    totalCredits += credits;
                }
            }

            // Count agent flow actions
            if (auditEvent.FlowActions != null && auditEvent.FlowActions.ActionCount > 0)
            {
                report.FlowActions = auditEvent.FlowActions.ActionCount;
                int flowCredits = (int)Math.Ceiling(auditEvent.FlowActions.ActionCount / 100.0) * AGENT_FLOW_CREDITS_PER_100_ACTIONS;
                totalCredits += flowCredits;
                report.CreditBreakdown["Agent Flow Actions"] = flowCredits;
            }

            // Resource breakdown (for reference)
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
    }
}
