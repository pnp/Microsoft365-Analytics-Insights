using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
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
                var report = await ReportAsync(request.Request, FirstResponseBudget, cancellationToken);
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
                var credits = await Service.Facts.CreditsAsync(range.FromUtc.Date, range.ToExclusiveUtc.Date);
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
                var report = await ReportAsync(request.Request, FirstResponseBudget, cancellationToken);
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
                var report = await ReportAsync(request.Request, FirstResponseBudget, cancellationToken);
                if (report == null) return ResponseMessage(StillBuilding());

                // Only someone this reader's scope covers: the drill-down is never a way round the filter.
                var person = report.People.FirstOrDefault(p => p.UserId == userId);
                if (person == null)
                    return Content(HttpStatusCode.NotFound, new ApiErrorModel("The person is not in this report.", "personNotFound"));

                var range = request.Request.Range;
                var facts = await Service.Facts.Source.GetPersonDetailAsync(userId, person.CreditObjectId, person.CreditObservationDate,
                    range.FromUtc, range.ToExclusiveUtc, cancellationToken);
                return Ok(CopilotInvestmentPersonDetailBuilder.Build(person, facts, report.Investment.Currency, range.FromUtc, range.ToExclusiveUtc));
            });

        [HttpGet, Route("export/workbook")]
        public async Task<HttpResponseMessage> ExportWorkbook(int windowDays = 28, string userFilter = null, string dimension = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var request = await PrepareAsync(windowDays, userFilter, dimension, cancellationToken);
                if (request.Refusal != null) return request.Refusal;
                var report = await ReportAsync(request.Request, ExportWaitBudget, cancellationToken);
                if (report == null)
                {
                    var notReady = Request.CreateResponse(HttpStatusCode.ServiceUnavailable);
                    notReady.Content = new StringContent(
                        "The Copilot evidence behind this report is still being prepared, so the workbook is not ready yet. "
                        + "Open the Copilot investment page, wait for it to load, then use its Excel button.", Encoding.UTF8, "text/plain");
                    notReady.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
                    return notReady;
                }

                var includePeople = CanSeeIndividuals();
                var bytes = CopilotInvestmentWorkbook.Build(includePeople ? report : report.WithoutIndividualData(), includePeople, BuildConstants.BuildLabel);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(CopilotInvestmentWorkbook.ContentType);
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = CopilotInvestmentWorkbook.FileName(report),
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
                return Plain(HttpStatusCode.BadRequest, "The what-if figures in this link are not valid: " + string.Join(", ", ex.ValidationErrors));
            }
            catch (ArgumentException ex)
            {
                return Plain(HttpStatusCode.BadRequest, ex.Message);
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotInvestment.Workbook");
                return Plain(HttpStatusCode.InternalServerError, "The Copilot investment workbook could not be generated. The failure has been logged.");
            }
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

        /// <summary>The report, or null while the shared adoption evidence is still being built.</summary>
        private async Task<CopilotInvestmentSummary> ReportAsync(CopilotInvestmentRequest request, TimeSpan budget, CancellationToken cancellationToken)
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
            return await service.BuildAsync(request, analysis, cancellationToken);
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

        internal static IEnumerable<CopilotInvestmentPersonRow> SortPeople(IEnumerable<CopilotInvestmentPersonRow> rows, string sortBy, bool descending)
        {
            var keys = new List<Func<CopilotInvestmentPersonRow, object>>();
            switch ((sortBy ?? string.Empty).Trim())
            {
                case "user": keys.Add(r => r.UserPrincipalName ?? string.Empty); break;
                case "activeDays": keys.Add(r => r.ActiveDays); break;
                case "credits": keys.Add(r => r.Credits ?? -1); break;
                case "studioCredits": keys.Add(r => r.StudioCredits); break;
                case "cost": keys.Add(r => r.Cost ?? -1); break;
                case "valueBase": keys.Add(r => r.ValueBase ?? -1); break;
                case "valueToCost": keys.Add(r => r.ValueToCostBase ?? -1); break;
                case "gap": keys.Add(r => r.BreakEvenGapDaysPerMonth ?? -1); break;
                default:
                    // Modelled cost, then the latest balance, then Studio credits: ranked one after another and never
                    // added, because a period cost and a balance in an unverified period are not the same kind of money.
                    keys.Add(r => r.Cost ?? -1);
                    keys.Add(r => r.Credits ?? -1);
                    keys.Add(r => r.StudioCredits);
                    break;
            }
            var ordered = descending ? rows.OrderByDescending(keys[0]) : rows.OrderBy(keys[0]);
            foreach (var key in keys.Skip(1)) ordered = descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
            return ordered.ThenBy(r => r.UserId);
        }

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
