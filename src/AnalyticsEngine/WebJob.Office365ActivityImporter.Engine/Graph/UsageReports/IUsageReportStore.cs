using Common.Entities;
using Common.Entities.ActivityReports;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// Storage for ONE daily Graph usage-report table - the reads and writes
    /// <c>AbstractDailyActivityLoader</c> used to perform against <c>AnalyticsEntitiesContext</c> inline.
    /// See issue #375.
    ///
    /// <para>
    /// The shape is deliberately day-scoped and incremental rather than the
    /// "hand me every stored date / hand me every row" shape issue #375 sketched. The loader reads and
    /// commits ONE DAY AT A TIME, dirty-checks each existing row and flushes every
    /// <c>SaveBatchSize</c> writes, precisely because a ~200k-user tenant times 28 days is enough rows
    /// to OutOfMemory EF6 when they are built into a single command tree (observed on a 7GB P2v2). A
    /// coarser port would have forced that behaviour back out again, so the seam follows the batching
    /// instead of fighting it.
    /// </para>
    /// </summary>
    /// <typeparam name="TReportDbType">The EF entity for this report's table.</typeparam>
    public interface IUsageReportStore<TReportDbType> where TReportDbType : AbstractUsageActivityLog
    {
        /// <summary>
        /// Is there at least one stored row for this date? A bounded existence probe, used when the table
        /// has a date-leading index so the finalized-date scan costs one index seek per candidate date.
        /// </summary>
        Task<bool> HasAnyRowForDateAsync(DateTime date);

        /// <summary>
        /// The distinct stored dates in <c>[fromInclusive, toExclusive)</c> - one range scan, used when
        /// the table has no date-leading index and repeated existence probes would each scan it.
        /// Values come back exactly as stored; the caller normalises.
        /// </summary>
        Task<IReadOnlyList<DateTime>> GetStoredDatesInRangeAsync(DateTime fromInclusive, DateTime toExclusive);

        /// <summary>
        /// Every stored row for one date, TRACKED, so that updates to them go through the change tracker
        /// rather than needing an attach.
        /// </summary>
        Task<IReadOnlyList<TReportDbType>> GetRowsForDateAsync(DateTime date);

        /// <summary>Queue an insert for a row that has no stored counterpart.</summary>
        void AddRow(TReportDbType row);

        /// <summary>
        /// Queue an update for a row returned by <see cref="GetRowsForDateAsync"/>, but only when a mapped
        /// value on it actually differs from what was loaded. Returns whether it will be written.
        ///
        /// <para>
        /// This is the dominant cost of the import at scale: finalized days that the recent-window rule
        /// re-fetches are almost always byte-identical to what is stored, so skipping those UPDATEs is
        /// what keeps a re-import cheap. The check and the "mark it" are one call so the two can never
        /// drift apart.
        /// </para>
        /// </summary>
        bool MarkUpdatedIfChanged(TReportDbType row);

        /// <summary>Commit everything queued since the last commit.</summary>
        Task SaveChangesAsync();

        /// <summary>
        /// Release the usage-log rows held since the last call, so a 28-day import does not accumulate
        /// every day's rows. Lookup entities (users / groups) are deliberately NOT released - the shared
        /// id cache still needs them.
        /// </summary>
        void ReleaseSavedRows();

        /// <summary>
        /// Enter a bulk-write scope, restoring whatever was changed when disposed. For the SQL adapter
        /// that means turning EF6 change auto-detection off, without which adding a day's rows is
        /// O(n^2) - and restoring the PREVIOUS value, not a hard-coded "on", because the context may
        /// have been handed in with it already off.
        /// </summary>
        IDisposable BeginBulkWrite();
    }

    /// <summary>
    /// EF6/SQL Server <see cref="IUsageReportStore{TReportDbType}"/>. Every query, the dirty check, the
    /// detach sweep and the change-tracking toggle are the ones that were inline in
    /// <c>AbstractDailyActivityLoader</c>, moved unchanged (issue #375).
    /// </summary>
    public sealed class SqlUsageReportStore<TReportDbType> : IUsageReportStore<TReportDbType>
        where TReportDbType : AbstractUsageActivityLog
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly DbSet<TReportDbType> _table;

        public SqlUsageReportStore(AnalyticsEntitiesContext db, DbSet<TReportDbType> table)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _table = table ?? throw new ArgumentNullException(nameof(table));
        }

        public Task<bool> HasAnyRowForDateAsync(DateTime date)
            => _table.AnyAsync(activity => activity.Date == date);

        public async Task<IReadOnlyList<DateTime>> GetStoredDatesInRangeAsync(DateTime fromInclusive, DateTime toExclusive)
        {
            return await _table
                .Where(activity => activity.Date >= fromInclusive && activity.Date < toExclusive)
                .Select(activity => activity.Date)
                .Distinct()
                .ToListAsync();
        }

        public async Task<IReadOnlyList<TReportDbType>> GetRowsForDateAsync(DateTime date)
            => await _table.Where(t => t.Date == date).ToListAsync();

        public void AddRow(TReportDbType row) => _table.Add(row);

        public bool MarkUpdatedIfChanged(TReportDbType row)
        {
            var entry = _db.Entry(row);
            if (!HasMappedValueChanged(entry))
            {
                return false;
            }

            // Auto-detect is off inside the bulk-write scope, so state the change explicitly.
            entry.State = EntityState.Modified;
            return true;
        }

        public Task SaveChangesAsync() => _db.SaveChangesAsync();

        public void ReleaseSavedRows()
        {
            foreach (var entry in _db.ChangeTracker.Entries<AbstractUsageActivityLog>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        public IDisposable BeginBulkWrite() => new AutoDetectChangesSuspension(_db);

        // True if any mapped scalar value on the tracked entity differs from the value originally loaded from the
        // DB. Compares the EF6 original/current value snapshots directly so it works with
        // AutoDetectChangesEnabled = false (auto-detect is deliberately off to keep bulk saves O(n)). Navigation
        // properties and [NotMapped] members (e.g. AssociatedLookupId) are not in these snapshots, so only real
        // column changes trigger an UPDATE.
        private static bool HasMappedValueChanged(DbEntityEntry entry)
        {
            var current = entry.CurrentValues;
            var original = entry.OriginalValues;
            foreach (var propertyName in current.PropertyNames)
            {
                if (!object.Equals(original[propertyName], current[propertyName]))
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class AutoDetectChangesSuspension : IDisposable
        {
            private readonly AnalyticsEntitiesContext _db;
            private readonly bool _wasEnabled;

            internal AutoDetectChangesSuspension(AnalyticsEntitiesContext db)
            {
                _db = db;
                _wasEnabled = db.Configuration.AutoDetectChangesEnabled;
                db.Configuration.AutoDetectChangesEnabled = false;
            }

            public void Dispose() => _db.Configuration.AutoDetectChangesEnabled = _wasEnabled;
        }
    }
}
