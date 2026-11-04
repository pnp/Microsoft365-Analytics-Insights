using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Filters;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// The site's cross-site request forgery (CSRF) defence for the Web API, applied to every action: a call that can
    /// change something - any method but GET, HEAD, OPTIONS and TRACE - must come from the portal's own scripts on this
    /// origin, by the test <see cref="RequireSameOriginXhrAttribute"/> describes. Registered globally in
    /// <see cref="WebApiConfig"/>, so an action added later is protected without anyone remembering to protect it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The API authenticates with the site's cookie, which a browser attaches whichever page started the request. Before
    /// this filter, each state-changing action had to carry <see cref="RequireSameOriginXhrAttribute"/>, and some did
    /// not: <c>PUT api/TeamsAuthAPI</c> stored and deleted per-Team refresh tokens relying only on browsers refusing a
    /// cross-site PUT without a CORS preflight. The attribute is still valid on an action, and redundant with this.
    /// </para>
    /// <para>
    /// The portal's <c>apiFetch</c> sends <c>X-Requested-With</c> on every call, so the portal never meets the refusal,
    /// a bare 403 that runs nothing.
    /// </para>
    /// <para>
    /// Only the deliberately anonymous endpoints opt out, with <see cref="AllowCrossSiteRequestsAttribute"/>: other
    /// systems call them from other origins, and they act on no signed-in session. An action that requires sign-in can't
    /// opt out - the filter ignores the attribute there - and <c>PortalPermissionTests</c> pins which actions carry it.
    /// </para>
    /// </remarks>
    public sealed class SameOriginStateChangeFilter : AuthorizationFilterAttribute
    {
        public override bool AllowMultiple => false;

        public override void OnAuthorization(HttpActionContext actionContext)
        {
            if (!IsAllowed(actionContext.Request, actionContext.ActionDescriptor))
            {
                actionContext.Response = actionContext.Request.CreateResponse(HttpStatusCode.Forbidden);
            }
        }

        internal static bool IsAllowed(HttpRequestMessage request, HttpActionDescriptor action)
        {
            if (request == null) return false;
            if (!ChangesState(request.Method)) return true;
            if (IsExempt(action)) return true;
            return RequireSameOriginXhrAttribute.IsSameOriginScriptRequest(request);
        }

        /// <summary>Anything but the methods HTTP defines as safe, so a method nobody expected is checked too.</summary>
        internal static bool ChangesState(HttpMethod method) =>
            method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;

        /// <summary>Carries <see cref="AllowCrossSiteRequestsAttribute"/> and needs no sign-in.</summary>
        internal static bool IsExempt(HttpActionDescriptor action)
        {
            if (action == null) return false;
            var optedOut = action.GetCustomAttributes<AllowCrossSiteRequestsAttribute>().Any()
                || action.ControllerDescriptor?.GetCustomAttributes<AllowCrossSiteRequestsAttribute>().Any() == true;
            return optedOut && !RequiresSignIn(action);
        }

        internal static bool RequiresSignIn(HttpActionDescriptor action)
        {
            if (action.GetCustomAttributes<AllowAnonymousAttribute>().Any()) return false;
            return action.GetCustomAttributes<AuthorizeAttribute>().Any()
                || action.ControllerDescriptor?.GetCustomAttributes<AuthorizeAttribute>().Any() == true;
        }
    }

    /// <summary>
    /// Lets an anonymous endpoint that another system calls from another origin take a state-changing request without
    /// <see cref="SameOriginStateChangeFilter"/>'s check. Only for endpoints that act on no signed-in session: it has no
    /// effect on an action that requires sign-in.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class AllowCrossSiteRequestsAttribute : Attribute
    {
        public AllowCrossSiteRequestsAttribute(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the endpoint is called cross-site.", nameof(reason));
            Reason = reason;
        }

        /// <summary>Why the endpoint takes requests from other origins.</summary>
        public string Reason { get; }
    }
}
