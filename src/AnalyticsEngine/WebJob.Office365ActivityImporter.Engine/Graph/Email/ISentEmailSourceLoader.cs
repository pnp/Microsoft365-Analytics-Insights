using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Source-agnostic loader for sent emails for a single user mailbox.
    /// </summary>
    public interface ISentEmailSourceLoader
    {
        /// <summary>
        /// Verify that the loader has the access required to read mail data. Implementations
        /// should return <c>false</c> when the configured identity is missing the necessary
        /// permission so callers can skip the import gracefully instead of failing per-user.
        /// </summary>
        Task<bool> HasMailReadAccessAsync();

        /// <summary>
        /// Load all new sent messages for the user since the last delta token.
        /// </summary>
        /// <param name="user">User whose mailbox should be scanned.</param>
        /// <param name="includeBody">Whether the message body must be returned. Implementations may
        /// omit the body to save bandwidth when sentiment scoring is disabled.</param>
        Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(Common.Entities.User user, bool includeBody);
    }

    /// <summary>
    /// Result of loading sent emails for one user.
    /// </summary>
    public class SentEmailLoadResult
    {
        public static readonly SentEmailLoadResult Empty = new SentEmailLoadResult
        {
            Messages = Array.Empty<GraphSentMessage>(),
            DeltaTokenReads = 0,
            DeltaTokenWrites = 0
        };

        public IReadOnlyList<GraphSentMessage> Messages { get; set; }
        public int DeltaTokenReads { get; set; }
        public int DeltaTokenWrites { get; set; }
    }

    #region Graph DTO classes

    public class GraphSentMessage
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("subject")]
        public string Subject { get; set; }

        [JsonProperty("sentDateTime")]
        public DateTime? SentDateTime { get; set; }

        [JsonProperty("from")]
        public GraphEmailRecipient From { get; set; }

        [JsonProperty("toRecipients")]
        public List<GraphEmailRecipient> ToRecipients { get; set; }

        [JsonProperty("body")]
        public GraphEmailBody Body { get; set; }
    }

    public class GraphEmailRecipient
    {
        [JsonProperty("emailAddress")]
        public GraphEmailAddress EmailAddress { get; set; }
    }

    public class GraphEmailAddress
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }
    }

    public class GraphEmailBody
    {
        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    #endregion
}
