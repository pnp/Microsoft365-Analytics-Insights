using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UsageReporting;
using Web.Auth;
using Web.Startup;

namespace Tests.Unit;

/// <summary>
/// Hosts the real telemetry service in-process so the wiring itself can be tested.
///
/// Everything the production composition root builds is kept, with three deliberate substitutions:
/// the Cosmos-backed adaptors become <see cref="FakeTelemetryStore"/>, the Cosmos schema initialiser
/// is removed, and JWT bearer validation is replaced by a header-driven test scheme. What remains
/// under test is exactly what unit tests cannot reach - routing, the authorisation policy wiring,
/// model binding, and the DI graph - running as it does in Azure.
/// </summary>
internal sealed class TelemetryWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>The shared secret uploads are signed with. Matches <c>TelemetrySecret</c> below.</summary>
    public const string SharedSecret = "integration-test-shared-secret";

    public const string TenantId = "00000000-0000-0000-0000-000000000000";
    public const string ClientId = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Configuration is injected through environment variables rather than
    /// <c>ConfigureAppConfiguration</c>, which does not work for this application.
    ///
    /// <c>Program.cs</c> reads <c>builder.Configuration</c> during service registration -
    /// <c>AddTelemetryServices</c> constructs <c>WebAppConfig</c>, which throws on a missing
    /// required value - and that happens inside <c>Main</c>, before WebApplicationFactory's
    /// <c>ConfigureAppConfiguration</c> callbacks are applied at <c>Build()</c>. Environment
    /// variables are read by <c>WebApplication.CreateBuilder</c> itself, so they are the only
    /// injection point early enough.
    ///
    /// Set once per process from a static constructor: every test wants the same values, and the
    /// assembly parallelises at method level so per-instance mutation would race.
    /// </summary>
    static TelemetryWebAppFactory()
    {
        // "__" is the .NET convention for nested configuration keys - the same form the Bicep
        // template uses for the deployed app settings.
        var settings = new Dictionary<string, string>
        {
            // Not "Development": that environment starts the Vite dev server through
            // Microsoft.AspNetCore.SpaProxy and maps the OpenAPI endpoint, neither of which belongs
            // in a test run - and the SPA proxy would block waiting for a server never coming.
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",

            ["TelemetrySecret"] = SharedSecret,

            // WebAppConfig treats these as required and refuses to construct without them.
            // Nothing ever connects: both adaptors are replaced in ConfigureTestServices.
            ["CosmosDb__AccountEndpoint"] = "https://telemetry-integration-tests.documents.azure.com:443/",
            ["CosmosDb__DatabaseName"] = "telemetry-tests",
            ["CosmosDb__ContainerNameCurrent"] = "current",
            ["CosmosDb__ContainerNameHistory"] = "history",

            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId,
            ["AzureAd__Scopes"] = DashboardAuthorization.RequiredScope,

            // Read straight through, so a record seeded by a test is visible to the very next
            // request rather than hidden behind the 60s dashboard cache.
            ["DashboardCacheSeconds"] = "0",
        };

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>Stands in for Cosmos. Seed it before a request, assert against it afterwards.</summary>
    public FakeTelemetryStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Would reach for Cosmos on startup. It swallows the failure, but only after the SDK has
            // exhausted its retries, so leaving it in makes every test pay that timeout.
            var schemaInitialiser = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(CosmosSchemaInitializer));
            if (schemaInitialiser != null)
            {
                services.Remove(schemaInitialiser);
            }

            // Registered last, so these win for both interfaces. StatsSaveService and
            // DashboardService are untouched and still the real implementations.
            services.AddSingleton(Store);
            services.AddSingleton<ITelemetrySaveAdaptor>(Store);
            services.AddSingleton<ITelemetryQueryAdaptor>(Store);

            // Overrides the JwtBearer default scheme set in Program.cs. ConfigureTestServices runs
            // after the app's own registrations, so this configuration of AuthenticationOptions wins.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>A client that surfaces a redirect as a redirect instead of quietly following it.</summary>
    public HttpClient CreateApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

/// <summary>
/// Stands in for JWT bearer validation. The caller's claims come from request headers so a test can
/// describe the principal it wants - "has the role but not the scope" - without minting real Entra
/// tokens or embedding signing keys in the repository.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IntegrationTest";
    public const string RolesHeader = "X-Test-Roles";
    public const string ScopesHeader = "X-Test-Scopes";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Neither header present models a request carrying no bearer token at all. NoResult - rather
        // than Fail - is what an absent token produces, so the pipeline challenges and returns 401.
        if (!Request.Headers.ContainsKey(RolesHeader) && !Request.Headers.ContainsKey(ScopesHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "integration-test-user"),
            new("oid", "22222222-2222-2222-2222-222222222222"),
        };

        foreach (var role in Split(Request.Headers[RolesHeader].ToString()))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var scopes = Request.Headers[ScopesHeader].ToString();
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            // "scp" is what Entra puts in an access token, and the claim Microsoft.Identity.Web's
            // RequiredScopeAttribute looks for first.
            claims.Add(new Claim("scp", scopes));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Builds requests describing the caller, so each test reads as the case it covers.</summary>
internal static class TestRequests
{
    /// <summary>Omit both arguments for an anonymous caller; omit one to leave that claim off.</summary>
    public static HttpRequestMessage Get(string url, string? roles = null, string? scopes = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (roles != null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (scopes != null)
        {
            request.Headers.Add(TestAuthHandler.ScopesHeader, scopes);
        }

        return request;
    }
}
