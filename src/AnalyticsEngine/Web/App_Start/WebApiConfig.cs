using System.Web.Http;
using System.Web.Http.ExceptionHandling;

namespace Web.AnalyticsWeb
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            config.MapHttpAttributeRoutes();

            // Without this, an exception escaping a controller becomes a bare 500 with no telemetry
            // anywhere - which is precisely why the fault behind issue #360 could not be found.
            config.Services.Add(typeof(IExceptionLogger), new AnalyticsWebApiExceptionLogger());

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
