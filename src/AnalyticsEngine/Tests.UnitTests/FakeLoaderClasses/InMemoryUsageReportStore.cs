using Common.Entities.ActivityReports;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IUsageReportStore{TReportDbType}"/> - the daily usage-report table replaced by
    /// a list, so the whole finalized-date scan and the whole save loop (upsert, dirty check, batch
    /// boundary, per-day detach) can be asserted with no SQL Server. See issue #375.
    ///
    /// <para>
    /// It deliberately mirrors four things about the EF6 adapter rather than being merely convenient,
    /// because a lenient fake would let a broken refactor pass:
    /// </para>
    /// <list type="number">
    /// <item>The dirty check compares against a snapshot taken WHEN THE ROW WAS HANDED OUT, not against
    /// the row itself. Comparing an object with itself is always "unchanged", which is how a dirty-check
    /// test can pass by reference identity and prove nothing.</item>
    /// <item><see cref="MarkUpdatedIfChanged"/> on a row that is queued for INSERT throws, because EF6's
    /// <c>DbEntityEntry.OriginalValues</c> throws for an entity in the <c>Added</c> state. That is a real,
    /// pre-existing edge of the save loop (two Graph rows for the same new lookup on one date) and the
    /// fake must not paper over it.</item>
    /// <item><see cref="ReleaseSavedRows"/> DISCARDS anything still queued, because detaching an unsaved
    /// entity in EF6 drops the pending insert. A refactor that released before the final flush would
    /// silently lose a day's rows, and this is what catches it.</item>
    /// <item>A successful save resets the snapshot, so an unchanged row saved twice is only written once.</item>
    /// </list>
    /// </summary>
    public class InMemoryUsageReportStore<TReportDbType> : IUsageReportStore<TReportDbType>
        where TReportDbType : AbstractUsageActivityLog
    {
        private readonly List<TReportDbType> _stored = new List<TReportDbType>();
        private readonly List<TReportDbType> _pendingInserts = new List<TReportDbType>();
        private readonly List<TReportDbType> _pendingUpdates = new List<TReportDbType>();

        // Reference-keyed: these entities do not override Equals, exactly like EF's identity map.
        private readonly Dictionary<TReportDbType, Dictionary<string, object>> _snapshots
            = new Dictionary<TReportDbType, Dictionary<string, object>>();

        /// <summary>Everything committed so far, in insertion order.</summary>
        public IReadOnlyList<TReportDbType> Stored => _stored;

        /// <summary>Dates the bounded existence probe was asked about, in order.</summary>
        public List<DateTime> ExistenceProbes { get; } = new List<DateTime>();

        /// <summary>Ranges the fallback scan was asked about, in order.</summary>
        public List<Tuple<DateTime, DateTime>> RangeScans { get; } = new List<Tuple<DateTime, DateTime>>();

        /// <summary>Dates a day's rows were loaded for, in order.</summary>
        public List<DateTime> RowsLoadedForDates { get; } = new List<DateTime>();

        /// <summary>How many times the loader flushed. Pins the batch boundary.</summary>
        public int SaveCount { get; private set; }

        /// <summary>Rows committed as INSERTs, in order.</summary>
        public List<TReportDbType> Inserted { get; } = new List<TReportDbType>();

        /// <summary>Rows committed as UPDATEs, in order (a row saved twice appears twice).</summary>
        public List<TReportDbType> Updated { get; } = new List<TReportDbType>();

        /// <summary>How many times per-day rows were released.</summary>
        public int ReleaseCount { get; private set; }

        /// <summary>How many bulk-write scopes were opened, and how many of those were disposed.</summary>
        public int BulkWriteScopesOpened { get; private set; }
        public int BulkWriteScopesDisposed { get; private set; }

        /// <summary>
        /// False if any add / dirty-check / flush happened outside a bulk-write scope. Removing the
        /// scope would make bulk saves O(n^2) again, which no row-count assertion would notice.
        /// </summary>
        public bool AllWritesInsideBulkScope { get; private set; } = true;

        private int _openScopes;

        /// <summary>Pre-populate a committed row (no snapshot: it has not been handed out yet).</summary>
        public InMemoryUsageReportStore<TReportDbType> Seed(params TReportDbType[] rows)
        {
            _stored.AddRange(rows);
            return this;
        }

        public Task<bool> HasAnyRowForDateAsync(DateTime date)
        {
            ExistenceProbes.Add(date);
            return Task.FromResult(_stored.Any(r => r.Date == date));
        }

        public Task<IReadOnlyList<DateTime>> GetStoredDatesInRangeAsync(DateTime fromInclusive, DateTime toExclusive)
        {
            RangeScans.Add(Tuple.Create(fromInclusive, toExclusive));
            IReadOnlyList<DateTime> dates = _stored
                .Where(r => r.Date >= fromInclusive && r.Date < toExclusive)
                .Select(r => r.Date)
                .Distinct()
                .ToList();
            return Task.FromResult(dates);
        }

        public Task<IReadOnlyList<TReportDbType>> GetRowsForDateAsync(DateTime date)
        {
            RowsLoadedForDates.Add(date);
            var rows = _stored.Where(r => r.Date == date).ToList();
            foreach (var row in rows)
            {
                _snapshots[row] = SnapshotMappedValues(row);
            }
            return Task.FromResult<IReadOnlyList<TReportDbType>>(rows);
        }

        public void AddRow(TReportDbType row)
        {
            NoteWrite();
            _pendingInserts.Add(row);
        }

        public bool MarkUpdatedIfChanged(TReportDbType row)
        {
            NoteWrite();

            if (_pendingInserts.Contains(row))
            {
                // Matches EF6: DbEntityEntry.OriginalValues throws for an entity in the Added state.
                throw new InvalidOperationException(
                    "Cannot get original values for an entity in the Added state - the row is queued for insert and has never been loaded.");
            }

            if (!_snapshots.TryGetValue(row, out var original))
            {
                throw new InvalidOperationException(
                    "The row was never handed out by GetRowsForDateAsync (or has been released), so it has no original values.");
            }

            var current = SnapshotMappedValues(row);
            if (!HasChanged(original, current))
            {
                return false;
            }

            if (!_pendingUpdates.Contains(row))
            {
                _pendingUpdates.Add(row);
            }
            return true;
        }

        public virtual Task SaveChangesAsync()
        {
            NoteWrite();
            SaveCount++;

            foreach (var row in _pendingInserts)
            {
                _stored.Add(row);
                Inserted.Add(row);
                // Committed rows become tracked-and-clean, as an EF Added entity does after SaveChanges.
                _snapshots[row] = SnapshotMappedValues(row);
            }
            _pendingInserts.Clear();

            foreach (var row in _pendingUpdates)
            {
                Updated.Add(row);
                _snapshots[row] = SnapshotMappedValues(row);
            }
            _pendingUpdates.Clear();

            return Task.CompletedTask;
        }

        public void ReleaseSavedRows()
        {
            ReleaseCount++;

            // Detaching in EF6 drops anything not yet saved. Mirroring that is what makes a "released
            // before the final flush" refactor show up as missing rows rather than as a silent pass.
            _pendingInserts.Clear();
            _pendingUpdates.Clear();
            _snapshots.Clear();
        }

        public IDisposable BeginBulkWrite()
        {
            BulkWriteScopesOpened++;
            _openScopes++;
            return new Scope(this);
        }

        private void NoteWrite()
        {
            if (_openScopes == 0)
            {
                AllWritesInsideBulkScope = false;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly InMemoryUsageReportStore<TReportDbType> _owner;
            private bool _disposed;

            internal Scope(InMemoryUsageReportStore<TReportDbType> owner) => _owner = owner;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner._openScopes--;
                _owner.BulkWriteScopesDisposed++;
            }
        }

        private static bool HasChanged(Dictionary<string, object> original, Dictionary<string, object> current)
            => current.Any(kv => !object.Equals(original[kv.Key], kv.Value));

        /// <summary>
        /// The values EF6 would put in its original/current snapshots: readable AND writable public
        /// scalar properties that are not <c>[NotMapped]</c>. That excludes navigation properties (not
        /// scalar), computed properties such as <c>IsSavedToDB</c> (no setter) and
        /// <c>AssociatedLookupId</c> (<c>[NotMapped]</c> - it is a facade over the mapped FK column,
        /// which IS included).
        /// </summary>
        private static Dictionary<string, object> SnapshotMappedValues(TReportDbType row)
        {
            var values = new Dictionary<string, object>();
            foreach (var property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                if (property.GetCustomAttribute<NotMappedAttribute>() != null) continue;
                if (!IsMappedScalar(property.PropertyType)) continue;

                values[property.Name] = property.GetValue(row);
            }
            return values;
        }

        private static bool IsMappedScalar(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPrimitive
                || underlying.IsEnum
                || underlying == typeof(string)
                || underlying == typeof(decimal)
                || underlying == typeof(DateTime)
                || underlying == typeof(DateTimeOffset)
                || underlying == typeof(TimeSpan)
                || underlying == typeof(Guid);
        }
    }
}
