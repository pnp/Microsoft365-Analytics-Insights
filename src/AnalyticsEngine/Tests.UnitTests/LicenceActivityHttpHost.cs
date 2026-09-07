extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.LicenceActivity;
using Common.Entities.LicenceActivity;
using System;
using System.Collections.Generic;
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
    internal sealed class LicenceActivityHttpHost : IDisposable
    {
        private readonly HttpConfiguration _configuration;
        private readonly HttpServer _server;
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(4, 4);
        internal HttpClient Client { get; }

        internal LicenceActivityHttpHost(
            ILicenceActivityStore store, LicenceActivitySources sources, Func<DateTime> utcNow,
            Func<IPrincipal> principal = null, Action<LicenceActivityDiagnosticEvent> diagnostic = null)
        {
            var overview = new LicenceActivitySnapshotCache<LicenceActivityOverview>(16, TimeSpan.FromMinutes(5), _slots,
                utcNow, id => new LicenceActivityRunDiagnostics(id, item => { diagnostic?.Invoke(item); return true; }),
                reportFailure: (id, ex) => { });
            var users = new LicenceActivitySnapshotCache<LicenceActivityUsers>(32, TimeSpan.FromMinutes(2), _slots,
                utcNow, id => new LicenceActivityRunDiagnostics(id, item => { diagnostic?.Invoke(item); return true; }),
                reportFailure: (id, ex) => { });
            _configuration = new HttpConfiguration();
            _configuration.Services.Replace(typeof(IHttpControllerTypeResolver), new ControllerTypes());
            _configuration.Services.Replace(typeof(IHttpControllerActivator), new ControllerActivator(() =>
                new LicenceActivityAPIController(() => new LicenceActivityRequestContext("synthetic-scope", sources, store), overview, users)));
            _configuration.MessageHandlers.Add(new PrincipalHandler(principal ?? Administrator));
            _configuration.MapHttpAttributeRoutes();
            _server = new HttpServer(_configuration);
            Client = new HttpClient(_server) { BaseAddress = new Uri("http://localhost/") };
            Client.DefaultRequestHeaders.Add("Cookie", "ASP.NET_SessionId=synthetic-same-browser");
        }

        private static IPrincipal Administrator()
        {
            var identity = new ClaimsIdentity("synthetic-load-test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-administrator"));
            identity.AddClaim(new Claim(ClaimTypes.Role, LicenceActivityAPIController.UserDetailRole));
            return new ClaimsPrincipal(identity);
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            _configuration.Dispose();
            // Shared loads can outlive a cancelled HTTP caller. Do not dispose their semaphore
            // before those loads have released it; no wait handle is created by this host.
        }

        private sealed class ControllerTypes : IHttpControllerTypeResolver
        {
            public ICollection<Type> GetControllerTypes(IAssembliesResolver assembliesResolver) =>
                new[] { typeof(LicenceActivityAPIController) };
        }
        private sealed class ControllerActivator : IHttpControllerActivator
        {
            private readonly Func<IHttpController> _create;
            internal ControllerActivator(Func<IHttpController> create) { _create = create; }
            public IHttpController Create(HttpRequestMessage request, HttpControllerDescriptor controllerDescriptor, Type controllerType) => _create();
        }
        private sealed class PrincipalHandler : DelegatingHandler
        {
            private readonly Func<IPrincipal> _principal;
            internal PrincipalHandler(Func<IPrincipal> principal) { _principal = principal; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.GetRequestContext().Principal = _principal();
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
