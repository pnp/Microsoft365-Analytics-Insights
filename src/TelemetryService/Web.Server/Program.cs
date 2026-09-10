using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Web.Auth;
using Web.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Application Insights. This MUST be wired up in-process: the service runs on Linux App
// Service, where the ApplicationInsightsAgent_EXTENSION_VERSION codeless attach used on
// Windows does nothing for .NET, so without this the connection string is configured but
// no telemetry is ever emitted. Reads APPLICATIONINSIGHTS_CONNECTION_STRING and silently
// no-ops when it is unset (e.g. local development).
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

var azureAdSection = builder.Configuration.GetSection("AzureAd");

// Entra can only attribute signing-key discovery to this app registration if the requests carry our
// client ID - see EntraKeyDiscovery. Null when the app registration settings are absent (local
// development, tests), in which case Microsoft.Identity.Web derives the address as it normally would.
var keyDiscoveryMetadataAddress = EntraKeyDiscovery.BuildMetadataAddress(
    azureAdSection["Instance"],
    azureAdSection["TenantId"],
    azureAdSection["ClientId"]);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    // The IConfigurationSection overload is expanded here so MetadataAddress can be set. It binds the
    // section to BOTH JwtBearerOptions and MicrosoftIdentityOptions, so both binds are kept.
    .AddMicrosoftIdentityWebApi(
        jwtBearerOptions =>
        {
            azureAdSection.Bind(jwtBearerOptions);

            // Must be assigned in this Configure delegate, not a PostConfigure: JwtBearerPostConfigureOptions
            // builds the ConfigurationManager from MetadataAddress during PostConfigure, so a later write
            // would update the string while the key fetch kept using the old address.
            if (keyDiscoveryMetadataAddress is not null)
            {
                jwtBearerOptions.MetadataAddress = keyDiscoveryMetadataAddress;
            }
        },
        identityOptions => azureAdSection.Bind(identityOptions));

// Registers Microsoft.Identity.Web's ScopeAuthorizationHandler. Without it a ScopeAuthorizationRequirement
// has nothing to evaluate it, so any scope check silently passes.
builder.Services.AddRequiredScopeAuthorization();

builder.Services.AddAuthorization(options =>
{
    // Role and scope are deliberately combined into a single policy - see DashboardAuthorization.PolicyName
    // for why [Authorize(Roles = ...)] together with [RequiredScope(...)] only enforces the role.
    options.AddPolicy(DashboardAuthorization.PolicyName, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(DashboardAuthorization.RequiredRole)
        .RequireScope(DashboardAuthorization.RequiredScope));
});

// Config, Cosmos, telemetry store and dashboard services (see TelemetryServiceCollectionExtensions).
builder.Services.AddTelemetryServices(builder.Configuration);

var app = builder.Build();

app.UseDefaultFiles();
app.MapStaticAssets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }))
    .AllowAnonymous();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();

/// <summary>
/// Exposed so integration tests can host the real application with <c>WebApplicationFactory</c>.
/// </summary>
public partial class Program;
