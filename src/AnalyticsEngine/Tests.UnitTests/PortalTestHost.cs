extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// An in-memory ASP.NET Core host for selected portal controllers, with both the caller and the
    /// portal role-enforcement policy under the test's control.
    /// </summary>
    internal sealed class PortalTestHost : IDisposable
    {
        private const string TestScheme = "synthetic-portal-test";
        private readonly IHost _host;
        private readonly PrincipalAccessor _principal;

        internal HttpClient Client { get; }

        internal IPrincipal Principal
        {
            get => _principal.Current;
            set => _principal.Current = value;
        }

        internal PortalTestHost(
            IEnumerable<Type> controllers,
            IPrincipal principal,
            PortalAccessPolicy policy,
            Func<Type, ControllerBase> create = null)
        {
            var selected = controllers.ToList();
            _principal = new PrincipalAccessor(principal);

            _host = new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services
                            .AddControllers()
                            .AddWebApiCompatibleJson()
                            .ConfigureApplicationPartManager(parts =>
                            {
                                parts.ApplicationParts.Clear();
                                parts.ApplicationParts.Add(new AssemblyPart(typeof(PortalPermission).Assembly));
                                parts.FeatureProviders.Clear();
                                parts.FeatureProviders.Add(new SelectedControllers(selected));
                            })
                            .AddControllersAsServices();

                        foreach (var controller in selected)
                        {
                            services.AddScoped(controller, provider =>
                                create?.Invoke(controller)
                                ?? (ControllerBase)ActivatorUtilities.CreateInstance(provider, controller));
                        }

                        services.AddSingleton(_principal);
                        services.AddAuthentication(TestScheme)
                            .AddScheme<AuthenticationSchemeOptions, SuppliedPrincipalHandler>(TestScheme, null);
                        services.AddAuthorization();
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.Use(async (context, next) =>
                        {
                            context.Items[typeof(PortalAccessPolicy)] = policy;
                            await next();
                        });
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .Start();

            Client = _host.GetTestClient();
            Client.BaseAddress = new Uri("http://localhost/");
        }

        /// <summary>Every API controller the portal ships, excluding the server-rendered MVC controllers.</summary>
        internal static IReadOnlyList<Type> WebControllers() =>
            typeof(PortalPermission).Assembly.GetTypes()
                .Where(t => t.IsClass
                    && !t.IsAbstract
                    && typeof(ControllerBase).IsAssignableFrom(t)
                    && (t.Name.EndsWith("APIController", StringComparison.Ordinal)
                        || t.Name.EndsWith("WebhookController", StringComparison.Ordinal)
                        || t.Name == "ImportConfigController"))
                .ToList();

        internal static IPrincipal Anonymous() =>
            new GenericPrincipal(new GenericIdentity(string.Empty), Array.Empty<string>());

        internal static IPrincipal SignedIn(params string[] roles)
        {
            var identity = new ClaimsIdentity(TestScheme);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-portal-user"));
            foreach (var role in roles)
            {
                identity.AddClaim(new Claim("roles", role));
            }
            return new ClaimsPrincipal(identity);
        }

        public void Dispose()
        {
            Client.Dispose();
            _host.Dispose();
        }

        private sealed class PrincipalAccessor
        {
            internal PrincipalAccessor(IPrincipal current) { Current = current; }
            internal IPrincipal Current { get; set; }
        }

        private sealed class SuppliedPrincipalHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            private readonly PrincipalAccessor _accessor;

            public SuppliedPrincipalHandler(
                IOptionsMonitor<AuthenticationSchemeOptions> options,
                ILoggerFactory logger,
                UrlEncoder encoder,
                PrincipalAccessor accessor)
                : base(options, logger, encoder)
            {
                _accessor = accessor;
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var current = _accessor.Current;
                if (current?.Identity?.IsAuthenticated != true)
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var claims = current as ClaimsPrincipal ?? new ClaimsPrincipal(current.Identity);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(claims, Scheme.Name)));
            }
        }

        private sealed class SelectedControllers : IApplicationFeatureProvider<ControllerFeature>
        {
            private readonly IReadOnlyList<Type> _controllers;

            internal SelectedControllers(IReadOnlyList<Type> controllers)
            {
                _controllers = controllers;
            }

            public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
            {
                foreach (var controller in _controllers)
                {
                    feature.Controllers.Add(controller.GetTypeInfo());
                }
            }
        }
    }
}
