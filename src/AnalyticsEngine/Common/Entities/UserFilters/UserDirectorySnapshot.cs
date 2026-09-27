using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>One user as the directory loader reads them, before it is folded into a snapshot.</summary>
    public sealed class UserDirectoryEntry
    {
        public int UserId { get; set; }

        public string UserPrincipalName { get; set; }

        public string Mail { get; set; }

        public bool? AccountEnabled { get; set; }

        /// <summary>The manager's <c>dbo.users.id</c>, or <c>null</c> when the person has none.</summary>
        public int? ManagerUserId { get; set; }

        public string Department { get; set; }

        public string JobTitle { get; set; }

        public string CompanyName { get; set; }

        public string OfficeLocation { get; set; }

        public string Country { get; set; }

        public string StateOrProvince { get; set; }

        public string UsageLocation { get; set; }
    }

    /// <summary>One filterable attribute, as the portal's property picker lists it.</summary>
    public sealed class UserDirectoryDimension
    {
        public string Key { get; set; }

        public UserFilterDimensionKind Kind { get; set; }

        /// <summary>The org type id, for a custom organisation type.</summary>
        public int? OrgTypeId { get; set; }

        /// <summary>
        /// The name an admin gave a custom organisation type. <c>null</c> for an Entra dimension, which the
        /// portal labels in the reader's language from <see cref="Key"/>.
        /// </summary>
        public string Name { get; set; }

        /// <summary>How many distinct values the dimension has.</summary>
        public int DistinctValues { get; set; }

        /// <summary>How many people have any value at all.</summary>
        public int PeopleWithValue { get; set; }
    }

    /// <summary>
    /// The values of one dimension, for every person in the snapshot.
    /// </summary>
    /// <remarks>
    /// Stored as an index into a table of distinct values per person rather than as a string per person:
    /// a department column for 200,000 people is 800 KB of integers instead of 200,000 references into
    /// a few hundred strings, and a filter compares integers rather than strings.
    /// </remarks>
    public sealed class UserDirectoryColumn
    {
        private readonly Dictionary<string, int> _indexByValue;

        internal UserDirectoryColumn(string key, List<string> values, int[] valueByRow, int[] peoplePerValue, int? peopleWithValue = null)
        {
            Key = key;
            Values = values.AsReadOnly();
            ValueByRow = valueByRow;
            PeoplePerValue = peoplePerValue;

            _indexByValue = new Dictionary<string, int>(values.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < values.Count; i++)
            {
                // First spelling wins, matching how the builder interned them.
                if (!_indexByValue.ContainsKey(values[i])) _indexByValue.Add(values[i], i);
            }

            // Supplied for the management chain, where one person is counted under every manager above
            // them, so the per-value counts overlap and their sum would count people several times.
            PeopleWithValue = peopleWithValue ?? peoplePerValue.Sum();
        }

        public string Key { get; }

        /// <summary>The distinct values, in the order they were first seen.</summary>
        public IReadOnlyList<string> Values { get; }

        /// <summary>How many people hold each value, by index into <see cref="Values"/>.</summary>
        public IReadOnlyList<int> PeoplePerValue { get; }

        public int PeopleWithValue { get; }

        /// <summary>Per row, the index of the person's value, or -1 when they have none.</summary>
        /// <remarks>
        /// <c>null</c> for the management chain, which is not a per-person value but a position in the
        /// hierarchy - see <see cref="UserDirectorySnapshot.RowsReportingTo"/>.
        /// </remarks>
        internal int[] ValueByRow { get; }

        /// <summary>
        /// The index of a value, compared the way SQL Server's default collation compares it:
        /// case-insensitively and ignoring surrounding whitespace. -1 when nobody holds it.
        /// </summary>
        public int IndexOf(string value)
        {
            if (value == null) return -1;
            return _indexByValue.TryGetValue(value.Trim(), out var index) ? index : -1;
        }
    }

    /// <summary>
    /// Every person in the directory with the value they hold for every filterable attribute: the
    /// standard Entra ID attributes plus each enabled custom organisation type.
    /// </summary>
    /// <remarks>
    /// <para>Built once and shared: a report narrowed by a user filter evaluates the filter against this
    /// snapshot in memory rather than re-querying the database per filter, so changing the filter on a
    /// 200,000-user tenant costs a pass over integer arrays, not a SQL round trip per clause.</para>
    /// <para>Immutable after <see cref="UserDirectorySnapshotBuilder.Build"/>, so any number of requests
    /// can read it concurrently. The one lazily-built part - the reverse manager index behind the
    /// management chain - is guarded by <see cref="Lazy{T}"/>.</para>
    /// </remarks>
    public sealed class UserDirectorySnapshot
    {
        private readonly int[] _userIdByRow;
        private readonly Dictionary<int, int> _rowByUserId;
        private readonly Dictionary<string, UserDirectoryColumn> _columns;
        private readonly int[] _managerRowByRow;
        private readonly int[] _managerRowByManagerValue;
        private readonly Lazy<ReportsIndex> _reports;

        internal UserDirectorySnapshot(
            DateTime loadedUtc,
            int[] userIdByRow,
            Dictionary<int, int> rowByUserId,
            Dictionary<string, UserDirectoryColumn> columns,
            int[] managerRowByRow,
            int[] managerRowByManagerValue,
            List<UserDirectoryDimension> dimensions)
        {
            LoadedUtc = loadedUtc;
            _userIdByRow = userIdByRow;
            _rowByUserId = rowByUserId;
            _columns = columns;
            _managerRowByRow = managerRowByRow;
            _managerRowByManagerValue = managerRowByManagerValue;
            Dimensions = dimensions.AsReadOnly();
            _reports = new Lazy<ReportsIndex>(() => ReportsIndex.Build(_managerRowByRow), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public DateTime LoadedUtc { get; }

        /// <summary>How many people the snapshot holds.</summary>
        public int PeopleCount => _userIdByRow.Length;

        /// <summary>Every dimension a filter can use right now, in the order the portal offers them.</summary>
        public IReadOnlyList<UserDirectoryDimension> Dimensions { get; }

        public bool TryGetRow(int userId, out int row)
        {
            return _rowByUserId.TryGetValue(userId, out row);
        }

        public int UserIdAt(int row) => _userIdByRow[row];

        /// <summary>The column for a dimension, or <c>null</c> when it does not exist (any more).</summary>
        public UserDirectoryColumn Column(string key)
        {
            return key != null && _columns.TryGetValue(key, out var column) ? column : null;
        }

        /// <summary>The name of a custom organisation type, or <c>null</c>.</summary>
        public string DimensionName(string key)
        {
            return Dimensions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal))?.Name;
        }

        /// <summary>
        /// Marks every row that reports - directly or through any number of managers - to one of the
        /// given managers. The managers themselves are not marked unless they report to another of them.
        /// </summary>
        /// <param name="managerValueIndexes">Indexes into the management chain column's values.</param>
        /// <remarks>
        /// A breadth-first walk down the reverse manager index, so the cost is the size of the
        /// organisations found, not the depth of the tree. Cycles - which Entra does not prevent, and
        /// which a half-finished reorganisation produces - terminate, because a row is only ever
        /// enqueued once.
        /// </remarks>
        public bool[] RowsReportingTo(IEnumerable<int> managerValueIndexes)
        {
            var marked = new bool[PeopleCount];
            var reports = _reports.Value;
            var queue = new Queue<int>();

            foreach (var valueIndex in managerValueIndexes)
            {
                if (valueIndex < 0 || valueIndex >= _managerRowByManagerValue.Length) continue;

                var managerRow = _managerRowByManagerValue[valueIndex];
                if (managerRow >= 0) queue.Enqueue(managerRow);
            }

            while (queue.Count > 0)
            {
                var manager = queue.Dequeue();
                for (var i = reports.Start[manager]; i < reports.Start[manager + 1]; i++)
                {
                    var report = reports.Rows[i];
                    if (marked[report]) continue;

                    marked[report] = true;
                    queue.Enqueue(report);
                }
            }

            return marked;
        }

        /// <summary>Whether a row has a manager at all - the management chain's "not set".</summary>
        internal bool HasManager(int row) => _managerRowByRow[row] >= 0;

        /// <summary>The reverse of the manager column: each row's direct reports, in compressed rows.</summary>
        private sealed class ReportsIndex
        {
            public int[] Start;
            public int[] Rows;

            public static ReportsIndex Build(int[] managerRowByRow)
            {
                var count = managerRowByRow.Length;
                var start = new int[count + 1];

                foreach (var manager in managerRowByRow)
                {
                    if (manager >= 0) start[manager + 1]++;
                }

                for (var i = 0; i < count; i++) start[i + 1] += start[i];

                var rows = new int[start[count]];
                var next = (int[])start.Clone();
                for (var row = 0; row < count; row++)
                {
                    var manager = managerRowByRow[row];
                    if (manager >= 0) rows[next[manager]++] = row;
                }

                return new ReportsIndex { Start = start, Rows = rows };
            }
        }
    }

    /// <summary>
    /// Folds users and organisation assignments into a <see cref="UserDirectorySnapshot"/>.
    /// </summary>
    /// <remarks>
    /// Kept apart from the SQL adapter so the snapshot - and every filter evaluated against it - can be
    /// tested with hand-written users and no database. Values are interned per dimension as they
    /// arrive, so the builder never holds 200,000 copies of the same department name.
    /// </remarks>
    public sealed class UserDirectorySnapshotBuilder
    {
        /// <summary>How far up a management chain the report counts will walk before assuming a cycle.</summary>
        private const int MaxChainDepth = 64;

        private static readonly string[] TextDimensions =
        {
            UserFilterDimensions.Department,
            UserFilterDimensions.JobTitle,
            UserFilterDimensions.CompanyName,
            UserFilterDimensions.OfficeLocation,
            UserFilterDimensions.Country,
            UserFilterDimensions.StateOrProvince,
            UserFilterDimensions.UsageLocation,
            UserFilterDimensions.EmailDomain,
            UserFilterDimensions.UserType,
            UserFilterDimensions.AccountStatus,
        };

        private readonly List<int> _userIds = new List<int>();
        private readonly Dictionary<int, int> _rowByUserId = new Dictionary<int, int>();
        private readonly List<string> _upnByRow = new List<string>();
        private readonly List<int?> _managerIdByRow = new List<int?>();
        private readonly Dictionary<string, ColumnBuilder> _text = new Dictionary<string, ColumnBuilder>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<int, string>> _orgTypes = new List<KeyValuePair<int, string>>();
        private readonly Dictionary<int, ColumnBuilder> _orgColumns = new Dictionary<int, ColumnBuilder>();
        private readonly List<PendingAssignment> _assignments = new List<PendingAssignment>();

        public UserDirectorySnapshotBuilder()
        {
            foreach (var key in TextDimensions) _text[key] = new ColumnBuilder(key);
        }

        /// <summary>Adds a person. A second entry for the same user id is ignored.</summary>
        public void AddUser(UserDirectoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (_rowByUserId.ContainsKey(entry.UserId)) return;

            _rowByUserId.Add(entry.UserId, _userIds.Count);
            _userIds.Add(entry.UserId);
            _upnByRow.Add(entry.UserPrincipalName);
            _managerIdByRow.Add(entry.ManagerUserId);

            _text[UserFilterDimensions.Department].Append(entry.Department);
            _text[UserFilterDimensions.JobTitle].Append(entry.JobTitle);
            _text[UserFilterDimensions.CompanyName].Append(entry.CompanyName);
            _text[UserFilterDimensions.OfficeLocation].Append(entry.OfficeLocation);
            _text[UserFilterDimensions.Country].Append(entry.Country);
            _text[UserFilterDimensions.StateOrProvince].Append(entry.StateOrProvince);
            _text[UserFilterDimensions.UsageLocation].Append(entry.UsageLocation);
            _text[UserFilterDimensions.EmailDomain].Append(CopilotAdoptionEmailDomain.From(entry.UserPrincipalName, entry.Mail));
            _text[UserFilterDimensions.UserType].Append(string.IsNullOrWhiteSpace(entry.UserPrincipalName)
                ? null
                : CopilotAdoptionEmailDomain.IsExternalGuest(entry.UserPrincipalName) ? UserFilterTokens.Guest : UserFilterTokens.Member);
            _text[UserFilterDimensions.AccountStatus].Append(entry.AccountEnabled.HasValue
                ? (entry.AccountEnabled.Value ? UserFilterTokens.Enabled : UserFilterTokens.Disabled)
                : null);
        }

        /// <summary>Adds an enabled custom organisation type. Its people are added with <see cref="AddOrgAssignment"/>.</summary>
        public void AddOrgType(int orgTypeId, string name)
        {
            if (_orgColumns.ContainsKey(orgTypeId)) return;

            _orgTypes.Add(new KeyValuePair<int, string>(orgTypeId, name));
            _orgColumns.Add(orgTypeId, new ColumnBuilder(UserFilterDimensions.ForOrgType(orgTypeId)));
        }

        /// <summary>
        /// Records one person's value for one organisation type. Ignored for a type that was not added,
        /// so an assignment read a moment after its type was disabled cannot resurrect it.
        /// </summary>
        public void AddOrgAssignment(int userId, int orgTypeId, string value)
        {
            _assignments.Add(new PendingAssignment { UserId = userId, OrgTypeId = orgTypeId, Value = value });
        }

        public UserDirectorySnapshot Build(DateTime loadedUtc)
        {
            var count = _userIds.Count;
            var columns = new Dictionary<string, UserDirectoryColumn>(StringComparer.Ordinal);

            foreach (var builder in _text.Values)
            {
                columns[builder.Key] = builder.Build(count);
            }

            // Manager: the value is the manager's UPN, resolved through the row index so a manager who
            // is not in the directory (deleted, or never imported) reads as "no manager" rather than as
            // an id nobody can recognise.
            var managerRowByRow = new int[count];
            var manager = new ColumnBuilder(UserFilterDimensions.Manager);
            for (var row = 0; row < count; row++)
            {
                var managerId = _managerIdByRow[row];
                int managerRow;
                if (managerId.HasValue && _rowByUserId.TryGetValue(managerId.Value, out managerRow) && managerRow != row
                    && !string.IsNullOrWhiteSpace(_upnByRow[managerRow]))
                {
                    managerRowByRow[row] = managerRow;
                    manager.Append(_upnByRow[managerRow]);
                }
                else
                {
                    managerRowByRow[row] = -1;
                    manager.Append(null);
                }
            }

            var managerColumn = manager.Build(count);
            columns[UserFilterDimensions.Manager] = managerColumn;

            // The management chain offers the same people as the manager column, counted by the size of
            // the whole organisation beneath them rather than by their direct reports.
            var managerRowByValue = new int[managerColumn.Values.Count];
            for (var i = 0; i < managerRowByValue.Length; i++) managerRowByValue[i] = -1;
            for (var row = 0; row < count; row++)
            {
                var managerRow = managerRowByRow[row];
                if (managerRow < 0) continue;

                var valueIndex = managerColumn.ValueByRow[row];
                if (managerRowByValue[valueIndex] < 0) managerRowByValue[valueIndex] = managerRow;
            }

            var peopleWithManager = managerRowByRow.Count(m => m >= 0);
            columns[UserFilterDimensions.ManagementChain] = new UserDirectoryColumn(
                UserFilterDimensions.ManagementChain,
                managerColumn.Values.ToList(),
                null,
                CountOrganisationSizes(managerRowByRow, managerColumn, managerRowByValue),
                peopleWithManager);

            foreach (var assignment in _assignments)
            {
                ColumnBuilder column;
                int row;
                if (_orgColumns.TryGetValue(assignment.OrgTypeId, out column) && _rowByUserId.TryGetValue(assignment.UserId, out row))
                {
                    column.Set(row, assignment.Value);
                }
            }

            var dimensions = new List<UserDirectoryDimension>();
            foreach (var key in UserFilterDimensions.EntraKeys)
            {
                var column = columns[key];
                dimensions.Add(new UserDirectoryDimension
                {
                    Key = key,
                    Kind = UserFilterDimensionKind.Entra,
                    DistinctValues = column.Values.Count,
                    PeopleWithValue = column.PeopleWithValue,
                });
            }

            foreach (var type in _orgTypes.OrderBy(t => t.Value, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Key))
            {
                var column = _orgColumns[type.Key].Build(count);
                columns[column.Key] = column;
                dimensions.Add(new UserDirectoryDimension
                {
                    Key = column.Key,
                    Kind = UserFilterDimensionKind.Custom,
                    OrgTypeId = type.Key,
                    Name = type.Value,
                    DistinctValues = column.Values.Count,
                    PeopleWithValue = column.PeopleWithValue,
                });
            }

            return new UserDirectorySnapshot(
                loadedUtc,
                _userIds.ToArray(),
                new Dictionary<int, int>(_rowByUserId),
                columns,
                managerRowByRow,
                managerRowByValue,
                dimensions);
        }

        /// <summary>
        /// How many people report to each manager at any level. Walks up from every person, so the cost
        /// is the population times the depth of the tree - a few million steps at 200,000 people.
        /// </summary>
        private static int[] CountOrganisationSizes(int[] managerRowByRow, UserDirectoryColumn managerColumn, int[] managerRowByValue)
        {
            var sizes = new int[managerColumn.Values.Count];
            var valueByManagerRow = new Dictionary<int, int>(managerRowByValue.Length);
            for (var i = 0; i < managerRowByValue.Length; i++)
            {
                if (managerRowByValue[i] >= 0 && !valueByManagerRow.ContainsKey(managerRowByValue[i]))
                {
                    valueByManagerRow.Add(managerRowByValue[i], i);
                }
            }

            var visited = new HashSet<int>();
            for (var row = 0; row < managerRowByRow.Length; row++)
            {
                visited.Clear();
                var current = managerRowByRow[row];
                var depth = 0;

                // A cycle would otherwise count the same person again and again; the visited set stops
                // it at the first repeat, and the depth cap bounds a pathological chain regardless.
                while (current >= 0 && depth++ < MaxChainDepth && visited.Add(current))
                {
                    if (current != row && valueByManagerRow.TryGetValue(current, out var valueIndex)) sizes[valueIndex]++;
                    current = managerRowByRow[current];
                }
            }

            return sizes;
        }

        private sealed class PendingAssignment
        {
            public int UserId;
            public int OrgTypeId;
            public string Value;
        }

        /// <summary>Interns one dimension's values as rows arrive.</summary>
        private sealed class ColumnBuilder
        {
            private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _values = new List<string>();
            private readonly List<int> _byRow = new List<int>();
            private readonly Dictionary<int, int> _sparse = new Dictionary<int, int>();

            public ColumnBuilder(string key)
            {
                Key = key;
            }

            public string Key { get; }

            /// <summary>Appends the next row's value, for columns filled in row order.</summary>
            public void Append(string value)
            {
                _byRow.Add(Intern(value));
            }

            /// <summary>Sets one row's value, for columns filled by assignment. The first value wins.</summary>
            public void Set(int row, string value)
            {
                var index = Intern(value);
                if (index >= 0 && !_sparse.ContainsKey(row)) _sparse.Add(row, index);
            }

            public UserDirectoryColumn Build(int rowCount)
            {
                var byRow = new int[rowCount];
                if (_byRow.Count > 0)
                {
                    for (var i = 0; i < rowCount; i++) byRow[i] = i < _byRow.Count ? _byRow[i] : -1;
                }
                else
                {
                    for (var i = 0; i < rowCount; i++) byRow[i] = -1;
                    foreach (var pair in _sparse) byRow[pair.Key] = pair.Value;
                }

                var people = new int[_values.Count];
                foreach (var index in byRow)
                {
                    if (index >= 0) people[index]++;
                }

                return new UserDirectoryColumn(Key, _values, byRow, people);
            }

            private int Intern(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return -1;

                var trimmed = value.Trim();
                if (_index.TryGetValue(trimmed, out var existing)) return existing;

                var index = _values.Count;
                _values.Add(trimmed);
                _index.Add(trimmed, index);
                return index;
            }
        }
    }
}
