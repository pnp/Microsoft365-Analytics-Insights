using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Provider-agnostic abstraction for scoring the sentiment of sent email messages.
    /// </summary>
    public interface ISentEmailSentimentScorer
    {
        /// <summary>
        /// True when scoring is configured and message bodies should be requested from the source loader.
        /// Implementations that always return null/empty results should return false to allow callers
        /// to skip retrieving (and transferring) the message body.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Score the supplied messages and return a map of <c>messageId -&gt; positive sentiment score</c>.
        /// Messages without a usable body should be skipped silently.
        /// </summary>
        Task<Dictionary<string, double?>> ScoreAsync(IReadOnlyCollection<GraphSentMessage> messagesWithBody);
    }

    /// <summary>
    /// No-op scorer used when sentiment analysis is not configured. Returns an empty result and
    /// declares itself disabled so callers can skip body retrieval.
    /// </summary>
    public sealed class NullSentEmailSentimentScorer : ISentEmailSentimentScorer
    {
        public static readonly NullSentEmailSentimentScorer Instance = new NullSentEmailSentimentScorer();

        public bool IsEnabled => false;

        public Task<Dictionary<string, double?>> ScoreAsync(IReadOnlyCollection<GraphSentMessage> messagesWithBody)
        {
            return Task.FromResult<Dictionary<string, double?>>(null);
        }
    }
}
