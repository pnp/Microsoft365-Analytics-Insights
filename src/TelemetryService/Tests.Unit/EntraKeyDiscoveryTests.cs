using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Web.Auth;

namespace Tests.Unit;

/// <summary>
/// Covers the <c>appid</c> tagging of Entra key discovery.
///
/// The API used to fetch Entra's signing keys anonymously, so eSTS could not attribute the key
/// discovery to this app registration and the S360 MISE Compliance KPI reported no key-discovery
/// telemetry for it at all.
/// </summary>
[TestClass]
public class EntraKeyDiscoveryTests
{
    private const string Instance = "https://login.microsoftonline.com/";
    private const string TenantId = "00000000-0000-0000-0000-000000000000";
    private const string ClientId = "11111111-1111-1111-1111-111111111111";

    [TestMethod]
    public void MetadataAddress_CarriesTheClientId()
    {
        var address = EntraKeyDiscovery.BuildMetadataAddress(Instance, TenantId, ClientId);

        Assert.AreEqual(
            $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration?appid={ClientId}",
            address);
    }

    [TestMethod]
    public void MetadataAddress_IsTheV2Endpoint()
    {
        var address = EntraKeyDiscovery.BuildMetadataAddress(Instance, TenantId, ClientId);

        // Microsoft.Identity.Web forces the authority to v2 (EnsureAuthorityIsV2). A v1 metadata
        // address here would have the API validating against a different issuer than it expects.
        StringAssert.Contains(address, "/v2.0/.well-known/openid-configuration");
    }

    [TestMethod]
    public void MetadataAddress_DoesNotDoubleUpTheSeparator()
    {
        // The instance is conventionally written with a trailing slash, and the tenant segment
        // must not end up as "//{tenant}".
        var withSlash = EntraKeyDiscovery.BuildMetadataAddress("https://login.microsoftonline.com/", TenantId, ClientId);
        var withoutSlash = EntraKeyDiscovery.BuildMetadataAddress("https://login.microsoftonline.com", TenantId, ClientId);

        Assert.AreEqual(withoutSlash, withSlash);
        Assert.IsFalse(withSlash!.Contains(".com//"), "Trailing slash on the instance produced a doubled separator.");
    }

    [TestMethod]
    [DataRow(null, TenantId, ClientId, DisplayName = "no instance")]
    [DataRow(Instance, null, ClientId, DisplayName = "no tenant")]
    [DataRow(Instance, TenantId, null, DisplayName = "no client id")]
    [DataRow("", TenantId, ClientId, DisplayName = "empty instance")]
    [DataRow(Instance, "   ", ClientId, DisplayName = "whitespace tenant")]
    public void MetadataAddress_IsNullWhenNotFullyConfigured(string? instance, string? tenantId, string? clientId)
    {
        // Null tells Program.cs to leave MetadataAddress alone so Microsoft.Identity.Web derives it,
        // which is what local development and any host without an app registration rely on.
        Assert.IsNull(EntraKeyDiscovery.BuildMetadataAddress(instance, tenantId, clientId));
    }

    /// <summary>
    /// The wiring test that matters. Microsoft.Identity.Web post-configures JwtBearerOptions
    /// (JwtBearerOptionsMerger), and ASP.NET Core's own JwtBearerPostConfigureOptions builds the
    /// ConfigurationManager that performs the actual key fetch. This asserts the address survives
    /// all of that, so an upgrade of either package cannot silently drop the attribution.
    /// </summary>
    [TestMethod]
    public void ConfiguredJwtBearerOptions_FetchKeysWithTheClientId()
    {
        using var factory = new TelemetryWebAppFactory();

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        var expected = $"appid={TelemetryWebAppFactory.ClientId}";

        StringAssert.Contains(options.MetadataAddress, expected,
            "JwtBearerOptions.MetadataAddress lost the appid parameter, so Entra cannot attribute " +
            "key discovery to this app registration.");

        // MetadataAddress is only a string; the ConfigurationManager is what actually calls Entra.
        // If it was built before the address was set, the string would look right while the real
        // request still went out anonymously.
        var configurationManager = options.ConfigurationManager as BaseConfigurationManager;
        Assert.IsNotNull(configurationManager, "Expected a BaseConfigurationManager to inspect.");
        StringAssert.Contains(configurationManager.MetadataAddress, expected,
            "The ConfigurationManager was built from an address without the appid, so the key " +
            "request would still be anonymous no matter what MetadataAddress says.");
    }
}
