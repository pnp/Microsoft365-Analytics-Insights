using Newtonsoft.Json;
using System.Collections.Generic;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate
{

    /// <summary>
    /// Represents a Microsoft Copilot audit event from the Office 365 Management API.
    /// Contains information about Copilot interactions including messages and accessed resources.
    /// </summary>
    public class CopilotAuditEvent
    {
        [JsonProperty("AISystemPlugin")]
        public List<AISystemPlugin> AISystemPlugin { get; set; }

        [JsonProperty("AccessedResources")]
        public List<AccessedResource> AccessedResources { get; set; }

        [JsonProperty("Messages")]
        public List<Message> Messages { get; set; }

        [JsonProperty("ModelTransparencyDetails")]
        public List<ModelTransparencyDetail> ModelTransparencyDetails { get; set; }

        // Additional properties to support comprehensive billing calculation
        [JsonProperty("AnswerType")]
        public string AnswerType { get; set; } // "Classic", "Generative", "TenantGraph"
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
        public bool IsPrompt { get; set; }

        /// <summary>
        /// Type of response: "Classic" (1 credit), "Generative" (2 credits), or "TenantGraph" (10 credits).
        /// Note: This property is available in audit logs but not currently populated by Microsoft.
        /// Cost estimation infers type from accessed resources instead.
        /// </summary>
        [JsonProperty("Type")]
        public string Type { get; set; }
    }

    /// <summary>
    /// Details about the AI model used for generating responses.
    /// Used to detect deep reasoning (DEEP_LEO model) which has premium billing rates.
    /// </summary>
    public class ModelTransparencyDetail
    {
        /// <summary>
        /// The name of the AI model used. Known values:
        /// - "DEEP_LEO": Deep reasoning model (premium, 5 credits per agent action)
        /// - Other GPT models: Standard generative models
        /// </summary>
        [JsonProperty("ModelName")]
        public string ModelName { get; set; }
    }
}
