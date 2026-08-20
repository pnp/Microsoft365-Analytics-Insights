using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// The content-free projection of a Graph <c>aiInteraction</c>. This is the only representation that is
    /// allowed past the importer's boundary, and it deliberately has no field capable of holding the prompt
    /// or response text.
    /// </summary>
    public class InteractionStats
    {
        public string GraphInteractionId { get; set; }
        public string SessionRef { get; set; }
        public string RequestId { get; set; }

        /// <summary><c>userPrompt</c> or <c>aiResponse</c>.</summary>
        public string InteractionType { get; set; }

        public string AppClass { get; set; }
        public string ConversationType { get; set; }
        public string Locale { get; set; }
        public string Device { get; set; }

        public DateTime CreatedUtc { get; set; }

        public int BodyCharCount { get; set; }
        public int BodyWordCount { get; set; }
        public int AttachmentCount { get; set; }
        public int LinkCount { get; set; }
        public int MentionCount { get; set; }
        public int ContextCount { get; set; }

        /// <summary>Set only on <c>aiResponse</c> rows whose matching prompt was in the same batch.</summary>
        public int? ResponseLatencyMs { get; set; }

        public bool IsUserPrompt =>
            string.Equals(InteractionType, InteractionTypes.UserPrompt, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True only for an explicit <c>aiResponse</c>. Checked positively rather than as "not a prompt" so
        /// that a future or unknown interaction type (the schema declares an <c>unknownFutureValue</c>
        /// sentinel) is never mistaken for a Copilot answer and given a response latency.
        /// </summary>
        public bool IsAiResponse =>
            string.Equals(InteractionType, InteractionTypes.AiResponse, StringComparison.OrdinalIgnoreCase);

        #region Cognitive - populated only when Azure AI Language is configured, and only for prompts

        public double? SentimentScore { get; set; }
        public string LanguageName { get; set; }
        public List<string> KeyPhrases { get; set; } = new List<string>();

        #endregion
    }

    /// <summary>
    /// Converts Graph <c>aiInteraction</c> payloads into <see cref="InteractionStats"/>.
    /// </summary>
    /// <remarks>
    /// This class is the privacy boundary of the feature: everything downstream of it sees counts, never
    /// content. It also does the <c>requestId</c> pairing that produces response latency - the single piece
    /// of insight this API offers that neither the audit feed nor the Graph usage reports can.
    /// </remarks>
    public static class InteractionStatsExtractor
    {
        /// <summary>
        /// Absurd latencies are dropped rather than stored. A prompt and its response can legitimately be
        /// minutes apart (long generations, or a client that batches its telemetry), but anything beyond an
        /// hour is far more likely to be a recycled <c>requestId</c> or a clock problem than a real wait, and
        /// a handful of such rows would wreck any average.
        /// </summary>
        internal const int MaxPlausibleLatencyMs = 60 * 60 * 1000;

        /// <summary>
        /// Projects a batch of interactions to stats and fills in <see cref="InteractionStats.ResponseLatencyMs"/>
        /// by pairing prompts with responses on <c>requestId</c>.
        /// </summary>
        /// <remarks>
        /// Pairing is done over the whole batch rather than per session because a request id is unique across
        /// the user's history. Rows whose prompt fell outside this batch (for instance the prompt was already
        /// imported on a previous run) simply get no latency, which is correct - a wrong number would be worse
        /// than a missing one.
        /// </remarks>
        public static List<InteractionStats> Extract(IEnumerable<AiInteraction> interactions)
        {
            if (interactions == null)
                return new List<InteractionStats>();

            var stats = new List<InteractionStats>();
            foreach (var interaction in interactions)
            {
                var s = ToStats(interaction);
                if (s != null)
                    stats.Add(s);
            }

            ApplyResponseLatencies(stats);
            return stats;
        }

        /// <summary>
        /// Projects one interaction. Returns null for a payload we can't key on (no id, no session, or no
        /// timestamp) because such a row could neither be de-duplicated nor ordered.
        /// </summary>
        public static InteractionStats ToStats(AiInteraction interaction)
        {
            if (interaction == null)
                return null;
            if (string.IsNullOrWhiteSpace(interaction.Id))
                return null;
            if (string.IsNullOrWhiteSpace(interaction.SessionId))
                return null;
            if (interaction.CreatedDateTime == null)
                return null;

            // Strip markup before measuring so an HTML-heavy body isn't reported as a longer prompt than the
            // identical plain-text one. The stripped text is a local that goes out of scope immediately.
            var plainBody = StringUtils.StripHtmlToPlainText(interaction.Body?.Content);

            return new InteractionStats
            {
                GraphInteractionId = interaction.Id,
                SessionRef = interaction.SessionId,
                RequestId = interaction.RequestId,
                InteractionType = interaction.InteractionType,
                AppClass = interaction.AppClass,
                ConversationType = interaction.ConversationType,
                Locale = interaction.Locale,
                Device = interaction.GetDeviceName(),
                CreatedUtc = ToUtc(interaction.CreatedDateTime.Value),
                BodyCharCount = plainBody?.Length ?? 0,
                BodyWordCount = StringUtils.CountWords(plainBody),
                AttachmentCount = interaction.Attachments?.Count ?? 0,
                LinkCount = interaction.Links?.Count ?? 0,
                MentionCount = interaction.Mentions?.Count ?? 0,
                ContextCount = interaction.Contexts?.Count ?? 0,
            };
        }

        /// <summary>
        /// Pairs each <c>aiResponse</c> with the <c>userPrompt</c> sharing its request id and records the gap
        /// in milliseconds on the response row.
        /// </summary>
        internal static void ApplyResponseLatencies(List<InteractionStats> stats)
        {
            if (stats == null || stats.Count == 0)
                return;

            // Earliest prompt per request id. Ordinal comparison: request ids are opaque Graph identifiers,
            // so case-insensitive matching would risk collapsing two genuinely different ids.
            var earliestPromptByRequestId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var s in stats)
            {
                if (string.IsNullOrEmpty(s.RequestId) || !s.IsUserPrompt)
                    continue;

                if (!earliestPromptByRequestId.TryGetValue(s.RequestId, out var existing) || s.CreatedUtc < existing)
                    earliestPromptByRequestId[s.RequestId] = s.CreatedUtc;
            }

            if (earliestPromptByRequestId.Count == 0)
                return;

            foreach (var s in stats)
            {
                if (!s.IsAiResponse || string.IsNullOrEmpty(s.RequestId))
                    continue;
                if (!earliestPromptByRequestId.TryGetValue(s.RequestId, out var promptCreated))
                    continue;

                var deltaMs = (s.CreatedUtc - promptCreated).TotalMilliseconds;

                // Negative means the response is timestamped before its prompt, which can only be clock skew
                // between the producing services. Storing it would poison any average, so skip it.
                if (deltaMs < 0 || deltaMs > MaxPlausibleLatencyMs)
                    continue;

                s.ResponseLatencyMs = (int)deltaMs;
            }
        }

        /// <summary>
        /// Normalises to UTC. Graph sends an offset, but Json.NET may hand back Local or Unspecified kinds
        /// depending on the payload, and everything downstream (watermarks, reports) assumes UTC.
        /// </summary>
        internal static DateTime ToUtc(DateTime value)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    return value;
                case DateTimeKind.Local:
                    return value.ToUniversalTime();
                default:
                    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Newest <c>createdDateTime</c> in a batch, used as the next per-user watermark. Null for an empty
        /// batch so the caller can leave the existing watermark untouched.
        /// </summary>
        public static DateTime? GetNewestCreatedUtc(IEnumerable<InteractionStats> stats)
        {
            if (stats == null)
                return null;

            DateTime? newest = null;
            foreach (var s in stats)
            {
                if (newest == null || s.CreatedUtc > newest.Value)
                    newest = s.CreatedUtc;
            }
            return newest;
        }
    }
}
