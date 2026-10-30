using Common.Entities.CopilotInvestment;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.CopilotInvestment;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>What Administration &gt; Copilot investment assumptions shows.</summary>
    public sealed class CopilotInvestmentSettingsModel
    {
        /// <summary>False when no Storage connection string is configured: nothing can be saved.</summary>
        [JsonProperty("durable")]
        public bool Durable { get; set; }

        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("assumptions")]
        public CopilotInvestmentAssumptions Assumptions { get; set; }

        [JsonProperty("updatedBy")]
        public string UpdatedBy { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; set; }

        /// <summary>Newest first. Names who made each change, which is why the page is Administration-only.</summary>
        [JsonProperty("history")]
        public List<CopilotInvestmentAssumptionsChange> History { get; set; } = new List<CopilotInvestmentAssumptionsChange>();
    }

    public sealed class CopilotInvestmentSettingsSaveRequest
    {
        /// <summary>The version the administrator was editing; a save made against an older one is refused.</summary>
        [JsonProperty("expectedVersion")]
        public long? ExpectedVersion { get; set; }

        [JsonProperty("assumptions")]
        public CopilotInvestmentAssumptions Assumptions { get; set; }
    }

    public sealed class CopilotInvestmentSettingsError
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Administration &gt; Copilot investment assumptions: the tenant defaults the investment report models money
    /// with. Administration only - the history names who changed what - and every write is a same-origin script
    /// request, so another site cannot make an administrator's browser change them.
    /// </summary>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/CopilotInvestmentSettings")]
    public class CopilotInvestmentSettingsAPIController : ControllerBase
    {
        private readonly Func<CopilotInvestmentAssumptionsStore> _store;

        public CopilotInvestmentSettingsAPIController() : this(CopilotInvestmentAssumptionsProvider.Production) { }

        internal CopilotInvestmentSettingsAPIController(Func<CopilotInvestmentAssumptionsStore> store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // GET: api/CopilotInvestmentSettings
        [HttpGet]
        [Route("")]
        public Task<IActionResult> Get(CancellationToken cancellationToken = default) =>
            Answer(async store => Ok(ToModel(await store.GetAsync(cancellationToken).ConfigureAwait(false), store.IsDurable)));

        // POST: api/CopilotInvestmentSettings
        [HttpPost]
        [Route("")]
        [RequireSameOriginXhr]
        public Task<IActionResult> Save([FromBody] CopilotInvestmentSettingsSaveRequest request, CancellationToken cancellationToken = default) =>
            Answer(async store =>
            {
                if (request?.Assumptions == null || request.ExpectedVersion == null)
                    throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.InvalidRequest);
                var saved = await store.SaveAsync(request.Assumptions, request.ExpectedVersion.Value, ChangedBy, cancellationToken).ConfigureAwait(false);
                return Ok(ToModel(saved, store.IsDurable));
            });

        /// <summary>The administrator's UPN where the token carries one, as the history shows it.</summary>
        private string ChangedBy => PortalViewer.UserPrincipalNameOf(User) ?? User?.Identity?.Name;

        private async Task<IActionResult> Answer(Func<CopilotInvestmentAssumptionsStore, Task<IActionResult>> action)
        {
            if (!ModelState.IsValid)
                return BadRequest(new CopilotInvestmentSettingsError { Code = CopilotInvestmentErrorCodes.InvalidRequest });
            try
            {
                return await action(_store());
            }
            catch (CopilotInvestmentAssumptionsRejectedException ex)
            {
                return StatusCode((int)StatusFor(ex.Code), new CopilotInvestmentSettingsError { Code = ex.Code, ValidationErrors = ex.ValidationErrors.ToList() });
            }
            catch (CopilotInvestmentAssumptionsUnavailableException ex)
            {
                WebExceptionTelemetry.Report(ex, "CopilotInvestmentAssumptions");
                return StatusCode(503, new CopilotInvestmentSettingsError { Code = CopilotInvestmentErrorCodes.StateUnavailable });
            }
        }

        internal static HttpStatusCode StatusFor(string code)
        {
            switch (code)
            {
                case CopilotInvestmentErrorCodes.VersionConflict: return HttpStatusCode.Conflict;
                case CopilotInvestmentErrorCodes.StorageNotConfigured: return HttpStatusCode.ServiceUnavailable;
                default: return HttpStatusCode.BadRequest;
            }
        }

        private static CopilotInvestmentSettingsModel ToModel(CopilotInvestmentAssumptionsDocument document, bool durable) =>
            new CopilotInvestmentSettingsModel
            {
                Durable = durable,
                Version = document.Version,
                Assumptions = document.Assumptions,
                UpdatedBy = document.UpdatedBy,
                UpdatedUtc = document.UpdatedUtc,
                History = (document.History ?? new List<CopilotInvestmentAssumptionsChange>()).ToList(),
            };
    }
}
