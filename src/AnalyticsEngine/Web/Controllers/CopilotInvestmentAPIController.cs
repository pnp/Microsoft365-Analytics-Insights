using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.Localisation;
using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Caching;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.CopilotAdoption;
using Web.AnalyticsWeb.Models.CopilotInvestment;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The Copilot investment report: modelled seat cost and value, break-even and sensitivity over the Copilot
    /// Adoption evidence, with the preview credit balances and the Copilot Studio ledger beside it.
    /// </summary>
    /// <remarks>
    /// <para><b>One evidence base.</b> Activity comes from the same cached, shared adoption analysis the Copilot
    /// Adoption page uses, so the two pages agree about who used Copilot, and this page never scans the audit
    /// history a second time.</para>
    /// <para><b>Who sees what.</b> Aggregates are for every signed-in reader, narrowed by the administrator's
    /// global filter. A reader's own filter, a breakdown that names managers, the people list, a person's
    /// drill-down and the people sheet of the workbook need See PII; a summary for a reader without it is a
    /// trimmed copy.</para>
    /// </remarks>
    [Authorize]
    [RoutePrefix("api/CopilotInvestment")]
    public class CopilotInvestmentAPIController : ApiController
    {
        internal static readonly TimeSpan FirstResponseBudget = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan ExportWaitBudget = TimeSpan.FromSeconds(150);
        private const int RetryAfterSeconds = 5;
        private const int DefaultTake = 50;
        private const int MaxTake = 500;

        public CopilotInvestmentAPIController()
            : this(CopilotAdoptionAnalysisCoordinator.Default, ReportScopeResolver.Default, CopilotInvestmentReportService.ForThisDeployment)
        {
        }

        internal CopilotInvestmentAPIController(
            CopilotAdoptionAnalysisCoordinator coordinator, ReportScopeResolver scopes, Func<CopilotInvestmentReportService> service)
        {
            Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            Scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        internal CopilotAdoptionAnalysisCoordinator Coordinator { get; }
        internal ReportScopeResolver Scopes { get; }
        private readonly Func<CopilotInvestmentReportService> _service;
        private CopilotInvestmentReportService Service => _service();

        /// <summary>What this deployment can show, as stable codes for the page to explain.</summary>
        [HttpGet, Route("availability")]
        public IHttpActionResult Availability()
        {
            var service = Service;
            var settings = service.Settings;
            var notes = new List<string>();
            if (!settings.GraphUsersMetadata) notes.Add("userMetadataDisabled");
            if (!settings.Copilot && !settings.GraphCopilotUsageReports) notes.Add("copilotSourcesDisabled");
            else if (!settings.Copilot) notes.Add("auditDisabled");
            if (!service.StudioImportEnabled) notes.Add("studioDisabled");
            if (!service.CreditCollectionEnabled) notes.Add("creditsDisabled");
            return Ok(new
            {
                adoptionAvailable = service.AdoptionAvailable,
                studioImportEnabled = service.StudioImportEnabled,
                creditCollectionEnabled = service.CreditCollectionEnabled,
                notes,
            });
        }

        [HttpGet, Route("summary")]
        public Task<IHttpActionResult> Summary(int windowDays = 28, string userFilter = null, string dimension = null,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, dimension, cancellationToken);
                if (request.Refusal != null) return ResponseMessage(request.Refusal);
                var report = await ReportAsync(request.Request, FirstResponseBudget, withPeople: false, cancellationToken);
                if (report == null) return ResponseMessage(StillBuilding());
                return Ok(CanSeeIndividuals() ? report : report.WithoutIndividualData());
            });

        /// <summary>Balances on a bounded set of sample dates: a balance curve, never a running total.</summary>
        [HttpGet, Route("trend")]
        public Task<IHttpActionResult> Trend(int windowDays = 28, string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, null, cancellationToken);
                if (request.Refusal != null) return ResponseMessage(request.Refusal);
                var range = request.Request.Range;
                var credits = await Service.Facts.CreditsAsync(range.FromUtc.Date, CopilotInvestmentWindow.LastDay(range));
                if (!credits.SnapshotDate.HasValue)
                    return Ok(new { points = new List<CreditTrendPoint>(), snapshotDate = (DateTime?)null, failureCode = credits.FailureCode });

                var dates = CopilotInvestmentSql.TrendDates(range.FromUtc.Date, credits.SnapshotDate.Value);
                var scope = request.Request.Scope.Sql;
                var key = "CopilotInvestment::Trend::" + string.Join(",", dates.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
                    + "::" + scope.Key;
                if (!(MemoryCache.Default.Get(key) is List<CreditTrendPoint> points))
                {
                    points = await Service.Facts.Source.GetCreditTrendAsync(dates, scope, cancellationToken);
                    MemoryCache.Default.Set(key, points, DateTimeOffset.UtcNow.Add(CopilotInvestmentFactsCache.DefaultTtl));
                }
                return Ok(new { points, snapshotDate = credits.SnapshotDate, failureCode = (string)null });
            });

        [HttpGet, Route("people")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public Task<IHttpActionResult> People(int windowDays = 28, string userFilter = null, string dimension = null,
            string signals = null, string flag = null, string seat = null, string search = null,
            string sortBy = null, bool sortDesc = true, int skip = 0, int take = DefaultTake,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, dimension, cancellationToken);
                if (request.Refusal != null) return ResponseMessage(request.Refusal);
                var report = await ReportAsync(request.Request, FirstResponseBudget, withPeople: true, cancellationToken);
                if (report == null) return ResponseMessage(StillBuilding());

                var rows = FilterPeople(report.People, signals, flag, seat, search);
                rows = SortPeople(rows, sortBy, sortDesc);
                var list = rows.ToList();
                return Ok(new CopilotInvestmentPeoplePage
                {
                    Total = list.Count,
                    Rows = list.Skip(Math.Max(0, skip)).Take(Math.Max(1, Math.Min(MaxTake, take))).ToList(),
                    Currency = report.Investment.Currency,
                    ValueAvailable = report.Value.Available,
                });
            });

        [HttpGet, Route("person/{userId:int}")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public Task<IHttpActionResult> Person(int userId, int windowDays = 28, string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, null, cancellationToken);
                if (request.Refusal != null) return ResponseMessage(request.Refusal);
                var report = await ReportAsync(request.Request, FirstResponseBudget, withPeople: true, cancellationToken);
                if (report == null) return ResponseMessage(StillBuilding());

                // Only someone this reader's scope covers: the drill-down is never a way round the filter.
                var person = report.People.FirstOrDefault(p => p.UserId == userId);
                if (person == null)
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel("The person is not in this report.", "personNotFound"));

                var range = request.Request.Range;
                var facts = await Service.Facts.Source.GetPersonDetailAsync(userId, person.CreditObjectId, person.CreditObservationDate,
                    range.FromUtc, range.ToExclusiveUtc, CopilotInvestmentWindow.LastDay(range), withCreditEstimate: false,
                    withPromptCategories: true, cancellationToken);
                return Ok(CopilotInvestmentPersonDetailBuilder.Build(person, facts, report.Investment.Currency, range.FromUtc, range.ToExclusiveUtc));
            });

        [HttpGet, Route("export/workbook")]
        public async Task<HttpResponseMessage> ExportWorkbook(int windowDays = 28, string userFilter = null, string dimension = null,
            string language = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // The page sends the reader's language. A link from before it did - or any value but a language the portal
            // ships - gets English, and nothing the caller wrote ever reaches a CultureInfo.
            var lang = PortalLanguages.Parse(language);
            try
            {
                var request = await PrepareAsync(windowDays, userFilter, dimension, cancellationToken);
                if (request.Refusal != null) return request.Refusal;
                var includePeople = CanSeeIndividuals();
                var report = await ReportAsync(request.Request, ExportWaitBudget, includePeople, cancellationToken);
                if (report == null)
                {
                    var notReady = WorkbookError(HttpStatusCode.ServiceUnavailable, "notReady", lang);
                    notReady.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
                    return notReady;
                }

                var bytes = CopilotInvestmentWorkbook.Build(includePeople ? report : report.WithoutIndividualData(), includePeople, BuildConstants.BuildLabel, lang);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(CopilotInvestmentWorkbook.ContentType);
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = CopilotInvestmentWorkbook.FileName(report, lang),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
                return response;
            }
            catch (HttpResponseException ex)
            {
                return ex.Response;
            }
            catch (CopilotInvestmentAssumptionsRejectedException ex)
            {
                return WhatIfWorkbookError(ex, lang);
            }
            catch (ArgumentException)
            {
                return WorkbookError(HttpStatusCode.BadRequest, "invalid", lang);
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotInvestment.Workbook");
                return WorkbookError(HttpStatusCode.InternalServerError, "failed", lang);
            }
        }

        /// <summary>
        /// The text a workbook link answers with instead of a file. The Excel button is a plain link, so the browser shows
        /// this in place of the download: it is written in the reader's language, from the same catalog as the page.
        /// </summary>
        internal static HttpResponseMessage WorkbookError(HttpStatusCode status, string error, PortalLanguage language, params (string Name, string Value)[] values) =>
            Plain(status, PortalCatalog.For(CopilotInvestmentWorkbook.CatalogModule, language).Text("copilotInvestment.workbook.error." + error, values));

        /// <summary>
        /// A what-if the workbook link carried that breaks a rule, in the page's own words for each rule it breaks. A figure
        /// that could not be read at all names a field rather than a rule, so it reads as the general "the figures break a rule".
        /// </summary>
        internal static HttpResponseMessage WhatIfWorkbookError(CopilotInvestmentAssumptionsRejectedException rejected, PortalLanguage language)
        {
            const string rule = "copilotInvestment.settings.error.";
            var text = PortalCatalog.For(CopilotInvestmentWorkbook.CatalogModule, language);
            var errors = rejected.ValidationErrors.Where(code => text.Has(rule + code)).Select(code => text.Text(rule + code)).Distinct().ToList();
            if (errors.Count == 0)
                errors.Add(text.Text(rule + (text.Has(rule + rejected.Code) ? rejected.Code : CopilotInvestmentErrorCodes.ScenarioInvalid)));
            return WorkbookError(HttpStatusCode.BadRequest, "whatIf", language, ("errors", string.Join(" ", errors)));
        }

        #region Request handling

        private sealed class Prepared
        {
            public CopilotInvestmentRequest Request;
            public HttpResponseMessage Refusal;
        }

        /// <summary>Validates the parameters and resolves the scope, refusing what the reader may not ask for.</summary>
        private async Task<Prepared> PrepareAsync(int windowDays, string userFilter, string dimension, CancellationToken cancellationToken)
        {
            UserFilterExpression filter;
            try
            {
                filter = UserFilterCodec.Parse(userFilter);
            }
            catch (UserFilterFormatException ex)
            {
                throw new ArgumentException("The filter could not be applied: " + ex.Message);
            }

            var seesIndividuals = CanSeeIndividuals();
            var group = string.IsNullOrWhiteSpace(dimension) ? CopilotInvestmentDimensions.Department : dimension.Trim();
            if (!CopilotInvestmentDimensions.Groupable(group)) throw new ArgumentException("'" + group + "' cannot be used to group this report.");

            // A filter that selects one person turns every aggregate into their record, and a manager grouping
            // names a person in every row: both need See PII, as on the Copilot Adoption page.
            if (!seesIndividuals && ((filter != null && !filter.IsEmpty) || CopilotInvestmentDimensions.NamesPeople(group)))
                return new Prepared { Refusal = PortalPermissionDenied.Response(Request, PortalPermission.SeePii) };

            var scope = await Scopes.ResolveAsync(Request, User, filter, cancellationToken);
            return new Prepared
            {
                Request = new CopilotInvestmentRequest
                {
                    Range = CopilotAdoptionDateRange.Create(windowDays, null, null, DateTime.UtcNow),
                    Scope = scope,
                    Scenario = ParseScenario(Request?.GetQueryNameValuePairs()),
                    Dimension = group,
                },
            };
        }

        /// <summary>
        /// The report, or null while the shared adoption evidence is still being built. <paramref name="withPeople"/> asks for the
        /// per-person rows too: only the See PII people views and workbook sheet use them.
        /// </summary>
        private async Task<CopilotInvestmentSummary> ReportAsync(CopilotInvestmentRequest request, TimeSpan budget, bool withPeople,
            CancellationToken cancellationToken)
        {
            var service = Service;
            CopilotAdoptionAnalysis analysis = null;
            if (service.AdoptionAvailable)
            {
                try
                {
                    var waited = await Coordinator.TryGetWithRunIdAsync(request.Range, new List<int>(), budget, cancellationToken);
                    if (waited.Analysis == null) return null;
                    analysis = waited.Analysis;
                }
                catch (CopilotAdoptionScoreSettingsUnavailableException ex)
                {
                    WebExceptionTelemetry.Report(ex, "CopilotInvestment.AdoptionSettings");
                    throw ReportScopeFailure.Exception(Request, CopilotAdoptionScoreSettingsErrorCodes.ReportSettingsUnavailable,
                        "The Copilot Adoption score settings could not be read, so this report is not available right now. Try again shortly.");
                }
            }
            return await service.BuildAsync(request, analysis, cancellationToken, withPeople);
        }

        /// <summary>
        /// The reader's what-if figures, parsed with the INVARIANT culture so "0.5" means one half on every
        /// server. An unparseable figure is refused rather than ignored, so a link never models something else.
        /// </summary>
        internal static CopilotInvestmentScenario ParseScenario(IEnumerable<KeyValuePair<string, string>> query)
        {
            var values = (query ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            double? Number(string name)
            {
                if (!values.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed))
                    throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.ScenarioInvalid, new[] { name });
                return parsed;
            }
            values.TryGetValue("currency", out var currency);
            return new CopilotInvestmentScenario
            {
                Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim(),
                SeatPricePerMonth = Number("seatPrice"),
                CreditPrice = Number("creditPrice"),
                StudioCreditPrice = Number("studioCreditPrice"),
                HourlyCost = Number("hourlyCost"),
                MinutesConservative = Number("minutesLow"),
                MinutesBase = Number("minutesBase"),
                MinutesUpside = Number("minutesHigh"),
                RealisationConservative = Number("realisationLow"),
                RealisationBase = Number("realisationBase"),
                RealisationUpside = Number("realisationHigh"),
            };
        }

        internal static IEnumerable<CopilotInvestmentPersonRow> FilterPeople(IEnumerable<CopilotInvestmentPersonRow> rows,
            string signals, string flag, string seat, string search)
        {
            var wanted = (signals ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                .Where(s => s.Length > 0).ToList();
            if (wanted.Count > 0) rows = rows.Where(r => wanted.Contains(r.Signal, StringComparer.Ordinal));
            if (!string.IsNullOrWhiteSpace(flag)) rows = rows.Where(r => r.Flags.Contains(flag.Trim(), StringComparer.Ordinal));
            if (string.Equals(seat, "licensed", StringComparison.OrdinalIgnoreCase)) rows = rows.Where(r => r.Licensed);
            else if (string.Equals(seat, "unlicensed", StringComparison.OrdinalIgnoreCase)) rows = rows.Where(r => !r.Licensed);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                rows = rows.Where(r => Contains(r.UserPrincipalName, term) || Contains(r.Department, term) || Contains(r.JobTitle, term));
            }
            return rows;
        }

        private static bool Contains(string value, string term) =>
            value != null && value.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0;

        /// <summary>
        /// Orders the whole filtered list - before it is paged, so a column sorts every person, not the fifty on screen. A value
        /// a person cannot have yet (no modelled cost without prices, no balance without an observation, no band without
        /// adoption evidence) sorts last whichever way the column is read; ties fall back to the sign-in name and then the user
        /// id, so the same request always pages the same way.
        /// </summary>
        internal static IEnumerable<CopilotInvestmentPersonRow> SortPeople(IEnumerable<CopilotInvestmentPersonRow> rows, string sortBy, bool descending)
        {
            IOrderedEnumerable<CopilotInvestmentPersonRow> ordered = null;
            foreach (var key in PeopleSortKeys(sortBy))
            {
                var value = key;
                ordered = ordered == null ? rows.OrderBy(r => value(r) == null) : ordered.ThenBy(r => value(r) == null);
                ordered = descending ? ordered.ThenByDescending(value, SortValueOrder) : ordered.ThenBy(value, SortValueOrder);
            }
            return ordered.ThenBy(r => r.UserPrincipalName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.UserId);
        }

        /// <summary>Text compares case-insensitively and culture-free, so the order is the same on every server.</summary>
        private static readonly IComparer<object> SortValueOrder = Comparer<object>.Create((a, b) =>
            a is string x && b is string y ? StringComparer.OrdinalIgnoreCase.Compare(x, y) : Comparer<object>.Default.Compare(a, b));

        /// <summary>What each column sorts by. A key returns null for a value the person does not have.</summary>
        private static Func<CopilotInvestmentPersonRow, object>[] PeopleSortKeys(string sortBy)
        {
            switch ((sortBy ?? string.Empty).Trim())
            {
                case "user": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.UserPrincipalName };
                case "licensed": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.Licensed };
                case "band": return new Func<CopilotInvestmentPersonRow, object>[] { r => BandRank(r.Band) };
                case "activeDays": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.ActiveDaysPerMonth, r => r.ActiveDays };
                case "breakEven": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.BreakEvenDaysPerMonth };
                case "apps": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.AppsUsed };
                case "credits": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.Credits };
                case "studioCredits": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.StudioCredits };
                case "cost": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.Cost };
                case "signal": return new Func<CopilotInvestmentPersonRow, object>[] { r => SignalRank(r.Signal) };
                case "valueBase": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.ValueBase };
                case "valueToCost": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.ValueToCostBase };
                case "gap": return new Func<CopilotInvestmentPersonRow, object>[] { r => r.BreakEvenGapDaysPerMonth };
                default:
                    // Modelled cost, then the latest balance, then Studio credits: ranked one after another and never
                    // added, because a period cost and a balance in an unverified period are not the same kind of money.
                    return new Func<CopilotInvestmentPersonRow, object>[] { r => r.Cost, r => r.Credits, r => r.StudioCredits };
            }
        }

        /// <summary>An adoption band by how established the habit is: never used lowest. Unknown bands have no rank.</summary>
        private static object BandRank(string band) =>
            Enum.TryParse(band, out AdoptionBand parsed) && Enum.IsDefined(typeof(AdoptionBand), parsed) ? (object)(int)parsed : null;

        /// <summary>
        /// A signal by the strength of the evidence it reports, strongest highest, so "descending" reads strongest first. The
        /// signals that do not judge at all (another period, not analysed, too new) sit just above no evidence.
        /// </summary>
        private static readonly Dictionary<string, int> SignalRanks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CopilotInvestmentSignals.Supported] = 12,
            [CopilotInvestmentSignals.StrongEvidence] = 11,
            [CopilotInvestmentSignals.ExpandCandidate] = 10,
            [CopilotInvestmentSignals.Plausible] = 9,
            [CopilotInvestmentSignals.Developing] = 8,
            [CopilotInvestmentSignals.BelowBreakEven] = 7,
            [CopilotInvestmentSignals.LightEvidence] = 6,
            [CopilotInvestmentSignals.Metered] = 5,
            [CopilotInvestmentSignals.ObservedUnlicensed] = 4,
            [CopilotInvestmentSignals.OtherPeriod] = 3,
            [CopilotInvestmentSignals.TooNewToJudge] = 2,
            [CopilotInvestmentSignals.NotAnalysed] = 1,
            [CopilotInvestmentSignals.NoEvidence] = 0,
        };

        private static object SignalRank(string signal) => signal != null && SignalRanks.TryGetValue(signal, out var rank) ? (object)rank : null;

        private bool CanSeeIndividuals() => PortalAccess.Evaluate(Request, User).SeePii;

        private HttpResponseMessage StillBuilding()
        {
            var response = Request.CreateResponse(HttpStatusCode.Accepted, new Dictionary<string, object>
            {
                { "status", "building" },
                { "retryAfterSeconds", RetryAfterSeconds },
                { "message", "The Copilot evidence behind this report is still being prepared. The page will refresh automatically." },
            });
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
            return response;
        }

        private static HttpResponseMessage Plain(HttpStatusCode status, string message) =>
            new HttpResponseMessage(status) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };

        private async Task<IHttpActionResult> Execute(Func<Task<IHttpActionResult>> handler)
        {
            try
            {
                return await handler();
            }
            catch (HttpResponseException)
            {
                throw;
            }
            catch (CopilotInvestmentAssumptionsRejectedException ex)
            {
                return Content(HttpStatusCode.BadRequest, new { code = ex.Code, validationErrors = ex.ValidationErrors });
            }
            catch (ArgumentException ex)
            {
                return Content(HttpStatusCode.BadRequest, new ApiErrorModel(ex.Message, "invalidRequest"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // To Application Insights, not to the browser: a SQL message can name the server and objects.
                WebExceptionTelemetry.Report(ex, "CopilotInvestment");
                return Content(HttpStatusCode.InternalServerError, new ApiErrorModel(
                    "The Copilot investment report could not be loaded. If this keeps happening, check the database is reachable.",
                    "investmentLoadFailed"));
            }
        }

        #endregion
    }
}
