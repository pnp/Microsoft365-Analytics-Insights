using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.Localisation;
using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.CopilotInvestment;
using Web.AnalyticsWeb.Models.CopilotSpend;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Copilot spend: who consumed what, when, from which source and where in the organisation - descriptive accounting with
    /// no value, return or recommendation, and no prices, so it works before anyone sets the investment assumptions.
    /// </summary>
    /// <remarks>
    /// <para><b>Two sources, never added.</b> Billed Copilot Studio usage is a daily ledger and sums over the period. The
    /// preview Copilot credit balances are each person's latest reading in a period the source does not identify, so they are
    /// a balance on a date, never a sum of days.</para>
    /// <para><b>Who sees what.</b> Aggregates are for every signed-in reader, narrowed by the administrator's global filter,
    /// with groups of fewer than five people folded together. A reader's own filter, a breakdown that names managers, the
    /// people list, a person's drill-down and the people sheet of the workbook need See PII; a summary for a reader without
    /// it is a trimmed copy.</para>
    /// </remarks>
    [Authorize]
    [Route("api/CopilotSpend")]
    [ApiReplyExceptionFilter]
    public class CopilotSpendAPIController : ControllerBase
    {
        private const int DefaultTake = 50;
        private const int MaxTake = 500;

        public CopilotSpendAPIController()
            : this(ReportScopeResolver.Default, CopilotSpendReportService.ForThisDeployment)
        {
        }

        internal CopilotSpendAPIController(ReportScopeResolver scopes, Func<CopilotSpendReportService> service)
        {
            Scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        internal ReportScopeResolver Scopes { get; }
        private readonly Func<CopilotSpendReportService> _service;
        private CopilotSpendReportService Service => _service();

        [HttpGet, Route("summary")]
        public Task<IActionResult> Summary(int windowDays = 28, string userFilter = null, string dimension = null,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, dimension, cancellationToken);
                if (request.Refusal != null) return request.Refusal;
                var report = await Service.BuildAsync(request.Request, cancellationToken);
                return Ok(CanSeeIndividuals() ? report : report.WithoutIndividualData());
            });

        /// <summary>Latest balances on a bounded set of sample dates: a balance curve, never a running total.</summary>
        [HttpGet, Route("trend")]
        public Task<IActionResult> Trend(int windowDays = 28, string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, null, cancellationToken);
                if (request.Refusal != null) return request.Refusal;
                var range = request.Request.Range;
                var credits = await Service.Facts.CreditsAsync(range.FromUtc.Date, CopilotInvestmentWindow.LastDay(range));
                if (!credits.SnapshotDate.HasValue)
                    return Ok(new { points = new List<CreditTrendPoint>(), snapshotDate = (DateTime?)null, failureCode = credits.FailureCode });

                var dates = CopilotInvestmentSql.TrendDates(range.FromUtc.Date, credits.SnapshotDate.Value);
                var scope = request.Request.Scope.Sql;
                var key = "CopilotSpend::Trend::" + string.Join(",", dates.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
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
        public Task<IActionResult> People(int windowDays = 28, string userFilter = null, string source = null, string search = null,
            string sortBy = null, bool sortDesc = true, int skip = 0, int take = DefaultTake,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, null, cancellationToken);
                if (request.Refusal != null) return request.Refusal;
                var report = await Service.BuildAsync(request.Request, cancellationToken);
                var rows = CopilotSpendPeople.Sort(CopilotSpendPeople.Filter(report.People, source, search), sortBy, sortDesc).ToList();
                return Ok(new CopilotSpendPeoplePage
                {
                    Total = rows.Count,
                    Rows = rows.Skip(Math.Max(0, skip)).Take(Math.Max(1, Math.Min(MaxTake, take))).ToList(),
                });
            });

        [HttpGet, Route("person/{userId:int}")]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public Task<IActionResult> Person(int userId, int windowDays = 28, string userFilter = null,
            CancellationToken cancellationToken = default(CancellationToken))
            => Execute(async () =>
            {
                var request = await PrepareAsync(windowDays, userFilter, null, cancellationToken);
                if (request.Refusal != null) return request.Refusal;

                // Only someone this reader's scope covers: the drill-down is never a way round the filter.
                var detail = await Service.PersonAsync(request.Request, userId, cancellationToken);
                if (detail == null)
                    return NotFound(new ApiErrorModel("The person is not in this report.", "personNotFound"));
                return Ok(detail);
            });

        [HttpGet, Route("export/workbook")]
        public async Task<IActionResult> ExportWorkbook(int windowDays = 28, string userFilter = null, string dimension = null,
            string language = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // The page sends the reader's language; anything else - or nothing, from an older link - is English.
            var lang = PortalLanguages.Parse(language);
            try
            {
                var request = await PrepareAsync(windowDays, userFilter, dimension, cancellationToken);
                if (request.Refusal != null) return request.Refusal;
                var report = await Service.BuildAsync(request.Request, cancellationToken);

                var includePeople = CanSeeIndividuals();
                var bytes = CopilotSpendWorkbook.Build(includePeople ? report : report.WithoutIndividualData(), includePeople, BuildConstants.BuildLabel, lang);
                Response.Headers.CacheControl = "no-store, private";
                return File(bytes, CopilotSpendWorkbook.ContentType, CopilotSpendWorkbook.FileName(report, lang));
            }
            catch (ApiReplyException ex)
            {
                Response.Headers.CacheControl = "no-store, private";
                return ex.Result;
            }
            catch (ArgumentException)
            {
                return WorkbookError(HttpStatusCode.BadRequest, "invalid", lang);
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotSpend.Workbook");
                return WorkbookError(HttpStatusCode.InternalServerError, "failed", lang);
            }
        }

        /// <summary>The text a workbook link answers with instead of a file, in the reader's language (the button is a plain link).</summary>
        internal static ContentResult WorkbookError(HttpStatusCode status, string error, PortalLanguage language) =>
            new ContentResult
            {
                StatusCode = (int)status,
                Content = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, language).Text("copilotSpend.workbook.error." + error),
                ContentType = "text/plain; charset=utf-8",
            };

        #region Request handling

        private sealed class Prepared
        {
            public CopilotSpendRequest Request;
            public IActionResult Refusal;
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

            var group = string.IsNullOrWhiteSpace(dimension) ? CopilotInvestmentDimensions.Department : dimension.Trim();
            if (!CopilotInvestmentDimensions.Groupable(group)) throw new ArgumentException("'" + group + "' cannot be used to group this report.");

            // A filter that selects one person turns every aggregate into their record, and a manager grouping names a
            // person in every row: both need See PII, as on the other Copilot reports.
            if (!CanSeeIndividuals() && ((filter != null && !filter.IsEmpty) || CopilotInvestmentDimensions.NamesPeople(group)))
                return new Prepared { Refusal = PortalPermissionDenied.Result(Request, PortalPermission.SeePii) };

            var scope = await Scopes.ResolveAsync(Request, User, filter, cancellationToken);
            return new Prepared
            {
                Request = new CopilotSpendRequest
                {
                    Range = CopilotAdoptionDateRange.Create(windowDays, null, null, DateTime.UtcNow),
                    Scope = scope,
                    Dimension = group,
                },
            };
        }

        private bool CanSeeIndividuals() => PortalAccess.Evaluate(Request, User).SeePii;

        private async Task<IActionResult> Execute(Func<Task<IActionResult>> handler)
        {
            try
            {
                return await handler();
            }
            catch (ApiReplyException)
            {
                throw;
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ApiErrorModel(ex.Message, "invalidRequest"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // To Application Insights, not to the browser: a SQL message can name the server and objects.
                WebExceptionTelemetry.Report(ex, "CopilotSpend");
                return StatusCode(500, new ApiErrorModel(
                    "The Copilot spend report could not be loaded. If this keeps happening, check the database is reachable.",
                    "spendLoadFailed"));
            }
        }

        #endregion
    }
}
