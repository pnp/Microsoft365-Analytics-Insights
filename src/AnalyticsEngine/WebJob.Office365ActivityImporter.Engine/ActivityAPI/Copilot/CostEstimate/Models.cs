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
        // Resolves to Entities.Serialisation.AISystemPlugin (see the using above). A duplicate
        // AISystemPlugin class used to live in this namespace and silently shadowed it; the two had
        // to be kept in step by hand, so the duplicate was removed rather than extended when the
        // schema's Version field was added.
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
    /// Represents a single message in a Copilot conversation.
    /// Messages can be either prompts (user input) or responses (Copilot output).
    /// Only response messages (isPrompt=false) are billable.
    /// </summary>
    public class Message
    {
        [JsonProperty("Id")]
        public string Id { get; set; }

        /// <summary>
        /// True for the user's prompt, false for Copilot's response (schema field <c>isPrompt</c>).
        /// </summary>
        /// <remarks>
        /// NULLABLE ON PURPOSE. This used to be a non-nullable <c>bool</c>, which meant a payload that
        /// omitted <c>isPrompt</c> deserialised to <c>false</c> - indistinguishable from an explicit
        /// "this is a response". Since only responses are billable, an absent flag was silently charged
        /// as a generative answer, over-stating estimated Copilot credit consumption. It also disagreed
        /// with the persisted <see cref="Common.Entities.Entities.AuditLog.CopilotEventMessage.IsPrompt"/>,
        /// which was already <c>bool?</c> and documented as null when the payload omits it.
        ///
        /// Consumers must therefore treat null as "direction unknown" and neither bill it nor count it as
        /// a prompt. See <c>CopilotCreditEstimation</c>.
        ///
        /// One consequence is worth knowing about, because it is invisible from here. This model is what
        /// <c>CopilotAuditEventManager.SerializeMessages</c> re-serialises into the staging table's
        /// <c>messages_json</c>, and the merge derives a fallback <c>persisted_message_id</c> from
        /// event id + this flag + size for messages that carry no <c>Id</c>. An omitted flag used to
        /// serialise as <c>false</c> and now serialises as <c>null</c>, so that fallback id changes for
        /// exactly those messages. See the comment above <c>parsed_messages</c> in
        /// <c>common_upsert_copilot_agents.sql</c> for the bounded one-time effect that has on re-staged
        /// events, and do not "fix" it by making this non-nullable again.
        /// </remarks>
        [JsonProperty("isPrompt")]
        public bool? IsPrompt { get; set; }

        /// <summary>
        /// Whether a jailbreak attempt was detected in this prompt message (schema field
        /// <c>JailbreakDetected</c>). Null when the payload omits it.
        /// </summary>
        /// <remarks>
        /// Documented on the Purview audit schema page but absent from the older OData Management API
        /// schema, which is why it was missed until now: the model was written against the OData schema
        /// and Newtonsoft silently discarded the field. One of only two prompt-safety signals the audit
        /// feed carries (the other is <see cref="Entities.Serialisation.AccessedResource.XPIADetected"/>).
        /// https://learn.microsoft.com/en-us/purview/audit-copilot
        /// </remarks>
        [JsonProperty("JailbreakDetected")]
        public bool? JailbreakDetected { get; set; }

        /// <summary>
        /// Size of the message as reported by the audit schema (MessageData.Size, Edm.Int64).
        /// </summary>
        /// <remarks>
        /// EXPECT THIS TO BE NULL. The OData Management API schema declares <c>MessageData.Size</c> as
        /// Edm.Int64, but Microsoft's Purview audit documentation states plainly that "Size is currently
        /// not used", and its example payloads omit it. The two official sources disagree, so the field
        /// is kept for forward compatibility - but nothing may assume a value is present, and no metric
        /// may be built on it until a real payload is observed carrying one.
        /// https://learn.microsoft.com/en-us/purview/audit-copilot
        /// </remarks>
        [JsonProperty("Size")]
        public long? Size { get; set; }

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

        /// <summary>
        /// The provider of the model (schema field ModelProviderName), e.g. "OpenAI".
        /// </summary>
        [JsonProperty("ModelProviderName")]
        public string ModelProviderName { get; set; }

        /// <summary>
        /// The version of the model (schema field ModelVersion).
        /// </summary>
        [JsonProperty("ModelVersion")]
        public string ModelVersion { get; set; }
    }
}
