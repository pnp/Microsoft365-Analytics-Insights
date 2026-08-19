using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UsageReporting;
using Web;
using Web.Config;
using Web.Dashboard;

namespace Tests.Unit;

[TestClass]
public class StatsSaveServiceTests
{
    [TestMethod]
    public async Task UnseenClient_IsSavedAsIs()
    {
        var store = new FakeTelemetryStore();
        var service = new StatsSaveService(store, NullLogger<StatsSaveService>.Instance);
        var incoming = TestData.Client("new-client", DateTime.UtcNow, build: "1756");

        await service.SaveOrUpdate(incoming);

        Assert.HasCount(1, store.SavedModels);
        Assert.AreEqual("new-client", store.SavedModels[0].AnonClientId);
        Assert.AreEqual("1756", store.SavedModels[0].BuildVersionLabel);
    }

    [TestMethod]
    public async Task KnownClient_IsMergedRatherThanReplaced()
    {
        var existing = TestData.Client("known", DateTime.UtcNow.AddDays(-1), build: "1732", imports: "A=True");
        var store = new FakeTelemetryStore();
        store.Seed(existing);
        var service = new StatsSaveService(store, NullLogger<StatsSaveService>.Instance);

        // The new report carries a build but no imports; the previous imports must survive.
        var incoming = TestData.Client("known", DateTime.UtcNow, build: "1756");

        await service.SaveOrUpdate(incoming);

        var saved = store.SavedModels.Single();
        Assert.AreEqual("1756", saved.BuildVersionLabel, "Newer value wins.");
        Assert.AreEqual("A=True", saved.ConfiguredImportsEnabledDescription,
            "A field absent from the new report must not wipe the stored value.");
    }

    [TestMethod]
    public async Task NullModel_Throws()
    {
        var service = new StatsSaveService(new FakeTelemetryStore(), NullLogger<StatsSaveService>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SaveOrUpdate(null!));
    }
}

[TestClass]
public class TelemetryControllerTests
{
    private const string Secret = "unit-test-shared-secret";

    private static TelemetryController BuildController(FakeTelemetryStore store, string? overrideSecret = null)
    {
        var config = BuildConfig(Secret);
        if (overrideSecret != null)
        {
            // TelemetrySecret is a *required* config value, so WebAppConfig refuses to construct
            // without it (covered in WebAppConfigTests). Blanking it afterwards is the only way to
            // reach the controller's own defence-in-depth guard.
            config.TelemetrySecret = overrideSecret;
        }

        var dashboard = new DashboardService(
            store, NullLogger<DashboardService>.Instance, new MemoryCache(new MemoryCacheOptions()), 5000, TimeSpan.Zero);

        return new TelemetryController(
            new StatsSaveService(store, NullLogger<StatsSaveService>.Instance),
            dashboard,
            config,
            NullLogger<TelemetryController>.Instance);
    }

    private static WebAppConfig BuildConfig(string? secret) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TelemetrySecret"] = secret ?? string.Empty,
                ["CosmosDb:AccountEndpoint"] = "https://example.documents.azure.com:443/",
                ["CosmosDb:DatabaseName"] = "Telemetry",
                ["CosmosDb:ContainerNameCurrent"] = "Current",
                ["CosmosDb:ContainerNameHistory"] = "History",
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureAd:Scopes"] = "Telemetry.Read",
            })
            .Build());

    private static TelemetryPayload ValidPayload(string clientId = "client-1")
    {
        var model = TestData.Client(clientId, DateTime.UtcNow).WithTables(("dbo", "t", 1, 1m));
        return new TelemetryPayload(model, Secret);
    }

    [TestMethod]
    public async Task Post_ValidSignedPayload_IsAccceptedAndStored()
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store);

        var result = await controller.Post(ValidPayload());

        Assert.IsInstanceOfType(result, typeof(OkResult));
        Assert.HasCount(1, store.SavedModels);
    }

    [TestMethod]
    public async Task Post_WrongSignature_IsRejectedAndNothingIsStored()
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store);

        var payload = ValidPayload();
        payload.Secret = new TelemetryPayload(TestData.Client("x", DateTime.UtcNow), "a-different-secret").Secret;

        var result = await controller.Post(payload);

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.IsEmpty(store.SavedModels, "An unauthorised payload must never reach the store.");
    }

    [TestMethod]
    public async Task Post_NullPayload_IsBadRequest()
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store);

        var result = await controller.Post(null!);

        Assert.IsInstanceOfType(result, typeof(BadRequestResult));
        Assert.IsEmpty(store.SavedModels);
    }

    [TestMethod]
    public async Task Post_InvalidModel_IsBadRequest()
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store);

        // No AnonClientId / Generated => IsValid is false.
        var payload = new TelemetryPayload { StatsModel = new AnonUsageStatsModel(), Secret = "whatever" };

        var result = await controller.Post(payload);

        Assert.IsInstanceOfType(result, typeof(BadRequestResult));
    }

    [TestMethod]
    public async Task Post_ServerWithNoConfiguredSecret_FailsLoudlyRatherThanAcceptingAnything()
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store, overrideSecret: string.Empty);

        await Assert.ThrowsAsync<Exception>(() => controller.Post(ValidPayload()));
        Assert.IsEmpty(store.SavedModels);
    }

    [TestMethod]
    public async Task GetStats_ReturnsAggregatedFigures()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("a", DateTime.UtcNow).WithTables(("dbo", "t", 42, 4m)));
        var controller = BuildController(store);

        var result = await controller.GetStats();

        var ok = (OkObjectResult)result.Result!;
        var stats = (DashboardStats)ok.Value!;
        Assert.AreEqual(1, stats.ClientCount);
        Assert.AreEqual(42, stats.TotalRows);
    }

    [TestMethod]
    public async Task GetClients_ReturnsPerClientRows()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("a", DateTime.UtcNow), TestData.Client("b", DateTime.UtcNow));
        var controller = BuildController(store);

        var result = await controller.GetClients();

        var ok = (OkObjectResult)result.Result!;
        var clients = (IReadOnlyList<ClientSummary>)ok.Value!;
        Assert.HasCount(2, clients);
    }
}

[TestClass]
public class WebAppConfigTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> Minimum() => new()
    {
        ["TelemetrySecret"] = "secret",
        ["CosmosDb:AccountEndpoint"] = "https://example.documents.azure.com:443/",
        ["CosmosDb:DatabaseName"] = "Telemetry",
        ["CosmosDb:ContainerNameCurrent"] = "Current",
        ["CosmosDb:ContainerNameHistory"] = "History",
        ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
        ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
        ["AzureAd:ClientId"] = "11111111-1111-1111-1111-111111111111",
        ["AzureAd:Scopes"] = "Telemetry.Read",
    };

    [TestMethod]
    public void BindsValuesAndSections()
    {
        var config = new WebAppConfig(Config(Minimum()));

        Assert.AreEqual("secret", config.TelemetrySecret);
        Assert.AreEqual("Telemetry", config.CosmosDb.DatabaseName);
        Assert.AreEqual("11111111-1111-1111-1111-111111111111", config.AzureAd.ClientId);
    }

    [TestMethod]
    public void MissingRequiredValue_ThrowsWithThePropertyName()
    {
        var values = Minimum();
        values.Remove("TelemetrySecret");

        var ex = Assert.Throws<ConfigurationMissingException>(() => new WebAppConfig(Config(values)));
        StringAssert.Contains(ex.Message, nameof(WebAppConfig.TelemetrySecret));
    }

    [TestMethod]
    public void AuthorityAndApiScope_AreDerivedFromInstanceAndClientId()
    {
        var config = new WebAppConfig(Config(Minimum()));

        Assert.AreEqual(
            "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000",
            config.AzureAd.Authority,
            "The trailing slash on Instance must not be doubled up.");
        Assert.AreEqual(
            "api://11111111-1111-1111-1111-111111111111/Telemetry.Read",
            config.AzureAd.ApiScope);
    }

    [TestMethod]
    [DataRow(null, 5000)]
    [DataRow("", 5000)]
    [DataRow("not-a-number", 5000)]
    [DataRow("0", 5000)]
    [DataRow("-1", 5000)]
    [DataRow("250", 250)]
    public void MaxDashboardItems_FallsBackToTheDefaultForUnusableValues(string? configured, int expected)
    {
        var values = Minimum();
        if (configured != null) values["MaxDashboardItems"] = configured;

        Assert.AreEqual(expected, new WebAppConfig(Config(values)).GetMaxDashboardItems());
    }

    [TestMethod]
    [DataRow(null, 60)]
    [DataRow("", 60)]
    [DataRow("rubbish", 60)]
    [DataRow("-5", 60)]
    [DataRow("0", 0)]
    [DataRow("120", 120)]
    public void DashboardCacheSeconds_SupportsDisablingWithZeroButRejectsNegatives(string? configured, int expectedSeconds)
    {
        var values = Minimum();
        if (configured != null) values["DashboardCacheSeconds"] = configured;

        Assert.AreEqual(
            TimeSpan.FromSeconds(expectedSeconds),
            new WebAppConfig(Config(values)).GetDashboardCacheDuration());
    }
}
