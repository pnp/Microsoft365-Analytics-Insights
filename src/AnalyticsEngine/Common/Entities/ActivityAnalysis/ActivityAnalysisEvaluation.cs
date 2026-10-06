using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>Who is asking, and which people they may see - resolved by the web tier for each request.</summary>
    public sealed class ActivityAnalysisAudience
    {
        /// <summary>The directory snapshot department, company and sign-in names come from.</summary>
        public UserDirectorySnapshot Directory { get; set; }

        /// <summary>The administrator's global filter: who is in the population at all. <c>null</c> for everyone.</summary>
        public CompiledUserFilter Population { get; set; }

        /// <summary>The reader's own filter. <c>null</c> when they set none.</summary>
        public CompiledUserFilter UserFilter { get; set; }

        /// <summary>Whether the reader holds the portal's See PII permission.</summary>
        public bool SeesIndividuals { get; set; }

        /// <summary>
        /// For a reader without See PII, the fewest matching people a figure may describe, and the smallest group shown
        /// by name. The web tier passes <c>ReportScopeResolver.MinimumPeopleWithoutSeePii</c>, so every report uses one floor.
        /// </summary>
        public int MinimumPeopleWithoutSeePii { get; set; }
    }

    /// <summary>
    /// One request's filters applied to a read model: the population, the matching people, and everything the report
    /// and the people list are built from.
    /// </summary>
    /// <remarks>
    /// Linear in the people the model holds - an array read per filter per person - with no SQL. The only figures the
    /// model cannot answer in memory are the weekly series of a filtered set of people, which
    /// <see cref="NeedsWeeklyTotals"/> says to read (<c>SqlActivityAnalysisSource.LoadWeeklyTotalsAsync</c>).
    /// </remarks>
    public sealed class ActivityAnalysisEvaluation
    {
        /// <summary>Groups beyond this many, largest first, are folded into the charts' "other" row.</summary>
        public const int MaximumChartGroups = 50;

        private static readonly int MetricCount = ActivityAnalysisMetricCatalogue.Count;

        private readonly int[] _matching;
        private readonly int[] _rows;
        private readonly int[] _licenceHolders;
        private readonly int[] _maximumTotals;
        private int[] _matchingUserIds;

        private ActivityAnalysisEvaluation(
            ActivityAnalysisReadModel model, ActivityAnalysisQuery query, ActivityAnalysisAudience audience,
            UserDirectorySnapshot directory, int[] rows, int populationPeople, int[] matching,
            int[] licenceHolders, int[] maximumTotals)
        {
            Model = model;
            Query = query;
            Audience = audience;
            Directory = directory;
            _rows = rows;
            PopulationPeople = populationPeople;
            _matching = matching;
            _licenceHolders = licenceHolders;
            _maximumTotals = maximumTotals;
        }

        public ActivityAnalysisReadModel Model { get; }

        public ActivityAnalysisQuery Query { get; }

        public ActivityAnalysisAudience Audience { get; }

        public UserDirectorySnapshot Directory { get; }

        /// <summary>People with a compiled week in the period whom the administrator's filter admits.</summary>
        public int PopulationPeople { get; }

        /// <summary>Of those, the people every one of the reader's conditions admits.</summary>
        public int MatchingPeople => _matching.Length;

        /// <summary>True when every person in the model matches, so the model's own weekly totals are the series.</summary>
        public bool CoversEveryone => _matching.Length == Model.PeopleCount;

        /// <summary>
        /// True when so few people match that the figures would be a few individuals' records - 1 to 4 - and the reader
        /// may not see individuals. Nobody matching is not suppressed: an empty report shows nobody's activity.
        /// </summary>
        public bool Suppressed =>
            !Audience.SeesIndividuals && _matching.Length > 0 && _matching.Length < Audience.MinimumPeopleWithoutSeePii;

        /// <summary>True when the weekly series has to be read for exactly the matching people.</summary>
        public bool NeedsWeeklyTotals => !Suppressed && _matching.Length > 0 && !CoversEveryone;

        /// <summary>The matching people's user ids, ascending - the set the filtered weekly series is read for.</summary>
        public int[] MatchingUserIds()
        {
            var ids = _matchingUserIds;
            if (ids != null) return ids;

            ids = new int[_matching.Length];
            for (var i = 0; i < ids.Length; i++) ids[i] = Model.UserIdAt(_matching[i]);
            Array.Sort(ids);
            _matchingUserIds = ids;
            return ids;
        }

        internal static ActivityAnalysisEvaluation Run(
            ActivityAnalysisReadModel model, ActivityAnalysisQuery query, ActivityAnalysisAudience audience,
            CancellationToken cancellationToken)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (audience == null) throw new ArgumentNullException(nameof(audience));
            if (query.Period == null || query.Period.Key != model.Period.Key)
            {
                throw new ArgumentException("The query must be resolved for the read model's period.", nameof(query));
            }

            if (audience.MinimumPeopleWithoutSeePii < 1)
            {
                throw new ArgumentException("The audience must say how many people a reader without See PII may see at least.", nameof(audience));
            }

            // The schema the query was checked against may be a few minutes older than this read: check again.
            if (query.Metrics.Any(m => !model.IsAvailable(m)))
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidMetric,
                    "One or more of the chosen metrics is not compiled in this database. Choose from the metrics the page offers.");
            }

            if (query.Ranges.Any(r => !model.IsAvailable(r.Metric)))
            {
                throw new ActivityAnalysisQueryException(ActivityAnalysisErrorCodes.InvalidRange,
                    "An activity range names a metric that is not compiled in this database.");
            }

            var directory = audience.Directory ?? audience.Population?.Snapshot ?? audience.UserFilter?.Snapshot;
            var rows = directory == null ? null : model.RowsIn(directory);
            var population = Matches(audience.Population, model, directory, rows, cancellationToken);
            var userFilter = Matches(audience.UserFilter, model, directory, rows, cancellationToken);

            bool[] wantedLicences = null;
            if (query.LicenceTypeIds.Count > 0)
            {
                // Licences nobody in the period holds are simply never matched: asking only for those matches nobody.
                wantedLicences = new bool[model.Licences.Count];
                foreach (var id in query.LicenceTypeIds)
                {
                    if (model.TryGetLicenceIndex(id, out var index)) wantedLicences[index] = true;
                }
            }

            var rangeCount = query.Ranges.Count;
            var rangeMetric = query.Ranges.Select(r => r.Metric.Index).ToArray();
            var rangeMinimum = query.Ranges.Select(r => r.Minimum ?? long.MinValue).ToArray();
            var rangeMaximum = query.Ranges.Select(r => r.Maximum ?? long.MaxValue).ToArray();

            var available = model.AvailableMetrics.Select(m => m.Index).ToArray();
            var maximumTotals = population == null ? null : new int[MetricCount];
            var licenceHolders = new int[model.Licences.Count];
            var chunks = model.Chunks;
            var matching = new List<int>();
            var populationPeople = 0;

            for (var person = 0; person < model.PeopleCount; person++)
            {
                if ((person & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (population != null && !population[person]) continue;

                populationPeople++;
                var licenceEnd = model.LicenceEnd(person);
                for (var i = model.LicenceStart(person); i < licenceEnd; i++) licenceHolders[model.LicenceIndexAt(i)]++;

                var chunk = chunks[person >> ActivityAnalysisReadModel.ChunkBits];
                var offset = (person & ActivityAnalysisReadModel.ChunkMask) * MetricCount;
                if (maximumTotals != null)
                {
                    foreach (var m in available)
                    {
                        if (chunk[offset + m] > maximumTotals[m]) maximumTotals[m] = chunk[offset + m];
                    }
                }

                if (userFilter != null && !userFilter[person]) continue;

                if (wantedLicences != null)
                {
                    var holdsOne = false;
                    for (var i = model.LicenceStart(person); i < licenceEnd && !holdsOne; i++) holdsOne = wantedLicences[model.LicenceIndexAt(i)];
                    if (!holdsOne) continue;
                }

                var inRange = true;
                for (var r = 0; r < rangeCount && inRange; r++)
                {
                    var total = chunk[offset + rangeMetric[r]];
                    inRange = total >= rangeMinimum[r] && total <= rangeMaximum[r];
                }

                if (!inRange) continue;

                matching.Add(person);
            }

            return new ActivityAnalysisEvaluation(
                model, query, audience, directory, rows, populationPeople, matching.ToArray(), licenceHolders,
                maximumTotals);
        }

        /// <summary>
        /// The report. <paramref name="matchingWeeks"/> is the weekly series read for exactly the matching people, required
        /// when <see cref="NeedsWeeklyTotals"/> and ignored otherwise.
        /// </summary>
        public ActivityAnalysisReport BuildReport(ActivityAnalysisWeeklyTotals matchingWeeks, UserFilterEcho userFilter)
        {
            var metrics = Query.Metrics;
            var report = new ActivityAnalysisReport
            {
                GeneratedUtc = TruncateToSeconds(Model.LoadedUtc),
                From = ActivityAnalysisWeeks.Format(Model.Period.From),
                To = ActivityAnalysisWeeks.Format(Model.Period.To),
                WeekStarts = Model.Period.WeekStarts.Select(ActivityAnalysisWeeks.Format).ToList(),
                Metrics = metrics.Select(m => m.Key).ToList(),
                PopulationPeople = PopulationPeople,
                MatchingPeople = MatchingPeople,
                Suppressed = Suppressed,
                Licences = BuildLicences(),
                RangeMaxima = Model.AvailableMetrics.Select(m => new ActivityAnalysisRangeMaximum
                {
                    Metric = m.Key,
                    Max = _maximumTotals != null ? _maximumTotals[m.Index] : Model.MaximumTotalOf(m.Index),
                }).ToList(),
                UserFilter = userFilter,
                Total = new ActivityAnalysisTotal { People = MatchingPeople },
            };

            // A handful of people the reader may not see individually: the counts stay, every figure goes.
            if (Suppressed) return report;

            var selected = metrics.Select(m => m.Index).ToArray();
            var s = selected.Length;
            var departments = Directory?.Column(UserFilterDimensions.Department);
            var companies = Directory?.Column(UserFilterDimensions.CompanyName);
            var departmentSlots = (departments?.Values.Count ?? 0) + 1;
            var companySlots = (companies?.Values.Count ?? 0) + 1;

            var departmentPeople = new int[departmentSlots];
            var departmentActive = new int[departmentSlots];
            var departmentSums = new long[departmentSlots * s];
            var departmentUnique = new int[departmentSlots * s];
            var companyPeople = new int[companySlots];
            var companyActive = new int[companySlots];
            var totalSums = new long[s];
            var totalUnique = new int[s];
            var active = 0;
            var chunks = Model.Chunks;

            foreach (var person in _matching)
            {
                var row = _rows == null ? -1 : _rows[person];
                var department = GroupOf(departments, row, departmentSlots);
                var company = GroupOf(companies, row, companySlots);
                departmentPeople[department]++;
                companyPeople[company]++;

                var chunk = chunks[person >> ActivityAnalysisReadModel.ChunkBits];
                var offset = (person & ActivityAnalysisReadModel.ChunkMask) * MetricCount;
                var any = false;
                for (var i = 0; i < s; i++)
                {
                    var total = chunk[offset + selected[i]];
                    totalSums[i] += total;
                    departmentSums[department * s + i] += total;
                    if (total > 0)
                    {
                        any = true;
                        totalUnique[i]++;
                        departmentUnique[department * s + i]++;
                    }
                }

                if (!any) continue;
                active++;
                departmentActive[department]++;
                companyActive[company]++;
            }

            report.ActivePeople = active;
            report.Total.Values = Values(metrics, totalSums, totalUnique, 0);
            report.Series = BuildSeries(matchingWeeks);
            report.ByCompany = BuildChart(companies, companyPeople, companyActive);
            report.ByDepartment = BuildChart(departments, departmentPeople, departmentActive);

            var otherDepartments = 0;
            report.Departments = BuildDepartments(departments, departmentPeople, departmentSums, departmentUnique, out otherDepartments);
            report.OtherDepartments = otherDepartments;
            return report;
        }

        /// <summary>
        /// The matching people themselves - in one department, in none, or everywhere - largest total of
        /// <paramref name="sort"/> first, then by sign-in name. For a reader with See PII only: the caller checks.
        /// </summary>
        public ActivityAnalysisPeople BuildPeople(string department, bool noDepartment, ActivityAnalysisMetric sort, int top)
        {
            if (sort == null) throw new ArgumentNullException(nameof(sort));
            if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

            var departments = Directory?.Column(UserFilterDimensions.Department);
            var notSet = (departments?.Values.Count ?? 0);
            var wanted = -1;
            var anyDepartment = !noDepartment && string.IsNullOrWhiteSpace(department);
            if (noDepartment) wanted = notSet;
            else if (!anyDepartment) wanted = departments?.IndexOf(department) ?? -1;

            var result = new ActivityAnalysisPeople
            {
                Department = noDepartment || anyDepartment ? null : department.Trim(),
                NoDepartment = noDepartment,
            };

            // A department nobody holds matches nobody, rather than everybody.
            if (!anyDepartment && wanted < 0) return result;

            var userNames = Directory?.Column(UserFilterDimensions.UserName);
            var heap = new PeopleHeap(top, (a, b) => Compare(a, b, sort.Index, userNames));
            var total = 0;
            foreach (var person in _matching)
            {
                if (!anyDepartment && GroupOf(departments, _rows == null ? -1 : _rows[person], notSet + 1) != wanted) continue;
                total++;
                heap.Offer(person);
            }

            result.TotalPeople = total;
            result.Truncated = total > top;
            result.People = heap.Sorted().Select(person =>
            {
                var row = _rows == null ? -1 : _rows[person];
                var group = GroupOf(departments, row, notSet + 1);
                return new ActivityAnalysisPerson
                {
                    UserPrincipalName = ValueAt(userNames, row),
                    Department = group == notSet ? null : departments.Values[group],
                    Values = Query.Metrics.Select(m => new ActivityAnalysisPersonValue
                    {
                        Metric = m.Key,
                        Sum = Model.TotalOf(person, m.Index),
                    }).ToList(),
                };
            }).ToList();
            return result;
        }

        private int Compare(int a, int b, int metricIndex, UserDirectoryColumn userNames)
        {
            var totalA = Model.TotalOf(a, metricIndex);
            var totalB = Model.TotalOf(b, metricIndex);
            if (totalA != totalB) return totalA > totalB ? -1 : 1;

            var nameA = ValueAt(userNames, _rows == null ? -1 : _rows[a]);
            var nameB = ValueAt(userNames, _rows == null ? -1 : _rows[b]);
            if (nameA == null || nameB == null)
            {
                if (nameA != null) return -1;
                if (nameB != null) return 1;
            }
            else
            {
                var byName = string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                byName = string.CompareOrdinal(nameA, nameB);
                if (byName != 0) return byName;
            }

            return Model.UserIdAt(a).CompareTo(Model.UserIdAt(b));
        }

        private List<ActivityAnalysisSeries> BuildSeries(ActivityAnalysisWeeklyTotals matchingWeeks)
        {
            var weeks = Model.Period.Weeks;
            ActivityAnalysisWeeklyTotals source;
            if (_matching.Length == 0) source = null;
            else if (CoversEveryone) source = Model.PopulationWeeks;
            else source = matchingWeeks ?? throw new InvalidOperationException(
                "The weekly series of a filtered set of people must be read before the report is built.");

            if (source != null && source.Weeks != weeks)
            {
                throw new ArgumentException("The weekly series does not cover the read model's period.", nameof(matchingWeeks));
            }

            return Query.Metrics.Select(metric =>
            {
                var series = new ActivityAnalysisSeries { Metric = metric.Key, Sum = new long[weeks], ActivePeople = new int[weeks] };
                if (source != null)
                {
                    for (var week = 0; week < weeks; week++)
                    {
                        series.Sum[week] = source.SumOf(week, metric.Index);
                        series.ActivePeople[week] = source.ActivePeopleOf(week, metric.Index);
                    }
                }

                return series;
            }).ToList();
        }

        private ActivityAnalysisGroupChart BuildChart(UserDirectoryColumn column, int[] people, int[] active)
        {
            var notSet = people.Length - 1;
            var shown = new List<int>();
            var chart = new ActivityAnalysisGroupChart();
            var otherActive = 0;

            for (var group = 0; group < people.Length; group++)
            {
                if (active[group] == 0) continue;
                if (IsSmall(people[group]))
                {
                    chart.OtherGroups++;
                    otherActive += active[group];
                }
                else
                {
                    shown.Add(group);
                }
            }

            shown.Sort((a, b) =>
            {
                if (active[a] != active[b]) return active[a] > active[b] ? -1 : 1;
                return CompareNames(NameOf(column, a, notSet), NameOf(column, b, notSet));
            });

            if (shown.Count > MaximumChartGroups)
            {
                for (var i = MaximumChartGroups; i < shown.Count; i++)
                {
                    chart.OtherGroups++;
                    otherActive += active[shown[i]];
                }

                shown.RemoveRange(MaximumChartGroups, shown.Count - MaximumChartGroups);
            }

            chart.Rows = shown.Select(group => new ActivityAnalysisGroupRow
            {
                Name = NameOf(column, group, notSet),
                Other = false,
                ActivePeople = active[group],
            }).ToList();

            if (chart.OtherGroups > 0)
            {
                chart.Rows.Add(new ActivityAnalysisGroupRow { Name = null, Other = true, ActivePeople = otherActive });
            }

            return chart;
        }

        private List<ActivityAnalysisDepartmentRow> BuildDepartments(
            UserDirectoryColumn column, int[] people, long[] sums, int[] unique, out int otherDepartments)
        {
            var metrics = Query.Metrics;
            var s = metrics.Count;
            var notSet = people.Length - 1;
            var named = new List<int>();
            var otherPeople = 0;
            var otherSums = new long[s];
            var otherUnique = new int[s];
            otherDepartments = 0;

            for (var group = 0; group < people.Length; group++)
            {
                if (people[group] == 0) continue;
                if (IsSmall(people[group]))
                {
                    otherDepartments++;
                    otherPeople += people[group];
                    for (var i = 0; i < s; i++)
                    {
                        otherSums[i] += sums[group * s + i];
                        otherUnique[i] += unique[group * s + i];
                    }
                }
                else
                {
                    named.Add(group);
                }
            }

            // Sorted by name, "not set" after every named department.
            named.Sort((a, b) =>
            {
                if (a == notSet || b == notSet) return a == b ? 0 : a == notSet ? 1 : -1;
                return CompareNames(column.Values[a], column.Values[b]);
            });

            var rows = named.Select(group => new ActivityAnalysisDepartmentRow
            {
                Name = NameOf(column, group, notSet),
                Other = false,
                People = people[group],
                Values = Values(metrics, sums, unique, group * s),
            }).ToList();

            if (otherDepartments > 0)
            {
                rows.Add(new ActivityAnalysisDepartmentRow
                {
                    Name = null,
                    Other = true,
                    People = otherPeople,
                    Values = Values(metrics, otherSums, otherUnique, 0),
                });
            }

            return rows;
        }

        private List<ActivityAnalysisLicenceModel> BuildLicences()
        {
            return Model.Licences
                .Select((licence, index) => new { licence, people = _licenceHolders[index] })
                .Where(l => l.people > 0)
                .OrderBy(l => l.licence.Name == null ? 1 : 0)
                .ThenBy(l => l.licence.Name, NameComparer)
                .ThenBy(l => l.licence.SkuId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.licence.Id)
                .Select(l => new ActivityAnalysisLicenceModel
                {
                    Id = l.licence.Id,
                    Name = l.licence.Name,
                    SkuId = l.licence.SkuId,
                    People = l.people,
                })
                .ToList();
        }

        /// <summary>A group a reader without See PII may not see by name - fewer people than the floor.</summary>
        private bool IsSmall(int people) => !Audience.SeesIndividuals && people < Audience.MinimumPeopleWithoutSeePii;

        private static List<ActivityAnalysisMetricValue> Values(
            IReadOnlyList<ActivityAnalysisMetric> metrics, long[] sums, int[] unique, int offset)
        {
            var values = new List<ActivityAnalysisMetricValue>(metrics.Count);
            for (var i = 0; i < metrics.Count; i++)
            {
                values.Add(new ActivityAnalysisMetricValue { Metric = metrics[i].Key, Sum = sums[offset + i], Unique = unique[offset + i] });
            }

            return values;
        }

        /// <summary>The group slot of a directory row: the value's index, or the last slot for "not set".</summary>
        private static int GroupOf(UserDirectoryColumn column, int row, int slots)
        {
            if (column == null || row < 0) return slots - 1;
            var index = column.ValueByRow[row];
            return index < 0 ? slots - 1 : index;
        }

        private static string NameOf(UserDirectoryColumn column, int group, int notSet) =>
            group == notSet || column == null ? null : column.Values[group];

        private static string ValueAt(UserDirectoryColumn column, int row)
        {
            if (column?.ValueByRow == null || row < 0) return null;
            var index = column.ValueByRow[row];
            return index < 0 ? null : column.Values[index];
        }

        /// <summary>Names in reading order whatever the server's culture, then by exact spelling; "not set" (null) last.</summary>
        private static int CompareNames(string a, string b)
        {
            if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
            var compared = NameComparer.Compare(a, b);
            return compared != 0 ? compared : string.CompareOrdinal(a, b);
        }

        private static readonly StringComparer NameComparer = StringComparer.InvariantCultureIgnoreCase;

        private static DateTime TruncateToSeconds(DateTime value) =>
            new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        /// <summary>
        /// Who is in, as one flag per person: the filter's own flags when it was compiled against the same snapshot,
        /// otherwise a lookup by user id. <c>null</c> for no filter.
        /// </summary>
        private static bool[] Matches(
            CompiledUserFilter filter, ActivityAnalysisReadModel model, UserDirectorySnapshot directory, int[] rows,
            CancellationToken cancellationToken)
        {
            if (filter == null) return null;

            var sameSnapshot = rows != null && ReferenceEquals(filter.Snapshot, directory);
            var matches = new bool[model.PeopleCount];
            for (var person = 0; person < matches.Length; person++)
            {
                if ((person & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                matches[person] = sameSnapshot
                    ? rows[person] >= 0 && filter.MatchesRow(rows[person])
                    : filter.Matches(model.UserIdAt(person));
            }

            return matches;
        }

        /// <summary>The best <c>capacity</c> people seen, kept with the worst at the root so most are rejected in one comparison.</summary>
        private sealed class PeopleHeap
        {
            private readonly int[] _items;
            private readonly Func<int, int, int> _compare;
            private int _count;

            internal PeopleHeap(int capacity, Func<int, int, int> compare)
            {
                _items = new int[capacity];
                _compare = compare;
            }

            internal void Offer(int person)
            {
                if (_count < _items.Length)
                {
                    _items[_count] = person;
                    SiftUp(_count++);
                    return;
                }

                // Better than the worst kept: replace it.
                if (_compare(person, _items[0]) < 0)
                {
                    _items[0] = person;
                    SiftDown(0);
                }
            }

            internal IEnumerable<int> Sorted()
            {
                var kept = new int[_count];
                Array.Copy(_items, kept, _count);
                Array.Sort(kept, (a, b) => _compare(a, b));
                return kept;
            }

            // The root is the worst kept person: a parent is never better than its children.
            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    var parent = (index - 1) / 2;
                    if (_compare(_items[parent], _items[index]) >= 0) return;
                    Swap(parent, index);
                    index = parent;
                }
            }

            private void SiftDown(int index)
            {
                while (true)
                {
                    var left = index * 2 + 1;
                    if (left >= _count) return;
                    var right = left + 1;
                    var worse = right < _count && _compare(_items[right], _items[left]) > 0 ? right : left;
                    if (_compare(_items[worse], _items[index]) <= 0) return;
                    Swap(worse, index);
                    index = worse;
                }
            }

            private void Swap(int a, int b)
            {
                var item = _items[a];
                _items[a] = _items[b];
                _items[b] = item;
            }
        }
    }
}
