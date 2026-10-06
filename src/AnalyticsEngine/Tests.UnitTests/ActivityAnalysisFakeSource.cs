using Common.Entities.ActivityAnalysis;
using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Activity analysis source over hand-written weekly rows: a reference implementation of what the SQL computes,
    /// so the read model, the caches and the API can be tested with no database. All data is synthetic.
    /// </summary>
    internal sealed class ActivityAnalysisFakeSource : IActivityAnalysisSource
    {
        private readonly Dictionary<int, Dictionary<DateTime, Dictionary<string, long>>> _rows =
            new Dictionary<int, Dictionary<DateTime, Dictionary<string, long>>>();
        private readonly List<KeyValuePair<int, int>> _holdings = new List<KeyValuePair<int, int>>();
        private readonly List<ActivityAnalysisLicence> _licenceTypes = new List<ActivityAnalysisLicence>();
        private int _schemaReads;
        private int _readModelLoads;
        private int _weeklyTotalsLoads;

        /// <summary>When set, the schema returned instead of one describing the rows.</summary>
        public ActivityAnalysisSchema Schema { get; set; }

        /// <summary>Awaited at the start of every read-model load - to hold one open.</summary>
        public Func<Task> BeforeReadModel { get; set; }

        public int SchemaReads => Volatile.Read(ref _schemaReads);

        public int ReadModelLoads => Volatile.Read(ref _readModelLoads);

        public int WeeklyTotalsLoads => Volatile.Read(ref _weeklyTotalsLoads);

        /// <summary>The ids each weekly-totals load was asked for.</summary>
        public List<int[]> WeeklyTotalsRequests { get; } = new List<int[]>();

        /// <summary>A compiled week for a person, with zero in every metric not given.</summary>
        public ActivityAnalysisFakeSource Week(int userId, DateTime monday, params (string Metric, long Value)[] values)
        {
            if (!_rows.TryGetValue(userId, out var weeks)) _rows[userId] = weeks = new Dictionary<DateTime, Dictionary<string, long>>();
            if (!weeks.TryGetValue(monday, out var metrics)) weeks[monday] = metrics = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                Assert(ActivityAnalysisMetricCatalogue.TryGet(value.Metric, out _), value.Metric);
                metrics[value.Metric] = (metrics.TryGetValue(value.Metric, out var existing) ? existing : 0) + value.Value;
            }

            return this;
        }

        public ActivityAnalysisFakeSource Licence(int id, string name, string skuId)
        {
            _licenceTypes.Add(new ActivityAnalysisLicence(id, name, skuId));
            return this;
        }

        public ActivityAnalysisFakeSource Holds(int userId, params int[] licenceIds)
        {
            foreach (var id in licenceIds) _holdings.Add(new KeyValuePair<int, int>(userId, id));
            return this;
        }

        public Task<ActivityAnalysisSchema> ReadSchemaAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _schemaReads);
            if (Schema != null) return Task.FromResult(Schema);

            var weeks = _rows.Values.SelectMany(w => w.Keys).ToList();
            return Task.FromResult(ActivityAnalysisSchema.Complete(
                weeks.Count == 0 ? (DateTime?)null : weeks.Min(), weeks.Count == 0 ? (DateTime?)null : weeks.Max()));
        }

        public async Task<ActivityAnalysisReadModel> LoadReadModelAsync(ActivityAnalysisPeriod period, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readModelLoads);
            if (BeforeReadModel != null) await BeforeReadModel();

            var schema = await ReadSchemaAsync(cancellationToken);
            if (!schema.Installed) throw new ActivityAnalysisNotInstalledException();
            return Build(period, schema.AvailableMetrics, DateTime.UtcNow);
        }

        /// <summary>The read model the SQL would produce for the period - totals, weekly figures and holdings.</summary>
        public ActivityAnalysisReadModel Build(ActivityAnalysisPeriod period, IEnumerable<ActivityAnalysisMetric> available = null, DateTime? loadedUtc = null)
        {
            var metrics = (available ?? ActivityAnalysisMetricCatalogue.All).ToList();
            var builder = new ActivityAnalysisReadModelBuilder(period, metrics);
            foreach (var user in _rows.OrderBy(u => u.Key))
            {
                var inPeriod = user.Value.Where(w => period.WeekIndexOf(w.Key) >= 0).ToList();
                if (inPeriod.Count == 0) continue;

                var person = builder.AddPerson(user.Key);
                foreach (var metric in metrics)
                {
                    var total = inPeriod.Sum(w => w.Value.TryGetValue(metric.Key, out var v) ? v : 0);
                    builder.AddTotal(person, metric, total);
                }
            }

            var weekly = WeeklyTotals(period, metrics, _rows.Keys);
            foreach (var week in period.WeekStarts)
            {
                var index = period.WeekIndexOf(week);
                foreach (var metric in metrics)
                {
                    builder.AddWeek(week, metric, weekly.SumOf(index, metric.Index), weekly.ActivePeopleOf(index, metric.Index));
                }
            }

            foreach (var licence in _licenceTypes) builder.AddLicenceType(licence.Id, licence.Name, licence.SkuId);
            foreach (var holding in _holdings) builder.AddHolding(holding.Key, holding.Value);
            return builder.Build(loadedUtc ?? new DateTime(2026, 2, 9, 8, 30, 15, DateTimeKind.Utc));
        }

        public Task<ActivityAnalysisWeeklyTotals> LoadWeeklyTotalsAsync(
            ActivityAnalysisReadModel model, IReadOnlyList<int> userIds, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _weeklyTotalsLoads);
            lock (WeeklyTotalsRequests) WeeklyTotalsRequests.Add(userIds.ToArray());
            return Task.FromResult(WeeklyTotals(model.Period, model.AvailableMetrics, userIds));
        }

        private ActivityAnalysisWeeklyTotals WeeklyTotals(
            ActivityAnalysisPeriod period, IEnumerable<ActivityAnalysisMetric> metrics, IEnumerable<int> userIds)
        {
            var totals = new ActivityAnalysisWeeklyTotals(period.Weeks);
            foreach (var userId in userIds)
            {
                if (!_rows.TryGetValue(userId, out var weeks)) continue;
                foreach (var week in weeks)
                {
                    var index = period.WeekIndexOf(week.Key);
                    if (index < 0) continue;
                    foreach (var metric in metrics)
                    {
                        var value = week.Value.TryGetValue(metric.Key, out var v) ? v : 0;
                        totals.Add(index, metric.Index, value, value > 0 ? 1 : 0);
                    }
                }
            }

            return totals;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new ArgumentException("Not a catalogue metric: " + message);
        }
    }

    /// <summary>Synthetic directories for the Activity analysis tests.</summary>
    internal static class ActivityAnalysisTestDirectory
    {
        internal static UserDirectorySnapshot Build(params (int Id, string Upn, string Department, string Company)[] people)
        {
            var builder = new UserDirectorySnapshotBuilder();
            foreach (var person in people)
            {
                builder.AddUser(new UserDirectoryEntry
                {
                    UserId = person.Id,
                    UserPrincipalName = person.Upn,
                    Department = person.Department,
                    CompanyName = person.Company,
                    AccountEnabled = true,
                });
            }

            return builder.Build(new DateTime(2026, 2, 9, 8, 0, 0, DateTimeKind.Utc));
        }

        internal static CompiledUserFilter Filter(UserDirectorySnapshot snapshot, string json) =>
            UserFilterCompiler.Compile(UserFilterCodec.Parse(json), snapshot);
    }
}
