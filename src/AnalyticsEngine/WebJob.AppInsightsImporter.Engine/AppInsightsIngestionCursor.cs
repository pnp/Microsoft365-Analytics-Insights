using Common.Entities.State;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>How a cycle decides what to read.</summary>
    public enum AppInsightsReadMode
    {
        /// <summary>Rows ingested since the stored cursor (less an overlap), whatever day their timestamp falls on.</summary>
        Incremental,

        /// <summary>No cursor yet: whole days from the newest stored hit, as before the cursor existed. Sets the cursor.</summary>
        Bootstrap,

        /// <summary><c>ReadHitsDaysBeforeToday</c>: whole days from that many days ago. Leaves the cursor alone.</summary>
        Backfill
    }

    /// <summary>
    /// The incremental-read rule: which rows a cycle reads once it knows how far ingestion has already been imported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The import used to resume from the newest stored hit, which is a browser-clock timestamp, rewound a minute. Once a
    /// hit after 00:01 on day D was stored, D was never read again, so telemetry for D that reached Application Insights
    /// later - a browser that was offline, a batch sent late - was never imported; and every cycle re-read and re-staged
    /// the whole current day. The cursor is <c>ingestion_time()</c> instead: when App Insights made the row queryable.
    /// Each cycle reads the rows ingested since the cursor, whatever their timestamp, and the cursor only moves once
    /// every one of them has saved.
    /// </para>
    /// <para>
    /// Azure Monitor documents the bounds this relies on (https://learn.microsoft.com/azure/azure-monitor/logs/data-ingestion-time):
    /// "The TimeGenerated value can't be more than two days before the received time or more than a day in the future",
    /// and "Use ingestion_time() if you need to filter only records that were ingested in a certain time window. In such
    /// cases, also add a TimeGenerated filter with a larger range." <c>ingestion_time()</c> is approximate
    /// (https://learn.microsoft.com/kusto/query/ingestion-time-function), hence <see cref="Overlap"/>.
    /// </para>
    /// </remarks>
    public static class AppInsightsIngestionCursorPolicy
    {
        /// <summary>
        /// Each cycle re-reads this much ingestion before the cursor. <c>ingestion_time()</c> is approximate and
        /// concurrent ingestion is not ordered, so a row stamped just before the previous cycle's bound can become
        /// queryable after it. Re-reading is idempotent: the merges skip what is already stored.
        /// </summary>
        public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How far a row's timestamp can be before its ingestion: two days by the documented rule, plus a day of margin
        /// for the time between receipt and ingestion. Only bounds the query (and lets it prune by time).
        /// </summary>
        public static readonly TimeSpan LateArrivalBound = TimeSpan.FromDays(3);

        /// <summary>How far a row's timestamp can be ahead of now: one day by the documented rule, plus a day of margin.</summary>
        public static readonly TimeSpan FutureBound = TimeSpan.FromDays(2);

        /// <summary>
        /// The oldest ingestion a cycle reads from, however old the cursor: Application Insights' default retention.
        /// A cursor older than this belongs to an importer that was stopped for months; the rows before it are gone.
        /// </summary>
        public static readonly TimeSpan MaxLookback = TimeSpan.FromDays(90);

        /// <summary>
        /// What a cycle reads when the cursor is <paramref name="cursorUtc"/>: rows ingested from the cursor less
        /// <see cref="Overlap"/> (never earlier than <see cref="MaxLookback"/> ago) up to <paramref name="nowUtc"/>,
        /// with timestamps bounded by <see cref="LateArrivalBound"/> and <see cref="FutureBound"/>.
        /// </summary>
        public static AppInsightsReadWindow IncrementalRange(DateTime cursorUtc, DateTime nowUtc)
        {
            var ingestedFrom = cursorUtc - Overlap;
            if (ingestedFrom < nowUtc - MaxLookback)
            {
                ingestedFrom = nowUtc - MaxLookback;
            }
            if (ingestedFrom >= nowUtc)
            {
                // A cursor ahead of this host's clock (the clock was moved back, or another host wrote it): read the overlap.
                ingestedFrom = nowUtc - Overlap;
            }
            return new AppInsightsReadWindow(ingestedFrom - LateArrivalBound, nowUtc + FutureBound, ingestedFrom, nowUtc);
        }
    }

    /// <summary>Where the ingestion cursor is kept between cycles and restarts.</summary>
    public interface IAppInsightsIngestionCursorStore
    {
        /// <summary>For log lines.</summary>
        string Description { get; }

        /// <summary>The cursor, or null when there is none yet. Throws when the store cannot be read.</summary>
        Task<DateTime?> ReadAsync();

        /// <summary>Everything ingested before <paramref name="cursorUtc"/> has been saved.</summary>
        Task WriteAsync(DateTime cursorUtc);
    }

    /// <summary>
    /// The ingestion cursor in the runtime state table (<see cref="StatePartitions.AppInsightsImport"/>), or in an
    /// in-memory store when no Storage is configured. Keyed by the Application Insights application id, so pointing
    /// the importer at a different resource starts that resource from scratch instead of from another one's cursor.
    /// </summary>
    public sealed class KeyValueIngestionCursorStore : IAppInsightsIngestionCursorStore
    {
        /// <summary>The key prefix. Part of the stored data's address: do not rename.</summary>
        public const string KeyPrefix = "IngestionCursor";

        private readonly IKeyValueStore _store;
        private readonly string _key;

        public KeyValueIngestionCursorStore(IKeyValueStore store, string appInsightsApplicationId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _key = string.IsNullOrWhiteSpace(appInsightsApplicationId) ? KeyPrefix : KeyPrefix + ":" + appInsightsApplicationId.Trim();
        }

        public string Description => _store.Description + ", key '" + _key + "'";

        public async Task<DateTime?> ReadAsync()
        {
            var value = await _store.GetStringAsync(_key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return DateTime.ParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        public Task WriteAsync(DateTime cursorUtc)
        {
            var utc = cursorUtc.Kind == DateTimeKind.Utc ? cursorUtc : DateTime.SpecifyKind(cursorUtc, DateTimeKind.Utc);
            return _store.SetStringAsync(_key, utc.ToString("o", CultureInfo.InvariantCulture));
        }
    }
}
