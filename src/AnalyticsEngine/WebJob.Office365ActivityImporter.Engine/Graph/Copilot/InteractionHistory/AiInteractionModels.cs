using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// DTO for a Microsoft Graph <c>aiInteraction</c> returned by
    /// <c>/copilot/users/{userId}/interactionHistory/getAllEnterpriseInteractions</c>.
    /// </summary>
    /// <remarks>
    /// <b>This type is transient by design.</b> <see cref="Body"/> holds the user's real prompt or Copilot's
    /// real answer, so instances must never be persisted, logged, or included in telemetry. The importer
    /// converts each one into an <c>InteractionStats</c> (counts and, optionally, cognitive scores) and drops
    /// the DTO. Anything that echoes <see cref="Body"/> outside the cognitive call is a data-protection bug.
    /// </remarks>
    public class AiInteraction
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Thread/conversation id. Same value as <c>copilot_chats.thread_id</c> in the audit feed.</summary>
        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        /// <summary>Groups a <c>userPrompt</c> with the <c>aiResponse</c> it produced.</summary>
        [JsonProperty("requestId")]
        public string RequestId { get; set; }

        /// <summary>e.g. <c>IPM.SkypeTeams.Message.Copilot.Excel</c>.</summary>
        [JsonProperty("appClass")]
        public string AppClass { get; set; }

        /// <summary><c>userPrompt</c> or <c>aiResponse</c>.</summary>
        [JsonProperty("interactionType")]
        public string InteractionType { get; set; }

        /// <summary>e.g. <c>appchat</c> or <c>bizchat</c>.</summary>
        [JsonProperty("conversationType")]
        public string ConversationType { get; set; }

        [JsonProperty("createdDateTime")]
        public DateTime? CreatedDateTime { get; set; }

        [JsonProperty("locale")]
        public string Locale { get; set; }

        /// <summary>
        /// The prompt or response text. Read to derive counts and (for prompts) cognitive scores, then
        /// discarded - see the remarks on <see cref="AiInteraction"/>.
        /// </summary>
        [JsonProperty("body")]
        public AiInteractionBody Body { get; set; }

        /// <summary>
        /// Graph <c>identitySet</c>. Kept as a raw token because the shape of <c>device</c> has varied
        /// between a plain string and an <c>identity</c> object; <see cref="GetDeviceName"/> copes with both.
        /// </summary>
        [JsonProperty("from")]
        public JToken From { get; set; }

        [JsonProperty("attachments")]
        public List<JToken> Attachments { get; set; }

        [JsonProperty("links")]
        public List<JToken> Links { get; set; }

        [JsonProperty("mentions")]
        public List<JToken> Mentions { get; set; }

        [JsonProperty("contexts")]
        public List<JToken> Contexts { get; set; }

        /// <summary>True when this interaction is a prompt written by the user (not a Copilot response).</summary>
        [JsonIgnore]
        public bool IsUserPrompt =>
            string.Equals(InteractionType, InteractionTypes.UserPrompt, StringComparison.OrdinalIgnoreCase);
        /// <summary>
        /// Extracts a device name from the <c>from</c> identity set, tolerating both the documented
        /// <c>identity</c> object shape (<c>from.device.displayName</c>) and the plain-string shape
        /// (<c>from.device</c>) that has been observed in the wild. Returns null when absent.
        /// </summary>
        public string GetDeviceName()
        {
            var device = From?["device"];
            if (device == null || device.Type == JTokenType.Null)
                return null;

            if (device.Type == JTokenType.String)
                return device.ToString();

            var displayName = device["displayName"]?.ToString();
            return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        }
    }

    /// <summary>Graph <c>itemBody</c>. See the data-protection note on <see cref="AiInteraction"/>.</summary>
    public class AiInteractionBody
    {
        /// <summary><c>html</c> or <c>text</c>.</summary>
        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    /// <summary>Graph <c>aiInteractionType</c> values.</summary>
    public static class InteractionTypes
    {
        public const string UserPrompt = "userPrompt";
        public const string AiResponse = "aiResponse";
    }
}
