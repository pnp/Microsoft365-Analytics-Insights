using Newtonsoft.Json;
using System;

namespace Common.Entities.Calls
{
    /// <summary>
    /// What the importer remembers about the call-records subscription it created. Graph never returns a subscription's
    /// clientState, so this is the only way to tell a subscription created with the current
    /// <see cref="CallWebhookClientState"/> from one created before it - or before the client secret was rotated.
    /// Holds the <see cref="CallWebhookClientState.Fingerprint"/>, never the clientState itself.
    /// </summary>
    /// <remarks>
    /// Stored as JSON in the runtime state table (partition <see cref="State.StatePartitions.CallsWebhook"/>, keyed by the
    /// notification URL). The importer writes it; the web app reads it to keep accepting a superseded subscription's
    /// notifications for <see cref="CallSubscriptionRules.PreviousClientStateWindow"/> after a client-secret rotation.
    /// Property names are part of the stored data: add new ones, never rename.
    /// </remarks>
    public sealed class CallSubscriptionRecord
    {
        [JsonProperty("subscriptionId")]
        public string SubscriptionId { get; set; }

        [JsonProperty("clientStateFingerprint")]
        public string ClientStateFingerprint { get; set; }

        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// When the subscription was last known to expire. Lets the importer say how long the webhook was down when it
        /// finds the subscription gone, and bounds how long the web app accepts it after a rotation the importer has not
        /// noticed yet. Null in records written before it was added.
        /// </summary>
        [JsonProperty("expiresUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? ExpiresUtc { get; set; }

        /// <summary>
        /// When the importer found that the client secret had changed since it created this subscription. From then the
        /// web app accepts its notifications for <see cref="CallSubscriptionRules.PreviousClientStateWindow"/> at most,
        /// while the importer replaces it. Null while the subscription's clientState is current.
        /// </summary>
        [JsonProperty("supersededUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? SupersededUtc { get; set; }
    }
}
