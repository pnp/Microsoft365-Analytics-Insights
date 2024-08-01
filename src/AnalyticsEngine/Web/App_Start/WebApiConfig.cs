using System.Web.Http;
using System.Web.Http.Cors;

namespace Web.AnalyticsWeb
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            config.MapHttpAttributeRoutes();

            config.SetCorsPolicyProviderFactory(new AllowCorsForOrgUrlsFactory());
            config.EnableCors();

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );
        }
    }
}
