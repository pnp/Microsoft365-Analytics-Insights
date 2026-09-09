using Common.Entities;
using Common.Entities.AgentCosts;
using Common.Entities.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Powers the "Agent costs" tab - what Microsoft actually charged for Copilot Studio agents, and what
    /// agent workloads cost in Azure.
    ///
    /// <para>Written for an M365 admin who has been asked "why has our Copilot bill gone up?". The answer
    /// they need is not a single number but a path: which agent, on which day, doing what - so the API
    /// exposes the full billing tuple (agent x environment x harness x feature x model x tool x knowledge
    /// source x channel) rather than a fixed set of pre-canned charts.</para>
    ///
    /// <para><b>Per-user attribution is available for Copilot Studio only, and comes from Microsoft.</b>
    /// The per-user entitlement routes Microsoft added in July 2026 report a user's billed credits directly;
    /// nothing here is apportioned or inferred. <b>Azure spend has no per-user view and cannot have one</b> -
    /// Azure billing is resource-scoped and no Cost Management surface, including the full cost-details
    /// export, carries a user identity.</para>
    /// </summary>
    [Authorize]
    [RoutePrefix("api/AgentCosts")]
    public class AgentCostsAPIController : ApiController
    {
        /// <summary>Default window. A month is the unit Microsoft bills in, so it is the natural default.</summary>
        private const int DefaultDays = 30;

        /// <summary>
        /// Widest window the page will load. The Power Platform licensing API only retains around 180 days,
        /// so a longer request could not be answered completely anyway.
        /// </summary>
        private const int MaxDays = 180;

        private readonly IAgentCostReportStore _store;
        private readonly Func<ImportTaskSettings> _importSettings;

        public AgentCostsAPIController()
            : this(new SqlAgentCostReportStore(), () => new AppConfig().ImportJobSettings)
        {
        }

        internal AgentCostsAPIController(IAgentCostReportStore store, Func<ImportTaskSettings> importSettings)
        {
            _store = store;
            _importSettings = importSettings;
        }

        /// <summary>
        /// Whether the imports are on, when they last ran, and what to do about it if they are not working.
        /// The page calls this first so it can explain an empty report instead of just showing zeroes.
        /// </summary>
        [HttpGet, Route("availability")]
        public async Task<IHttpActionResult> Availability()
        {
            var settings = _importSettings();
            var result = await _store.GetAvailabilityAsync(settings.CopilotStudioCredits, settings.AzureCostManagement);
            return Ok(result);
        }

        [HttpGet, Route("summary")]
        public Task<IHttpActionResult> Summary(string from = null, string to = null, string agentId = null,
            string environmentId = null, string harness = null, string feature = null, string model = null,
            string search = null, string tool = null, string knowledge = null, string channel = null)
            => Execute(async () =>
            {
                var query = BuildQuery(from, to, agentId, environmentId, harness, feature, model, search, tool, knowledge, channel);
                return Ok(await _store.GetSummaryAsync(query));
            });

        [HttpGet, Route("trend")]
        public Task<IHttpActionResult> Trend(string from = null, string to = null, string agentId = null,
            string environmentId = null, string harness = null, string feature = null, string model = null,
            string search = null, string tool = null, string knowledge = null, string channel = null)
            => Execute(async () =>
            {
                var query = BuildQuery(from, to, agentId, environmentId, harness, feature, model, search, tool, knowledge, channel);
                return Ok(await _store.GetDailyTrendAsync(query));
            });

        /// <summary>
        /// Credits grouped by one billing dimension. The dimension is validated against a closed list rather
        /// than passed through, because it selects a grouping expression.
        /// </summary>
        [HttpGet, Route("breakdown")]
        public Task<IHttpActionResult> Breakdown(string dimension, string from = null, string to = null,
            string agentId = null, string environmentId = null, string harness = null, string feature = null,
            string model = null, string search = null, string tool = null, string knowledge = null, string channel = null, int top = 20)
            => Execute(async () =>
            {
                if (!AgentCostDimensions.IsValid(dimension))
                {
                    return Content(HttpStatusCode.BadRequest, new
                    {
                        message = $"'{dimension}' is not something these figures can be broken down by.",
                        supported = AgentCostDimensions.All,
                    });
                }

                var query = BuildQuery(from, to, agentId, environmentId, harness, feature, model, search, tool, knowledge, channel);
                return Ok(await _store.GetBreakdownAsync(query, dimension, top));
            });

        /// <summary>The full billing tuple, paged. The deepest view the source data supports.</summary>
        [HttpGet, Route("detail")]
        public Task<IHttpActionResult> Detail(string from = null, string to = null, string agentId = null,
            string environmentId = null, string harness = null, string feature = null, string model = null,
            string search = null, string tool = null, string knowledge = null, string channel = null,
            int page = 1, int pageSize = 50, string sort = "credits", string direction = "desc")
            => Execute(async () =>
            {
                var query = BuildQuery(from, to, agentId, environmentId, harness, feature, model, search, tool, knowledge, channel);
                query.Page = page;
                query.PageSize = pageSize;
                query.Sort = sort;
                query.Direction = direction;
                return Ok(await _store.GetDetailAsync(query));
            });

        [HttpGet, Route("azure")]
        public Task<IHttpActionResult> Azure(string dimension = AzureCostDimensions.Meter, string from = null,
            string to = null, int top = 20)
            => Execute(async () =>
            {
                if (!AzureCostDimensions.IsValid(dimension))
                {
                    return Content(HttpStatusCode.BadRequest, new
                    {
                        message = $"'{dimension}' is not something Azure costs can be broken down by.",
                        supported = AzureCostDimensions.All,
                    });
                }

                var query = BuildQuery(from, to, null, null, null, null, null, null, null, null, null);
                return Ok(await _store.GetAzureBreakdownAsync(query, dimension, top));
            });

        /// <summary>Filter values that actually occur in the window, so a picker never offers a dead end.</summary>
        [HttpGet, Route("filters")]
        public Task<IHttpActionResult> Filters(string from = null, string to = null)
            => Execute(async () =>
            {
                var query = BuildQuery(from, to, null, null, null, null, null, null, null, null, null);
                return Ok(await _store.GetFilterOptionsAsync(query));
            });

        /// <summary>
        /// The biggest per-user credit consumers in the window.
        /// </summary>
        /// <remarks>
        /// Only Copilot Studio has this. Azure spend is resource-scoped and carries no user identity on any
        /// Cost Management surface, so there is no equivalent endpoint for it and there cannot be one.
        /// </remarks>
        [HttpGet, Route("users")]
        public Task<IHttpActionResult> Users(string from = null, string to = null, string environmentId = null, int top = 20)
            => Execute(async () =>
            {
                var query = BuildQuery(from, to, null, environmentId, null, null, null, null, null, null, null);
                return Ok(await _store.GetTopUsersAsync(query, top));
            });

        /// <summary>
        /// Parses the window and filters. Dates are read as UTC dates: the underlying data has a daily grain
        /// stamped in UTC, so interpreting a query-string date in the server's local zone would shift the
        /// whole report by a day for anyone not on UTC.
        /// </summary>
        private static AgentCostQuery BuildQuery(string from, string to, string agentId, string environmentId,
            string harness, string feature, string model, string search,
            string tool = null, string knowledge = null, string channel = null)
        {
            var today = DateTime.UtcNow.Date;

            // An unparseable date is rejected rather than treated as absent. Silently substituting the
            // default window would answer a request for a period the caller never asked for, with
            // perfectly plausible-looking figures - the worst kind of wrong answer for a cost report.
            var toDate = ParseDate(to, nameof(to)) ?? today;
            var fromDate = ParseDate(from, nameof(from)) ?? toDate.AddDays(-(DefaultDays - 1));

            if (fromDate > toDate)
            {
                var swap = fromDate;
                fromDate = toDate;
                toDate = swap;
            }

            // Clamped rather than rejected: a too-wide window is a reasonable thing for someone to ask for,
            // and silently answering the widest supported window beats an error page.
            if ((toDate - fromDate).TotalDays > MaxDays)
            {
                fromDate = toDate.AddDays(-MaxDays);
            }

            return new AgentCostQuery
            {
                FromUtc = fromDate,
                ToUtc = toDate,
                AgentId = Trimmed(agentId),
                EnvironmentId = Trimmed(environmentId),
                Harness = Trimmed(harness),
                FeatureName = Trimmed(feature),
                LlmModel = Trimmed(model),
                Search = Trimmed(search),
                ToolInvoked = Trimmed(tool),
                KnowledgeSources = Trimmed(knowledge),
                ChannelId = Trimmed(channel),
            };
        }

        private static string Trimmed(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Parses a query-string date, or returns null when the parameter was not supplied at all.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when a value WAS supplied but could not be parsed. Absent and invalid must not be
        /// conflated: treating a typo as "use the default period" answers a different question than the one
        /// asked, and the caller has no way to tell.
        /// </exception>
        private static DateTime? ParseDate(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                throw new ArgumentException(
                    $"'{value}' is not a date this report understands. Use the form yyyy-MM-dd, for example 2026-09-01.",
                    parameterName);
            }

            return parsed.Date;
        }

        /// <summary>
        /// Runs a handler, turning an unexpected failure into a message an admin can act on rather than a
        /// blank 500. The report is read-only, so there is nothing to roll back.
        /// </summary>
        private async Task<IHttpActionResult> Execute(Func<Task<IHttpActionResult>> handler)
        {
            try
            {
                return await handler();
            }
            catch (ArgumentException ex)
            {
                return Content(HttpStatusCode.BadRequest, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return Content(HttpStatusCode.InternalServerError, new
                {
                    message = "The agent cost figures could not be loaded. If this keeps happening, check the "
                        + "database is reachable and that the agent cost imports have run at least once.",
                    detail = ex.Message,
                });
            }
        }
    }
}
