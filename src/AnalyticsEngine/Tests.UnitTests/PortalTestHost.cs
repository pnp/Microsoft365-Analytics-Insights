extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Dispatcher;

namespace Tests.UnitTests
{
    /// <summary>
    /// An in-memory Web API host for the portal's own controllers, with the caller's identity and the
    /// portal's role-enforcement policy under the test's control (#660, #661).
    /// </summary>
    /// <remarks>
    /// Routes are registered the way <c>WebApiConfig</c> registers them - attribute routes plus the
    /// conventional <c>api/{controller}/{id}</c> route that <c>SiteTokenAPI</c> and <c>TeamsAuthAPI</c> rely
    /// on - so a URL resolves to the same action it does in production. Only the controller types handed
    /// in are visible, which keeps the fake controllers other tests define out of the route table.
    /// </remarks>
    internal sealed class PortalTestHost : IDisposable
    {
        private readonly HttpConfiguration _configuration;
        private readonly HttpServer _server;

        internal HttpClient Client { get; }

        /// <summary>Who the next request is from. Read per request, so a test can switch users mid-way.</summary>
        internal IPrincipal Principal { get; set; }

        internal PortalTestHost(
            IEnumerable<Type> controllers,
            IPrincipal principal,
            PortalAccessPolicy policy,
            Func<Type, IHttpController> create = null)
        {
            Principal = principal;
            _configuration = new HttpConfiguration();
            _configuration.Properties[typeof(PortalAccessPolicy)] = policy;
            _configuration.Services.Replace(typeof(IHttpControllerTypeResolver), new ControllerTypes(controllers.ToList()));
            _configuration.Services.Replace(typeof(IHttpControllerActivator), new ControllerActivator(create));
            _configuration.MessageHandlers.Add(new PrincipalHandler(() => Principal));
            _configuration.MapHttpAttributeRoutes();
            _configuration.Routes.MapHttpRoute("DefaultApi", "api/{controller}/{id}", new { id = RouteParameter.Optional });
            _configuration.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always;
            _server = new HttpServer(_configuration);
            Client = new HttpClient(_server) { BaseAddress = new Uri("http://localhost/") };
        }

        /// <summary>Every Web API controller the portal ships.</summary>
        internal static IReadOnlyList<Type> WebControllers() =>
            typeof(PortalPermission).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ApiController).IsAssignableFrom(t))
                .ToList();

        internal static IPrincipal Anonymous() => new GenericPrincipal(new GenericIdentity(string.Empty), new string[0]);

        /// <summary>A signed-in user holding the given app roles, carried the way the ID token names them.</summary>
        internal static IPrincipal SignedIn(params string[] roles)
        {
            var identity = new ClaimsIdentity("synthetic-test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-portal-user"));
            foreach (var role in roles) identity.AddClaim(new Claim("roles", role));
            return new ClaimsPrincipal(identity);
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            _configuration.Dispose();
        }

        private sealed class ControllerTypes : IHttpControllerTypeResolver
        {
            private readonly ICollection<Type> _types;
            internal ControllerTypes(ICollection<Type> types) { _types = types; }
            public ICollection<Type> GetControllerTypes(IAssembliesResolver assembliesResolver) => _types;
        }

        private sealed class ControllerActivator : IHttpControllerActivator
        {
            private readonly Func<Type, IHttpController> _create;
            internal ControllerActivator(Func<Type, IHttpController> create) { _create = create; }

            public IHttpController Create(HttpRequestMessage request, HttpControllerDescriptor controllerDescriptor, Type controllerType)
                => _create?.Invoke(controllerType) ?? (IHttpController)Activator.CreateInstance(controllerType, nonPublic: true);
        }

        private sealed class PrincipalHandler : DelegatingHandler
        {
            private readonly Func<IPrincipal> _principal;
            internal PrincipalHandler(Func<IPrincipal> principal) { _principal = principal; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var principal = _principal();
                request.GetRequestContext().Principal = principal;

                // Code that reads ClaimsPrincipal.Current sees the signed-in user in production because ASP.NET
                // sets Thread.CurrentPrincipal. Mirror that here, and put it back so it cannot leak into another test.
                var previous = Thread.CurrentPrincipal;
                Thread.CurrentPrincipal = principal;
                try
                {
                    return await base.SendAsync(request, cancellationToken);
                }
                finally
                {
                    Thread.CurrentPrincipal = previous;
                }
            }
        }
    }
}
