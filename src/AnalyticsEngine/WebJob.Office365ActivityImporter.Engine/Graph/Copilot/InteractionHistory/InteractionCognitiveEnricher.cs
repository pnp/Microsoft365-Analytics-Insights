using Azure;
using Azure.AI.TextAnalytics;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Enriches Copilot interactions with sentiment, detected language and key phrases.
    /// </summary>
    public interface IInteractionCognitiveEnricher
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Scores the given interactions in place, writing to <see cref="InteractionStats.SentimentScore"/>,
        /// <see cref="InteractionStats.LanguageName"/> and <see cref="InteractionStats.KeyPhrases"/>.
        /// </summary>
        /// <param name="stats">Stats objects to populate, aligned by index with <paramref name="bodies"/>.</param>
        /// <param name="bodies">
        /// Plain-text prompt bodies. Held only for the duration of the call and never returned.
        /// </param>
        /// <returns>Number of documents actually sent to Azure AI Language.</returns>
        Task<int> EnrichAsync(IReadOnlyList<InteractionStats> stats, IReadOnlyList<string> bodies);
    }

    /// <summary>No-op used when cognitive services aren't configured. Everything else still imports.</summary>
    public class NullInteractionCognitiveEnricher : IInteractionCognitiveEnricher
    {
        public static readonly NullInteractionCognitiveEnricher Instance = new NullInteractionCognitiveEnricher();

        public bool IsEnabled => false;

        public Task<int> EnrichAsync(IReadOnlyList<InteractionStats> stats, IReadOnlyList<string> bodies) => Task.FromResult(0);
    }

    /// <summary>
    /// Azure AI Language implementation: detects language, scores sentiment and extracts key phrases for
    /// <b>user prompts only</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copilot responses are deliberately not scored. They are model output rather than a signal about the
    /// user, they are far longer than prompts (so they would dominate the per-character cognitive bill), and
    /// "how did our people feel about what they asked for" is the question this feature exists to answer.
    /// </para>
    /// <para>
    /// Every call is best-effort: a cognitive failure logs and continues, because losing a sentiment score
    /// must never cost us the interaction statistics, which are the point of the import.
    /// </para>
    /// </remarks>
    public class AzureLanguageInteractionCognitiveEnricher : IInteractionCognitiveEnricher
    {
        /// <summary>Azure AI Language caps a batch at 10 documents.</summary>
        private const int BatchSize = 10;

        /// <summary>
        /// Prompts longer than this are truncated before scoring. Azure AI Language bills per character and
        /// rejects oversized documents; a prompt's sentiment and topic are established long before this point.
        /// </summary>
        internal const int MaxDocChars = 5_000;

        /// <summary>
        /// Cap on key phrases kept per prompt. Azure returns them ranked, so the tail is mostly noise, and an
        /// unbounded list would balloon the link table for one verbose prompt.
        /// </summary>
        internal const int MaxKeyPhrasesPerPrompt = 10;

        /// <summary>
        /// Matches the <c>keywords.name</c> column width. Longer phrases are dropped rather than truncated -
        /// a chopped phrase is meaningless as a topic, and truncation would silently merge distinct phrases
        /// that share a prefix.
        /// </summary>
        internal const int MaxKeyPhraseLength = 100;

        private readonly CognitiveServicesClient _client;
        private readonly ILogger _logger;

        public AzureLanguageInteractionCognitiveEnricher(CognitiveServicesClient client, ILogger logger)
        {
            _client = client;
            _logger = logger;
        }

        public bool IsEnabled => _client != null;

        public async Task<int> EnrichAsync(IReadOnlyList<InteractionStats> stats, IReadOnlyList<string> bodies)
        {
            if (_client == null || stats == null || bodies == null || stats.Count == 0)
                return 0;
            if (stats.Count != bodies.Count)
                throw new ArgumentException("stats and bodies must be the same length.", nameof(bodies));

            // Build the documents once. The index into this list is the document id, so results can be mapped
            // back without ever needing to look at the text again.
            var docs = new List<TextDocumentInput>(stats.Count);
            var statsByDocId = new Dictionary<string, InteractionStats>(StringComparer.Ordinal);

            for (int i = 0; i < stats.Count; i++)
            {
                var s = stats[i];
                if (s == null || !s.IsUserPrompt)
                    continue;

                var text = bodies[i];
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (text.Length > MaxDocChars)
                    text = text.Substring(0, MaxDocChars);

                var docId = i.ToString();
                docs.Add(new TextDocumentInput(docId, text));
                statsByDocId[docId] = s;
            }

            if (docs.Count == 0)
                return 0;

            var scored = 0;
            for (int i = 0; i < docs.Count; i += BatchSize)
            {
                // GetRange, not Skip().Take() - Skip on a List walks every preceding element, so chunking
                // inside a loop is quadratic. Same reasoning as the sent-email scorer.
                var slice = docs.GetRange(i, Math.Min(BatchSize, docs.Count - i));

                await DetectLanguagesAsync(slice, statsByDocId);
                await ScoreSentimentAsync(slice, statsByDocId);
                await ExtractKeyPhrasesAsync(slice, statsByDocId);

                scored += slice.Count;
            }

            return scored;
        }

        private async Task DetectLanguagesAsync(List<TextDocumentInput> slice, Dictionary<string, InteractionStats> statsByDocId)
        {
            try
            {
                var inputs = slice.Select(d => new DetectLanguageInput(d.Id, d.Text)).ToList();
                var response = await _client.ExecuteAsync(c => c.DetectLanguageBatchAsync(inputs));

                foreach (var doc in response.Value)
                {
                    if (doc.HasError || !statsByDocId.TryGetValue(doc.Id, out var target))
                        continue;
                    target.LanguageName = doc.PrimaryLanguage.Name;
                }
            }
            catch (Exception ex)
            {
                LogCognitiveFailure(ex, "language detection", slice.Count);
            }
        }

        private async Task ScoreSentimentAsync(List<TextDocumentInput> slice, Dictionary<string, InteractionStats> statsByDocId)
        {
            try
            {
                var response = await _client.ExecuteAsync(c => c.AnalyzeSentimentBatchAsync(slice));

                foreach (var doc in response.Value)
                {
                    if (doc.HasError || !statsByDocId.TryGetValue(doc.Id, out var target))
                        continue;

                    // Store the positive-confidence score rather than the categorical label, matching what
                    // the sent-email import records so the two are directly comparable in reports.
                    target.SentimentScore = doc.DocumentSentiment.ConfidenceScores.Positive;
                }
            }
            catch (Exception ex)
            {
                LogCognitiveFailure(ex, "sentiment analysis", slice.Count);
            }
        }

        private async Task ExtractKeyPhrasesAsync(List<TextDocumentInput> slice, Dictionary<string, InteractionStats> statsByDocId)
        {
            try
            {
                var response = await _client.ExecuteAsync(c => c.ExtractKeyPhrasesBatchAsync(slice));

                foreach (var doc in response.Value)
                {
                    if (doc.HasError || !statsByDocId.TryGetValue(doc.Id, out var target))
                        continue;

                    target.KeyPhrases = NormaliseKeyPhrases(doc.KeyPhrases);
                }
            }
            catch (Exception ex)
            {
                LogCognitiveFailure(ex, "key phrase extraction", slice.Count);
            }
        }

        /// <summary>
        /// Trims, de-duplicates case-insensitively, drops phrases too long for the <c>keywords</c> lookup and
        /// caps the count. Azure returns phrases in rank order, so taking the head keeps the most salient.
        /// </summary>
        internal static List<string> NormaliseKeyPhrases(IEnumerable<string> phrases)
        {
            var result = new List<string>();
            if (phrases == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in phrases)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var phrase = raw.Trim();
                if (phrase.Length > MaxKeyPhraseLength)
                    continue;

                // The set already compares case-insensitively, so no ToLower() here - that would allocate a
                // string per phrase for no benefit.
                if (!seen.Add(phrase))
                    continue;

                result.Add(phrase);
                if (result.Count >= MaxKeyPhrasesPerPrompt)
                    break;
            }

            return result;
        }

        private void LogCognitiveFailure(Exception ex, string operation, int docCount)
        {
            // Warning, not Error: cognitive enrichment is optional and the import continues without it.
            //
            // Only RequestFailedException detail is logged. Azure AI Language's own service errors are
            // status/code pairs that never echo the document, but an arbitrary exception message can:
            // a serialisation or argument failure routinely quotes the offending value, which here is the
            // user's literal Copilot prompt. Logging ex.Message on the general path would write prompt text
            // into Application Insights in clear - the one thing this feature must never do - so unexpected
            // exceptions are reduced to their type name.
            if (ex is RequestFailedException rfe)
            {
                _logger.LogWarning($"Copilot interaction {operation} failed for {docCount} prompt(s) " +
                    $"(HTTP {rfe.Status}, code '{rfe.ErrorCode ?? "unknown"}'): {rfe.Message}. Continuing without scores for this batch.");
            }
            else
            {
                _logger.LogWarning($"Copilot interaction {operation} failed for {docCount} prompt(s) " +
                    $"({ex.GetType().Name}). Continuing without scores for this batch. " +
                    "The exception message is withheld because it can contain prompt text.");
            }
        }
    }

    /// <summary>
    /// Builds the enricher from <see cref="AppConfig"/>: the Azure AI Language one when cognitive services
    /// are configured, otherwise the no-op.
    /// </summary>
    public static class InteractionCognitiveEnricherFactory
    {
        public static IInteractionCognitiveEnricher Create(AppConfig settings, ILogger logger)
        {
            if (settings == null || !settings.IsValidCognitiveConfig)
                return NullInteractionCognitiveEnricher.Instance;

            // One client per importer run: it caches the inner TextAnalyticsClient and falls back to RBAC by
            // itself if the resource has key auth disabled.
            var client = settings.CreateCognitiveServicesClient(logger);
            if (client == null)
                return NullInteractionCognitiveEnricher.Instance;

            return new AzureLanguageInteractionCognitiveEnricher(client, logger);
        }
    }
}
