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

            // Exception type, message and stack trace in a 500 only for a request from this machine. The default
            // policy follows <customErrors>, which the site has always shipped Off, so every caller - including the
            // anonymous webhook and tracker endpoints - was sent the full exception. The details still reach
            // Application Insights through the logger above.
            config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.LocalOnly;

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
