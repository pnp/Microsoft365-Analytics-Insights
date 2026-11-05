using System.Text.Json;
using System.Text.RegularExpressions;
using Web.Config;
using Web.Storage;

namespace Tests.Unit;

/// <summary>
/// The deployment template (<c>infra/TelemetryService/azuredeploy.json</c>, which <c>deploy.ps1</c> deploys) must
/// provision every Cosmos container the service uses, with the partition key the code reads and writes by.
/// </summary>
/// <remarks>
/// The service cannot create a missing container itself: it authenticates to Cosmos DB with Microsoft Entra ID,
/// and data-plane roles cannot create containers (see <see cref="CosmosSchema"/>). The annotations container was
/// once missing from the template, so every fresh deployment lacked it while <c>/health</c> still passed.
/// </remarks>
[TestClass]
public partial class CosmosProvisioningTests
{
    private const string ContainerType = "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers";

    [TestMethod]
    [DataRow("CosmosDb__ContainerNameCurrent", CosmosTelemetrySaveAdaptor.PartitionKeyPath, null)]
    [DataRow("CosmosDb__ContainerNameHistory", CosmosTelemetrySaveAdaptor.PartitionKeyPath, null)]
    [DataRow("CosmosDb__ContainerNameAnnotations", CosmosClientAnnotationStore.PartitionKeyPath, CosmosConfig.DefaultAnnotationsContainer)]
    public void Template_ProvisionsTheContainerTheAppSettingNames(string setting, string partitionKeyPath, string? defaultName)
    {
        var template = ResourceGroupTemplate();
        var variables = template.GetProperty("variables");

        // The app settings are one ARM expression; each container setting maps to a template variable.
        var appSettings = Resources(template, "Microsoft.Web/sites/config")
            .Single(r => r.GetProperty("name").GetString()!.Contains("'appsettings'"))
            .GetProperty("properties").GetString()!;
        var match = Regex.Match(appSettings, $@"'{Regex.Escape(setting)}', variables\('(?<variable>\w+)'\)");

        string containerName;
        if (match.Success)
        {
            containerName = variables.GetProperty(match.Groups["variable"].Value).GetString()!;
        }
        else
        {
            Assert.IsNotNull(defaultName, $"The template must set {setting}: the service has no default for it.");
            containerName = defaultName;
        }

        var provisioned = Resources(template, ContainerType)
            .Select(r => r.GetProperty("properties").GetProperty("resource"))
            .Select(r => new
            {
                Name = Resolve(r.GetProperty("id").GetString()!, variables),
                PartitionKeyPaths = r.GetProperty("partitionKey").GetProperty("paths").EnumerateArray()
                    .Select(p => p.GetString())
                    .ToList(),
            })
            .SingleOrDefault(c => c.Name == containerName);

        Assert.IsNotNull(provisioned,
            $"The service uses container '{containerName}' ({setting}) but the template does not provision it, " +
            "and the service cannot create it with Entra data-plane access.");
        CollectionAssert.AreEqual(new[] { partitionKeyPath }, provisioned.PartitionKeyPaths,
            $"Container '{containerName}' must be partitioned on the key the service reads and writes by.");
    }

    [TestMethod]
    public void AnnotationsContainer_InTheBicepSource_MatchesTheServiceDefault()
    {
        // azuredeploy.json is generated from the Bicep; the drift check reads the Bicep. Both must agree.
        var bicep = File.ReadAllText(Path.Combine(InfraDirectory(), "resources.bicep"));

        StringAssert.Contains(bicep, $"var annotationsContainerName = '{CosmosConfig.DefaultAnnotationsContainer}'");
        StringAssert.Contains(bicep, "CosmosDb__ContainerNameAnnotations: annotationsContainerName");
    }

    /// <summary>The resource-group template nested inside the subscription-scope <c>azuredeploy.json</c>.</summary>
    private static JsonElement ResourceGroupTemplate()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(InfraDirectory(), "azuredeploy.json")));

        return Resources(document.RootElement, "Microsoft.Resources/deployments")
            .Single()
            .GetProperty("properties")
            .GetProperty("template")
            .Clone();
    }

    private static IEnumerable<JsonElement> Resources(JsonElement template, string type) =>
        template.GetProperty("resources").EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == type);

    /// <summary>Resolves a plain <c>[variables('x')]</c> expression; anything else is returned unchanged.</summary>
    private static string Resolve(string value, JsonElement variables)
    {
        var match = VariableReference().Match(value);
        return match.Success ? variables.GetProperty(match.Groups["name"].Value).GetString()! : value;
    }

    private static string InfraDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "infra", "TelemetryService");
            if (File.Exists(Path.Combine(candidate, "azuredeploy.json")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find infra/TelemetryService above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^\[variables\('(?<name>\w+)'\)\]$")]
    private static partial Regex VariableReference();
}
