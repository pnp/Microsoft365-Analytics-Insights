using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>One SQL merge: lookup plus fact inserts, facts alone, invalid inputs, and searches still awaiting visits.</summary>
    public sealed class SearchSaveResult
    {
        public static readonly SearchSaveResult Empty = new SearchSaveResult(0, 0, 0, new Dictionary<DateTime, int>());

        public SearchSaveResult(int rowsAffected, int factsInserted, int skipped, IReadOnlyDictionary<DateTime, int> unmatchedByHour, int missingIngestionTime = 0)
        {
            RowsAffected = rowsAffected;
            FactsInserted = factsInserted;
            Skipped = skipped;
            UnmatchedByHour = unmatchedByHour;
            MissingIngestionTime = missingIngestionTime;
        }

        public int RowsAffected { get; }
        public int FactsInserted { get; }
        public int Skipped { get; }
        public IReadOnlyDictionary<DateTime, int> UnmatchedByHour { get; }
        public int MissingIngestionTime { get; }
        public int Unmatched => MissingIngestionTime + UnmatchedByHour.Values.Sum();
    }
}
