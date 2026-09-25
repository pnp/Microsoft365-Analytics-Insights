using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// How the host serves the portal: its static assets from wwwroot, and its page - index.html -
    /// only ever through <see cref="Controllers.HomeController"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// index.html is not a plain static file. It carries the <c>__BuildLabel__</c> placeholder that
    /// <c>HomeController.InjectBuildLabel</c> replaces with the running build's label, which the SPA
    /// prints in the footer of every printed report, and HomeController is also the
    /// <c>[Authorize]</c>d gate in front of the page. Any other route to the file serves it raw: the
    /// page still loads and still prints, the footer quietly says "(development build)" whatever build
    /// is running, and nothing fails. On the Web API 2 build the page is only ever reached through
    /// HomeController.
    /// </para>
    /// <para>
    /// This host had two such routes: <c>MapFallbackToFile("index.html")</c>, which answered every
    /// unmatched path with the raw file (to anonymous callers too), and <c>UseStaticFiles</c>, which
    /// serves <c>/index.html</c> itself. Both now go to HomeController. Shared by Program.cs and the
    /// tests, so the pipeline tested is the pipeline deployed.
    /// </para>
    /// </remarks>
    public static class PortalHosting
    {
        /// <summary>
        /// <c>UseStaticFiles</c> for the portal's assets, with a request for the page itself handed on to
        /// routing - and so to HomeController - instead of being served as a file.
        /// </summary>
        public static IApplicationBuilder UsePortalStaticFiles(this IApplicationBuilder app)
        {
            app.Use((context, next) =>
            {
                if (IsPortalPageFileRequest(context.Request.Path)) context.Request.Path = "/";
                return next(context);
            });

            return app.UseStaticFiles();
        }

        /// <summary>
        /// The routes carried across from RouteConfig - attribute routes on the API controllers, then the
        /// two conventional MVC routes the sign-in pages rely on - and a fallback that serves the portal
        /// page through HomeController rather than from disk.
        /// </summary>
        public static IEndpointRouteBuilder MapPortalRoutes(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapControllers();
            endpoints.MapControllerRoute(name: "Account", pattern: "Account/{action=Index}/{id?}", defaults: new { controller = "Account" });
            endpoints.MapControllerRoute(name: "Home", pattern: "{action=Index}/{id?}", defaults: new { controller = "Home" });

            // Anything not matched above - a deep link or a refresh - gets the portal page, from the same
            // action as "/" so it is stamped and authorised like every other load. (The SPA's HashRouter
            // keeps its routes in the fragment, so in practice this is rarely reached.)
            endpoints.MapFallbackToController("Index", "Home");
            return endpoints;
        }

        /// <summary>A request for index.html by its file name, in any case (static files are case-insensitive on Windows).</summary>
        internal static bool IsPortalPageFileRequest(PathString path)
        {
            return path.Equals(new PathString("/index.html"), StringComparison.OrdinalIgnoreCase);
        }
    }
}
