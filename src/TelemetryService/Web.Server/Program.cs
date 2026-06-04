using Azure.Identity;
using Microsoft.Azure.Cosmos;
using UsageReporting;
using Web.Config;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();


var config = new WebAppConfig(builder.Configuration);
builder.Services.AddSingleton(config);

// Use Microsoft Entra ID (AAD) authentication for Cosmos DB.
// The account has local (key) authorization disabled, so DefaultAzureCredential is used to
// pick up the developer's Visual Studio / Azure CLI credentials locally and the Managed Identity
// when running in Azure.
if (string.IsNullOrWhiteSpace(config.CosmosDb.AccountEndpoint))
{
    throw new InvalidOperationException(
        "CosmosDb:AccountEndpoint is not configured. Set it to the Cosmos account URL, e.g. https://<account>.documents.azure.com:443/");
}

// The Cosmos account only trusts tokens issued by its home tenant. If your default Azure
// tenant differs (e.g. you're signed into the Microsoft corp tenant but the account lives
// in a customer / lab tenant), set AZURE_TENANT_ID in configuration / env vars.
var tenantId = builder.Configuration["AZURE_TENANT_ID"];
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    TenantId = tenantId,
    // Allow the chained credentials (VS, Azure CLI, etc.) to silently re-auth against
    // the tenant required by the resource, instead of failing with an authority mismatch.
    AdditionallyAllowedTenants = { "*" }
});

var cosmosClient = new CosmosClient(config.CosmosDb.AccountEndpoint, credential);
builder.Services.AddSingleton(s => cosmosClient);


var adapter = new CosmosTelemetrySaveAdaptor(cosmosClient, config.CosmosDb);
builder.Services.AddSingleton(s => new StatsSaveService(adapter, s.GetService<ILogger<Program>>()));
await adapter.Init();


var app = builder.Build();

app.UseDefaultFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();
