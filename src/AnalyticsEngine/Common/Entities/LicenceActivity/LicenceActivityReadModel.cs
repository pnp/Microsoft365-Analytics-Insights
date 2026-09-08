using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Common.Entities.LicenceActivity
{
    public sealed class LicenceActivityDirectoryUser
    {
        public int UserId { get; set; }
        public string UserPrincipalName { get; set; }
        public string Mail { get; set; }
        public int DepartmentId { get; set; }
        public int CountryId { get; set; }
        public string Department { get; set; }
        public string Country { get; set; }
        public bool? AccountEnabled { get; set; }
    }

    public readonly struct LicenceActivityMembership
    {
        public LicenceActivityMembership(int userId, int licenceTypeId)
        {
            UserId = userId;
            LicenceTypeId = licenceTypeId;
        }

        public int UserId { get; }
        public int LicenceTypeId { get; }
    }

    public readonly struct LicenceActivityScore
    {
        public LicenceActivityScore(
            int activeSamples, int observedSamples, bool frequencyKnown,
            double? averageActions, DateTime? lastActivityUtc)
        {
            ActiveSamples = activeSamples;
            ObservedSamples = observedSamples;
            FrequencyKnown = frequencyKnown;
            AverageActions = averageActions;
            LastActivityUtc = lastActivityUtc;
        }

        public int ActiveSamples { get; }
        public int ObservedSamples { get; }
        public bool FrequencyKnown { get; }
        public double? AverageActions { get; }
        public DateTime? LastActivityUtc { get; }
    }

    /// <summary>
    /// Immutable, unique-user projection shared by every filter and licence drill-down for one exact date range.
    /// Ranking always uses ordinal-ignore-case UPN ordering followed by user ID, so equal names are deterministic.
    /// </summary>
    public sealed class LicenceActivityReadModel
    {
        private const sbyte UnknownBand = -1;
        private readonly string _from;
        private readonly string _to;
        private readonly DirectoryEntry[] _users;
        private readonly bool[] _eligible;
        private readonly SkuEntry[] _licences;
        private readonly Dictionary<int, int[]> _membersByLicence;
        private readonly Dictionary<int, int> _licenceSlotById;
        private readonly int[] _licenceOffsetsByUser;
        private readonly int[] _licenceSlotsByUser;
        private readonly LicenceActivityCoverage[] _coverage;
        private readonly IReadOnlyDictionary<int, LicenceActivityScore>[] _scores;
        private readonly sbyte[][] _bands;
        private readonly RankValue[][] _rankValues;
        private readonly object _rankingGate = new object();
        private readonly ConcurrentDictionary<string, RankingIndex> _rankings =
            new ConcurrentDictionary<string, RankingIndex>(StringComparer.Ordinal);

        public LicenceActivityReadModel(
            LicenceActivityQuery range,
            IReadOnlyList<LicenceActivitySku> licences,
            IReadOnlyList<LicenceActivityDirectoryUser> users,
            IReadOnlyList<LicenceActivityMembership> memberships,
            IReadOnlyList<LicenceActivityCoverage> coverage,
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, LicenceActivityScore>> scores)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            if (licences == null) throw new ArgumentNullException(nameof(licences));
            if (users == null) throw new ArgumentNullException(nameof(users));
            if (memberships == null) throw new ArgumentNullException(nameof(memberships));
            if (coverage == null) throw new ArgumentNullException(nameof(coverage));
            if (scores == null) throw new ArgumentNullException(nameof(scores));
            if (range.DepartmentId.HasValue || range.CountryId.HasValue || range.LicenceTypeId.HasValue)
                throw new ArgumentException("A licence activity read model must cover an unfiltered date range.", nameof(range));

            _from = range.From;
            _to = range.To;
            Id = Guid.NewGuid().ToString("N");

            var userIndexes = new Dictionary<int, int>(users.Count);
            _users = new DirectoryEntry[users.Count];
            for (var index = 0; index < users.Count; index++)
            {
                var user = users[index] ?? throw new ArgumentException("Directory users cannot contain null.", nameof(users));
                if (userIndexes.ContainsKey(user.UserId))
                    throw new ArgumentException("Directory user IDs must be unique.", nameof(users));
                userIndexes.Add(user.UserId, index);
                _users[index] = new DirectoryEntry(user);
            }

            var licenceIds = new HashSet<int>();
            _licences = licences.Select(licence =>
            {
                if (licence == null) throw new ArgumentException("Licences cannot contain null.", nameof(licences));
                if (!licenceIds.Add(licence.LicenceTypeId))
                    throw new ArgumentException("Licence type IDs must be unique.", nameof(licences));
                return new SkuEntry(licence);
            }).OrderBy(licence => licence.Name, NullFirstOrdinalIgnoreCaseComparer.Instance)
              .ThenBy(licence => licence.LicenceTypeId)
              .ToArray();
            _licenceSlotById = _licences
                .Select((licence, slot) => new { licence.LicenceTypeId, Slot = slot })
                .ToDictionary(item => item.LicenceTypeId, item => item.Slot);

            var membershipLists = _licences.ToDictionary(
                licence => licence.LicenceTypeId, licence => new List<int>());
            _eligible = new bool[_users.Length];
            foreach (var membership in memberships)
            {
                int userIndex;
                if (!userIndexes.TryGetValue(membership.UserId, out userIndex)) continue;
                _eligible[userIndex] = true;
                List<int> list;
                if (membershipLists.TryGetValue(membership.LicenceTypeId, out list))
                    list.Add(userIndex);
            }

            _membersByLicence = new Dictionary<int, int[]>(membershipLists.Count);
            foreach (var pair in membershipLists)
            {
                pair.Value.Sort();
                var write = 0;
                for (var read = 0; read < pair.Value.Count; read++)
                {
                    if (write == 0 || pair.Value[read] != pair.Value[write - 1])
                        pair.Value[write++] = pair.Value[read];
                }
                if (write < pair.Value.Count)
                    pair.Value.RemoveRange(write, pair.Value.Count - write);
                _membersByLicence.Add(pair.Key, pair.Value.ToArray());
            }
            _licenceOffsetsByUser = new int[_users.Length + 1];
            foreach (var pair in _membersByLicence)
                foreach (var user in pair.Value)
                    _licenceOffsetsByUser[user + 1]++;
            for (var user = 1; user < _licenceOffsetsByUser.Length; user++)
                _licenceOffsetsByUser[user] += _licenceOffsetsByUser[user - 1];
            _licenceSlotsByUser = new int[_licenceOffsetsByUser[_users.Length]];
            var nextLicence = (int[])_licenceOffsetsByUser.Clone();
            foreach (var pair in _membersByLicence)
            {
                var slot = _licenceSlotById[pair.Key];
                foreach (var user in pair.Value)
                    _licenceSlotsByUser[nextLicence[user]++] = slot;
            }

            _coverage = new LicenceActivityCoverage[LicenceActivityQuery.Workloads.Length];
            var suppliedCoverage = new Dictionary<string, LicenceActivityCoverage>(StringComparer.Ordinal);
            foreach (var item in coverage)
            {
                if (item == null || !LicenceActivityQuery.Workloads.Contains(item.Workload, StringComparer.Ordinal))
                    continue;
                if (suppliedCoverage.ContainsKey(item.Workload))
                    throw new ArgumentException("Coverage workloads must be unique.", nameof(coverage));
                suppliedCoverage.Add(item.Workload, item);
            }

            _scores = new IReadOnlyDictionary<int, LicenceActivityScore>[LicenceActivityQuery.Workloads.Length];
            _bands = new sbyte[LicenceActivityQuery.Workloads.Length][];
            _rankValues = new RankValue[LicenceActivityQuery.Workloads.Length][];
            for (var workload = 0; workload < LicenceActivityQuery.Workloads.Length; workload++)
            {
                var name = LicenceActivityQuery.Workloads[workload];
                LicenceActivityCoverage item;
                _coverage[workload] = suppliedCoverage.TryGetValue(name, out item)
                    ? CloneCoverage(item)
                    : MissingCoverage(name);
                IReadOnlyDictionary<int, LicenceActivityScore> workloadScores;
                _scores[workload] = scores.TryGetValue(name, out workloadScores) && workloadScores != null
                    ? workloadScores
                    : EmptyScores.Instance;
                _bands[workload] = new sbyte[_users.Length];
                _rankValues[workload] = new RankValue[_users.Length];
                for (var user = 0; user < _users.Length; user++)
                {
                    if (!_eligible[user])
                    {
                        _bands[workload][user] = UnknownBand;
                        continue;
                    }
                    var evidence = Evidence(workload, user);
                    _bands[workload][user] = BandCode(evidence);
                    _rankValues[workload][user] = new RankValue(evidence);
                }
            }
        }

        public string Id { get; }

        public LicenceActivityOverview BuildOverview(
            LicenceActivityQuery query, CancellationToken cancellationToken)
        {
            ValidateRange(query);
            cancellationToken.ThrowIfCancellationRequested();

            var included = new bool[_users.Length];
            var includedCount = 0;
            for (var index = 0; index < _users.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (_eligible[index] && InCohort(_users[index], query))
                {
                    included[index] = true;
                    includedCount++;
                }
            }

            var overview = new LicenceActivityOverview
            {
                ReadModelId = Id,
                Query = query,
                DistinctAssignedUsers = includedCount,
                Coverage = _coverage.Select(CloneCoverage).ToList()
            };

            foreach (var licence in _licences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = new LicenceActivitySku
                {
                    LicenceTypeId = licence.LicenceTypeId,
                    Name = licence.Name,
                    SkuId = licence.SkuId
                };
                var distributions = NewDistributions();
                int[] members;
                if (_membersByLicence.TryGetValue(licence.LicenceTypeId, out members))
                {
                    foreach (var user in members)
                    {
                        if (!included[user]) continue;
                        result.AssignedUsers++;
                        AddBands(distributions, user);
                    }
                }
                result.Workloads = ToDistributions(distributions, result.AssignedUsers);
                overview.Licences.Add(result);
            }

            var departments = BuildDemographics(included, true, cancellationToken);
            var countries = BuildDemographics(included, false, cancellationToken);
            overview.DemographicsTruncated = departments.Count > 50 || countries.Count > 50;
            overview.Departments = ProjectDemographics(departments, cancellationToken);
            overview.Countries = ProjectDemographics(countries, cancellationToken);

            if (overview.Licences.Count == 0)
                overview.Messages.Add(LicenceActivityRules.Notes.NoLicences);
            else if (overview.DistinctAssignedUsers == 0)
                overview.Messages.Add(LicenceActivityRules.Notes.NobodyHoldsALicence);
            overview.Messages.Add(LicenceActivityRules.Notes.NoDisplayNames);
            foreach (var item in overview.Coverage.Where(item => item.Status != LicenceActivitySql.Available))
            {
                if (!string.IsNullOrWhiteSpace(item.Message))
                    overview.Messages.Add(LicenceActivityRules.Notes.ForService(item.Workload, item.Message));
            }
            if (overview.DemographicsTruncated)
                overview.Messages.Add(LicenceActivityRules.Notes.DemographicsCapped);
            cancellationToken.ThrowIfCancellationRequested();
            return overview;
        }

        public LicenceActivityUsers BuildUsers(
            LicenceActivityOverview overview,
            LicenceActivityQuery query,
            CancellationToken cancellationToken)
        {
            if (overview == null) throw new ArgumentNullException(nameof(overview));
            ValidateRange(query);
            ValidateOverviewScope(overview, query);
            cancellationToken.ThrowIfCancellationRequested();

            var result = new LicenceActivityUsers
            {
                OverviewId = overview.SnapshotId,
                Query = query
            };
            if (!query.LicenceTypeId.HasValue)
                throw new ArgumentException("A licenceTypeId is required for individual users.", nameof(query));
            if (!overview.Licences.Any(licence => licence.LicenceTypeId == query.LicenceTypeId.Value))
                throw new ArgumentException("The selected licence is not present in this overview.", nameof(query));

            int[] members;
            int licenceSlot;
            if (!_membersByLicence.TryGetValue(query.LicenceTypeId.Value, out members)
                || !_licenceSlotById.TryGetValue(query.LicenceTypeId.Value, out licenceSlot))
                throw new ArgumentException("The selected licence is not present in this read model.", nameof(query));

            var workload = WorkloadIndex(query.Workload);
            var visited = 0;
            foreach (var index in members)
            {
                if ((visited++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var user = _users[index];
                if (!InCohort(user, query) || !MatchesSearch(user, query.Search)) continue;
                result.TotalUsers++;
                if (_rankValues[workload][index].CanRankMost) result.RankedUsers++;
            }
            if (result.TotalUsers == 0)
            {
                AddUserMessages(result, query.Workload);
                return result;
            }

            var most = Pick(
                GetRanking(workload, RankingKind.Most, null, null, cancellationToken)
                    .ByLicence[licenceSlot],
                query, 0, query.Top, cancellationToken);
            var least = Pick(
                GetRanking(workload, RankingKind.Least, null, null, cancellationToken)
                    .ByLicence[licenceSlot],
                query, 0, query.Top, cancellationToken);
            var offset = (query.Page - 1) * query.PageSize;
            var page = Pick(
                GetRanking(workload, RankingKind.Page, query.Sort, query.Direction, cancellationToken)
                    .ByLicence[licenceSlot],
                query, offset, query.PageSize, cancellationToken);

            result.MostActive = most.Select(MaterializeUser).ToList();
            result.LeastActive = least.Select(MaterializeUser).ToList();
            result.Users = page.Select(MaterializeUser).ToList();
            AddUserMessages(result, query.Workload);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private void ValidateRange(LicenceActivityQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (query.From != _from || query.To != _to)
                throw new ArgumentException("The query date range does not match this licence activity read model.", nameof(query));
        }

        private void ValidateOverviewScope(LicenceActivityOverview overview, LicenceActivityQuery query)
        {
            if (!string.IsNullOrEmpty(overview.ReadModelId) && overview.ReadModelId != Id)
                throw new ArgumentException("The overview belongs to a different licence activity read model.", nameof(overview));
            if (overview.Query == null || overview.Query.From != _from || overview.Query.To != _to)
                throw new ArgumentException("The overview date range does not match this licence activity read model.", nameof(overview));
            if (overview.Query.DepartmentId != query.DepartmentId || overview.Query.CountryId != query.CountryId)
                throw new ArgumentException("The user query must use the overview's exact demographic scope.", nameof(query));
        }

        private List<DemographicAccumulator> BuildDemographics(
            bool[] included, bool department, CancellationToken cancellationToken)
        {
            var values = new Dictionary<int, DemographicAccumulator>();
            for (var index = 0; index < _users.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!included[index]) continue;
                var user = _users[index];
                var id = department ? user.DepartmentId : user.CountryId;
                var name = department ? user.Department : user.Country;
                if (id == 0) name = "Unknown";
                DemographicAccumulator value;
                if (!values.TryGetValue(id, out value))
                {
                    value = new DemographicAccumulator(id, string.IsNullOrEmpty(name) ? "Unknown" : name);
                    values.Add(id, value);
                }
                value.AssignedUsers++;
                AddBands(value.Distributions, index);
            }
            return values.Values
                .OrderByDescending(value => value.AssignedUsers)
                .ThenBy(value => value.Name, NullFirstOrdinalIgnoreCaseComparer.Instance)
                .ThenBy(value => value.Id)
                .ToList();
        }

        private static List<LicenceActivityDemographic> ProjectDemographics(
            List<DemographicAccumulator> values, CancellationToken cancellationToken)
        {
            var count = Math.Min(50, values.Count);
            var result = new List<LicenceActivityDemographic>(count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = values[index];
                result.Add(new LicenceActivityDemographic
                {
                    Id = value.Id,
                    Name = value.Name,
                    AssignedUsers = value.AssignedUsers,
                    Workloads = ToDistributions(value.Distributions, value.AssignedUsers)
                });
            }
            return result;
        }

        private RankingIndex GetRanking(
            int workload, RankingKind kind, string sort, string direction,
            CancellationToken cancellationToken)
        {
            var key = kind == RankingKind.Page && sort == "upn"
                ? "page|upn|" + direction
                : workload + "|" + kind + "|" + sort + "|" + direction;
            RankingIndex cached;
            if (_rankings.TryGetValue(key, out cached)) return cached;
            lock (_rankingGate)
            {
                if (_rankings.TryGetValue(key, out cached)) return cached;
                cancellationToken.ThrowIfCancellationRequested();
                cached = BuildRanking(workload, kind, sort, direction, cancellationToken);
                _rankings.TryAdd(key, cached);
                return cached;
            }
        }

        private RankingIndex BuildRanking(
            int workload, RankingKind kind, string sort, string direction,
            CancellationToken cancellationToken)
        {
            var indexes = new List<int>();
            for (var index = 0; index < _users.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!_eligible[index]) continue;
                var rank = _rankValues[workload][index];
                if (kind == RankingKind.Most && !rank.CanRankMost) continue;
                if (kind == RankingKind.Least && !rank.Available) continue;
                indexes.Add(index);
            }
            var built = indexes.ToArray();
            Array.Sort(built, new RankingComparer(this, workload, kind, sort, direction));
            cancellationToken.ThrowIfCancellationRequested();

            // Build every SKU intersection in one pass over the shared ordering. Subsequent
            // licence, search, cohort and page requests scan compact ID arrays without re-sorting.
            var counts = new int[_licences.Length];
            var visited = 0;
            foreach (var user in built)
            {
                if ((visited++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (var offset = _licenceOffsetsByUser[user];
                    offset < _licenceOffsetsByUser[user + 1];
                    offset++)
                {
                    counts[_licenceSlotsByUser[offset]]++;
                }
            }
            var byLicence = new int[_licences.Length][];
            for (var slot = 0; slot < byLicence.Length; slot++)
                byLicence[slot] = new int[counts[slot]];
            Array.Clear(counts, 0, counts.Length);
            visited = 0;
            foreach (var user in built)
            {
                if ((visited++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (var offset = _licenceOffsetsByUser[user];
                    offset < _licenceOffsetsByUser[user + 1];
                    offset++)
                {
                    var slot = _licenceSlotsByUser[offset];
                    byLicence[slot][counts[slot]++] = user;
                }
            }
            return new RankingIndex(byLicence);
        }

        private List<int> Pick(
            int[] ordered, LicenceActivityQuery query, int skip, int take,
            CancellationToken cancellationToken)
        {
            var result = new List<int>(take);
            var seen = 0;
            for (var index = 0; index < ordered.Length && result.Count < take; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var user = ordered[index];
                if (!InCohort(_users[user], query)
                    || !MatchesSearch(_users[user], query.Search))
                {
                    continue;
                }
                if (seen++ < skip) continue;
                result.Add(user);
            }
            return result;
        }

        private LicenceActivityUser MaterializeUser(int index)
        {
            var source = _users[index];
            var result = new LicenceActivityUser
            {
                UserId = source.UserId,
                UserPrincipalName = source.UserPrincipalName,
                Department = source.Department,
                Country = source.Country,
                AccountEnabled = source.AccountEnabled
            };
            for (var workload = 0; workload < LicenceActivityQuery.Workloads.Length; workload++)
            {
                var evidence = Evidence(workload, index);
                result.Workloads.Add(new LicenceActivityEvidence
                {
                    Workload = LicenceActivityQuery.Workloads[workload],
                    Status = evidence.Status,
                    Band = evidence.Available
                        ? LicenceActivityRules.Band(
                            evidence.ActiveSamples, evidence.ObservedSamples, _coverage[workload].ExpectedSamples)
                        : "unknown",
                    Source = _coverage[workload].Source,
                    Measure = _coverage[workload].Measure,
                    ActiveSamples = evidence.ActiveSamples,
                    ObservedSamples = evidence.ObservedSamples,
                    ExpectedSamples = _coverage[workload].ExpectedSamples,
                    AverageActions = evidence.AverageActions,
                    LastActivityUtc = evidence.LastActivityUtc
                });
            }
            return result;
        }

        private void AddUserMessages(LicenceActivityUsers result, string workload)
        {
            result.Messages.Add(LicenceActivityRules.Notes.RankingMethod);
            var selected = _coverage[WorkloadIndex(workload)];
            if (selected.Status != LicenceActivitySql.Available && !string.IsNullOrWhiteSpace(selected.Message))
                result.Messages.Add(selected.Message);
            if (result.TotalUsers > 0 && result.RankedUsers == 0)
                result.Messages.Add(LicenceActivityRules.Notes.NobodyRankable);
        }

        private EvidenceState Evidence(int workload, int userIndex)
        {
            var coverage = _coverage[workload];
            LicenceActivityScore score;
            var present = _scores[workload].TryGetValue(_users[userIndex].UserId, out score);
            var status = coverage.Status ?? LicenceActivitySql.MissingCoverage;
            var active = present ? score.ActiveSamples : 0;
            var observed = coverage.ObservedSamples;
            if (IsOfficialReport(coverage.Source))
            {
                observed = present ? score.ObservedSamples : 0;
                if (status == LicenceActivitySql.Available)
                {
                    if (!present)
                        status = LicenceActivitySql.MissingCoverage;
                    else if (!score.FrequencyKnown || observed != coverage.ExpectedSamples)
                        status = LicenceActivitySql.Partial;
                }
            }
            return new EvidenceState(
                status, active, observed, coverage.ExpectedSamples,
                present ? score.AverageActions : null,
                present ? score.LastActivityUtc : null,
                !present && status == LicenceActivitySql.Available && !IsOfficialReport(coverage.Source)
                    ? 0d
                    : present ? score.AverageActions : null);
        }

        private static sbyte BandCode(EvidenceState evidence)
        {
            if (!evidence.Available) return UnknownBand;
            switch (LicenceActivityRules.Band(
                evidence.ActiveSamples, evidence.ObservedSamples, evidence.ExpectedSamples))
            {
                case "zero": return 0;
                case "low": return 1;
                case "moderate": return 2;
                case "high": return 3;
                default: return UnknownBand;
            }
        }

        private void AddBands(DistributionAccumulator[] distributions, int user)
        {
            for (var workload = 0; workload < distributions.Length; workload++)
                distributions[workload].Add(_bands[workload][user]);
        }

        private static DistributionAccumulator[] NewDistributions()
        {
            var result = new DistributionAccumulator[LicenceActivityQuery.Workloads.Length];
            for (var index = 0; index < result.Length; index++)
                result[index] = new DistributionAccumulator();
            return result;
        }

        private static List<LicenceActivityDistribution> ToDistributions(
            DistributionAccumulator[] source, int assignedUsers)
        {
            var result = new List<LicenceActivityDistribution>(LicenceActivityQuery.Workloads.Length);
            for (var workload = 0; workload < LicenceActivityQuery.Workloads.Length; workload++)
            {
                var counts = source[workload];
                result.Add(new LicenceActivityDistribution
                {
                    Workload = LicenceActivityQuery.Workloads[workload],
                    High = counts.High,
                    Moderate = counts.Moderate,
                    Low = counts.Low,
                    Zero = counts.Zero,
                    Unknown = assignedUsers - counts.Known
                });
            }
            return result;
        }

        private static bool InCohort(DirectoryEntry user, LicenceActivityQuery query) =>
            (!query.DepartmentId.HasValue || user.DepartmentId == query.DepartmentId.Value)
            && (!query.CountryId.HasValue || user.CountryId == query.CountryId.Value);

        private static bool MatchesSearch(DirectoryEntry user, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return user.UserPrincipalName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                || (!string.IsNullOrEmpty(user.Mail)
                    && user.Mail.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsOfficialReport(string source) =>
            source == LicenceActivitySql.M365ReportSource
            || source == LicenceActivitySql.CopilotReportSource;

        private static int WorkloadIndex(string workload)
        {
            for (var index = 0; index < LicenceActivityQuery.Workloads.Length; index++)
                if (LicenceActivityQuery.Workloads[index] == workload) return index;
            throw new ArgumentException("Unknown licence activity workload.", nameof(workload));
        }

        private static LicenceActivityCoverage MissingCoverage(string workload) =>
            new LicenceActivityCoverage
            {
                Workload = workload,
                Status = LicenceActivitySql.MissingCoverage,
                Source = string.Empty,
                Measure = string.Empty
            };

        private static LicenceActivityCoverage CloneCoverage(LicenceActivityCoverage source) =>
            new LicenceActivityCoverage
            {
                Workload = source.Workload,
                Status = source.Status,
                Source = source.Source,
                Measure = source.Measure,
                Granularity = source.Granularity,
                Message = source.Message,
                EffectiveFromUtc = source.EffectiveFromUtc,
                EffectiveToUtc = source.EffectiveToUtc,
                LatestImportUtc = source.LatestImportUtc,
                LagDays = source.LagDays,
                ReportPeriodDays = source.ReportPeriodDays,
                ExpectedSamples = source.ExpectedSamples,
                ObservedSamples = source.ObservedSamples,
                UnmatchedUsers = source.UnmatchedUsers,
                SnapshotDates = source.SnapshotDates == null
                    ? new List<DateTime>()
                    : new List<DateTime>(source.SnapshotDates)
            };

        private sealed class DirectoryEntry
        {
            internal DirectoryEntry(LicenceActivityDirectoryUser source)
            {
                UserId = source.UserId;
                UserPrincipalName = source.UserPrincipalName ?? string.Empty;
                Mail = source.Mail;
                DepartmentId = source.DepartmentId;
                CountryId = source.CountryId;
                Department = source.Department;
                Country = source.Country;
                AccountEnabled = source.AccountEnabled;
            }

            internal int UserId { get; }
            internal string UserPrincipalName { get; }
            internal string Mail { get; }
            internal int DepartmentId { get; }
            internal int CountryId { get; }
            internal string Department { get; }
            internal string Country { get; }
            internal bool? AccountEnabled { get; }
        }

        private sealed class SkuEntry
        {
            internal SkuEntry(LicenceActivitySku source)
            {
                LicenceTypeId = source.LicenceTypeId;
                Name = source.Name;
                SkuId = source.SkuId;
            }

            internal int LicenceTypeId { get; }
            internal string Name { get; }
            internal string SkuId { get; }
        }

        private sealed class DemographicAccumulator
        {
            internal DemographicAccumulator(int id, string name)
            {
                Id = id;
                Name = name;
                Distributions = NewDistributions();
            }

            internal int Id { get; }
            internal string Name { get; }
            internal int AssignedUsers { get; set; }
            internal DistributionAccumulator[] Distributions { get; }
        }

        private sealed class DistributionAccumulator
        {
            internal int High;
            internal int Moderate;
            internal int Low;
            internal int Zero;
            internal int Known => High + Moderate + Low + Zero;

            internal void Add(sbyte band)
            {
                switch (band)
                {
                    case 3: High++; break;
                    case 2: Moderate++; break;
                    case 1: Low++; break;
                    case 0: Zero++; break;
                }
            }
        }

        private readonly struct EvidenceState
        {
            internal EvidenceState(
                string status, int activeSamples, int observedSamples, int expectedSamples,
                double? averageActions, DateTime? lastActivityUtc, double? rankAverageActions)
            {
                Status = status;
                ActiveSamples = activeSamples;
                ObservedSamples = observedSamples;
                ExpectedSamples = expectedSamples;
                AverageActions = averageActions;
                LastActivityUtc = lastActivityUtc;
                RankAverageActions = rankAverageActions;
            }

            internal string Status { get; }
            internal bool Available => Status == LicenceActivitySql.Available;
            internal int ActiveSamples { get; }
            internal int ObservedSamples { get; }
            internal double? AverageActions { get; }
            internal DateTime? LastActivityUtc { get; }
            internal double? RankAverageActions { get; }
            internal int ExpectedSamples { get; }
        }

        private readonly struct RankValue
        {
            internal RankValue(EvidenceState evidence)
            {
                ActiveSamples = evidence.ActiveSamples;
                AverageActions = evidence.RankAverageActions;
                LastActivityUtc = evidence.LastActivityUtc;
                Available = evidence.Available;
                CanRankMost = evidence.Available || evidence.ActiveSamples > 0;
                Category = evidence.Available
                    ? evidence.ActiveSamples > 0 ? (byte)0 : (byte)2
                    : evidence.ActiveSamples > 0 ? (byte)1 : (byte)3;
            }

            internal int ActiveSamples { get; }
            internal double? AverageActions { get; }
            internal DateTime? LastActivityUtc { get; }
            internal bool Available { get; }
            internal bool CanRankMost { get; }
            internal byte Category { get; }
        }

        private enum RankingKind
        {
            Most,
            Least,
            Page
        }

        private sealed class RankingIndex
        {
            internal RankingIndex(int[][] byLicence)
            {
                ByLicence = byLicence;
            }

            internal int[][] ByLicence { get; }
        }

        private sealed class RankingComparer : IComparer<int>
        {
            private readonly LicenceActivityReadModel _owner;
            private readonly int _workload;
            private readonly RankingKind _kind;
            private readonly string _sort;
            private readonly int _direction;

            internal RankingComparer(
                LicenceActivityReadModel owner, int workload, RankingKind kind,
                string sort, string direction)
            {
                _owner = owner;
                _workload = workload;
                _kind = kind;
                _sort = sort;
                _direction = direction == "desc" ? -1 : 1;
            }

            public int Compare(int left, int right)
            {
                var leftEvidence = _owner._rankValues[_workload][left];
                var rightEvidence = _owner._rankValues[_workload][right];
                int compared;

                if (_kind == RankingKind.Most)
                {
                    compared = leftEvidence.Category.CompareTo(rightEvidence.Category);
                    if (compared != 0) return compared;
                    compared = rightEvidence.ActiveSamples.CompareTo(leftEvidence.ActiveSamples);
                    if (compared != 0) return compared;
                    compared = CompareNullable(rightEvidence.AverageActions, leftEvidence.AverageActions);
                    if (compared != 0) return compared;
                    compared = CompareNullable(rightEvidence.LastActivityUtc, leftEvidence.LastActivityUtc);
                    if (compared != 0) return compared;
                }
                else if (_kind == RankingKind.Least)
                {
                    compared = leftEvidence.ActiveSamples.CompareTo(rightEvidence.ActiveSamples);
                    if (compared != 0) return compared;
                    compared = CompareNullable(leftEvidence.AverageActions, rightEvidence.AverageActions);
                    if (compared != 0) return compared;
                    compared = CompareNullable(leftEvidence.LastActivityUtc, rightEvidence.LastActivityUtc);
                    if (compared != 0) return compared;
                }
                else if (_sort == "activity")
                {
                    compared = leftEvidence.Category.CompareTo(rightEvidence.Category);
                    if (compared != 0) return compared;
                    compared = _direction * leftEvidence.ActiveSamples.CompareTo(rightEvidence.ActiveSamples);
                    if (compared != 0) return compared;
                    compared = _direction * CompareNullable(
                        leftEvidence.AverageActions, rightEvidence.AverageActions);
                    if (compared != 0) return compared;
                    compared = _direction * CompareNullable(
                        leftEvidence.LastActivityUtc, rightEvidence.LastActivityUtc);
                    if (compared != 0) return compared;
                }
                else if (_sort == "lastActivity")
                {
                    compared = leftEvidence.Category.CompareTo(rightEvidence.Category);
                    if (compared != 0) return compared;
                    compared = _direction * CompareNullable(
                        leftEvidence.LastActivityUtc, rightEvidence.LastActivityUtc);
                    if (compared != 0) return compared;
                    compared = _direction * leftEvidence.ActiveSamples.CompareTo(rightEvidence.ActiveSamples);
                    if (compared != 0) return compared;
                    compared = _direction * CompareNullable(
                        leftEvidence.AverageActions, rightEvidence.AverageActions);
                    if (compared != 0) return compared;
                }
                else
                {
                    compared = _direction * StringComparer.OrdinalIgnoreCase.Compare(
                        _owner._users[left].UserPrincipalName, _owner._users[right].UserPrincipalName);
                    if (compared != 0) return compared;
                    return _owner._users[left].UserId.CompareTo(_owner._users[right].UserId);
                }

                compared = StringComparer.OrdinalIgnoreCase.Compare(
                    _owner._users[left].UserPrincipalName, _owner._users[right].UserPrincipalName);
                return compared != 0
                    ? compared
                    : _owner._users[left].UserId.CompareTo(_owner._users[right].UserId);
            }

            private static int CompareNullable<T>(T? left, T? right) where T : struct, IComparable<T>
            {
                if (!left.HasValue) return right.HasValue ? -1 : 0;
                return right.HasValue ? left.Value.CompareTo(right.Value) : 1;
            }
        }

        private sealed class NullFirstOrdinalIgnoreCaseComparer : IComparer<string>
        {
            internal static readonly NullFirstOrdinalIgnoreCaseComparer Instance =
                new NullFirstOrdinalIgnoreCaseComparer();

            public int Compare(string left, string right)
            {
                if (left == null) return right == null ? 0 : -1;
                return right == null ? 1 : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            }
        }

        private sealed class EmptyScores : IReadOnlyDictionary<int, LicenceActivityScore>
        {
            internal static readonly EmptyScores Instance = new EmptyScores();
            public int Count => 0;
            public IEnumerable<int> Keys => Enumerable.Empty<int>();
            public IEnumerable<LicenceActivityScore> Values => Enumerable.Empty<LicenceActivityScore>();
            public LicenceActivityScore this[int key] => throw new KeyNotFoundException();
            public bool ContainsKey(int key) => false;
            public IEnumerator<KeyValuePair<int, LicenceActivityScore>> GetEnumerator() =>
                Enumerable.Empty<KeyValuePair<int, LicenceActivityScore>>().GetEnumerator();
            public bool TryGetValue(int key, out LicenceActivityScore value)
            {
                value = default(LicenceActivityScore);
                return false;
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
