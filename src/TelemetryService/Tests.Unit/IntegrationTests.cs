using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UsageReporting;
using Web.Auth;
using Web.Config;
using Web.Controllers;
using Web.Dashboard;
using Web.Startup;

namespace Tests.Unit;

/// <summary>
/// End-to-end tests over the real HTTP pipeline (see <see cref="TelemetryWebAppFactory"/>).
///
/// These cover what the unit tests structurally cannot: that the endpoints are actually reachable at
/// the routes the client calls, and that the authorisation attributes are wired to a pipeline that
/// enforces them. #264 was an auth failure that every unit test passed straight through.
///
/// Each test builds its own host because the assembly parallelises at method level and the fake
/// store is shared mutable state.
/// </summary>
[TestClass]
public class AnonymousEndpointTests
{
    [TestMethod]
    public async Task Health_RequiresNoToken()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "/health is what the deployment workflow polls to decide a release is good. If it ever " +
            "starts requiring auth, every deploy fails its verification step.");
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Healthy");
    }

    [TestMethod]
    public async Task AuthConfig_RequiresNoToken()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var response = await client.GetAsync("/api/auth/config");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "The SPA reads this before it can sign anyone in, so it cannot itself require a token.");
    }

    [TestMethod]
    public async Task AuthConfig_ReturnsTheValuesTheSpaNeedsToSignIn()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var config = await client.GetFromJsonAsync<AuthClientConfig>("/api/auth/config");

        Assert.IsNotNull(config);
        Assert.AreEqual($"https://login.microsoftonline.com/{TelemetryWebAppFactory.TenantId}", config.Authority);
        Assert.AreEqual(TelemetryWebAppFactory.ClientId, config.ClientId);
        Assert.AreEqual($"api://{TelemetryWebAppFactory.ClientId}/{DashboardAuthorization.RequiredScope}", config.Scope,
            "The scope the SPA requests must match the one the API demands, or every call 403s.");
    }
}

[TestClass]
public class DashboardAuthorizationTests
{
    private const string Role = DashboardAuthorization.RequiredRole;
    private const string Scope = DashboardAuthorization.RequiredScope;

    [TestMethod]
    [DataRow("/api/Telemetry/stats")]
    [DataRow("/api/Telemetry/clients")]
    public async Task WithoutAToken_Returns401(string url)
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(TestRequests.Get(url));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "An anonymous caller must be challenged, never quietly handed tenant telemetry.");
    }

    [TestMethod]
    [DataRow("/api/Telemetry/stats")]
    [DataRow("/api/Telemetry/clients")]
    public async Task WithAValidTokenButNoRole_Returns403(string url)
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        // Authenticated, correct scope, but not granted the app role.
        var response = await client.SendAsync(TestRequests.Get(url, scopes: Scope));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
            $"Authentication is not authorisation: without the {Role} role this must be refused.");
    }

    [TestMethod]
    [DataRow("/api/Telemetry/stats")]
    [DataRow("/api/Telemetry/clients")]
    public async Task WithTheRoleButTheWrongScope_Returns403(string url)
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(TestRequests.Get(url, roles: Role, scopes: "Some.Other.Scope"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
            "RequiredScope must be enforced independently of the role, or a token issued for a " +
            "different API would be accepted here.");
    }

    [TestMethod]
    [DataRow("/api/Telemetry/stats")]
    [DataRow("/api/Telemetry/clients")]
    public async Task WithTheRoleButNoScopeClaimAtAll_Returns403(string url)
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        // An app-only (client credentials) token looks like this: app roles, but no delegated scope.
        var response = await client.SendAsync(TestRequests.Get(url, roles: Role));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
            "A token carrying no scope claim must not satisfy a scope requirement.");
    }

    [TestMethod]
    public async Task Stats_WithTheRoleAndScope_ReturnsDataFromTheStore()
    {
        using var factory = new TelemetryWebAppFactory();
        factory.Store.Seed(
            TestData.Client("client-a", DateTime.UtcNow, build: "1801"),
            TestData.Client("client-b", DateTime.UtcNow.AddDays(-3), build: "1756"));
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(TestRequests.Get("/api/Telemetry/stats", roles: Role, scopes: Scope));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsGreaterThanOrEqualTo(1, factory.Store.LoadAllCallCount,
            "The real DashboardService should have queried the store rather than short-circuiting.");
    }

    [TestMethod]
    public async Task Clients_WithTheRoleAndScope_ReturnsTheSeededClients()
    {
        using var factory = new TelemetryWebAppFactory();
        factory.Store.Seed(TestData.Client("client-a", DateTime.UtcNow, build: "1801"));
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(TestRequests.Get("/api/Telemetry/clients", roles: Role, scopes: Scope));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var clients = await response.Content.ReadFromJsonAsync<List<ClientSummary>>();
        Assert.IsNotNull(clients);
        Assert.HasCount(1, clients);
    }

    [TestMethod]
    public async Task ExtraRolesAlongsideTheRequiredOne_AreStillAccepted()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(
            TestRequests.Get("/api/Telemetry/stats", roles: $"Some.Other.Role,{Role}", scopes: Scope));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "A principal holding additional app roles must not be locked out.");
    }
}

[TestClass]
public class TelemetryUploadEndpointTests
{
    /// <summary>Fixed and UTC: the signature is derived from Generated.Ticks, so it must round-trip exactly.</summary>
    private static readonly DateTime Generated = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task ValidSignature_IsAcceptedWithoutAToken_AndIsPersisted()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var stats = TestData.Client("integration-client", Generated, build: "1801");
        var payload = new TelemetryPayload(stats, TelemetryWebAppFactory.SharedSecret);

        var response = await client.PostAsJsonAsync("/api/Telemetry", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "Importers have no Entra identity - they authenticate the payload, not themselves.");
        var saved = factory.Store.SavedModels.Single();
        Assert.AreEqual("integration-client", saved.AnonClientId);
        Assert.AreEqual("1801", saved.BuildVersionLabel);
    }

    [TestMethod]
    public async Task WrongSignature_Returns401_AndPersistsNothing()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var stats = TestData.Client("hostile-client", Generated, build: "1801");
        var payload = new TelemetryPayload(stats, "not-the-shared-secret");

        var response = await client.PostAsJsonAsync("/api/Telemetry", payload);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.HasCount(0, factory.Store.SavedModels,
            "A rejected upload must not reach the store.");
    }

    [TestMethod]
    public async Task PayloadWithNoTimestamp_Returns400()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        // Built by hand rather than via TelemetryPayload: generating a signature requires Generated,
        // so this shape can only arrive from a malformed or hand-rolled caller.
        var json = """{"statsModel":{"anonClientId":"no-timestamp"},"secret":"irrelevant"}""";
        var response = await client.PostAsync(
            "/api/Telemetry", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
            "An invalid model must be refused before the signature check dereferences Generated.");
        Assert.HasCount(0, factory.Store.SavedModels);
    }

    [TestMethod]
    public async Task PayloadWithNoStatsModel_Returns400()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var response = await client.PostAsync(
            "/api/Telemetry", new StringContent("""{"secret":"irrelevant"}""", Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.HasCount(0, factory.Store.SavedModels);
    }
}

[TestClass]
public class DependencyInjectionTests
{
    /// <summary>
    /// The production registrations from <see cref="TelemetryServiceCollectionExtensions.AddTelemetryServices"/>,
    /// validated with no Azure dependency. Until now the only proof this graph resolved was the app
    /// starting in production - a missing registration reached customers as a 500 on first request.
    /// </summary>
    [TestMethod]
    public void AddTelemetryServices_ProducesAResolvableGraph()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TelemetrySecret"] = "irrelevant-for-composition",
                ["CosmosDb:AccountEndpoint"] = "https://composition-test.documents.azure.com:443/",
                ["CosmosDb:DatabaseName"] = "telemetry-tests",
                ["CosmosDb:ContainerNameCurrent"] = "current",
                ["CosmosDb:ContainerNameHistory"] = "history",
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = TelemetryWebAppFactory.TenantId,
                ["AzureAd:ClientId"] = TelemetryWebAppFactory.ClientId,
                ["AzureAd:Scopes"] = DashboardAuthorization.RequiredScope,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelemetryServices(configuration);

        // ValidateOnBuild proves every constructor-injected registration can be satisfied - which is
        // how CosmosSchemaInitializer's dependencies get checked - without instantiating anything,
        // so no Cosmos client is ever built and no network call is attempted.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.IsNotNull(provider.GetRequiredService<WebAppConfig>());
    }

    [TestMethod]
    public void HostedFactory_ResolvesTheRealDomainServices()
    {
        using var factory = new TelemetryWebAppFactory();

        // Forces the host to build. The services below are the production types; only the adaptors
        // beneath them were substituted.
        _ = factory.Services;

        Assert.IsNotNull(factory.Services.GetRequiredService<StatsSaveService>());
        Assert.IsNotNull(factory.Services.GetRequiredService<DashboardService>());
        Assert.IsNotNull(factory.Services.GetRequiredService<WebAppConfig>());
        Assert.AreSame<object>(
            factory.Services.GetRequiredService<ITelemetrySaveAdaptor>(),
            factory.Services.GetRequiredService<ITelemetryQueryAdaptor>(),
            "Both interfaces must resolve to one instance, as they do in production.");
    }
}
