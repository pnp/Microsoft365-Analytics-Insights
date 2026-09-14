using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Tests.UnitTests.FakeControllers;

namespace Tests.UnitTests
{
    /// <summary>
    /// Hosts the project's fake Office 365 / Graph controllers in memory and hands out an
    /// <see cref="HttpMessageHandler"/> that the importers can be pointed at instead of the real
    /// service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces Web API 2's <c>HttpServer</c>, which was the last thing keeping
    /// <c>Microsoft.AspNet.WebApi</c> - a .NET Framework package - in a net10.0 project. NuGet restored
    /// it under a net48 target and warned (NU1701) that it "may not be fully compatible"; it worked, but
    /// a Framework-only package inside a .NET 10 app is exactly the kind of thing that works right up
    /// until it doesn't.
    /// </para>
    /// <para>
    /// <b>Serialisation is deliberately Newtonsoft, not System.Text.Json.</b> The DTOs these fakes
    /// return carry <c>[JsonProperty]</c> attributes (<c>CallRecordDTO.Organizer</c> is wire-named
    /// <c>organizer</c>, and so on) and the importer deserialises them with Newtonsoft. ASP.NET Core
    /// defaults to System.Text.Json, which ignores those attributes - so switching hosts without also
    /// switching the formatter would silently rename fields on the wire and change what the tests
    /// actually exercise. <c>AddNewtonsoftJson</c> keeps the bytes identical to what Web API 2 produced.
    /// </para>
    /// <para>
    /// Only the fake controllers are registered. Discovery is otherwise assembly-wide, and this test
    /// assembly also references the portal, whose controllers reach for configuration and a database
    /// that these tests have not set up.
    /// </para>
    /// </remarks>
    internal sealed class FakeApiHost : IDisposable
    {
        private readonly IHost _host;

        /// <summary>The handler to give an importer in place of a real HTTP stack.</summary>
        internal HttpMessageHandler Handler { get; }

        internal FakeApiHost()
        {
            _host = new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services
                            .AddControllers()
                            .ConfigureApplicationPartManager(parts =>
                            {
                                parts.ApplicationParts.Clear();
                                parts.ApplicationParts.Add(new AssemblyPart(typeof(FakeApiHost).Assembly));
                                parts.FeatureProviders.Clear();
                                parts.FeatureProviders.Add(new OnlyFakeControllers());
                            })
                            .AddNewtonsoftJson();
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .Start();

            Handler = _host.GetTestServer().CreateHandler();
        }

        public void Dispose()
        {
            Handler.Dispose();
            _host.Dispose();
        }

        /// <summary>Restricts controller discovery to this project's fakes.</summary>
        private sealed class OnlyFakeControllers : IApplicationFeatureProvider<ControllerFeature>
        {
            private static readonly Type[] Fakes =
            {
                typeof(Office365FakeController),
                typeof(FakePageableResultsController),
                typeof(FakeCallsController),
            };

            public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
            {
                foreach (var fake in Fakes.Select(f => f.GetTypeInfo()))
                {
                    feature.Controllers.Add(fake);
                }
            }
        }
    }
}
