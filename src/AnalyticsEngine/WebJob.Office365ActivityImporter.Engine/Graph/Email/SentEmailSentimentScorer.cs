using Azure.AI.TextAnalytics;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Scores message bodies for sentiment using Azure AI Language, batching requests
    /// up to the per-call document limit.
    /// </summary>
    internal class AzureLanguageSentEmailSentimentScorer : ISentEmailSentimentScorer
    {
        private const int SentimentBatchSize = 10;
        private const int MaxSentimentDocChars = 5_000;

        private static readonly Regex HtmlTagRegex =
            new Regex("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly CognitiveServicesClient _client;
        private readonly AnalyticsLogger _telemetry;

        public AzureLanguageSentEmailSentimentScorer(CognitiveServicesClient client, AnalyticsLogger telemetry)
        {
            _client = client;
            _telemetry = telemetry;
        }

        public bool IsEnabled => _client != null;

        public async Task<Dictionary<string, double?>> ScoreAsync(IReadOnlyCollection<GraphSentMessage> messagesWithBody)
        {
            var result = new Dictionary<string, double?>(StringComparer.Ordinal);
            if (messagesWithBody == null || messagesWithBody.Count == 0)
                return result;

            var list = messagesWithBody as IList<GraphSentMessage> ?? messagesWithBody.ToList();
            // We slice with GetRange (not Skip().Take()) to avoid the O(N^2/K) walk that
            // List<T>.Skip incurs when called inside a chunking loop. At per-chunk sizes
            // up to a few thousand messages on a 200k-user tenant, this matters.
            // Materialise into a List<T> so GetRange is available without copying.
            var listAsList = list as List<GraphSentMessage> ?? list.ToList();

            for (int i = 0; i < listAsList.Count; i += SentimentBatchSize)
            {
                var take = Math.Min(SentimentBatchSize, listAsList.Count - i);
                var slice = listAsList.GetRange(i, take);
                var docs = new List<TextDocumentInput>(slice.Count);

                foreach (var msg in slice)
                {
                    var plain = StripHtml(msg.Body?.Content);
                    if (string.IsNullOrWhiteSpace(plain))
                        continue;
                    if (plain.Length > MaxSentimentDocChars)
                        plain = plain.Substring(0, MaxSentimentDocChars);
                    docs.Add(new TextDocumentInput(msg.Id, plain));
                }

                if (docs.Count == 0)
                    continue;

                try
                {
                    var response = await _client.ExecuteAsync(c => c.AnalyzeSentimentBatchAsync(docs));
                    foreach (var doc in response.Value)
                    {
                        if (doc.HasError)
                            continue;
                        result[doc.Id] = doc.DocumentSentiment.ConfidenceScores.Positive;
                    }
                }
                catch (Exception ex)
                {
                    _telemetry.LogWarning($"Cognitive batch analysis failed for {docs.Count} message(s): {ex.Message}");
                }
            }

            return result;
        }

        internal static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            var text = HtmlTagRegex.Replace(html, " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            return text.Trim();
        }
    }

    /// <summary>
    /// Builds the default <see cref="ISentEmailSentimentScorer"/> from <see cref="AppConfig"/>:
    /// returns the Azure AI Language scorer when configured, otherwise a no-op scorer.
    /// </summary>
    internal static class SentEmailSentimentScorerFactory
    {
        public static ISentEmailSentimentScorer Create(AppConfig settings, AnalyticsLogger telemetry)
        {
            if (settings == null || !settings.IsValidCognitiveConfig)
                return NullSentEmailSentimentScorer.Instance;

            // Single CognitiveServicesClient per importer run: caches its inner TextAnalyticsClient
            // and auto-falls back to RBAC if the key auth call returns 403 AuthenticationTypeDisabled.
            var client = settings.CreateCognitiveServicesClient(telemetry);
            if (client == null)
                return NullSentEmailSentimentScorer.Instance;

            return new AzureLanguageSentEmailSentimentScorer(client, telemetry);
        }
    }
}
