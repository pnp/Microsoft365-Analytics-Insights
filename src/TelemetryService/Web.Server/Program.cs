using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
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

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization();

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
