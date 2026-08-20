using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Powers the SPA's "Copilot Adoption" area - the licence-adoption tool.
    ///
    /// It answers two questions that decide real money:
    /// <list type="number">
    ///   <item><b>Who holds a Microsoft 365 Copilot licence and is not getting value from it?</b> Not as
    ///   a yes/no, but as a graded engagement score, because almost nobody is either a power user or a
    ///   complete non-user - and "50 people used it once" and "50 people use it daily" produce identical
    ///   "active user" counts while calling for opposite responses.</item>
    ///   <item><b>Who is a heavy Microsoft 365 user without a licence?</b> Ranked by a business case,
    ///   led by the strongest evidence there is: people already using Copilot Chat without a seat.</item>
    /// </list>
    ///
    /// Design notes:
    /// <list type="bullet">
    ///   <item>All the analysis lives in <see cref="CopilotAdoptionService"/> in Common.Entities, not
    ///   here. This controller caches, filters, pages and serialises. That keeps the whole thing
    ///   reusable by a scheduled report web-job later without lifting logic out of a controller.</item>
    ///   <item>The heavy work runs <b>once</b> per (window, licence override) and is cached, so paging,
    ///   sorting, filtering and CSV export are all served from the same in-memory result. That is a
    ///   performance decision, but mostly a correctness one: an exported spreadsheet is guaranteed to
    ///   match the summary it was exported from.</item>
    ///   <item>Concurrent first-hits share one execution (the cache holds the <see cref="Task{T}"/>),
    ///   so a page refresh during a slow analysis cannot start a second full scan of the audit history.</item>
    /// </list>
    /// </summary>
    [Authorize]
    [RoutePrefix("api/CopilotAdoption")]
    public class CopilotAdoptionAPIController : ApiController
    {
        private const string CacheKeyPrefix = "CopilotAdoption::Analysis::";

        /// <summary>
        /// How long a completed analysis is reused. Long enough that working through the list - sorting,
        /// filtering, exporting - never re-runs the queries, short enough that a refresh after an import
        /// shows new data. The imports themselves run far less often than this.
        /// </summary>
        private const int CacheMinutes = 10;

        /// <summary>Windows the UI offers. Anything else is snapped to the nearest, so a hand-edited URL cannot force a year-long scan.</summary>
        private static readonly int[] AllowedWindowDays = { 7, 28, 90, 180 };

        private const int DefaultTake = 50;
        private const int MaxTake = 500;

        /// <summary>
        /// Hard cap on a CSV export. Comfortably above any realistic Copilot seat count while stopping a
        /// single request from materialising an entire directory into one HTTP response.
        /// </summary>
        private const int MaxCsvRows = 100000;

        #region Availability

        /// <summary>
        /// Whether this deployment can show the tool at all, and what is missing if it cannot. Called
        /// before anything heavy so the SPA can hide the tab (or explain itself) rather than showing an
        /// empty dashboard that looks like zero adoption.
        /// </summary>
        // GET: api/CopilotAdoption/availability
        [HttpGet]
        [Route("availability")]
        public IHttpActionResult Availability()
        {
            var settings = new AppConfig().ImportJobSettings ?? new ImportTaskSettings();

            var model = new CopilotAdoptionAvailability
            {
                CopilotAuditImportEnabled = settings.Copilot,
                CopilotUsageReportImportEnabled = settings.GraphCopilotUsageReports,
                UserMetadataImportEnabled = settings.GraphUsersMetadata,
                M365UsageReportImportEnabled = settings.GraphUsageReports,
            };

            // Licence data is what makes this a *licence* adoption tool - without the user metadata
            // import there is no way to know who holds a Copilot seat, so nothing here works.
            model.Available = model.UserMetadataImportEnabled
                              && (model.CopilotAuditImportEnabled || model.CopilotUsageReportImportEnabled);

            if (!model.UserMetadataImportEnabled)
            {
                model.Messages.Add(
                    "The user metadata import is disabled, so licence assignments are unknown. Enable it in the "
                    + "installer to identify who holds a Microsoft 365 Copilot licence.");
            }

            if (!model.CopilotAuditImportEnabled && !model.CopilotUsageReportImportEnabled)
            {
                model.Messages.Add(
                    "Neither the Copilot audit import nor the Copilot usage-report import is enabled, so there is "
                    + "no Copilot usage to measure. Enable at least one in the installer.");
            }

            if (!model.CopilotAuditImportEnabled && model.CopilotUsageReportImportEnabled)
            {
                model.Messages.Add(
                    "The Copilot audit import is disabled. Engagement will be based on Microsoft's own usage "
                    + "report, which covers licensed users only - unlicensed Copilot Chat use, per-app breakdowns "
                    + "and Cowork adoption will not be visible.");
            }

            if (!model.M365UsageReportImportEnabled)
            {
                model.Messages.Add(
                    "The Microsoft 365 usage-report import is disabled, so licence candidates can only be ranked "
                    + "on existing unlicensed Copilot use. Enable it to also find heavy Microsoft 365 users who "
                    + "have never tried Copilot.");
            }

            return Ok(model);
        }

        #endregion

        #region Summary and licence types

        /// <summary>The executive view: headline figures, the adoption funnel and the breakdown charts.</summary>
        // GET: api/CopilotAdoption/summary?windowDays=28
        [HttpGet]
        [Route("summary")]
        public async Task<IHttpActionResult> Summary(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            return Ok(analysis.Summary);
        }

        /// <summary>
        /// Every licence type in the tenant and whether it was counted as a Copilot seat.
        ///
        /// Exposed deliberately: Microsoft ships new Copilot SKUs faster than any shipped classification
        /// list can track, so an admin has to be able to see what the tool decided - and override it -
        /// rather than discover from a wrong headline number that a SKU was missed.
        /// </summary>
        // GET: api/CopilotAdoption/licence-types
        [HttpGet]
        [Route("licence-types")]
        public async Task<IHttpActionResult> LicenceTypes(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            return Ok(analysis.Summary.SeatLicenceTypes);
        }

        /// <summary>The queries behind the numbers, for the SQL popover the rest of the admin site uses.</summary>
        // GET: api/CopilotAdoption/sql
        [HttpGet]
        [Route("sql")]
        public async Task<IHttpActionResult> Sql(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);
            return Ok(analysis.Sql);
        }

        /// <summary>
        /// The distinct departments and countries present in the analysis, for the filter drop-downs.
        /// Derived from the already-loaded result rather than from another query.
        /// </summary>
        // GET: api/CopilotAdoption/filters
        [HttpGet]
        [Route("filters")]
        public async Task<IHttpActionResult> Filters(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);

            return Ok(new
            {
                departments = Distinct(
                    analysis.LicensedUsers.Select(u => u.Department)
                        .Concat(analysis.Opportunities.Select(o => o.Department))),
                countries = Distinct(
                    analysis.LicensedUsers.Select(u => u.Country)
                        .Concat(analysis.Opportunities.Select(o => o.Country))),
                bands = CopilotAdoptionScoring.AllBands
                    .Select(b => new { value = (int)b, name = CopilotAdoptionScoring.BandDisplayName(b) })
                    .ToList(),
            });
        }

        #endregion

        #region Licensed users

        /// <summary>
        /// The licensed-user list: who holds a seat, how much they use it, and what to do about it.
        /// Defaults to the lowest scores first, because the people who need attention are the point.
        /// </summary>
        // GET: api/CopilotAdoption/licensed-users?windowDays=28&skip=0&take=50
        [HttpGet]
        [Route("licensed-users")]
        public async Task<IHttpActionResult> LicensedUsers(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string bands = null,
            string department = null,
            string country = null,
            bool coworkOnly = false,
            bool disabledOnly = false,
            double? minScore = null,
            double? maxScore = null,
            string sortBy = LicensedUserSortFields.Score,
            bool sortDesc = false,
            int skip = 0,
            int take = DefaultTake,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);

            var query = BuildLicensedUserQuery(
                search, bands, department, country, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc);

            var matched = CopilotAdoptionExports.Apply(analysis.LicensedUsers, query);

            return Ok(new LicensedUserPage
            {
                Total = matched.Count,
                Skip = Math.Max(0, skip),
                Take = Math.Min(Math.Max(1, take), MaxTake),
                Rows = CopilotAdoptionExports.Page(matched, skip, take, MaxTake),
                Warnings = analysis.Summary.Warnings,
            });
        }

        /// <summary>
        /// The same list as a CSV download. Takes the identical filter parameters, so what is exported
        /// is exactly what is on screen - just without the paging.
        /// </summary>
        // GET: api/CopilotAdoption/licensed-users/export
        [HttpGet]
        [Route("licensed-users/export")]
        public async Task<HttpResponseMessage> ExportLicensedUsers(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string bands = null,
            string department = null,
            string country = null,
            bool coworkOnly = false,
            bool disabledOnly = false,
            double? minScore = null,
            double? maxScore = null,
            string sortBy = LicensedUserSortFields.Score,
            bool sortDesc = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);

            var query = BuildLicensedUserQuery(
                search, bands, department, country, coworkOnly, disabledOnly, minScore, maxScore, sortBy, sortDesc);

            var rows = CopilotAdoptionExports.Apply(analysis.LicensedUsers, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(rows, CopilotAdoptionExports.LicensedUserColumns()),
                CsvSerialiser.FileName("copilot-licensed-users", analysis.Summary.GeneratedUtc));
        }

        #endregion

        #region Licence opportunities

        /// <summary>
        /// Unlicensed users ranked as candidates for a Copilot seat, strongest business case first.
        /// </summary>
        // GET: api/CopilotAdoption/opportunities?windowDays=28&skip=0&take=50
        [HttpGet]
        [Route("opportunities")]
        public async Task<IHttpActionResult> Opportunities(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string department = null,
            string country = null,
            bool recommendedOnly = false,
            bool existingCopilotUsersOnly = false,
            double? minScore = null,
            string sortBy = LicenceOpportunitySortFields.Score,
            bool sortDesc = true,
            int skip = 0,
            int take = DefaultTake,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);

            var query = BuildOpportunityQuery(
                search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

            var matched = CopilotAdoptionExports.Apply(analysis.Opportunities, query);

            return Ok(new LicenceOpportunityPage
            {
                Total = matched.Count,
                Skip = Math.Max(0, skip),
                Take = Math.Min(Math.Max(1, take), MaxTake),
                Rows = CopilotAdoptionExports.Page(matched, skip, take, MaxTake),
                Warnings = analysis.Summary.Warnings,
            });
        }

        // GET: api/CopilotAdoption/opportunities/export
        [HttpGet]
        [Route("opportunities/export")]
        public async Task<HttpResponseMessage> ExportOpportunities(
            int windowDays = 28,
            string seatLicenceTypeIds = null,
            string search = null,
            string department = null,
            string country = null,
            bool recommendedOnly = false,
            bool existingCopilotUsersOnly = false,
            double? minScore = null,
            string sortBy = LicenceOpportunitySortFields.Score,
            bool sortDesc = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var analysis = await GetAnalysisAsync(windowDays, seatLicenceTypeIds, cancellationToken);

            var query = BuildOpportunityQuery(
                search, department, country, recommendedOnly, existingCopilotUsersOnly, minScore, sortBy, sortDesc);

            var rows = CopilotAdoptionExports.Apply(analysis.Opportunities, query).Take(MaxCsvRows).ToList();

            return CsvResponse(
                CsvSerialiser.ToBytes(rows, CopilotAdoptionExports.LicenceOpportunityColumns()),
                CsvSerialiser.FileName("copilot-licence-opportunities", analysis.Summary.GeneratedUtc));
        }

        #endregion

        #region Analysis cache

        /// <summary>
        /// The cached analysis for this window and licence override, running it if necessary.
        ///
        /// The <see cref="Task{T}"/> itself is cached, not its result, so several requests arriving
        /// while the first analysis is still running all await the same execution instead of each
        /// starting their own scan of the Copilot audit history. A failed analysis is evicted so the
        /// next request retries rather than serving a cached error for ten minutes.
        /// </summary>
        private static Task<CopilotAdoptionAnalysis> GetAnalysisAsync(
            int windowDays,
            string seatLicenceTypeIds,
            CancellationToken cancellationToken)
        {
            var window = NormaliseWindowDays(windowDays);
            var overrideIds = ParseIds(seatLicenceTypeIds);
            var cacheKey = CacheKeyPrefix + window + "::"
                           + (overrideIds.Count == 0 ? "auto" : string.Join(",", overrideIds.OrderBy(i => i)));

            var existing = MemoryCache.Default.Get(cacheKey) as Task<CopilotAdoptionAnalysis>;
            if (existing != null && !existing.IsFaulted && !existing.IsCanceled)
            {
                return existing;
            }

            var options = CopilotAdoptionOptions.Default;
            options.WindowDays = window;

            var service = new CopilotAdoptionService(options);
            var task = service.AnalyseAsync(overrideIds.Count == 0 ? null : overrideIds, cancellationToken);

            // AddOrGetExisting is atomic, so if another request got there first we use its task and
            // abandon ours rather than running the analysis twice.
            var winner = MemoryCache.Default.AddOrGetExisting(
                cacheKey, task, DateTimeOffset.UtcNow.AddMinutes(CacheMinutes)) as Task<CopilotAdoptionAnalysis>;

            var effective = winner ?? task;

            effective.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted || completed.IsCanceled)
                    {
                        MemoryCache.Default.Remove(cacheKey);
                    }
                },
                TaskContinuationOptions.ExecuteSynchronously);

            return effective;
        }

        #endregion

        #region Parameter handling

        /// <summary>
        /// Snaps a requested window to one of the supported values. A free-form window would let a
        /// hand-edited URL ask for an arbitrarily long scan of the audit history.
        /// </summary>
        internal static int NormaliseWindowDays(int windowDays)
        {
            if (AllowedWindowDays.Contains(windowDays))
            {
                return windowDays;
            }

            return AllowedWindowDays
                .OrderBy(allowed => Math.Abs(allowed - windowDays))
                .ThenBy(allowed => allowed)
                .First();
        }

        /// <summary>
        /// Parses a comma-separated id list, ignoring anything that is not an integer. These ids are
        /// interpolated into SQL after being checked against the licence types that actually exist, so
        /// discarding non-numeric input here is the first of two gates rather than the only one.
        /// </summary>
        internal static List<int> ParseIds(string commaSeparated)
        {
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return new List<int>();
            }

            return commaSeparated
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Select(part => int.TryParse(part, out var id) ? (int?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .Distinct()
                .ToList();
        }

        /// <summary>Parses the band filter, accepting either the numeric value or the enum name.</summary>
        internal static List<AdoptionBand> ParseBands(string commaSeparated)
        {
            var result = new List<AdoptionBand>();
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return result;
            }

            foreach (var part in commaSeparated.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();

                if (int.TryParse(token, out var numeric) && Enum.IsDefined(typeof(AdoptionBand), numeric))
                {
                    result.Add((AdoptionBand)numeric);
                }
                else if (Enum.TryParse(token, ignoreCase: true, result: out AdoptionBand band)
                         && Enum.IsDefined(typeof(AdoptionBand), band))
                {
                    // Enum.TryParse happily accepts any numeric string - "99" would parse to
                    // (AdoptionBand)99 - so the IsDefined check is what stops an out-of-range value
                    // reaching the filter and silently matching nothing.
                    result.Add(band);
                }
            }

            return result.Distinct().ToList();
        }

        private static LicensedUserQuery BuildLicensedUserQuery(
            string search, string bands, string department, string country,
            bool coworkOnly, bool disabledOnly, double? minScore, double? maxScore,
            string sortBy, bool sortDesc)
        {
            return new LicensedUserQuery
            {
                Search = search,
                Bands = ParseBands(bands),
                Department = department,
                Country = country,
                CoworkOnly = coworkOnly,
                DisabledAccountsOnly = disabledOnly,
                MinScore = minScore,
                MaxScore = maxScore,
                SortBy = sortBy,
                SortDescending = sortDesc,
            };
        }

        private static LicenceOpportunityQuery BuildOpportunityQuery(
            string search, string department, string country,
            bool recommendedOnly, bool existingCopilotUsersOnly, double? minScore,
            string sortBy, bool sortDesc)
        {
            return new LicenceOpportunityQuery
            {
                Search = search,
                Department = department,
                Country = country,
                RecommendedOnly = recommendedOnly,
                ExistingCopilotUsersOnly = existingCopilotUsersOnly,
                MinScore = minScore,
                SortBy = sortBy,
                SortDescending = sortDesc,
            };
        }

        private static List<string> Distinct(IEnumerable<string> values)
        {
            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// A CSV file download. The bytes already carry a UTF-8 BOM (see <see cref="CsvSerialiser"/>)
        /// so Excel renders non-ASCII names correctly, and the charset is declared explicitly for
        /// everything that is not Excel.
        /// </summary>
        private static HttpResponseMessage CsvResponse(byte[] csv, string fileName)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(csv),
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName,
            };

            return response;
        }

        #endregion
    }
}
