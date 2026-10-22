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
        /// Load the user's new sent messages since the last checkpoint. An implementation may stop at a per-cycle bound
        /// and say where to continue (<see cref="SentEmailLoadResult.NextPageLink"/>), so a first or reset read of a
        /// large mailbox is spread over several cycles instead of being held in memory, and saved, all at once.
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
            DeltaTokenWrites = 0,
            NextDeltaToken = null
        };

        public IReadOnlyList<GraphSentMessage> Messages { get; set; }
        public int DeltaTokenReads { get; set; }
        public int DeltaTokenWrites { get; set; }
        public string NextDeltaToken { get; set; }

        /// <summary>
        /// Set when the read stopped at its per-cycle bound with more pages still to come: the Graph
        /// <c>@odata.nextLink</c> to continue from on the next cycle, once this cycle's messages are saved. Never set
        /// together with <see cref="NextDeltaToken"/>, which only the last page of a round carries.
        /// </summary>
        public string NextPageLink { get; set; }

        /// <summary>True when Graph rejected the stored checkpoint and this load re-read the mailbox from the start.</summary>
        public bool CheckpointWasReset { get; set; }
    }

    /// <summary>
    /// Commits per-user sent-email delta tokens once the caller's durable save has succeeded.
    /// </summary>
    public interface ISentEmailDeltaTokenCommitter
    {
        Task CommitDeltaTokenAsync(Common.Entities.User user, string deltaToken);
    }

    /// <summary>
    /// Commits where a mailbox read that stopped at its per-cycle bound continues from, once the caller's durable save
    /// of the pages read so far has succeeded. Separate from <see cref="ISentEmailDeltaTokenCommitter"/> so existing
    /// implementations of that interface are unaffected; a committer without it simply re-reads those pages next cycle.
    /// </summary>
    public interface ISentEmailSyncContinuationCommitter
    {
        Task CommitContinuationAsync(Common.Entities.User user, string nextPageLink);
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
