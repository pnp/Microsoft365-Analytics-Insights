using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using UsageReporting;
using Web;
using Web.Auth;
using Web.Config;
using Web.Dashboard;
using Web.Startup;

namespace Tests.Unit;

/// <summary>
/// The upload signature is checked for the exact shape importers send before BCrypt sees it, because a bcrypt
/// hash names its own cost and the endpoint is anonymous.
/// </summary>
[TestClass]
public class UploadSignatureTests
{
    private const string Secret = "unit-test-shared-secret";

    /// <summary>
    /// The contract with the client. If importers ever sign differently (another cost, another bcrypt revision),
    /// this fails here rather than every upload in the field being refused.
    /// </summary>
    [TestMethod]
    public void SignatureFromTheImporterCode_HasTheShapeTheServerAccepts()
    {
        // Each signature has a random salt, so try enough of them to cover the whole salt alphabet.
        for (var i = 0; i < 50; i++)
        {
            var payload = new TelemetryPayload(TestData.Client($"client-{i}", DateTime.UtcNow), Secret);

            Assert.IsTrue(UploadSignature.IsWellFormed(payload.Secret),
                $"The importer's own signature must be accepted, but '{UploadSignature.Prefix}...' did not match " +
                $"a signature of length {payload.Secret.Length} starting '{payload.Secret[..7]}'.");
        }
    }

    [TestMethod]
    [DataRow("31", DisplayName = "The highest cost bcrypt accepts")]
    [DataRow("12", DisplayName = "A typical password-hashing cost")]
    [DataRow("05", DisplayName = "One step above the importers' cost")]
    [DataRow("03", DisplayName = "Below the importers' cost")]
    public void ASignatureNamingAnyOtherCost_IsRefused(string cost)
    {
        var genuine = new TelemetryPayload(TestData.Client("client", DateTime.UtcNow), Secret).Secret;

        Assert.IsFalse(UploadSignature.IsWellFormed(WithCost(genuine, cost)),
            "Only cost 4 may reach BCrypt: the caller would otherwise choose how long verifying it takes.");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "Missing")]
    [DataRow("", DisplayName = "Empty")]
    [DataRow("not-a-bcrypt-hash", DisplayName = "Not bcrypt at all")]
    [DataRow("$2a$04$", DisplayName = "Prefix only")]
    public void AMalformedSignature_IsRefused(string? signature)
    {
        Assert.IsFalse(UploadSignature.IsWellFormed(signature));
    }

    [TestMethod]
    [DataRow("another bcrypt revision")]
    [DataRow("single-digit cost")]
    [DataRow("one character short")]
    [DataRow("one character long")]
    [DataRow("outside bcrypt's alphabet")]
    [DataRow("leading space")]
    public void AGenuineSignatureAlteredInAnyWay_IsRefused(string alteration)
    {
        var genuine = new TelemetryPayload(TestData.Client("client", DateTime.UtcNow), Secret).Secret;
        Assert.IsTrue(UploadSignature.IsWellFormed(genuine), "Precondition: the unaltered signature is accepted.");

        var altered = alteration switch
        {
            "another bcrypt revision" => "$2b$" + genuine[4..],
            "single-digit cost" => "$2a$4$" + genuine[7..] + "A",
            "one character short" => genuine[..^1],
            "one character long" => genuine + "A",
            "outside bcrypt's alphabet" => genuine[..20] + "+" + genuine[21..],
            "leading space" => " " + genuine[..^1],
            _ => throw new ArgumentOutOfRangeException(nameof(alteration)),
        };

        Assert.IsFalse(UploadSignature.IsWellFormed(altered));
    }

    [TestMethod]
    public void AWellFormedSignatureWithTheWrongSecret_PassesTheShapeCheck_AndIsLeftToBCrypt()
    {
        var signedWithAnotherSecret = new TelemetryPayload(TestData.Client("client", DateTime.UtcNow), "another-secret").Secret;

        Assert.IsTrue(UploadSignature.IsWellFormed(signedWithAnotherSecret),
            "The shape check only bounds the cost of verifying; whether the signature matches is still BCrypt's call.");
    }

    internal static string WithCost(string signature, string cost) =>
        signature[..4] + cost + signature[6..];
}

[TestClass]
public class UploadLimitsTests
{
    [TestMethod]
    public void LargestRealisticPayload_FitsWellWithinTheLimit()
    {
        // Serialised exactly as WebApiStatsUploader does it.
        var payload = new TelemetryPayload(LargePayloads.LargestRealisticReport("client", DateTime.UtcNow), "secret");
        var bytes = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(payload));

        Assert.IsTrue(bytes < UploadLimits.MaxRequestBodyBytes / 4,
            $"A report far larger than any real deployment's ({bytes:N0} bytes) must leave a wide margin under " +
            $"the {UploadLimits.MaxRequestBodyBytes:N0}-byte limit, or genuine uploads could start being refused.");
    }

    [TestMethod]
    [DataRow("203.0.113.10", "203.0.113.10")]
    [DataRow("::ffff:203.0.113.10", "203.0.113.10", DisplayName = "IPv4 over a dual-stack socket counts as IPv4")]
    [DataRow("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [DataRow("2001:db8:1:2::1", "2001:db8:1:2::/64", DisplayName = "Same /64, same budget")]
    [DataRow("2001:db8:1:3::1", "2001:db8:1:3::/64", DisplayName = "Another /64, another budget")]
    public void PartitionKey_IsTheAddress_WithIPv6GroupedBy64(string address, string expected)
    {
        Assert.AreEqual(expected, UploadLimits.PartitionKeyFor(IPAddress.Parse(address)));
    }

    [TestMethod]
    public void PartitionKey_ForAnUnknownAddress_IsOneSharedBudget()
    {
        Assert.AreEqual(UploadLimits.UnknownAddressPartition, UploadLimits.PartitionKeyFor(null));
    }

    [TestMethod]
    [DataRow(null, WebAppConfig.DefaultMaxUploadsPerAddressPerHour)]
    [DataRow("", WebAppConfig.DefaultMaxUploadsPerAddressPerHour)]
    [DataRow("lots", WebAppConfig.DefaultMaxUploadsPerAddressPerHour)]
    [DataRow("0", WebAppConfig.DefaultMaxUploadsPerAddressPerHour)]
    [DataRow("-3", WebAppConfig.DefaultMaxUploadsPerAddressPerHour)]
    [DataRow("120", 120)]
    public void MaxUploadsPerAddressPerHour_FallsBackToTheDefaultForUnusableValues(string? configured, int expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["TelemetrySecret"] = "secret",
            ["CosmosDb:AccountEndpoint"] = "https://example.documents.azure.com:443/",
            ["CosmosDb:DatabaseName"] = "Telemetry",
            ["CosmosDb:ContainerNameCurrent"] = "Current",
            ["CosmosDb:ContainerNameHistory"] = "History",
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:Scopes"] = "Telemetry.Read",
        };
        if (configured != null) values["MaxUploadsPerAddressPerHour"] = configured;

        var config = new WebAppConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.AreEqual(expected, config.GetMaxUploadsPerAddressPerHour(),
            "Zero must not switch the limit off, and nonsense must not either.");
    }
}

/// <summary>The upload endpoint's defences, through the real HTTP pipeline (see <see cref="TelemetryWebAppFactory"/>).</summary>
[TestClass]
public class UploadEndpointProtectionTests
{
    private static readonly DateTime Generated = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Long enough for a loaded build agent, far too short for cost-31 bcrypt, which would run for hours.
    /// </summary>
    private static readonly TimeSpan NoHashingBudget = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task SignatureNamingTheHighestCost_IsRefusedWithoutBeingHashed()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var payload = SignedPayload("hostile-client");
        payload.Secret = UploadSignatureTests.WithCost(payload.Secret, "31");

        var response = await client.SendAsync(Upload(payload)).WaitAsync(NoHashingBudget);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.HasCount(0, factory.Store.SavedModels);
    }

    [TestMethod]
    public async Task MalformedSignature_Returns401RatherThanAServerError()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var payload = SignedPayload("hostile-client");
        payload.Secret = "not-a-bcrypt-hash";

        var response = await client.SendAsync(Upload(payload));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "BCrypt throws on a hash it cannot parse, which used to surface as a 500.");
        Assert.HasCount(0, factory.Store.SavedModels);
    }

    [TestMethod]
    public async Task LargestRealisticPayload_IsAccepted()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        var stats = LargePayloads.LargestRealisticReport("large-client", Generated);
        var response = await client.SendAsync(Upload(new TelemetryPayload(stats, TelemetryWebAppFactory.SharedSecret)));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var saved = factory.Store.SavedModels.Single();
        Assert.HasCount(stats.TableStats.Count, saved.TableStats);
        Assert.HasCount(stats.Adoption.Licences.Skus.Count, saved.Adoption.Licences.Skus);
    }

    [TestMethod]
    public async Task BodyOverTheLimit_Returns413_AndPersistsNothing()
    {
        // On real Kestrel: TestServer has no IHttpMaxRequestBodySizeFeature, so it cannot enforce
        // [RequestSizeLimit] and would accept the body. Kestrel enforces it as IIS, which hosts the service in
        // App Service, does.
        using var factory = new TelemetryWebAppFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var address = factory.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var stats = TestData.Client("oversized-client", Generated);
        stats.TableStats = Enumerable.Range(0, 20_000)
            .Select(i => new AnonUsageStatsModel.TableStat { SchemaName = "dbo", TableName = $"table_{i:D6}", Rows = i, TotalSpaceMB = i })
            .ToList();
        var request = Upload(new TelemetryPayload(stats, TelemetryWebAppFactory.SharedSecret));
        Assert.IsTrue(request.Content!.Headers.ContentLength > UploadLimits.MaxRequestBodyBytes, "Test payload must exceed the limit.");

        // Wait for the server's go-ahead before sending the body. The server refuses on the declared length, so
        // without this the refusal can race the client still writing the body, and surface as a reset connection.
        request.Headers.ExpectContinue = true;

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.HasCount(0, factory.Store.SavedModels);
    }

    [TestMethod]
    public async Task UploadsBeyondTheHourlyBudget_FromOneAddress_Return429()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();
        const string address = "203.0.113.10";

        for (var i = 0; i < WebAppConfig.DefaultMaxUploadsPerAddressPerHour; i++)
        {
            var accepted = await client.SendAsync(Upload(SignedPayload($"client-{i}"), from: address));
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, $"Upload {i + 1} is within the budget.");
        }

        var refused = await client.SendAsync(Upload(SignedPayload("one-too-many"), from: address));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, refused.StatusCode,
            "429, not the rate limiter's default 503, which would read as an outage.");
        Assert.IsNotNull(refused.Headers.RetryAfter, "Tell the caller when to come back.");
        Assert.HasCount(WebAppConfig.DefaultMaxUploadsPerAddressPerHour, factory.Store.SavedModels,
            "A refused upload must not reach the store.");
    }

    [TestMethod]
    public async Task RefusedUploads_CountAgainstTheBudgetToo()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();
        const string address = "203.0.113.20";

        // Every signature check costs CPU, so failed attempts must use up the budget like successful ones.
        for (var i = 0; i < WebAppConfig.DefaultMaxUploadsPerAddressPerHour; i++)
        {
            var wrongSecret = new TelemetryPayload(TestData.Client($"guess-{i}", Generated), "a-guessed-secret");
            var rejected = await client.SendAsync(Upload(wrongSecret, from: address));
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var genuine = await client.SendAsync(Upload(SignedPayload("genuine-client"), from: address));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, genuine.StatusCode);
    }

    [TestMethod]
    public async Task AnotherAddress_AndTheOtherEndpoints_AreNotAffectedByOneAddressUsingItsBudget()
    {
        using var factory = new TelemetryWebAppFactory();
        using var client = factory.CreateApiClient();

        for (var i = 0; i <= WebAppConfig.DefaultMaxUploadsPerAddressPerHour; i++)
        {
            await client.SendAsync(Upload(SignedPayload($"busy-{i}"), from: "203.0.113.30"));
        }

        var otherAddress = await client.SendAsync(Upload(SignedPayload("another-install"), from: "198.51.100.40"));
        Assert.AreEqual(HttpStatusCode.OK, otherAddress.StatusCode, "Each address has its own budget.");

        var health = await client.SendAsync(WithAddress(new HttpRequestMessage(HttpMethod.Get, "/health"), "203.0.113.30"));
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "Only the upload endpoint is rate limited.");
    }

    private static TelemetryPayload SignedPayload(string clientId) =>
        new(TestData.Client(clientId, Generated, build: "1801"), TelemetryWebAppFactory.SharedSecret);

    /// <summary>A POST serialised exactly as <c>WebApiStatsUploader</c> sends it: Newtonsoft, PascalCase.</summary>
    private static HttpRequestMessage Upload(TelemetryPayload payload, string? from = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Telemetry")
        {
            Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"),
        };

        return from == null ? request : WithAddress(request, from);
    }

    private static HttpRequestMessage WithAddress(HttpRequestMessage request, string address)
    {
        request.Headers.Add(TelemetryWebAppFactory.RemoteAddressHeader, address);
        return request;
    }
}

/// <summary>The same checks one level down, on the controller, where a thrown exception shows directly.</summary>
[TestClass]
public class TelemetryControllerSignatureTests
{
    private const string Secret = "unit-test-shared-secret";

    [TestMethod]
    public async Task Post_SignatureNamingTheHighestCost_IsRejectedWithoutHashing()
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store);
        var payload = new TelemetryPayload(TestData.Client("hostile", DateTime.UtcNow), Secret);
        payload.Secret = UploadSignatureTests.WithCost(payload.Secret, "31");

        var stopwatch = Stopwatch.StartNew();
        var result = await Task.Run(() => controller.Post(payload)).WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.IsEmpty(store.SavedModels);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Refusing it should be a string check, not a hash; it took {stopwatch.Elapsed}.");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "Missing")]
    [DataRow("", DisplayName = "Empty")]
    [DataRow("not-a-bcrypt-hash", DisplayName = "Not bcrypt at all")]
    [DataRow("$2a$04$short", DisplayName = "Truncated")]
    public async Task Post_MalformedSignature_IsUnauthorisedRatherThanAnError(string? signature)
    {
        var store = new FakeTelemetryStore();
        var controller = BuildController(store);
        var payload = new TelemetryPayload(TestData.Client("client", DateTime.UtcNow), Secret) { Secret = signature! };

        var result = await controller.Post(payload);

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult),
            "BCrypt throws on a hash it cannot parse; that must be a refusal, not an unhandled exception.");
        Assert.IsEmpty(store.SavedModels);
    }

    private static TelemetryController BuildController(FakeTelemetryStore store)
    {
        var config = new WebAppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TelemetrySecret"] = Secret,
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
        var annotations = new FakeClientAnnotationStore();

        return new TelemetryController(
            new StatsSaveService(store, NullLogger<StatsSaveService>.Instance),
            new DashboardService(
                store, NullLogger<DashboardService>.Instance, new MemoryCache(new MemoryCacheOptions()), 5000,
                TimeSpan.Zero, annotations),
            annotations,
            config,
            NullLogger<TelemetryController>.Instance);
    }
}

internal static class LargePayloads
{
    /// <summary>
    /// A report far larger than any real deployment sends: 1,000 tables (a real database has a couple of hundred)
    /// and an adoption block with 100 licence SKUs, each with every workload.
    /// </summary>
    public static AnonUsageStatsModel LargestRealisticReport(string clientId, DateTime generated)
    {
        var imports = string.Join(";", Enumerable.Range(1, 60).Select(i => $"ImportFeatureNumber{i:D2}=True"));
        var model = TestData.Client(clientId, generated, build: "1900", imports: imports, aiDataPoints: 123_456_789);

        model.TableStats = Enumerable.Range(1, 1_000)
            .Select(i => new AnonUsageStatsModel.TableStat
            {
                SchemaName = i % 10 == 0 ? "profiling" : "dbo",
                TableName = $"copilot_event_metadata_accessed_resources_{i:D4}",
                Rows = 9_876_543_210 + i,
                TotalSpaceMB = 123_456.78m + i,
            })
            .ToList();

        static List<AnonAdoptionCategory> Categories(string prefix) => Enumerable.Range(1, 8)
            .Select(i => new AnonAdoptionCategory { Label = $"{prefix}-{i}", Value = 100_000 + i })
            .ToList();

        model.Adoption = new AnonAdoptionStats
        {
            GeneratedUtc = generated,
            WindowDays = 28,
            Copilot = new AnonCopilotAdoptionStats
            {
                AuditAvailable = true,
                CopilotUsageReportAvailable = true,
                M365UsageReportsAvailable = true,
                UserMetadataAvailable = true,
                LicensedUsers = 200_000,
                ScoredUsers = 200_000,
                ActiveUsers = 150_000,
                HabitualUsers = 100_000,
                NeverUsedUsers = 25_000,
                DormantUsers = 25_000,
                DisabledLicensedUsers = 1_000,
                ReclaimableSeats = 5_000,
                UnlicensedActiveUsers = 10_000,
                RecommendedForLicence = 10_000,
                CoworkUsers = 50_000,
                TotalInteractions = 50_000_000,
                CoworkInteractions = 5_000_000,
                AgentInteractions = 5_000_000,
                KnownAgents = 1_000,
                ActiveAgents = 500,
                CustomAgents = 250,
                AgentUsers = 50_000,
                AdoptionRatePct = 75.5,
                HabitRatePct = 50.5,
                CoworkAdoptionPct = 25.5,
                AverageAdoptionScore = 65.5,
                MedianAdoptionScore = 66.5,
                BandBreakdown = Categories("band"),
                Funnel = Categories("funnel"),
                HabitBuckets = Categories("habit"),
                WarningCount = 3,
            },
            Licences = new AnonLicenceAdoptionStats
            {
                DistinctAssignedUsers = 200_000,
                Skus = Enumerable.Range(1, 100)
                    .Select(i => new AnonLicenceSkuStats
                    {
                        SkuPartNumber = $"MICROSOFT_365_EXAMPLE_SKU_{i:D3}",
                        AssignedUsers = 10_000 + i,
                        Workloads = new[] { "teams", "outlook", "onedrive", "sharepoint", "copilot" }
                            .Select(w => new AnonWorkloadDistribution
                            {
                                Workload = w, High = 1_000, Moderate = 2_000, Low = 3_000, Zero = 4_000, Unknown = 100,
                            })
                            .ToList(),
                    })
                    .ToList(),
                Coverage = Enumerable.Range(1, 10)
                    .Select(i => new AnonLicenceCoverage
                    {
                        Workload = $"workload-{i}",
                        Status = "available",
                        Source = "getM365AppUserDetail",
                        Granularity = "perUser",
                        LagDays = 3,
                        ReportPeriodDays = 28,
                        ExpectedSamples = 28,
                        ObservedSamples = 28,
                        UnmatchedUsers = 100,
                    })
                    .ToList(),
            },
        };

        return model;
    }
}
