using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;

namespace Tests.FakeDataGen.StressTests.FakeLoaders
{
    /// <summary>
    /// Fake <see cref="ISentEmailSentimentScorer"/> that assigns every message a synthetic
    /// cognitive/sentiment score in the [0,1] range without calling Azure AI Language, so the
    /// stress test exercises the scoring + persistence path and writes real
    /// <c>sent_emails.cognitive_score</c> values instead of NULLs.
    ///
    /// Following the codebase convention, the score is the "positive" end of the scale:
    /// <c>0 = very unhappy, 1 = very happy</c>. Replies are deliberately skewed towards the happy
    /// end (mean ~0.7) but with a gentle "mood wave" along each user's message sequence plus
    /// per-message noise and the occasional bad day, so the data has realistic ups and downs
    /// rather than a single constant value.
    /// </summary>
    public class FakeSentEmailSentimentScorer : ISentEmailSentimentScorer
    {
        private readonly int _seed;

        public FakeSentEmailSentimentScorer(int seed = 2024)
        {
            _seed = seed;
        }

        // Enabled so the importer requests message bodies and runs the scoring path end-to-end.
        public bool IsEnabled => true;

        public Task<Dictionary<string, double?>> ScoreAsync(IReadOnlyCollection<GraphSentMessage> messagesWithBody)
        {
            var result = new Dictionary<string, double?>(StringComparer.Ordinal);
            if (messagesWithBody == null)
                return Task.FromResult(result);

            foreach (var msg in messagesWithBody)
            {
                if (string.IsNullOrEmpty(msg?.Id))
                    continue;
                result[msg.Id] = ScoreForMessage(msg.Id);
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// Produces a deterministic, reproducible score in [0,1] for a message id. Generally happy
        /// but with a sine "mood wave" over the per-user message sequence, random noise and an
        /// occasional bad day so the score dips down too.
        /// </summary>
        private double ScoreForMessage(string messageId)
        {
            long sequence = ExtractSequence(messageId);

            // Deterministic per-message RNG so reruns produce identical scores.
            var random = new Random(unchecked(_seed * 397 + StableHash(messageId)));

            // Happy baseline.
            double mood = 0.68;

            // Gentle ups and downs across the user's message timeline.
            mood += 0.18 * Math.Sin(sequence / 6.0);

            // Per-message noise, +/- 0.12.
            mood += (random.NextDouble() - 0.5) * 0.24;

            // Roughly one message in ten is a "bad day" that pulls the score well down.
            if (random.NextDouble() < 0.10)
                mood -= 0.35 + random.NextDouble() * 0.35;

            if (mood < 0.0) mood = 0.0;
            if (mood > 1.0) mood = 1.0;
            return mood;
        }

        /// <summary>
        /// Extracts the trailing numeric "sequence" from a fake message id of the shape
        /// <c>stress-msg-NNNNNN-SSSSSS</c> so the mood wave progresses along a user's messages.
        /// Falls back to a stable hash when the id doesn't match (the wave just becomes noise).
        /// </summary>
        private static long ExtractSequence(string messageId)
        {
            int lastDash = messageId.LastIndexOf('-');
            if (lastDash >= 0 && lastDash < messageId.Length - 1)
            {
                var tail = messageId.Substring(lastDash + 1);
                if (long.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                    return seq;
            }
            return StableHash(messageId);
        }

        /// <summary>
        /// Process-stable hash (FNV-1a) so scores are reproducible across runs - unlike
        /// <see cref="string.GetHashCode"/>, which can be randomised per process.
        /// </summary>
        private static int StableHash(string value)
        {
            unchecked
            {
                const int fnvPrime = 16777619;
                int hash = (int)2166136261;
                foreach (var ch in value)
                {
                    hash = (hash ^ ch) * fnvPrime;
                }
                return hash & 0x7FFFFFFF;
            }
        }
    }
}
