using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
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
        Backfill,

        /// <summary>Only pending search hours due by the last completed normal read. Does not advance the cursor.</summary>
        SearchRecovery
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
    /// they have been processed. An unmatched search's ingestion hour must be checkpointed for retry before the cursor
    /// can advance; the search itself stays in Application Insights.
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

        /// <summary>
        /// How far a stored hit's timestamp can be ahead of when it was ingested: a day, by the documented rule above.
        /// A cursor rewound to the newest stored hit is rewound this much further, so no row ingested after it is missed.
        /// </summary>
        public static readonly TimeSpan TimestampAheadOfIngestionBound = TimeSpan.FromDays(1);

        /// <summary>
        /// Margin for comparing the newest stored hit with the one recorded beside the cursor: the stored value
        /// round-trips through SQL <c>datetime</c> (3 ms precision) and text.
        /// </summary>
        private static readonly TimeSpan NewestHitComparisonTolerance = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Whether the database lost hits that the cursor says were imported - a point-in-time restore, or a copy of an
        /// older database - and if so, the cursor to read from instead; null to keep <paramref name="cursorUtc"/> (#849).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cursor lives in the state table, outside the database, so restoring the database does not move it back.
        /// Without this, every row ingested between the restore point and the cursor was never read again.
        /// </para>
        /// <para>
        /// The newest stored hit only ever moves forward while the database is the one the cursor was written for, so the
        /// newest stored hit recorded when the cursor was written (<paramref name="newestStoredHitAtCursorUtc"/>) is the
        /// test: the database now holding an older newest hit than that means hits went missing. A cursor written before
        /// that was recorded has no such record, so the fallback is a cursor more than <see cref="LateArrivalBound"/>
        /// ahead of the newest stored hit. That fallback can also fire for a tenant that has had no tracked page views
        /// for days, but only once: the cycle records the newest stored hit beside the cursor it writes.
        /// </para>
        /// <para>
        /// Re-reading is safe because the merges skip page-request ids that are already stored, and
        /// <see cref="IncrementalRange"/> still caps it at <see cref="MaxLookback"/>, Application Insights' retention.
        /// </para>
        /// </remarks>
        public static DateTime? CursorAfterDatabaseRestore(DateTime cursorUtc, DateTime newestStoredHitUtc, DateTime? newestStoredHitAtCursorUtc)
            => CursorAfterDatabaseRestore(cursorUtc, newestStoredHitUtc, newestStoredHitAtCursorUtc, out _);

        internal static DateTime? CursorAfterDatabaseRestore(DateTime cursorUtc, DateTime newestStoredHitUtc,
            DateTime? newestStoredHitAtCursorUtc, out bool hitsWentMissing)
        {
            hitsWentMissing = newestStoredHitAtCursorUtc.HasValue
                ? newestStoredHitUtc < newestStoredHitAtCursorUtc.Value - NewestHitComparisonTolerance
                : cursorUtc > newestStoredHitUtc + LateArrivalBound;
            if (!hitsWentMissing)
            {
                return null;
            }

            var rewound = newestStoredHitUtc - TimestampAheadOfIngestionBound;
            return rewound < cursorUtc ? rewound : (DateTime?)null;
        }
    }

    /// <summary>
    /// Bootstrap and database-restore reads start from the newest SQL hit. Partial SQL commits can change that
    /// hit before the cursor is written. Pin the original start to the old cursor so a restart can replay it.
    /// The next cursor invalidates this record without a separate cleanup write.
    /// </summary>
    public sealed class AppInsightsReplayStart
    {
        public AppInsightsReplayStart(DateTime? storedCursorUtc, DateTime fromUtc, DateTime? effectiveCursorUtc)
        {
            StoredCursorUtc = storedCursorUtc;
            FromUtc = fromUtc;
            EffectiveCursorUtc = effectiveCursorUtc;
        }

        public DateTime? StoredCursorUtc { get; }
        public DateTime FromUtc { get; }
        public DateTime? EffectiveCursorUtc { get; }
    }

    /// <summary>Where the ingestion cursor is kept between cycles and restarts.</summary>
    public interface IAppInsightsIngestionCursorStore
    {
        /// <summary>For log lines.</summary>
        string Description { get; }

        /// <summary>The cursor, or null when there is none yet. Throws when the store cannot be read.</summary>
        Task<DateTime?> ReadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// The newest stored hit when the cursor was last written. An explicitly empty database differs from an
        /// older cursor with no database watermark. Throws when the store cannot be read.
        /// </summary>
        Task<(DateTime? NewestHitUtc, bool KnownEmpty)> ReadDatabaseWatermarkAsync(DateTime cursorUtc, CancellationToken cancellationToken);
        Task<DateTime?> ReadNewestStoredHitAtCursorAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Normal import has processed ingestion before <paramref name="cursorUtc"/>; retryable search hours have
        /// already been checkpointed. The newest stored hit is <paramref name="newestStoredHitUtc"/> (null: no hits).
        /// </summary>
        Task WriteAsync(DateTime cursorUtc, DateTime? newestStoredHitUtc, CancellationToken cancellationToken = default);

        /// <summary>Pending ingestion-hour markers, not telemetry. Throws if the store cannot be read.</summary>
        Task<IReadOnlyList<AppInsightsSearchRetry>> ReadSearchRetriesAsync(CancellationToken cancellationToken = default);
        Task WriteSearchRetriesAsync(IReadOnlyCollection<AppInsightsSearchRetry> retries, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<DateTime>> ReadSearchReplayHoursAsync(CancellationToken cancellationToken);
        Task WriteSearchReplayHoursAsync(IReadOnlyCollection<DateTime> hours, CancellationToken cancellationToken);
        Task<AppInsightsReplayStart> ReadReplayStartAsync(CancellationToken cancellationToken);
        Task WriteReplayStartAsync(AppInsightsReplayStart start, CancellationToken cancellationToken);
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

        /// <summary>
        /// The key prefix of the newest stored hit recorded beside the cursor (#849). A separate key, so a build that
        /// predates it still reads the cursor. Part of the stored data's address: do not rename.
        /// </summary>
        public const string NewestHitKeyPrefix = "IngestionCursorNewestHit";

        private readonly IKeyValueStore _store;
        private readonly string _key;
        private readonly string _newestHitKey;
        private readonly string _emptyAtCursorKey;
        private readonly string _searchRetryKey;
        private readonly string _searchReplayKey;
        private readonly string _replayStartKey;

        public KeyValueIngestionCursorStore(IKeyValueStore store, string appInsightsApplicationId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            var suffix = string.IsNullOrWhiteSpace(appInsightsApplicationId) ? string.Empty : ":" + appInsightsApplicationId.Trim();
            _key = KeyPrefix + suffix;
            _newestHitKey = NewestHitKeyPrefix + suffix;
            _emptyAtCursorKey = "IngestionCursorEmptyAt" + suffix;
            _searchRetryKey = "IngestionCursorSearchRetries" + suffix;
            _searchReplayKey = "IngestionCursorSearchReplayHours" + suffix;
            _replayStartKey = "IngestionCursorReplayStart" + suffix;
        }

        public string Description => _store.Description + ", key '" + _key + "'";

        public Task<DateTime?> ReadAsync(CancellationToken cancellationToken = default) => ReadUtcAsync(_key, cancellationToken);

        public Task<DateTime?> ReadNewestStoredHitAtCursorAsync(CancellationToken cancellationToken = default) => ReadUtcAsync(_newestHitKey, cancellationToken);

        public async Task<(DateTime? NewestHitUtc, bool KnownEmpty)> ReadDatabaseWatermarkAsync(DateTime cursorUtc, CancellationToken cancellationToken)
        {
            var newest = await ReadNewestStoredHitAtCursorAsync(cancellationToken).ConfigureAwait(false);
            var knownEmpty = !newest.HasValue && await ReadUtcAsync(_emptyAtCursorKey, cancellationToken).ConfigureAwait(false) == cursorUtc;
            return (newest, knownEmpty);
        }

        public async Task<IReadOnlyList<AppInsightsSearchRetry>> ReadSearchRetriesAsync(CancellationToken cancellationToken = default)
        {
            var value = await _store.GetStringAsync(_searchRetryKey, cancellationToken).ConfigureAwait(false);
            return value == null ? Array.Empty<AppInsightsSearchRetry>() : JsonConvert.DeserializeObject<AppInsightsSearchRetry[]>(value);
        }

        public Task WriteSearchRetriesAsync(IReadOnlyCollection<AppInsightsSearchRetry> retries, CancellationToken cancellationToken = default) =>
            _store.SetStringAsync(_searchRetryKey, retries.Count == 0 ? null : JsonConvert.SerializeObject(retries), SearchHourTtl, cancellationToken);

        // The last partial hour can still be queried just beyond 90 days from its start.
        private static readonly TimeSpan SearchHourTtl = AppInsightsIngestionCursorPolicy.MaxLookback + TimeSpan.FromHours(1);

        public async Task<IReadOnlyList<DateTime>> ReadSearchReplayHoursAsync(CancellationToken cancellationToken)
        {
            var value = await _store.GetStringAsync(_searchReplayKey, cancellationToken).ConfigureAwait(false);
            return value == null ? Array.Empty<DateTime>() : JsonConvert.DeserializeObject<DateTime[]>(value);
        }

        public Task WriteSearchReplayHoursAsync(IReadOnlyCollection<DateTime> hours, CancellationToken cancellationToken) =>
            _store.SetStringAsync(_searchReplayKey, JsonConvert.SerializeObject(hours), SearchHourTtl, cancellationToken);

        public async Task<AppInsightsReplayStart> ReadReplayStartAsync(CancellationToken cancellationToken)
        {
            var value = await _store.GetStringAsync(_replayStartKey, cancellationToken).ConfigureAwait(false);
            return value == null ? null : JsonConvert.DeserializeObject<AppInsightsReplayStart>(value);
        }

        public Task WriteReplayStartAsync(AppInsightsReplayStart start, CancellationToken cancellationToken) =>
            _store.SetStringAsync(_replayStartKey, JsonConvert.SerializeObject(start), AppInsightsIngestionCursorPolicy.MaxLookback, cancellationToken);

        public async Task WriteAsync(DateTime cursorUtc, DateTime? newestStoredHitUtc, CancellationToken cancellationToken = default)
        {
            // The newest hit first: if the cursor then fails to save, the next cycle compares against a newer newest hit
            // than the cursor's, which is still one the database has reached.
            // A failed cursor write must NOT declare the previous cursor's database empty. Pair the empty
            // observation with the cursor it belongs to; older builds still see an absent newest hit.
            if (!newestStoredHitUtc.HasValue)
                await _store.SetStringAsync(_emptyAtCursorKey, ToRoundTrip(cursorUtc), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _store.SetStringAsync(_newestHitKey, newestStoredHitUtc.HasValue ? ToRoundTrip(newestStoredHitUtc.Value) : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _store.SetStringAsync(_key, ToRoundTrip(cursorUtc), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private async Task<DateTime?> ReadUtcAsync(string key, CancellationToken cancellationToken)
        {
            return ParseUtc(await _store.GetStringAsync(key, cancellationToken).ConfigureAwait(false));
        }

        private static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return DateTime.ParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        private static string ToRoundTrip(DateTime utc)
        {
            var value = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return value.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
