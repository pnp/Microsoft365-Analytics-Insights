using Microsoft.AspNetCore.Mvc.Filters;
using System;

namespace Web.AnalyticsWeb.Security
{
    /// <summary>
    /// Refuses an API or MVC call with 403 unless the signed-in caller holds <see cref="Permission"/>.
    /// </summary>
    /// <remarks>
    /// Sits beside <c>[Authorize]</c>, never instead of it. Anonymous callers are left to the normal
    /// authorization middleware, which answers 401; signed-in callers without the role receive the
    /// portal's stable 403 contract. Multiple instances can be stacked when an action needs both roles.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public class RequirePortalPermissionAttribute : Attribute, IAuthorizationFilter
    {
        public RequirePortalPermissionAttribute(PortalPermission permission)
        {
            Permission = permission;
        }

        public PortalPermission Permission { get; }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var principal = context?.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return;
            }

            if (PortalAccess.Evaluate(context.HttpContext.Request, principal).Has(Permission))
            {
                return;
            }

            context.Result = PortalPermissionDenied.Result(context.HttpContext.Request, Permission);
        }
    }

    /// <summary>
    /// Compatibility name for the account-controller annotation used by the .NET Framework branch.
    /// ASP.NET Core has one filter pipeline, so it delegates to the same implementation here.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class RequirePortalMvcPermissionAttribute : RequirePortalPermissionAttribute
    {
        public RequirePortalMvcPermissionAttribute(PortalPermission permission) : base(permission)
        {
        }
    }
}
