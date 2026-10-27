using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.State;
using Common.Entities.UserFilters;
using DataUtils;
using DataUtils.Sql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.UserFilters;

namespace Web.AnalyticsWeb.Models.CopilotInvestment
{
    /// <summary>The deployment's assumptions store: the AnalyticsState table when Storage is configured, otherwise read-only and empty.</summary>
    internal static class CopilotInvestmentAssumptionsProvider
    {
        private static readonly Lazy<CopilotInvestmentAssumptionsStore> ProductionStore = new Lazy<CopilotInvestmentAssumptionsStore>(() =>
        {
            var config = new AppConfig();
            var logger = new AnalyticsLogger(config.AppInsightsConnectionString, "CopilotInvestmentAssumptions");
            var durable = StateStore.TryOpen(config, StatePartitions.CopilotInvestmentAssumptions, logger);
            return durable != null
                ? new CopilotInvestmentAssumptionsStore(durable, isDurable: true)
                : new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: false);
        });

        public static CopilotInvestmentAssumptionsStore Production() => ProductionStore.Value;
    }

    /// <summary>
    /// Shares one read of the report's own facts per window between every reader, for a few minutes. Holds the
    /// running task, so concurrent first requests share one query; a failed read is kept only briefly, so the
    /// next request after it tries again.
    /// </summary>
    internal sealed class CopilotInvestmentFactsCache
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FailedTtl = TimeSpan.FromSeconds(30);
        private readonly ICopilotInvestmentFactsSource _source;
        private readonly MemoryCache _cache;
        private readonly TimeSpan _ttl;
        private readonly string _prefix = "CopilotInvestment::" + Guid.NewGuid().ToString("N") + "::";

        public CopilotInvestmentFactsCache(ICopilotInvestmentFactsSource source, MemoryCache cache = null, TimeSpan? ttl = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _cache = cache ?? MemoryCache.Default;
            _ttl = ttl ?? DefaultTtl;
        }

        public ICopilotInvestmentFactsSource Source => _source;

        public Task<CreditFacts> CreditsAsync(DateTime from, DateTime to) =>
            Shared(_prefix + "Credits::" + Day(from) + "::" + Day(to),
                () => _source.GetCreditFactsAsync(from, to, CancellationToken.None),
                () => new CreditFacts { FailureCode = "creditsUnavailable" });

        public Task<StudioFacts> StudioAsync(DateTime from, DateTime toExclusive) =>
            Shared(_prefix + "Studio::" + Day(from) + "::" + Day(toExclusive),
                () => _source.GetStudioFactsAsync(from, toExclusive, CancellationToken.None),
                () => new StudioFacts { FailureCode = "studioUnavailable" });

        private static string Day(DateTime date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        private Task<T> Shared<T>(string key, Func<Task<T>> load, Func<T> failed)
        {
            var lazy = new Lazy<Task<T>>(() => LoadAsync(key, load, failed), LazyThreadSafetyMode.ExecutionAndPublication);
            var existing = _cache.AddOrGetExisting(key, lazy, DateTimeOffset.UtcNow.Add(_ttl)) as Lazy<Task<T>> ?? lazy;
            return existing.Value;
        }

        /// <summary>
        /// A failure becomes an empty fact set carrying a code - the report still shows the other sources and says
        /// this one could not be read - and replaces the cached entry with one that expires in seconds.
        /// </summary>
        private async Task<T> LoadAsync<T>(string key, Func<Task<T>> load, Func<T> failed)
        {
            try
            {
                return await load().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotInvestment.Facts");
                var result = failed();
                _cache.Set(key, new Lazy<Task<T>>(() => Task.FromResult(result)), DateTimeOffset.UtcNow.Add(FailedTtl));
                return result;
            }
        }
    }

    /// <summary>One request's choices: period, scope, what-if figures and the breakdown dimension.</summary>
    internal sealed class CopilotInvestmentRequest
    {
        public CopilotAdoptionDateRange Range { get; set; }
        public ReportScope Scope { get; set; }
        public CopilotInvestmentScenario Scenario { get; set; } = new CopilotInvestmentScenario();
        public string Dimension { get; set; } = CopilotInvestmentDimensions.Department;
    }

    /// <summary>Builds the report from the shared adoption analysis, the shared facts and the assumptions in force.</summary>
    internal sealed class CopilotInvestmentReportService
    {
        private readonly CopilotInvestmentFactsCache _facts;
        private readonly Func<CopilotInvestmentAssumptionsStore> _assumptions;
        private readonly IUserDirectorySource _directory;
        private readonly Func<ImportTaskSettings> _settings;
        private readonly Func<bool> _creditCollectionEnabled;
        private readonly MemoryCache _results;

        public CopilotInvestmentReportService(
            CopilotInvestmentFactsCache facts,
            Func<CopilotInvestmentAssumptionsStore> assumptions,
            IUserDirectorySource directory,
            Func<ImportTaskSettings> settings,
            Func<bool> creditCollectionEnabled,
            MemoryCache results = null)
        {
            _facts = facts ?? throw new ArgumentNullException(nameof(facts));
            _assumptions = assumptions ?? throw new ArgumentNullException(nameof(assumptions));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _settings = settings ?? (() => new ImportTaskSettings());
            _creditCollectionEnabled = creditCollectionEnabled ?? (() => false);
            _results = results ?? MemoryCache.Default;
        }

        private static readonly Lazy<CopilotInvestmentReportService> Production = new Lazy<CopilotInvestmentReportService>(() =>
        {
            var connection = new AppConfig().ConnectionStrings?.DatabaseConnectionString;
            return new CopilotInvestmentReportService(
                new CopilotInvestmentFactsCache(new SqlCopilotInvestmentFactsSource(() => AzureSqlTokenAuth.CreateConnection(connection))),
                CopilotInvestmentAssumptionsProvider.Production,
                CachedUserDirectorySource.Default,
                () => new AppConfig().ImportJobSettings ?? new ImportTaskSettings(),
                () =>
                {
                    var config = new AppConfig();
                    return (config.ImportJobSettings?.CopilotCostManagement ?? false) && config.CopilotCostConnectionEnabled;
                });
        });

        public static CopilotInvestmentReportService ForThisDeployment() => Production.Value;

        public CopilotInvestmentFactsCache Facts => _facts;

        /// <summary>Whether the adoption evidence can exist at all on this deployment.</summary>
        public bool AdoptionAvailable
        {
            get
            {
                var settings = _settings();
                return settings.GraphUsersMetadata && (settings.Copilot || settings.GraphCopilotUsageReports);
            }
        }

        public bool StudioImportEnabled => _settings().CopilotStudioCredits;

        public bool CreditCollectionEnabled => _creditCollectionEnabled();

        public ImportTaskSettings Settings => _settings();

        /// <summary>
        /// The report for one request. <paramref name="analysis"/> is the shared adoption analysis for the period,
        /// or null when this deployment has no adoption evidence; it is narrowed here and never edited.
        /// </summary>
        /// <exception cref="CopilotInvestmentAssumptionsRejectedException">The reader's what-if figures break a rule.</exception>
        public async Task<CopilotInvestmentSummary> BuildAsync(CopilotInvestmentRequest request, CopilotAdoptionAnalysis analysis,
            CancellationToken cancellationToken)
        {
            var range = request.Range;
            // Both sources are kept by day: read the window's own days, never the day after an explicit end date.
            var creditsTask = _facts.CreditsAsync(range.FromUtc.Date, CopilotInvestmentWindow.LastDay(range));
            var studioTask = _facts.StudioAsync(range.FromUtc.Date, CopilotInvestmentWindow.DayAfter(range));
            var directoryTask = ReadDirectoryAsync(cancellationToken);
            var assumptions = await ReadAssumptionsAsync(cancellationToken).ConfigureAwait(false);

            var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
            CopilotInvestmentAssumptions effective = null;
            if (assumptions.Document != null)
                effective = request.Scenario.ApplyTo(assumptions.Document.Assumptions, provenance);

            var credits = await creditsTask.ConfigureAwait(false);
            var studio = await studioTask.ConfigureAwait(false);
            var directory = await directoryTask.ConfigureAwait(false);

            var scope = request.Scope ?? ReportScope.Unrestricted;
            var cacheKey = string.Join("|", "CopilotInvestment::Summary", AnalysisIdentity(analysis), RuntimeHelpers.GetHashCode(credits),
                RuntimeHelpers.GetHashCode(studio), directory == null ? "-" : directory.LoadedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                scope.Key, assumptions.Document?.Version.ToString(CultureInfo.InvariantCulture) ?? "unavailable",
                request.Scenario.Key, request.Dimension);
            if (_results.Get(cacheKey) is CopilotInvestmentSummary cached) return ForThisReader(cached, scope);

            var adoptionScope = scope.IsRestricted
                ? CopilotAdoptionScope.Create(null, scope.UserFilter, scope.Restriction, scope.GlobalEcho, scope.Global?.DescribeInEnglish())
                : CopilotAdoptionScope.WholeTenant;
            var scoped = analysis == null ? null : CopilotAdoptionScopeFilter.FilterRows(analysis, adoptionScope);
            Func<int, bool> inScope = scope.IsRestricted ? (Func<int, bool>)scope.Includes : _ => true;

            var notAnalysed = analysis?.LicensedUsersNotAnalysed?.Where(inScope).ToList();
            var people = CopilotInvestmentPeopleBuilder.Build(scoped, credits.Users, studio.Users, inScope,
                id => DirectoryEntry(directory, id), out var recommended, out var scopedCredits, notAnalysed);

            var input = new CopilotInvestmentInput
            {
                GeneratedUtc = DateTime.UtcNow,
                FromUtc = range.FromUtc,
                ToUtc = CopilotInvestmentWindow.End(range),
                WindowDays = range.WindowDays,
                WorkingDaysPerWeek = analysis?.Summary?.Options?.WorkingDaysPerWeek ?? 5,
                MinGroupSize = analysis?.Summary?.Options?.MinSeatsPerSegment ?? 5,
                Filtered = scope.IsRestricted,
                People = people,
                HoldersNotAnalysed = analysis?.LicensedUsersNotAnalysed?.Count(inScope) ?? 0,
                RecommendedCandidates = recommended,
                AdoptionAvailable = analysis != null,
                AuditAvailable = analysis?.Summary?.DataSources?.AuditAvailable ?? false,
                UsageReportAvailable = analysis?.Summary?.DataSources?.CopilotUsageReportAvailable ?? false,
                UsageReportDate = analysis?.Summary?.DataSources?.CopilotUsageReportDate,
                FiguresIncomplete = analysis?.Summary?.FiguresIncomplete ?? false,
                LicensedUsersCapped = analysis?.LicensedUsersCapped ?? false,
                AnalysisGeneratedUtc = analysis?.Summary?.GeneratedUtc,
                Assumptions = effective,
                AssumptionsStatusOverride = assumptions.Document == null ? CopilotInvestmentAssumptionStatus.Unavailable : null,
                Provenance = provenance,
                AssumptionsVersion = assumptions.Document?.Version ?? 0,
                AssumptionsUpdatedUtc = assumptions.Document?.UpdatedUtc,
                AssumptionsDurable = assumptions.Durable,
                Credits = credits,
                ScopedCredits = scopedCredits,
                CreditCollectionEnabled = CreditCollectionEnabled,
                Studio = studio,
                StudioImportEnabled = StudioImportEnabled,
                Dimension = request.Dimension,
                DimensionName = directory?.DimensionName(request.Dimension),
                GroupOf = GroupFunction(directory, request.Dimension, people),
                // The weekly trend is tenant-wide, so it is only shown when the view is too.
                WeeklyActiveHolders = scope.IsRestricted ? null : ActiveSeatHolderWeeks(analysis),
                WeeklyActiveHoldersUnavailable = scope.IsRestricted ? CopilotInvestmentTrajectoryUnavailable.Filtered
                    : analysis == null || !(analysis.Summary?.DataSources?.AuditAvailable ?? false) ? CopilotInvestmentTrajectoryUnavailable.NoAudit : null,
            };

            var summary = CopilotInvestmentCalculator.Calculate(input);
            // Cached without the filter echoes: the scope key is shared by readers whose echoes differ (see ForThisReader).
            _results.Set(cacheKey, summary, DateTimeOffset.UtcNow.AddMinutes(2));
            return ForThisReader(summary, scope);
        }

        /// <summary>
        /// The cached summary with this request's filter echoes, on a copy. The cache key is the people a scope covers, which
        /// readers with and without See PII share, but the administrator's filter echo and description withhold people's
        /// names from a reader without See PII - so they belong to the request, never to the cached object.
        /// </summary>
        internal static CopilotInvestmentSummary ForThisReader(CopilotInvestmentSummary summary, ReportScope scope) =>
            summary.WithFilterEchoes(scope.UserFilter?.ToEcho(scope.Restriction), scope.UserFilter?.DescribeInEnglish(), scope.GlobalEcho,
                scope.Global?.DescribeInEnglish());

        private static string AnalysisIdentity(CopilotAdoptionAnalysis analysis) =>
            analysis == null ? "none" : RuntimeHelpers.GetHashCode(analysis).ToString(CultureInfo.InvariantCulture) + ":"
                + (analysis.Summary?.GeneratedUtc.Ticks ?? 0).ToString(CultureInfo.InvariantCulture);

        /// <summary>The adoption analysis' weekly series of distinct active seat holders (today's seats, audit-measured).</summary>
        internal const string ActiveSeatHoldersSeries = "Active licensed users";

        private static List<AdoptionTimePoint> ActiveSeatHolderWeeks(CopilotAdoptionAnalysis analysis) =>
            analysis?.Summary?.WeeklyTrend?.FirstOrDefault(s => string.Equals(s.Name, ActiveSeatHoldersSeries, StringComparison.Ordinal))?.Points?.ToList();

        private async Task<UserDirectorySnapshot> ReadDirectoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _directory.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Grouping falls back to the departments the adoption rows carry; nothing is invented.
                WebExceptionTelemetry.Report(ex, "CopilotInvestment.Directory");
                return null;
            }
        }

        private sealed class AssumptionsRead
        {
            public CopilotInvestmentAssumptionsDocument Document;
            public bool Durable;
        }

        private async Task<AssumptionsRead> ReadAssumptionsAsync(CancellationToken cancellationToken)
        {
            var store = _assumptions();
            try
            {
                return new AssumptionsRead { Document = await store.GetAsync(cancellationToken).ConfigureAwait(false), Durable = store.IsDurable };
            }
            catch (CopilotInvestmentAssumptionsUnavailableException ex)
            {
                // Money is withheld rather than modelled with values nobody chose; the evidence still shows.
                WebExceptionTelemetry.Report(ex, "CopilotInvestment.Assumptions");
                return new AssumptionsRead { Document = null, Durable = store.IsDurable };
            }
        }

        internal static CopilotInvestmentDirectoryEntry DirectoryEntry(UserDirectorySnapshot directory, int userId)
        {
            if (directory == null || !directory.TryGetRow(userId, out var row)) return null;
            var status = directory.ValueOf(UserFilterDimensions.AccountStatus, row);
            return new CopilotInvestmentDirectoryEntry
            {
                UserPrincipalName = directory.ValueOf(UserFilterDimensions.UserName, row),
                Department = directory.ValueOf(UserFilterDimensions.Department, row),
                JobTitle = directory.ValueOf(UserFilterDimensions.JobTitle, row),
                Country = directory.ValueOf(UserFilterDimensions.Country, row),
                Company = directory.ValueOf(UserFilterDimensions.CompanyName, row),
                Manager = directory.ValueOf(UserFilterDimensions.Manager, row),
                AccountEnabled = status == null ? (bool?)null : !string.Equals(status, UserFilterTokens.Disabled, StringComparison.Ordinal),
            };
        }

        internal static Func<int, string> GroupFunction(UserDirectorySnapshot directory, string dimension, List<CopilotInvestmentPerson> people)
        {
            if (directory != null && directory.Column(dimension) != null)
                return id => directory.TryGetRow(id, out var row) ? directory.ValueOf(dimension, row) : null;
            if (dimension == UserFilterDimensions.Department)
            {
                var departments = people.ToDictionary(p => p.UserId, p => p.Department);
                return id => departments.TryGetValue(id, out var department) ? department : null;
            }
            return null;
        }
    }
}
