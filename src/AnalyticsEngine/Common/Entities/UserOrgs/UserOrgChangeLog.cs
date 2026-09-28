using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Where applied CSV imports' change lists are kept: user by user, the value before and after, so an
    /// administrator can see exactly what an import changed, when, and who ran it.
    /// </summary>
    /// <remarks>
    /// Two implementations: Azure Table Storage when a storage account is configured and usable, and
    /// <see cref="InMemoryUserOrgChangeLog"/> otherwise. The job row records which one an import's list
    /// went to (<see cref="UserOrgImportJob.ChangeLogStatus"/>), so it is always read back from the
    /// right place.
    /// </remarks>
    public interface IUserOrgChangeLog
    {
        /// <summary>Which store this is: <see cref="UserOrgChangeLogStatus.TableStorage"/> or <see cref="UserOrgChangeLogStatus.Memory"/>.</summary>
        UserOrgChangeLogStatus Destination { get; }

        /// <summary>
        /// Adds a batch of one import's changes. Idempotent: writing a change again replaces it, so an
        /// interrupted write can simply be repeated.
        /// </summary>
        Task AppendAsync(UserOrgChangeLogImport import, IReadOnlyList<UserOrgChangeRecord> changes, CancellationToken cancellationToken);

        /// <summary>Writes the import's summary. Written last: its presence is what says the log is complete.</summary>
        Task CompleteAsync(UserOrgChangeLogImport import, CancellationToken cancellationToken);

        /// <summary>The import's summary, or <c>null</c> when this store does not have a complete log for it.</summary>
        Task<UserOrgChangeLogImport> GetImportAsync(string logId, CancellationToken cancellationToken);

        /// <summary>
        /// One page of an import's changes in user principal name order - only those whose UPN starts with
        /// <paramref name="search"/>, when one is given.
        /// </summary>
        /// <param name="continuation">The <see cref="UserOrgChangeLogPage.Continuation"/> of the previous page, or <c>null</c>.</param>
        /// <exception cref="UserOrgValidationException">The continuation is not one this store issued.</exception>
        Task<UserOrgChangeLogPage> GetChangesAsync(
            string logId,
            string search,
            string continuation,
            int pageSize,
            CancellationToken cancellationToken);
    }

    /// <summary>One import, as its change log records it.</summary>
    public sealed class UserOrgChangeLogImport
    {
        /// <summary>See <see cref="UserOrgChangeLogKeys.LogId"/>.</summary>
        public string LogId { get; set; }

        public int JobId { get; set; }

        public int OrgTypeId { get; set; }

        /// <summary>
        /// The org type's name when the log was written - tenant data, shown as stored. That is moments
        /// after the import in the normal course, later only when the log had to wait for its store; a
        /// type renamed in between is recorded under its new name. <see cref="OrgTypeId"/> is the type's
        /// identity either way.
        /// </summary>
        public string OrgTypeName { get; set; }

        public UserOrgImportMode Mode { get; set; }

        public string StartedBy { get; set; }

        public string FileName { get; set; }

        public DateTime QueuedUtc { get; set; }

        public DateTime? FinishedUtc { get; set; }

        public int RowsTotal { get; set; }

        public int RowsUnknownUpn { get; set; }

        public int RowsInvalid { get; set; }

        public int Added { get; set; }

        public int Changed { get; set; }

        public int Cleared { get; set; }

        /// <summary>
        /// How many of the changes the log actually holds. Less than <see cref="ChangeCount"/> only when
        /// the in-memory store ran out of room.
        /// </summary>
        public int StoredChanges { get; set; }

        public DateTime WrittenUtc { get; set; }

        public int ChangeCount => Added + Changed + Cleared;

        public bool Truncated => StoredChanges < ChangeCount;

        internal UserOrgChangeLogImport Copy()
        {
            return (UserOrgChangeLogImport)MemberwiseClone();
        }
    }

    /// <summary>One user's change, as the change log returns it.</summary>
    public sealed class UserOrgChangeLogEntry
    {
        public string Upn { get; set; }

        /// <summary>The value before the import, or <c>null</c> when the user had none.</summary>
        public string Before { get; set; }

        /// <summary>The value after the import, or <c>null</c> when it was cleared.</summary>
        public string After { get; set; }

        public UserOrgChangeKind Kind { get; set; }
    }

    /// <summary>A page of changes.</summary>
    public sealed class UserOrgChangeLogPage
    {
        public IReadOnlyList<UserOrgChangeLogEntry> Items { get; set; } = new UserOrgChangeLogEntry[0];

        /// <summary>Pass back to get the next page, or <c>null</c> when this was the last one.</summary>
        public string Continuation { get; set; }
    }

    /// <summary>
    /// The keys the change log is stored under, shared by both stores so they order and search alike.
    /// </summary>
    public static class UserOrgChangeLogKeys
    {
        /// <summary>The row key of an import's summary. Sorts before every change's key.</summary>
        public const string SummaryRowKey = "import";

        /// <summary>Every change's row key starts with this.</summary>
        public const string ChangePrefix = "u:";

        /// <summary>The most changes one page returns.</summary>
        public const int MaxPageSize = 1000;

        /// <summary>
        /// One import's change log, keyed by the job id AND the moment it was queued.
        /// </summary>
        /// <remarks>
        /// The job id alone is not unique for as long as the log lives. The log outlives the database it
        /// describes - a storage account kept across a reinstall sees job 1 again - and writing a new job
        /// 1's changes over an old one would mix two imports' changes in one list without a trace.
        /// </remarks>
        public static string LogId(int jobId, DateTime queuedUtc)
        {
            return jobId.ToString(CultureInfo.InvariantCulture) + "-" + queuedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A change's row key: the lower-case UPN, so changes list alphabetically and a UPN prefix search
        /// is a key range, then the user id, so two users can never share a key.
        /// </summary>
        public static string ChangeRowKey(string upn, int userId)
        {
            return ChangePrefix + Escape(upn) + "|" + userId.ToString("D10", CultureInfo.InvariantCulture);
        }

        /// <summary>The key range a UPN search covers: every key starting with the returned prefix.</summary>
        public static string SearchPrefix(string search)
        {
            var trimmed = (search ?? string.Empty).Trim();
            return ChangePrefix + (trimmed.Length == 0 ? string.Empty : Escape(trimmed));
        }

        /// <summary>The first key after every key that starts with <paramref name="prefix"/>.</summary>
        /// <remarks>
        /// Correct because every character of a key is printable ASCII - see <see cref="Escape"/> - so
        /// incrementing the last one never reaches a character a key could contain after it.
        /// </remarks>
        public static string UpperBound(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                throw new ArgumentException("A prefix is required.", nameof(prefix));
            }

            return prefix.Substring(0, prefix.Length - 1) + (char)(prefix[prefix.Length - 1] + 1);
        }

        /// <summary>
        /// Lower-cases and percent-encodes everything but the characters a UPN is normally made of.
        /// </summary>
        /// <remarks>
        /// Table Storage keys cannot contain <c>/ \ # ?</c> or control characters, and a guest's UPN
        /// carries <c>#EXT#</c>. <c>|</c> and <c>%</c> are encoded too, so the separator and the escape
        /// character only ever mean one thing.
        /// </remarks>
        public static string Escape(string value)
        {
            var lower = (value ?? string.Empty).ToLowerInvariant();
            var builder = new StringBuilder(lower.Length + 8);
            foreach (var b in Encoding.UTF8.GetBytes(lower))
            {
                var c = (char)b;
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_' || c == '\'' || c == '!' || c == '^' || c == '~' || c == '@')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            return builder.ToString();
        }

        public static UserOrgChangeKind KindOf(string before, string after)
        {
            return before == null ? UserOrgChangeKind.Added
                : after == null ? UserOrgChangeKind.Cleared
                : UserOrgChangeKind.Changed;
        }
    }

    /// <summary>
    /// The change log kept in this process's memory, for a deployment with no usable storage account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded, because it shares the web process with every report. It holds at most
    /// <see cref="DefaultMaxChanges"/> changes across all imports, and at most <see cref="DefaultMaxLogs"/>
    /// imports' logs however few changes they hold. To make room it drops a log whose writer abandoned it
    /// part-written (<see cref="AbandonedAfter"/>) and then the oldest complete one, and an import too big
    /// on its own keeps its first changes and says how many it dropped
    /// (<see cref="UserOrgChangeLogImport.StoredChanges"/>). At roughly 150 bytes a change, the cap costs
    /// about 15 MB.
    /// </para>
    /// <para>
    /// Everything in it is lost when the process stops, and another instance of a scaled-out site never
    /// sees it. The portal says so wherever it shows a log from here.
    /// </para>
    /// </remarks>
    public sealed class InMemoryUserOrgChangeLog : IUserOrgChangeLog
    {
        public const int DefaultMaxChanges = 100000;

        /// <summary>
        /// The most import logs kept, whatever they hold. The change cap alone does not bound a log that
        /// changed nobody - importing the same file again and again would keep one summary each, forever.
        /// </summary>
        public const int DefaultMaxLogs = 1000;

        /// <summary>
        /// How long an unfinished log can go unwritten before it counts as abandoned. The shipper writes a
        /// log's pages back to back, so one idle this long has lost its writer - to a lease lost mid-write,
        /// say, with the retry landing on another instance's memory. Until then it can not be dropped, because
        /// its writer may still be going; after it, it would otherwise hold its changes until the process
        /// stopped, out of reach of every cap.
        /// </summary>
        public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

        /// <summary>The log every request in this process shares.</summary>
        public static readonly InMemoryUserOrgChangeLog Shared = new InMemoryUserOrgChangeLog();

        private readonly int _maxChanges;
        private readonly int _maxLogs;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Log> _logs = new Dictionary<string, Log>(StringComparer.Ordinal);
        private int _stored;
        private long _order;

        public InMemoryUserOrgChangeLog(int maxChanges = DefaultMaxChanges, int maxLogs = DefaultMaxLogs)
            : this(maxChanges, maxLogs, () => DateTime.UtcNow)
        {
        }

        /// <param name="utcNow">The clock that decides when an unfinished log was abandoned.</param>
        internal InMemoryUserOrgChangeLog(int maxChanges, int maxLogs, Func<DateTime> utcNow)
        {
            if (utcNow == null)
            {
                throw new ArgumentNullException(nameof(utcNow));
            }

            _maxChanges = Math.Max(1, maxChanges);
            _maxLogs = Math.Max(1, maxLogs);
            _utcNow = utcNow;
        }

        public UserOrgChangeLogStatus Destination => UserOrgChangeLogStatus.Memory;

        /// <summary>Changes held right now, across every import.</summary>
        public int StoredChanges
        {
            get
            {
                lock (_gate)
                {
                    return _stored;
                }
            }
        }

        /// <summary>Distinct value names held right now, across every import - for tests of the cap.</summary>
        internal int InternedValues
        {
            get
            {
                lock (_gate)
                {
                    return _logs.Values.Sum(l => l.InternedCount);
                }
            }
        }

        public Task AppendAsync(UserOrgChangeLogImport import, IReadOnlyList<UserOrgChangeRecord> changes, CancellationToken cancellationToken)
        {
            if (import == null)
            {
                throw new ArgumentNullException(nameof(import));
            }

            lock (_gate)
            {
                var log = Open(import.LogId);
                foreach (var change in changes ?? new UserOrgChangeRecord[0])
                {
                    var key = UserOrgChangeLogKeys.ChangeRowKey(change.Upn, change.UserId);
                    var replacing = log.Changes.ContainsKey(key);

                    // Room is decided before anything is kept: a change that will not be stored must
                    // not leave its values interned, or a large import of distinct values would hold
                    // all of them in memory despite the cap.
                    if (!replacing && _stored >= _maxChanges && !MakeRoom(log))
                    {
                        continue;
                    }

                    var entry = new UserOrgChangeLogEntry
                    {
                        Upn = change.Upn,
                        Before = log.Intern(change.OldValue),
                        After = log.Intern(change.NewValue),
                        Kind = UserOrgChangeLogKeys.KindOf(change.OldValue, change.NewValue),
                    };

                    if (replacing)
                    {
                        log.Changes[key] = entry;
                        continue;
                    }

                    log.Changes.Add(key, entry);
                    _stored++;
                }
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync(UserOrgChangeLogImport import, CancellationToken cancellationToken)
        {
            if (import == null)
            {
                throw new ArgumentNullException(nameof(import));
            }

            lock (_gate)
            {
                var log = Open(import.LogId);
                log.Sorted = log.Changes.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
                log.Summary = import.Copy();
                log.Summary.StoredChanges = log.Sorted.Length;

                // Abandoned and then the oldest complete logs go first, as when making room for changes -
                // never this one.
                while (_logs.Count > _maxLogs && Evict(log))
                {
                }
            }

            return Task.CompletedTask;
        }

        public Task<UserOrgChangeLogImport> GetImportAsync(string logId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Log log;
                return Task.FromResult(
                    logId != null && _logs.TryGetValue(logId, out log) && log.Summary != null ? log.Summary.Copy() : null);
            }
        }

        public Task<UserOrgChangeLogPage> GetChangesAsync(
            string logId,
            string search,
            string continuation,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var size = Math.Max(1, Math.Min(pageSize, UserOrgChangeLogKeys.MaxPageSize));
            var prefix = UserOrgChangeLogKeys.SearchPrefix(search);
            if (continuation != null && !continuation.StartsWith(UserOrgChangeLogKeys.ChangePrefix, StringComparison.Ordinal))
            {
                throw new UserOrgValidationException(
                    "That page of changes is no longer available. Open the change list again.",
                    UserOrgImportRefusalCodes.ChangePageExpired);
            }

            lock (_gate)
            {
                Log log;
                if (logId == null || !_logs.TryGetValue(logId, out log) || log.Sorted == null)
                {
                    return Task.FromResult(new UserOrgChangeLogPage());
                }

                // The first key at or after the prefix - or strictly after the previous page's last key.
                var start = LowerBound(log.Sorted, prefix);
                if (continuation != null)
                {
                    var after = LowerBound(log.Sorted, continuation);
                    if (after < log.Sorted.Length && string.CompareOrdinal(log.Sorted[after].Key, continuation) == 0)
                    {
                        after++;
                    }

                    start = Math.Max(start, after);
                }

                var items = new List<UserOrgChangeLogEntry>(size);
                var index = start;
                while (index < log.Sorted.Length && items.Count < size
                       && log.Sorted[index].Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    items.Add(log.Sorted[index].Value);
                    index++;
                }

                var more = index < log.Sorted.Length && log.Sorted[index].Key.StartsWith(prefix, StringComparison.Ordinal);
                return Task.FromResult(new UserOrgChangeLogPage
                {
                    Items = items,
                    Continuation = more && items.Count > 0 ? log.Sorted[index - 1].Key : null,
                });
            }
        }

        private Log Open(string logId)
        {
            if (string.IsNullOrEmpty(logId))
            {
                throw new ArgumentException("A log id is required.", nameof(logId));
            }

            Log log;
            if (!_logs.TryGetValue(logId, out log))
            {
                log = new Log();
                _logs.Add(logId, log);
            }
            else if (log.Sorted != null)
            {
                // Written again after it was complete: an interrupted write being repeated. Reopen it; the
                // summary goes back on when the repeat completes.
                log.Sorted = null;
                log.Summary = null;
            }

            // Every write counts, so a log being written is never the one that looks abandoned.
            log.Order = ++_order;
            log.LastWrittenUtc = _utcNow();
            return log;
        }

        /// <summary>Drops logs other than <paramref name="keep"/> until a change fits. False when none can go.</summary>
        private bool MakeRoom(Log keep)
        {
            while (_stored >= _maxChanges)
            {
                if (!Evict(keep))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Drops one log other than <paramref name="keep"/>: an abandoned unfinished one if there is one,
        /// since nobody can read it, and otherwise the oldest complete one. False when there is neither.
        /// </summary>
        private bool Evict(Log keep)
        {
            var abandonedBy = _utcNow() - AbandonedAfter;
            var oldest = _logs
                .Where(p => !ReferenceEquals(p.Value, keep)
                            && (p.Value.Summary != null || p.Value.LastWrittenUtc <= abandonedBy))
                .OrderBy(p => p.Value.Summary != null)
                .ThenBy(p => p.Value.Order)
                .Select(p => p.Key)
                .FirstOrDefault();
            if (oldest == null)
            {
                return false;
            }

            _stored -= _logs[oldest].Changes.Count;
            _logs.Remove(oldest);
            return true;
        }

        private static int LowerBound(KeyValuePair<string, UserOrgChangeLogEntry>[] sorted, string key)
        {
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (string.CompareOrdinal(sorted[mid].Key, key) < 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private sealed class Log
        {
            public readonly Dictionary<string, UserOrgChangeLogEntry> Changes =
                new Dictionary<string, UserOrgChangeLogEntry>(StringComparer.Ordinal);

            private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.Ordinal);

            public KeyValuePair<string, UserOrgChangeLogEntry>[] Sorted;

            public UserOrgChangeLogImport Summary;

            /// <summary>When it was last written, in write order: the oldest goes first.</summary>
            public long Order;

            public DateTime LastWrittenUtc;

            public int InternedCount => _names.Count;

            /// <summary>
            /// One copy of each value name per log. An import moves thousands of people between a few
            /// dozen organisations, so this is most of the difference between 40 MB and 15 MB at the cap.
            /// </summary>
            public string Intern(string value)
            {
                if (value == null)
                {
                    return null;
                }

                string existing;
                if (_names.TryGetValue(value, out existing))
                {
                    return existing;
                }

                _names.Add(value, value);
                return value;
            }
        }
    }
}
