using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.UserFilters;
using DataUtils.Sql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.CopilotInvestment;
using Web.AnalyticsWeb.Models.UserFilters;

namespace Web.AnalyticsWeb.Models.CopilotSpend
{
    /// <summary>One request's choices: period, scope and the breakdown dimension. No prices: this report has none.</summary>
    internal sealed class CopilotSpendRequest
    {
        public CopilotAdoptionDateRange Range { get; set; }
        public ReportScope Scope { get; set; }
        public string Dimension { get; set; } = CopilotInvestmentDimensions.Department;
    }

    /// <summary>
    /// Builds the Copilot Spend report. It shares the investment report's tenant-wide fact cache, so one read of the latest
    /// balances and of the Studio ledger serves both pages, and reads only the scoped Studio breakdown itself. It needs no
    /// adoption analysis and no assumptions, so it works on a deployment that has neither.
    /// </summary>
    internal sealed class CopilotSpendReportService
    {
        private static readonly TimeSpan BreakdownTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FailedTtl = TimeSpan.FromSeconds(30);
        private readonly CopilotInvestmentFactsCache _facts;
        private readonly ICopilotSpendFactsSource _spend;
        private readonly IUserDirectorySource _directory;
        private readonly Func<ImportTaskSettings> _settings;
        private readonly Func<bool> _creditCollectionEnabled;
        private readonly MemoryCache _results;
        private readonly string _prefix = "CopilotSpend::" + Guid.NewGuid().ToString("N") + "::";

        public CopilotSpendReportService(
            CopilotInvestmentFactsCache facts,
            ICopilotSpendFactsSource spend,
            IUserDirectorySource directory,
            Func<ImportTaskSettings> settings,
            Func<bool> creditCollectionEnabled,
            MemoryCache results = null)
        {
            _facts = facts ?? throw new ArgumentNullException(nameof(facts));
            _spend = spend ?? throw new ArgumentNullException(nameof(spend));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _settings = settings ?? (() => new ImportTaskSettings());
            _creditCollectionEnabled = creditCollectionEnabled ?? (() => false);
            _results = results ?? MemoryCache.Default;
        }

        private static readonly Lazy<CopilotSpendReportService> Production = new Lazy<CopilotSpendReportService>(() =>
        {
            var investment = CopilotInvestmentReportService.ForThisDeployment();
            var connection = new AppConfig().ConnectionStrings?.DatabaseConnectionString;
            return new CopilotSpendReportService(
                investment.Facts,
                new SqlCopilotSpendFactsSource(() => AzureSqlTokenAuth.CreateConnection(connection)),
                CachedUserDirectorySource.Default,
                () => investment.Settings,
                () => investment.CreditCollectionEnabled);
        });

        public static CopilotSpendReportService ForThisDeployment() => Production.Value;

        public CopilotInvestmentFactsCache Facts => _facts;

        public bool StudioImportEnabled => _settings().CopilotStudioCredits;

        public bool CreditCollectionEnabled => _creditCollectionEnabled();

        public async Task<CopilotSpendSummary> BuildAsync(CopilotSpendRequest request, CancellationToken cancellationToken)
        {
            var range = request.Range;
            var scope = request.Scope ?? ReportScope.Unrestricted;
            // Both sources are kept by day: the window's own days, never the day after an explicit end date.
            var creditsTask = _facts.CreditsAsync(range.FromUtc.Date, CopilotInvestmentWindow.LastDay(range));
            var studioTask = _facts.StudioAsync(range.FromUtc.Date, CopilotInvestmentWindow.DayAfter(range));
            var breakdownTask = BreakdownAsync(range.FromUtc.Date, CopilotInvestmentWindow.DayAfter(range), scope.Sql);
            var directory = await ReadDirectoryAsync(cancellationToken).ConfigureAwait(false);
            var credits = await creditsTask.ConfigureAwait(false);
            var studio = await studioTask.ConfigureAwait(false);
            var breakdown = await breakdownTask.ConfigureAwait(false);

            var cacheKey = string.Join("|", _prefix + "Summary", RuntimeHelpers.GetHashCode(credits), RuntimeHelpers.GetHashCode(studio),
                RuntimeHelpers.GetHashCode(breakdown), directory == null ? "-" : directory.LoadedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                scope.Key, request.Dimension, range.WindowDays.ToString(CultureInfo.InvariantCulture));
            if (_results.Get(cacheKey) is CopilotSpendSummary cached) return ForThisReader(cached, scope);

            Func<int, bool> inScope = scope.IsRestricted ? (Func<int, bool>)scope.Includes : _ => true;
            var grouping = Grouping(directory, request.Dimension);
            var summary = CopilotSpendCalculator.Calculate(new CopilotSpendInput
            {
                GeneratedUtc = DateTime.UtcNow,
                FromUtc = range.FromUtc,
                ToUtc = CopilotInvestmentWindow.End(range),
                LastDay = CopilotInvestmentWindow.LastDay(range),
                WindowDays = range.WindowDays,
                Filtered = scope.IsRestricted,
                InScope = inScope,
                Directory = id => CopilotInvestmentReportService.DirectoryEntry(directory, id),
                Dimension = request.Dimension,
                DimensionName = directory?.DimensionName(request.Dimension),
                GroupOf = grouping.GroupOf,
                GroupingUnavailable = grouping.UnavailableCode,
                Credits = credits,
                CreditCollectionEnabled = CreditCollectionEnabled,
                Studio = studio,
                StudioImportEnabled = StudioImportEnabled,
                StudioBreakdown = breakdown,
            });
            // Cached without the filter echoes: readers with and without See PII share the scope key, but the administrator's
            // filter echo withholds people's names from the second - so the echoes belong to each request (ForThisReader).
            _results.Set(cacheKey, summary, DateTimeOffset.UtcNow.AddMinutes(2));
            return ForThisReader(summary, scope);
        }

        /// <summary>
        /// One person's drill-down, or null when they are not in this reader's report: the drill-down is never a way round the
        /// filter. Their balances, environments and turns fail the request when they cannot be read. Their Studio days,
        /// prompts, goals, phrases and resources are read alongside and are best-effort, so a failure there leaves the rest of
        /// the drill-down showing, with one reason in place of what is missing.
        /// </summary>
        public async Task<CopilotSpendPersonDetail> PersonAsync(CopilotSpendRequest request, int userId, CancellationToken cancellationToken)
        {
            var report = await BuildAsync(request, cancellationToken).ConfigureAwait(false);
            var person = report.People.FirstOrDefault(p => p.UserId == userId);
            if (person == null) return null;

            var range = request.Range;
            var lastDay = CopilotInvestmentWindow.LastDay(range);
            // Goals are read with the person's usage, by taxonomy version, so the investment goal counts are not asked for.
            var factsTask = _facts.Source.GetPersonDetailAsync(userId, person.CreditObjectId, person.BalanceDate,
                range.FromUtc, range.ToExclusiveUtc, lastDay, withCreditEstimate: true, withPromptCategories: false, cancellationToken);
            var usageTask = ReadUsageAsync(userId, range.FromUtc, range.ToExclusiveUtc, lastDay, cancellationToken);
            await Task.WhenAll(factsTask, usageTask).ConfigureAwait(false);
            var facts = factsTask.Result;
            var usage = usageTask.Result;

            var agentIds = facts.Activity.Where(a => a.AgentId.HasValue).Select(a => a.AgentId.Value).Distinct().ToList();
            var agents = await ReadAgentsAsync(agentIds, cancellationToken).ConfigureAwait(false);
            return CopilotSpendPersonDetailBuilder.Build(new CopilotSpendPersonDetailInput
            {
                Person = person,
                People = report.People,
                StudioAvailable = report.Studio.Available,
                BalancesAvailable = report.Balances.Available,
                Facts = facts,
                Usage = usage.Facts,
                Agents = agents,
                FromUtc = range.FromUtc,
                ToExclusiveUtc = range.ToExclusiveUtc,
                LastDay = lastDay,
                StudioEarliest = report.Studio.EarliestDate,
                StudioLatest = report.Studio.LatestDate,
                UsageFailureCode = usage.FailureCode,
            });
        }

        private async Task<(CopilotSpendPersonFacts Facts, string FailureCode)> ReadUsageAsync(int userId, DateTime from, DateTime toExclusive,
            DateTime lastDay, CancellationToken cancellationToken)
        {
            try
            {
                return (await _spend.GetPersonUsageAsync(userId, from, toExclusive, lastDay, cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "CopilotSpend.PersonUsage");
                return (null, CopilotSpendErrorCodes.PersonUsageUnavailable);
            }
        }

        /// <summary>The names and kinds of the agents the person used. Without them the agents still show, unnamed.</summary>
        private async Task<List<CopilotSpendAgentFact>> ReadAgentsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
        {
            if (ids.Count == 0) return new List<CopilotSpendAgentFact>();
            try
            {
                return await _spend.GetAgentsAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "CopilotSpend.PersonAgents");
                return new List<CopilotSpendAgentFact>();
            }
        }

        /// <summary>The cached summary with this request's filter echoes, on a copy; the cached object never holds them.</summary>
        internal static CopilotSpendSummary ForThisReader(CopilotSpendSummary summary, ReportScope scope) =>
            summary.WithFilterEchoes(scope.UserFilter?.ToEcho(scope.Restriction), scope.GlobalEcho);

        /// <summary>
        /// How to group people by <paramref name="dimension"/>, or why the report cannot: with no directory there is nothing
        /// to group by, and a dimension the directory does not hold (a custom organisation type since removed) is not "not set"
        /// for everyone. Either way a reason is reported rather than invented values.
        /// </summary>
        internal static (Func<int, string> GroupOf, string UnavailableCode) Grouping(UserDirectorySnapshot directory, string dimension)
        {
            if (directory == null) return (null, CopilotSpendGroupingUnavailable.Directory);
            if (directory.Column(dimension) == null) return (null, CopilotSpendGroupingUnavailable.Dimension);
            return (id => directory.TryGetRow(id, out var row) ? directory.ValueOf(dimension, row) : null, null);
        }

        /// <summary>
        /// The scoped Studio breakdown, shared per window and scope for a few minutes; the running task is shared, so
        /// concurrent first requests run one query. A failure is kept briefly and reported as a code.
        /// </summary>
        private Task<CopilotSpendStudioBreakdown> BreakdownAsync(DateTime from, DateTime toExclusive, ReportUserScope scope)
        {
            var key = _prefix + "Breakdown::" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "::"
                + toExclusive.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "::" + (scope ?? ReportUserScope.Everyone).Key;
            var lazy = new Lazy<Task<CopilotSpendStudioBreakdown>>(() => LoadBreakdownAsync(key, from, toExclusive, scope), LazyThreadSafetyMode.ExecutionAndPublication);
            var existing = _results.AddOrGetExisting(key, lazy, DateTimeOffset.UtcNow.Add(BreakdownTtl)) as Lazy<Task<CopilotSpendStudioBreakdown>> ?? lazy;
            return existing.Value;
        }

        private async Task<CopilotSpendStudioBreakdown> LoadBreakdownAsync(string key, DateTime from, DateTime toExclusive, ReportUserScope scope)
        {
            try
            {
                return await _spend.GetStudioBreakdownAsync(from, toExclusive, scope, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotSpend.StudioBreakdown");
                var failed = new CopilotSpendStudioBreakdown { FailureCode = CopilotSpendErrorCodes.StudioBreakdownUnavailable };
                _results.Set(key, new Lazy<Task<CopilotSpendStudioBreakdown>>(() => Task.FromResult(failed)), DateTimeOffset.UtcNow.Add(FailedTtl));
                return failed;
            }
        }

        private async Task<UserDirectorySnapshot> ReadDirectoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _directory.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Rows then carry user ids only and no grouping is offered; nothing is invented.
                WebExceptionTelemetry.Report(ex, "CopilotSpend.Directory");
                return null;
            }
        }
    }
}
