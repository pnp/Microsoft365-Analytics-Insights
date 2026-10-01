using Common.Entities.UserFilters;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Models.UserFilters
{
    /// <summary>The administrator's global filter as it applies to one request.</summary>
    internal sealed class GlobalFilterApplication
    {
        /// <summary>True when a global filter is defined, whether or not it narrows this request.</summary>
        public bool Defined { get; set; }

        /// <summary>True when an administrator switched it off for their own view.</summary>
        public bool Bypassed { get; set; }

        /// <summary>True when the signed-in person may switch it off for their own view - a portal administrator.</summary>
        public bool CanBypass { get; set; }

        public int Revision { get; set; }

        /// <summary>The filter resolved for the signed-in person; <c>null</c> when it was not evaluated.</summary>
        public ResolvedGlobalFilter Resolved { get; set; }

        public CompiledUserFilter Compiled { get; set; }

        /// <summary>What the page shows: the conditions with the signed-in person's own values filled in.</summary>
        public GlobalFilterEcho Echo { get; set; }

        /// <summary>True when the filter narrows this request.</summary>
        public bool Applied => Defined && !Bypassed;

        /// <summary>The filter in plain English for the server's own artefacts, or <c>null</c> when it does not apply.</summary>
        public string DescribeInEnglish()
        {
            if (!Applied || Resolved == null || Compiled == null) return null;
            return GlobalFilterDescriber.Describe(Resolved, Compiled.EnglishDimensionName);
        }
    }

    /// <summary>
    /// What one report request may cover: the administrator's global filter, resolved for the person signed
    /// in, AND the reader's own filter where the report takes one.
    /// </summary>
    /// <remarks>
    /// Kept as two compiled filters rather than merged into one expression, because they are shown apart -
    /// the administrator's conditions locked, the reader's editable - and because ANDing two filters that
    /// each have OR groups would multiply their clauses out. A person is in scope when both match.
    /// </remarks>
    internal sealed class ReportScope
    {
        /// <summary>No global filter and no reader filter: the whole tenant.</summary>
        public static readonly ReportScope Unrestricted = new ReportScope(null, null, null, "all");

        private readonly Lazy<ReportUserScope> _sql;

        internal ReportScope(GlobalFilterApplication global, CompiledUserFilter restriction, CompiledUserFilter userFilter, string key)
        {
            Global = global;
            Restriction = restriction;
            UserFilter = userFilter;
            Key = key;
            _sql = new Lazy<ReportUserScope>(BuildSqlScope, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// The id set, shared between requests with the same <see cref="Key"/> - every reader the filters
        /// treat alike - for a few minutes. Building it walks the whole directory, which is cheap once but
        /// not on every request of every reader of a busy page. The key carries when the directory was
        /// read, so an entry can never outlive the directory it was computed from.
        /// </summary>
        private ReportUserScope BuildSqlScope()
        {
            var cacheKey = "ReportUserScope::" + Key;
            if (System.Runtime.Caching.MemoryCache.Default.Get(cacheKey) is ReportUserScope cached) return cached;

            var built = ReportUserScope.Matching(Restriction, UserFilter);
            System.Runtime.Caching.MemoryCache.Default.Set(
                cacheKey,
                built,
                new System.Runtime.Caching.CacheItemPolicy { SlidingExpiration = TimeSpan.FromMinutes(5) });
            return built;
        }

        /// <summary>The global filter as it applies to this request, or <c>null</c> when none is defined.</summary>
        public GlobalFilterApplication Global { get; }

        /// <summary>The global filter, compiled. <c>null</c> when none applies to this request.</summary>
        public CompiledUserFilter Restriction { get; }

        /// <summary>The reader's own filter, compiled. <c>null</c> when they set none.</summary>
        public CompiledUserFilter UserFilter { get; }

        public bool IsRestricted => Restriction != null || UserFilter != null;

        /// <summary>
        /// A stable identity for report caches: equal for two requests that cover the same people from the
        /// same read of the directory, so two readers the filters treat alike share cached results.
        /// </summary>
        public string Key { get; }

        /// <summary>The scope as a set of user ids, for the reports that aggregate in SQL. Computed on first use.</summary>
        public ReportUserScope Sql => IsRestricted ? _sql.Value : ReportUserScope.Everyone;

        /// <summary>The global filter's echo when it narrows this request, otherwise <c>null</c>.</summary>
        public GlobalFilterEcho GlobalEcho => Global != null && Global.Applied ? Global.Echo : null;

        public bool Includes(int userId)
        {
            return (Restriction == null || Restriction.Matches(userId)) && (UserFilter == null || UserFilter.Matches(userId));
        }
    }

    /// <summary>Who is signed in, as the directory knows them.</summary>
    internal static class PortalViewer
    {
        internal const string ObjectIdClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

        /// <summary>The Entra object id from the sign-in token, or <c>null</c>.</summary>
        internal static Guid? ObjectIdOf(IPrincipal principal)
        {
            if (!(principal is ClaimsPrincipal claims)) return null;

            var raw = claims.FindFirst(ObjectIdClaimType)?.Value ?? claims.FindFirst("oid")?.Value;
            return Guid.TryParse(raw?.Trim(), out var id) && id != Guid.Empty ? id : (Guid?)null;
        }

        /// <summary>
        /// The sign-in name from the token: <c>upn</c> in a v1 token, <c>preferred_username</c> in a v2 one,
        /// then the identity's name when it looks like an address.
        /// </summary>
        internal static string UserPrincipalNameOf(IPrincipal principal)
        {
            if (principal is ClaimsPrincipal claims)
            {
                foreach (var type in new[] { ClaimTypes.Upn, "upn", "preferred_username", "unique_name" })
                {
                    var value = claims.FindFirst(type)?.Value;
                    if (!string.IsNullOrWhiteSpace(value) && value.Contains("@")) return value.Trim();
                }
            }

            var name = principal?.Identity?.Name;
            return !string.IsNullOrWhiteSpace(name) && name.Contains("@") ? name.Trim() : null;
        }
    }

    /// <summary>The refusals a report gets when a filter applies but cannot be evaluated. Codes for the SPA, English for anyone else.</summary>
    internal static class ReportScopeFailure
    {
        /// <summary>The global filter could not be read from the database.</summary>
        internal const string FilterUnavailable = "globalFilterUnavailable";

        /// <summary>The stored global filter is not one this version can read.</summary>
        internal const string FilterInvalid = "globalFilterInvalid";

        /// <summary>The directory a filter is evaluated against could not be read.</summary>
        internal const string DirectoryUnavailable = "filterDirectoryUnavailable";

        internal const string FilterUnavailableMessage =
            "The administrator's report filter could not be read, so this report is not available right now. The failure has been logged. Try again shortly.";

        internal const string FilterInvalidMessage =
            "The administrator's report filter cannot be read by this version of the portal, so this report is not available. Ask a portal administrator to review the filter.";

        internal const string DirectoryUnavailableMessage =
            "The directory the report's filters are applied to could not be read, so the filtered report is not available right now. The failure has been logged. Try again shortly.";

        internal static HttpResponseException Exception(HttpRequestMessage request, string code, string message)
        {
            HttpResponseMessage response;
            if (request == null)
            {
                response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(message, Encoding.UTF8, "text/plain"),
                };
            }
            else if (PortalPermissionDenied.IsDocumentNavigation(request))
            {
                // An export link opened in a tab: the person who clicked has to be able to read it.
                response = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
                response.Content = new StringContent(message, Encoding.UTF8, "text/plain");
            }
            else
            {
                var json = (MediaTypeFormatter)request.GetConfiguration()?.Formatters.JsonFormatter ?? new JsonMediaTypeFormatter();
                response = request.CreateResponse(HttpStatusCode.ServiceUnavailable, new ApiErrorModel(message, code), json);
            }

            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
            return new HttpResponseException(response);
        }
    }

    /// <summary>
    /// Works out a request's <see cref="ReportScope"/>: every insights report calls it before reading data.
    /// </summary>
    /// <remarks>
    /// <para><b>Enforced here, not in the page.</b> The portal shows the global filter, but the server applies
    /// it to every report request whatever the page sends - a reader editing a URL or calling the API
    /// directly gets exactly the data the filter allows.</para>
    /// <para><b>Fails closed.</b> A global filter that cannot be read, cannot be parsed, or cannot be
    /// evaluated because the directory is unavailable refuses the report with a 503 rather than answering
    /// unfiltered. A condition on the viewer's own values that the directory cannot answer matches nobody
    /// (see <see cref="GlobalFilterResolver"/>).</para>
    /// <para><b>Administrators.</b> The filter applies to administrators too, so they see what everyone sees.
    /// One may switch it off for their own view with the <see cref="BypassCookie"/> cookie, which the portal
    /// sets from the filter bar; it is honoured only for a caller holding the Administration permission,
    /// and is a cookie so that export links - plain navigations that cannot send a header - follow it too.</para>
    /// <para><b>Free when unused.</b> With no global filter and no reader filter nothing is resolved and the
    /// directory is never read: a tenant that does not use the feature pays for one cached lookup.</para>
    /// </remarks>
    internal sealed class ReportScopeResolver
    {
        /// <summary>The cookie an administrator's portal sets to see reports without the global filter.</summary>
        internal const string BypassCookie = "GlobalFilterBypass";

        public static readonly ReportScopeResolver Default =
            new ReportScopeResolver(CachedGlobalFilterProvider.Default, CachedUserDirectorySource.Default);

        public ReportScopeResolver(IGlobalFilterProvider filters, IUserDirectorySource directory)
        {
            Filters = filters ?? throw new ArgumentNullException(nameof(filters));
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        internal IGlobalFilterProvider Filters { get; }

        internal IUserDirectorySource Directory { get; }

        /// <summary>The scope a report request may cover.</summary>
        /// <param name="userFilter">The reader's own filter, for the reports that take one; <c>null</c> otherwise.</param>
        /// <exception cref="HttpResponseException">503 - a filter applies but cannot be evaluated.</exception>
        public async Task<ReportScope> ResolveAsync(
            HttpRequestMessage request, IPrincipal principal, UserFilterExpression userFilter, CancellationToken cancellationToken)
        {
            var state = await ReadFilterAsync(request, cancellationToken).ConfigureAwait(false);
            var canBypass = PortalAccess.Evaluate(request, principal).Administration;
            var bypassed = state.IsDefined && canBypass && BypassRequested(request);

            var applyGlobal = state.IsDefined && !bypassed;
            var applyUser = userFilter != null && !userFilter.IsEmpty;

            var global = state.IsDefined
                ? new GlobalFilterApplication
                {
                    Defined = true,
                    Bypassed = bypassed,
                    CanBypass = canBypass,
                    Revision = state.Record.Revision,
                }
                : null;

            if (applyGlobal && state.ParseError != null)
            {
                throw ReportScopeFailure.Exception(request, ReportScopeFailure.FilterInvalid, ReportScopeFailure.FilterInvalidMessage);
            }

            if (!applyGlobal && !applyUser)
            {
                return global == null ? ReportScope.Unrestricted : new ReportScope(global, null, null, "all");
            }

            var snapshot = await ReadDirectoryAsync(request, cancellationToken).ConfigureAwait(false);

            CompiledUserFilter restriction = null;
            if (applyGlobal)
            {
                Evaluate(global, state.Definition, snapshot, principal);
                restriction = global.Compiled;
            }

            var compiledUser = applyUser ? UserFilterCompiler.Compile(userFilter, snapshot) : null;

            var key = "d:" + snapshot.LoadedUtc.Ticks
                + "|g:" + (applyGlobal ? global.Resolved.Key : string.Empty)
                + "|u:" + (applyUser ? UserFilterCodec.Serialize(userFilter) : string.Empty);

            return new ReportScope(global, restriction, compiledUser, key);
        }

        /// <summary>
        /// The global filter as it applies to the signed-in person - evaluated even when they have switched it
        /// off, so the filter bar can still show what everyone else sees. <c>null</c> when none is defined.
        /// </summary>
        public async Task<GlobalFilterApplication> DescribeAsync(
            HttpRequestMessage request, IPrincipal principal, CancellationToken cancellationToken)
        {
            var state = await ReadFilterAsync(request, cancellationToken).ConfigureAwait(false);
            if (!state.IsDefined) return null;

            var canBypass = PortalAccess.Evaluate(request, principal).Administration;
            var global = new GlobalFilterApplication
            {
                Defined = true,
                Bypassed = canBypass && BypassRequested(request),
                CanBypass = canBypass,
                Revision = state.Record.Revision,
            };

            if (state.ParseError != null) return global;

            var snapshot = await ReadDirectoryAsync(request, cancellationToken).ConfigureAwait(false);
            Evaluate(global, state.Definition, snapshot, principal);
            return global;
        }

        /// <summary>A draft definition evaluated for the signed-in person - the administration page's preview.</summary>
        public async Task<GlobalFilterApplication> PreviewAsync(
            HttpRequestMessage request, IPrincipal principal, GlobalFilterDefinition draft, CancellationToken cancellationToken)
        {
            var global = new GlobalFilterApplication { Defined = draft != null && !draft.IsEmpty, CanBypass = true };
            if (!global.Defined) return global;

            var snapshot = await ReadDirectoryAsync(request, cancellationToken).ConfigureAwait(false);
            Evaluate(global, draft, snapshot, principal);
            return global;
        }

        private static void Evaluate(GlobalFilterApplication global, GlobalFilterDefinition definition, UserDirectorySnapshot snapshot, IPrincipal principal)
        {
            int? viewerRow = null;
            if (snapshot.TryFindPerson(PortalViewer.ObjectIdOf(principal), PortalViewer.UserPrincipalNameOf(principal), out var row))
            {
                viewerRow = row;
            }

            global.Resolved = GlobalFilterResolver.Resolve(definition, snapshot, viewerRow);
            global.Compiled = UserFilterCompiler.Compile(global.Resolved.Expression, snapshot);
            global.Echo = GlobalFilterEcho.From(global.Resolved, global.Compiled, snapshot);
        }

        private async Task<GlobalFilterState> ReadFilterAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await Filters.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "GlobalFilter.Read");
                throw ReportScopeFailure.Exception(request, ReportScopeFailure.FilterUnavailable, ReportScopeFailure.FilterUnavailableMessage);
            }
        }

        private async Task<UserDirectorySnapshot> ReadDirectoryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await Directory.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                WebExceptionTelemetry.Report(ex, "GlobalFilter.Directory");
                throw ReportScopeFailure.Exception(request, ReportScopeFailure.DirectoryUnavailable, ReportScopeFailure.DirectoryUnavailableMessage);
            }
        }

        /// <summary>Whether the request carries the administrator's "switch it off for my view" cookie.</summary>
        internal static bool BypassRequested(HttpRequestMessage request)
        {
            if (request == null) return false;

            return request.Headers.GetCookies(BypassCookie)
                .SelectMany(c => c.Cookies)
                .Any(c => string.Equals(c.Name, BypassCookie, StringComparison.Ordinal)
                          && string.Equals(c.Value?.Trim(), "1", StringComparison.Ordinal));
        }
    }
}
