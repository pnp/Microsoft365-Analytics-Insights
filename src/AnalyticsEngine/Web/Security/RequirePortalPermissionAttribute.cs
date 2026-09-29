using System;
using System.Web.Http.Controllers;
using System.Web.Http.Filters;

namespace Web.AnalyticsWeb.Security
{
    /// <summary>
    /// Refuses a Web API call with 403 unless the signed-in caller holds <see cref="Permission"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sits beside <c>[Authorize]</c>, never instead of it. An anonymous caller is left alone here so that
    /// <c>[Authorize]</c> answers 401 and the SPA can re-authenticate; a signed-in caller without the
    /// permission gets 403, which the OIDC middleware leaves alone - a 401 would be turned into a sign-in
    /// redirect, and signing in again cannot grant a role.
    /// </para>
    /// <para>
    /// Stackable: an endpoint that is both administrative and per-person (User lookup) carries one
    /// attribute per permission and needs both. <see cref="AllowMultiple"/> is overridden because Web API
    /// otherwise keeps only the most specific instance of a filter type, which would silently drop a
    /// controller-level requirement whenever an action added its own.
    /// </para>
    /// <para>
    /// Every Web API action must be classified - by this attribute, or by an entry in
    /// <c>PortalPermissionTests.ExpectedAccess</c> recording that it is open to every signed-in user - so a new
    /// endpoint cannot ship without someone deciding who may call it.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class RequirePortalPermissionAttribute : AuthorizationFilterAttribute
    {
        public RequirePortalPermissionAttribute(PortalPermission permission)
        {
            Permission = permission;
        }

        public PortalPermission Permission { get; }

        public override bool AllowMultiple => true;

        public override void OnAuthorization(HttpActionContext actionContext)
        {
            var principal = actionContext?.RequestContext?.Principal;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return;
            }

            if (PortalAccess.Evaluate(actionContext.Request, principal).Has(Permission))
            {
                return;
            }

            actionContext.Response = PortalPermissionDenied.Response(actionContext.Request, Permission);
        }
    }
}
