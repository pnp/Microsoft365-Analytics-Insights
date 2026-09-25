extern alias AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.LicenceActivity;
using Common.Entities.LicenceActivity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Hosts <see cref="LicenceActivityAPIController"/> in an in-memory HTTP pipeline so the licence
    /// activity API can be tested through real routing, model binding, authorization and response
    /// headers rather than by calling the controller's methods directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be a Web API 2 <c>HttpServer</c> built from an <c>HttpConfiguration</c>. That type
    /// does not exist on ASP.NET Core, which is why these tests were excluded from the build during the
    /// .NET 10 port; this is the ASP.NET Core <c>TestServer</c> equivalent.
    /// </para>
    /// <para>
    /// Testing through the pipeline is the point, not incidental. The authorization filter is what
    /// turns an anonymous caller into a 401, and model binding is what rejects an unparseable date
    /// before the action ever runs - neither is reachable by calling the controller's methods
    /// directly, and both changed shape between Web API 2 and ASP.NET Core. (The 404, the page-size
    /// 400 and the <c>Cache-Control</c> header do come from the controller body, so those parts of
    /// these tests would survive a simpler harness; the auth and binding ones would not.)
    /// </para>
    /// </remarks>
    internal sealed class LicenceActivityHttpHost : IDisposable
    {
        private readonly IHost _host;
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

            // Resolved per request, not captured once: several tests change the caller's identity
            // part-way through (anonymous first, then signed in) against the same host.
            var principalSource = principal ?? Administrator;

            _host = new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services
                            .AddControllers()
                            // The serializer production uses (Program.cs), so the bytes asserted here
                            // are the bytes the portal receives.
                            .AddWebApiCompatibleJson()
                            // Only this one controller. The Web assembly's other controllers would
                            // otherwise be discovered too, and they reach for configuration and a
                            // database that this test has deliberately not set up.
                            .ConfigureApplicationPartManager(parts =>
                            {
                                parts.ApplicationParts.Clear();
                                parts.ApplicationParts.Add(new AssemblyPart(typeof(LicenceActivityAPIController).Assembly));
                                parts.FeatureProviders.Clear();
                                parts.FeatureProviders.Add(new OnlyLicenceActivityController());
                            })
                            // Makes the controller come from DI, so the test's fake store and caches
                            // can be injected through the internal constructor.
                            .AddControllersAsServices();

                        services.AddScoped(_ => new LicenceActivityAPIController(
                            () => new LicenceActivityRequestContext("synthetic-scope", sources, store), overview, users));

                        services.AddAuthentication(TestScheme)
                            .AddScheme<AuthenticationSchemeOptions, SuppliedPrincipalHandler>(TestScheme, null);
                        services.AddSingleton(new PrincipalAccessor(principalSource));
                        services.AddAuthorization();
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .Start();

            Client = _host.GetTestClient();
            Client.BaseAddress = new Uri("http://localhost/");
            Client.DefaultRequestHeaders.Add("Cookie", "ASP.NET_SessionId=synthetic-same-browser");
        }

        private const string TestScheme = "synthetic-load-test";

        private static IPrincipal Administrator()
        {
            var identity = new ClaimsIdentity(TestScheme);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-administrator"));
            return new ClaimsPrincipal(identity);
        }

        public void Dispose()
        {
            Client.Dispose();
            _host.Dispose();
            // Shared loads can outlive a cancelled HTTP caller. Do not dispose their semaphore
            // before those loads have released it; no wait handle is created by this host.
        }

        /// <summary>Carries the test's principal callback into the authentication handler.</summary>
        private sealed class PrincipalAccessor
        {
            internal PrincipalAccessor(Func<IPrincipal> principal) { Principal = principal; }
            internal Func<IPrincipal> Principal { get; }
        }

        /// <summary>
        /// Authenticates as whatever principal the test currently supplies.
        /// </summary>
        /// <remarks>
        /// Returning <see cref="AuthenticateResult.NoResult"/> for an unauthenticated identity is what
        /// makes <c>[Authorize]</c> produce a 401, which is the behaviour the anonymous-caller test
        /// asserts. Web API 2 reached the same outcome by assigning the request's principal directly.
        /// </remarks>
        private sealed class SuppliedPrincipalHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            private readonly PrincipalAccessor _accessor;

            public SuppliedPrincipalHandler(
                IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
                UrlEncoder encoder, PrincipalAccessor accessor)
                : base(options, logger, encoder)
            {
                _accessor = accessor;
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var current = _accessor.Principal();
                if (current?.Identity == null || !current.Identity.IsAuthenticated)
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var claimsPrincipal = current as ClaimsPrincipal ?? new ClaimsPrincipal(current.Identity);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(claimsPrincipal, Scheme.Name)));
            }
        }

        /// <summary>Restricts controller discovery to the one controller under test.</summary>
        private sealed class OnlyLicenceActivityController : IApplicationFeatureProvider<ControllerFeature>
        {
            public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
            {
                feature.Controllers.Add(typeof(LicenceActivityAPIController).GetTypeInfo());
            }
        }
    }
}
