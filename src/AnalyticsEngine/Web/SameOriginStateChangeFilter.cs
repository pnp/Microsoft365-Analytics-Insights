using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Linq;
using System.Reflection;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// The site's cross-site request forgery (CSRF) defence for the API, applied to every action: a call that can
    /// change something - any method but GET, HEAD, OPTIONS and TRACE - must come from the portal's own scripts on this
    /// origin, by the test <see cref="RequireSameOriginXhrAttribute"/> describes. Registered globally by
    /// <see cref="CrossSiteRequestDefence.AddCrossSiteRequestDefence"/>, so an action added later is protected without
    /// anyone remembering to protect it.
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
    /// opt out - the filter ignores the attribute there - and <c>SameOriginStateChangeFilterTests</c> pins which actions
    /// carry it.
    /// </para>
    /// <para>
    /// net10: the .NET Framework build registers this as a Web API filter, which never runs for its MVC controllers. Here
    /// API and MVC controllers share one filter pipeline, so this is an MVC global filter that leaves alone any controller
    /// deriving from <see cref="Controller"/> - <c>AccountController</c> and <c>HomeController</c>, whose sign-in,
    /// sign-out and page loads are browser navigations that cannot carry the header - and checks every other controller:
    /// the API controllers, which derive from <see cref="ControllerBase"/> only, as the Web API build's
    /// <c>ApiController</c>s were ported.
    /// </para>
    /// <para>
    /// net10: <c>[Authorize]</c> is enforced by the authorization middleware before any MVC filter runs, so an anonymous
    /// call to an action that requires sign-in is answered 401 before this filter sees it, where the Web API build's
    /// global filter answered first with its 403. Neither runs the action.
    /// </para>
    /// </remarks>
    public sealed class SameOriginStateChangeFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (!AppliesTo(context.ActionDescriptor)) return;

            if (!IsAllowed(context.HttpContext.Request, context.ActionDescriptor))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            }
        }

        /// <summary>
        /// An API action: one on a controller that is not an MVC <see cref="Controller"/>, the kind the Web API build's
        /// filter covered.
        /// </summary>
        internal static bool AppliesTo(ActionDescriptor action) =>
            action is ControllerActionDescriptor controllerAction
            && !typeof(Controller).IsAssignableFrom(controllerAction.ControllerTypeInfo);

        internal static bool IsAllowed(HttpRequest request, ActionDescriptor action)
        {
            if (request == null) return false;
            if (!ChangesState(request.Method)) return true;
            if (IsExempt(action)) return true;
            return RequireSameOriginXhrAttribute.IsSameOriginPortalRequest(request);
        }

        /// <summary>Anything but the methods HTTP defines as safe, so a method nobody expected is checked too.</summary>
        internal static bool ChangesState(string method) =>
            !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

        /// <summary>Carries <see cref="AllowCrossSiteRequestsAttribute"/> and needs no sign-in.</summary>
        internal static bool IsExempt(ActionDescriptor action)
        {
            if (!(action is ControllerActionDescriptor controllerAction)) return false;
            var optedOut = controllerAction.MethodInfo.GetCustomAttributes<AllowCrossSiteRequestsAttribute>(true).Any()
                || controllerAction.ControllerTypeInfo.GetCustomAttributes<AllowCrossSiteRequestsAttribute>(true).Any();
            return optedOut && !RequiresSignIn(controllerAction);
        }

        internal static bool RequiresSignIn(ControllerActionDescriptor action)
        {
            if (action.MethodInfo.GetCustomAttributes<AllowAnonymousAttribute>(true).Any()) return false;
            return action.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(true).Any()
                || action.ControllerTypeInfo.GetCustomAttributes<AuthorizeAttribute>(true).Any();
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

    /// <summary>
    /// Registers <see cref="SameOriginStateChangeFilter"/>. The .NET Framework build's <c>WebApiConfig.AddCrossSiteRequestDefence</c>,
    /// shared by <c>Program</c> and the in-memory test hosts so the pipeline tested is the pipeline deployed.
    /// </summary>
    /// <remarks>
    /// It relies on the CORS policy never allowing credentials: <see cref="OrgUrlCorsPolicy.Configure"/>.
    /// </remarks>
    public static class CrossSiteRequestDefence
    {
        /// <summary>Adds the filter to every controller action, once however often it is called.</summary>
        public static MvcOptions AddCrossSiteRequestDefence(this MvcOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!options.Filters.Any(f => f is SameOriginStateChangeFilter))
            {
                options.Filters.Add(new SameOriginStateChangeFilter());
            }
            return options;
        }
    }
}