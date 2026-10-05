using Common.Entities.Config;
using Common.Entities.UserFilters;
using DataUtils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The administrator's global report filter: the conditions every insights report applies on top of a
    /// reader's own filters - "Department is the viewer's department", "User type is member" - which only a
    /// portal administrator can change.
    /// </summary>
    /// <remarks>
    /// <para>This controller only shows and edits the filter. It is <i>applied</i> by every report's own
    /// endpoint through <see cref="ReportScopeResolver"/>, so a reader cannot get round it by skipping the
    /// page that shows it.</para>
    /// <para>Every change is a POST carrying <see cref="RequireSameOriginXhrAttribute"/>, and is recorded
    /// in Application Insights with who made it - a filter that decides who sees what needs an audit trail
    /// that outlives the row it overwrote.</para>
    /// </remarks>
    [Authorize]
    [Route("api/GlobalFilter")]
    [ApiReplyExceptionFilter]
    public class GlobalFilterAPIController : ControllerBase
    {
        internal const string InvalidFilterCode = "invalidFilter";
        internal const string RevisionConflictCode = "revisionConflict";
        internal const string StorageUnavailableCode = "storageUnavailable";
        internal const string SaveFailedCode = "saveFailed";

        internal const string RevisionConflictMessage =
            "Someone else changed the global filter after you opened it. Reload the page to see their change before saving yours.";

        internal const string StorageUnavailableMessage =
            "The database has not been upgraded to hold a global filter yet. Run the installer, or the manual upgrade script, and try again.";

        internal const string SaveFailedMessage =
            "The global filter could not be saved. The failure has been logged. Try again shortly.";

        private readonly Func<AnalyticsLogger> _logger;

        public GlobalFilterAPIController()
            : this(ReportScopeResolver.Default, () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "GlobalFilter"))
        {
        }

        internal GlobalFilterAPIController(ReportScopeResolver resolver, Func<AnalyticsLogger> logger = null)
        {
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _logger = logger;
        }

        internal ReportScopeResolver Resolver { get; }

        /// <summary>
        /// GET api/GlobalFilter/effective - the global filter as it applies to the signed-in person, with their
        /// own values filled in. Open to every signed-in reader: it is how the page explains what they see.
        /// </summary>
        [HttpGet]
        [Route("effective")]
        public async Task<IActionResult> Effective(CancellationToken cancellationToken = default(CancellationToken))
        {
            var canBypass = PortalAccess.Evaluate(Request, User).Administration;
            var global = await Resolver.DescribeAsync(Request, User, cancellationToken).ConfigureAwait(false);
            return NoStore(HttpStatusCode.OK, GlobalFilterEffectiveModel.From(global, canBypass));
        }

        /// <summary>GET api/GlobalFilter - the definition, for the administrator's editor.</summary>
        [HttpGet]
        [Route("")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequirePortalPermission(PortalPermission.SeePii)]
        public async Task<IActionResult> Get(CancellationToken cancellationToken = default(CancellationToken))
        {
            GlobalFilterState state;
            try
            {
                state = await Resolver.Filters.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "GlobalFilterAPI.Get");
                return NoStore(HttpStatusCode.ServiceUnavailable,
                    new ApiErrorModel(ReportScopeFailure.FilterUnavailableMessage, ReportScopeFailure.FilterUnavailable));
            }

            return NoStore(HttpStatusCode.OK, ToAdminModel(state));
        }

        /// <summary>
        /// POST api/GlobalFilter <c>{ "filter": "[...]", "revision": 3 }</c> - replaces the global filter. An empty
        /// filter removes it. Refused with 409 when someone else saved since the editor opened it.
        /// </summary>
        [HttpPost]
        [Route("")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequirePortalPermission(PortalPermission.SeePii)]
        [RequireSameOriginXhr]
        public async Task<IActionResult> Save(
            [FromBody] GlobalFilterSaveRequest body, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (body == null)
            {
                return NoStore(HttpStatusCode.BadRequest, new ApiErrorModel("The request had no body.", InvalidFilterCode));
            }

            GlobalFilterDefinition definition;
            try
            {
                definition = GlobalFilterCodec.Parse(body.Filter);
            }
            catch (UserFilterFormatException ex)
            {
                return NoStore(HttpStatusCode.BadRequest, new ApiErrorModel(ex.Message, InvalidFilterCode));
            }

            var by = PortalViewer.UserPrincipalNameOf(User) ?? User?.Identity?.Name;
            var canonical = GlobalFilterCodec.Serialize(definition) ?? string.Empty;

            GlobalFilterRecord saved;
            try
            {
                saved = await Resolver.Filters.Store.SaveAsync(canonical, body.Revision, by, cancellationToken).ConfigureAwait(false);
            }
            catch (GlobalFilterStorageMissingException)
            {
                return NoStore(HttpStatusCode.ServiceUnavailable, new ApiErrorModel(StorageUnavailableMessage, StorageUnavailableCode));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "GlobalFilterAPI.Save");
                return NoStore(HttpStatusCode.InternalServerError, new ApiErrorModel(SaveFailedMessage, SaveFailedCode));
            }

            if (saved == null)
            {
                return NoStore(HttpStatusCode.Conflict, new ApiErrorModel(RevisionConflictMessage, RevisionConflictCode));
            }

            // This process answers from the new filter at once; other instances within their cache lifetime.
            Resolver.Filters.Invalidate();
            Audit(saved, definition);

            return NoStore(HttpStatusCode.OK, ToAdminModel(CachedGlobalFilterProvider.Parse(saved)));
        }

        /// <summary>
        /// POST api/GlobalFilter/preview <c>{ "filter": "[...]" }</c> - a draft evaluated for the signed-in
        /// administrator: how many people it would leave them seeing, and the values it would take from them.
        /// Saves nothing.
        /// </summary>
        [HttpPost]
        [Route("preview")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequirePortalPermission(PortalPermission.SeePii)]
        [RequireSameOriginXhr]
        public async Task<IActionResult> Preview(
            [FromBody] GlobalFilterPreviewRequest body, CancellationToken cancellationToken = default(CancellationToken))
        {
            GlobalFilterDefinition definition;
            try
            {
                definition = GlobalFilterCodec.Parse(body?.Filter);
            }
            catch (UserFilterFormatException ex)
            {
                return NoStore(HttpStatusCode.BadRequest, new ApiErrorModel(ex.Message, InvalidFilterCode));
            }

            var global = await Resolver.PreviewAsync(Request, User, definition, cancellationToken).ConfigureAwait(false);
            return NoStore(HttpStatusCode.OK, GlobalFilterEffectiveModel.From(global.Defined ? global : null, canBypass: true));
        }

        private static GlobalFilterAdminModel ToAdminModel(GlobalFilterState state)
        {
            return new GlobalFilterAdminModel
            {
                Filter = state.ParseError == null ? (GlobalFilterCodec.Serialize(state.Definition) ?? string.Empty) : string.Empty,
                Clauses = state.ParseError == null ? GlobalFilterEcho.ToModels(state.Definition) : new List<GlobalFilterClauseModel>(),
                Revision = state.Record.Revision,
                ModifiedUtc = state.Record.ModifiedUtc,
                ModifiedBy = state.Record.ModifiedBy,
                StorageAvailable = state.Record.StorageAvailable,
                RolesEnforced = PortalAccessPolicy.FromAppSettings().Enforced,
                Invalid = state.ParseError != null,
            };
        }

        /// <summary>Records who changed the filter and what it now says. Never fails the save.</summary>
        private void Audit(GlobalFilterRecord saved, GlobalFilterDefinition definition)
        {
            try
            {
                var analytics = _logger?.Invoke();
                if (analytics == null) return;

                analytics.TrackEvent(
                    AnalyticsLogger.AnalyticsEvent.GlobalFilterChanged,
                    new Dictionary<string, string>
                    {
                        ["ModifiedBy"] = saved.ModifiedBy ?? string.Empty,
                        ["Revision"] = saved.Revision.ToString(CultureInfo.InvariantCulture),
                        // The definition as the administrator wrote it: attributes and the values they chose.
                        // It names a person only where the administrator chose someone by name.
                        ["Filter"] = saved.FilterJson ?? string.Empty,
                    },
                    new Dictionary<string, double>
                    {
                        ["Conditions"] = definition.Clauses.Count,
                    });
            }
            catch (Exception ex)
            {
                WebExceptionTelemetry.Report(ex, "GlobalFilterAPI.Audit");
            }
        }

        /// <remarks>
        /// net10: Web API 2's <c>Request.CreateResponse(status, body)</c> with its cache headers set. The body is
        /// written by the Web API compatible JSON formatter, so the models' <c>[JsonProperty]</c> names hold.
        /// </remarks>
        private IActionResult NoStore(HttpStatusCode status, object body)
        {
            Response.Headers.CacheControl = "no-store, private";
            return StatusCode((int)status, body);
        }
    }
}
