using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>A licence type as <c>dbo.license_types</c> holds it. Tenant data - never translated.</summary>
    public sealed class ActivityAnalysisLicence
    {
        public ActivityAnalysisLicence(int id, string name, string skuId)
        {
            Id = id;
            Name = name;
            SkuId = skuId;
        }

        public int Id { get; }

        public string Name { get; internal set; }

        public string SkuId { get; internal set; }
    }

    /// <summary>
    /// Every metric's total per week, and how many people had a value above zero that week, for one set of people.
    /// </summary>
    /// <remarks>Dense: weeks x metrics, at most 105 x 58 slots - about 75 KB.</remarks>
    public sealed class ActivityAnalysisWeeklyTotals
    {
        private static readonly int MetricCount = ActivityAnalysisMetricCatalogue.Count;
        private readonly long[] _sums;
        private readonly int[] _activePeople;

        public ActivityAnalysisWeeklyTotals(int weeks)
        {
            if (weeks < 0) throw new ArgumentOutOfRangeException(nameof(weeks));
            Weeks = weeks;
            _sums = new long[weeks * MetricCount];
            _activePeople = new int[weeks * MetricCount];
        }

        public int Weeks { get; }

        /// <summary>
        /// The weeks the table held when these figures were read (<see cref="ActivityAnalysisWeeks.DataVersion"/>);
        /// <c>null</c> when no read said.
        /// </summary>
        public string DataVersion { get; set; }

        public long SumOf(int week, int metricIndex) => _sums[week * MetricCount + metricIndex];

        public int ActivePeopleOf(int week, int metricIndex) => _activePeople[week * MetricCount + metricIndex];

        /// <summary>Adds to a week's figures for one metric. Out-of-range weeks are ignored.</summary>
        public void Add(int week, int metricIndex, long sum, int activePeople)
        {
            if (week < 0 || week >= Weeks) return;
            if (metricIndex < 0 || metricIndex >= MetricCount) throw new ArgumentOutOfRangeException(nameof(metricIndex));
            var slot = week * MetricCount + metricIndex;
            _sums[slot] += sum;
            _activePeople[slot] += activePeople;
        }

        /// <summary>
        /// The figures of the people in <paramref name="whole"/> who are not in <paramref name="part"/>: a week's sum and
        /// its count of active people both add up across separate sets of people. Never below zero.
        /// </summary>
        public static ActivityAnalysisWeeklyTotals Except(ActivityAnalysisWeeklyTotals whole, ActivityAnalysisWeeklyTotals part)
        {
            if (whole == null) throw new ArgumentNullException(nameof(whole));
            if (part == null) throw new ArgumentNullException(nameof(part));
            if (whole.Weeks != part.Weeks) throw new ArgumentException("Both series must cover the same weeks.", nameof(part));

            var result = new ActivityAnalysisWeeklyTotals(whole.Weeks);
            for (var slot = 0; slot < result._sums.Length; slot++)
            {
                result._sums[slot] = Math.Max(0L, whole._sums[slot] - part._sums[slot]);
                result._activePeople[slot] = Math.Max(0, whole._activePeople[slot] - part._activePeople[slot]);
            }

            return result;
        }
    }

    /// <summary>
    /// One period of <c>profiling.ActivitiesWeeklyColumns</c>, read once and shared by every reader: each person's
    /// total of every metric over the period, the population's weekly totals, and who holds which licence.
    /// </summary>
    /// <remarks>
    /// <para><b>Shape.</b> Totals are dense 32-bit integers, one slot per person per catalogue metric, saturating at
    /// <see cref="int.MaxValue"/> - a person's total of any metric over two years is far below it. At 200,000 people and
    /// 58 metrics that is 46 MB, stored in 256-person chunks (59 KB each, below the large object heap's threshold) so a
    /// load never needs one 46 MB block or a second copy of it. A metric whose column does not exist is all zeros.</para>
    /// <para><b>Filters run in memory.</b> The administrator's global filter, the reader's user filter, licences and
    /// activity ranges are applied per request with one pass over the people (<see cref="Evaluate"/>), so changing a
    /// filter never re-reads the table.</para>
    /// <para>Immutable after <see cref="ActivityAnalysisReadModelBuilder.Build"/>: any number of requests read it at once.</para>
    /// </remarks>
    public sealed class ActivityAnalysisReadModel
    {
        internal const int ChunkBits = 8;
        internal const int ChunkSize = 1 << ChunkBits;
        internal const int ChunkMask = ChunkSize - 1;
        internal static readonly int MetricCount = ActivityAnalysisMetricCatalogue.Count;

        private readonly int[][] _chunks;
        private readonly int[] _userIds;
        private readonly int[] _licenceStart;
        private readonly int[] _licenceIndexes;
        private readonly int[] _maximumTotals;
        private readonly bool[] _available;
        private readonly Dictionary<int, int> _licenceIndexById;
        private readonly ConditionalWeakTable<UserDirectorySnapshot, int[]> _rowsBySnapshot =
            new ConditionalWeakTable<UserDirectorySnapshot, int[]>();

        internal ActivityAnalysisReadModel(
            ActivityAnalysisPeriod period, DateTime loadedUtc, bool[] available, int[][] chunks, int[] userIds,
            ActivityAnalysisWeeklyTotals populationWeeks, bool populationWeeksExact, string dataVersion,
            List<ActivityAnalysisLicence> licences, int[] licenceStart, int[] licenceIndexes)
        {
            Id = Guid.NewGuid().ToString("N");
            Period = period;
            LoadedUtc = loadedUtc;
            DataVersion = dataVersion;
            _available = available;
            _chunks = chunks;
            _userIds = userIds;
            PopulationWeeks = populationWeeks;
            PopulationWeeksExact = populationWeeksExact;
            Licences = licences.AsReadOnly();
            _licenceStart = licenceStart;
            _licenceIndexes = licenceIndexes;
            _licenceIndexById = new Dictionary<int, int>(licences.Count);
            for (var i = 0; i < licences.Count; i++) _licenceIndexById[licences[i].Id] = i;
            AvailableMetrics = ActivityAnalysisMetricCatalogue.All.Where(m => available[m.Index]).ToList().AsReadOnly();

            _maximumTotals = new int[MetricCount];
            for (var person = 0; person < userIds.Length; person++)
            {
                var chunk = chunks[person >> ChunkBits];
                var offset = (person & ChunkMask) * MetricCount;
                for (var m = 0; m < MetricCount; m++)
                {
                    if (chunk[offset + m] > _maximumTotals[m]) _maximumTotals[m] = chunk[offset + m];
                }
            }
        }

        /// <summary>Unique per load, so figures derived from it - a cached weekly series - never outlive it.</summary>
        public string Id { get; }

        public ActivityAnalysisPeriod Period { get; }

        /// <summary>When the figures were read from the database.</summary>
        public DateTime LoadedUtc { get; }

        /// <summary>
        /// The weeks the table held when the period was read (<see cref="ActivityAnalysisWeeks.DataVersion"/>), so figures
        /// read later can be told apart from figures read after the runbooks have changed the table.
        /// </summary>
        public string DataVersion { get; }

        /// <summary>Everyone in <c>profiling.users</c> with at least one compiled week in the period.</summary>
        public int PeopleCount => _userIds.Length;

        /// <summary>Every licence type, by index.</summary>
        public IReadOnlyList<ActivityAnalysisLicence> Licences { get; }

        /// <summary>Every metric's weekly total over all <see cref="PeopleCount"/> people - the unfiltered series.</summary>
        public ActivityAnalysisWeeklyTotals PopulationWeeks { get; }

        /// <summary>
        /// True when <see cref="PopulationWeeks"/> counts exactly the people the model holds - nobody counted in the weeks
        /// was left out of the people - so one set's weekly figures are everyone's less everybody else's.
        /// </summary>
        public bool PopulationWeeksExact { get; }

        /// <summary>The metrics whose columns existed when the period was read, in catalogue order.</summary>
        public IReadOnlyList<ActivityAnalysisMetric> AvailableMetrics { get; }

        public bool IsAvailable(ActivityAnalysisMetric metric) => metric != null && _available[metric.Index];

        public int UserIdAt(int person) => _userIds[person];

        /// <summary>A person's total of one metric over the period.</summary>
        public int TotalOf(int person, int metricIndex) => _chunks[person >> ChunkBits][(person & ChunkMask) * MetricCount + metricIndex];

        /// <summary>The highest per-person total of a metric over every person in the period.</summary>
        public int MaximumTotalOf(int metricIndex) => _maximumTotals[metricIndex];

        /// <summary>The licences one person holds, as indexes into <see cref="Licences"/>.</summary>
        public IEnumerable<int> LicencesOf(int person)
        {
            for (var i = _licenceStart[person]; i < _licenceStart[person + 1]; i++) yield return _licenceIndexes[i];
        }

        /// <summary>
        /// What the model holds, approximately - the totals, the ids and the licence memberships. For diagnostics and
        /// the performance tests, which hold it to its stated budget.
        /// </summary>
        public long ApproximateBytes =>
            (long)_chunks.Sum(c => (long)c.Length) * sizeof(int)
            + (long)_userIds.Length * sizeof(int)
            + (long)_licenceStart.Length * sizeof(int)
            + (long)_licenceIndexes.Length * sizeof(int);

        internal int[][] Chunks => _chunks;

        internal int LicenceStart(int person) => _licenceStart[person];

        internal int LicenceEnd(int person) => _licenceStart[person + 1];

        internal int LicenceIndexAt(int position) => _licenceIndexes[position];

        internal bool TryGetLicenceIndex(int licenceTypeId, out int index) => _licenceIndexById.TryGetValue(licenceTypeId, out index);

        /// <summary>
        /// Each person's row in a directory snapshot, -1 for somebody it does not hold. Worked out once per snapshot -
        /// a dictionary lookup per person - and kept for as long as the snapshot lives, so a request pays array reads.
        /// </summary>
        internal int[] RowsIn(UserDirectorySnapshot snapshot)
        {
            return _rowsBySnapshot.GetValue(snapshot, s =>
            {
                var rows = new int[_userIds.Length];
                for (var person = 0; person < rows.Length; person++)
                {
                    rows[person] = s.TryGetRow(_userIds[person], out var row) ? row : -1;
                }

                return rows;
            });
        }

        /// <summary>
        /// Applies one request's filters: who is in the population, who matches, and what they hold. One pass over the
        /// people, no SQL.
        /// </summary>
        /// <exception cref="ActivityAnalysisQueryException">A selected metric or range is not available in this period's data.</exception>
        public ActivityAnalysisEvaluation Evaluate(ActivityAnalysisQuery query, ActivityAnalysisAudience audience, CancellationToken cancellationToken = default(CancellationToken))
        {
            return ActivityAnalysisEvaluation.Run(this, query, audience, cancellationToken);
        }

        internal static int Saturate(long value)
        {
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }
    }

    /// <summary>
    /// Builds an <see cref="ActivityAnalysisReadModel"/> from the rows the SQL loader reads - or from hand-written people
    /// in a test.
    /// </summary>
    public sealed class ActivityAnalysisReadModelBuilder
    {
        private static readonly int MetricCount = ActivityAnalysisMetricCatalogue.Count;
        private readonly bool[] _available = new bool[MetricCount];
        private readonly List<int[]> _chunks = new List<int[]>();
        private readonly List<int> _userIds = new List<int>();
        private readonly Dictionary<int, int> _personByUserId = new Dictionary<int, int>();
        private readonly List<ActivityAnalysisLicence> _licences = new List<ActivityAnalysisLicence>();
        private readonly Dictionary<int, int> _licenceIndexById = new Dictionary<int, int>();
        private readonly List<long> _holdings = new List<long>();
        private int _peopleLeftOut;

        public ActivityAnalysisReadModelBuilder(ActivityAnalysisPeriod period, IEnumerable<ActivityAnalysisMetric> availableMetrics)
        {
            Period = period ?? throw new ArgumentNullException(nameof(period));
            foreach (var metric in availableMetrics ?? Enumerable.Empty<ActivityAnalysisMetric>()) _available[metric.Index] = true;
            Weeks = new ActivityAnalysisWeeklyTotals(period.Weeks);
        }

        public ActivityAnalysisPeriod Period { get; }

        /// <summary>The population's weekly totals, filled with <see cref="AddWeek"/>.</summary>
        public ActivityAnalysisWeeklyTotals Weeks { get; }

        /// <summary>The weeks the table held when the period was read (<see cref="ActivityAnalysisWeeks.DataVersion"/>).</summary>
        public string DataVersion { get; set; }

        public int PeopleCount => _userIds.Count;

        /// <summary>The person index for a user id, adding them with all-zero totals the first time.</summary>
        public int AddPerson(int userId)
        {
            if (_personByUserId.TryGetValue(userId, out var existing)) return existing;

            var person = _userIds.Count;
            if ((person & ActivityAnalysisReadModel.ChunkMask) == 0)
            {
                _chunks.Add(new int[ActivityAnalysisReadModel.ChunkSize * MetricCount]);
            }

            _userIds.Add(userId);
            _personByUserId.Add(userId, person);
            return person;
        }

        /// <summary>
        /// Records that somebody counted in the weekly figures could not be added as a person, so those figures are no
        /// longer exactly the people's (<see cref="ActivityAnalysisReadModel.PopulationWeeksExact"/>).
        /// </summary>
        public void LeaveOutPerson() => _peopleLeftOut++;

        /// <summary>Adds to a person's total of one metric, saturating. Ignored for a metric whose column does not exist.</summary>
        public void AddTotal(int person, ActivityAnalysisMetric metric, long value)
        {
            if (metric == null) throw new ArgumentNullException(nameof(metric));
            AddTotal(person, metric.Index, value);
        }

        internal void AddTotal(int person, int metricIndex, long value)
        {
            if (!_available[metricIndex] || value == 0) return;
            var chunk = _chunks[person >> ActivityAnalysisReadModel.ChunkBits];
            var slot = (person & ActivityAnalysisReadModel.ChunkMask) * MetricCount + metricIndex;
            chunk[slot] = ActivityAnalysisReadModel.Saturate(chunk[slot] + value);
        }

        /// <summary>Adds to the population's figures for one week and metric. Weeks outside the period are ignored.</summary>
        public void AddWeek(DateTime week, ActivityAnalysisMetric metric, long sum, int activePeople)
        {
            if (metric == null) throw new ArgumentNullException(nameof(metric));
            AddWeek(Period.WeekIndexOf(week), metric.Index, sum, activePeople);
        }

        internal void AddWeek(int weekIndex, int metricIndex, long sum, int activePeople)
        {
            if (!_available[metricIndex]) return;
            Weeks.Add(weekIndex, metricIndex, sum, activePeople);
        }

        public void AddLicenceType(int id, string name, string skuId)
        {
            var index = LicenceIndex(id);
            _licences[index].Name = name;
            _licences[index].SkuId = skuId;
        }

        /// <summary>
        /// Records that a user holds a licence. Ignored for somebody with no compiled week in the period - they are not
        /// in the population - so call it after the people have been added.
        /// </summary>
        public void AddHolding(int userId, int licenceTypeId)
        {
            if (!_personByUserId.TryGetValue(userId, out var person)) return;
            _holdings.Add(((long)person << 32) | (uint)LicenceIndex(licenceTypeId));
        }

        public ActivityAnalysisReadModel Build(DateTime loadedUtc)
        {
            var count = _userIds.Count;

            // Memberships as compressed rows, sorted and de-duplicated: a person's licences are a slice of one array.
            var holdings = _holdings.ToArray();
            Array.Sort(holdings);
            var start = new int[count + 1];
            var indexes = new List<int>(holdings.Length);
            long previous = -1;
            foreach (var holding in holdings)
            {
                if (holding == previous) continue;
                previous = holding;
                start[(int)(holding >> 32) + 1]++;
                indexes.Add((int)(holding & 0xFFFFFFFF));
            }

            for (var person = 0; person < count; person++) start[person + 1] += start[person];

            return new ActivityAnalysisReadModel(
                Period, loadedUtc, (bool[])_available.Clone(), _chunks.ToArray(), _userIds.ToArray(), Weeks,
                _peopleLeftOut == 0, DataVersion, _licences, start, indexes.ToArray());
        }

        private int LicenceIndex(int id)
        {
            if (_licenceIndexById.TryGetValue(id, out var index)) return index;
            index = _licences.Count;
            _licences.Add(new ActivityAnalysisLicence(id, null, null));
            _licenceIndexById.Add(id, index);
            return index;
        }
    }
}
