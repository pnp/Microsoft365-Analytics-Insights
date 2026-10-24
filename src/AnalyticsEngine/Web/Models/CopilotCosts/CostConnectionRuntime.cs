using Common.Entities.Config;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Security.Claims;
using System.Threading;
using Azure;
using Azure.Identity;
using Azure.Data.Tables;
using Common.Entities.CopilotCosts;

namespace Web.AnalyticsWeb.Models.CopilotCosts
{
    public sealed class CostConnectionRuntime
    {
        public const string OperatorRole = "CopilotCostConnection.Operator";
        public const string CallbackPath = "/copilot-cost/callback";
        public const string BindingCookie = "__Host-CopilotCostBinding";
        public static readonly Lazy<CostConnectionRuntime> Default = new Lazy<CostConnectionRuntime>(() => new CostConnectionRuntime(new AppConfig()));
        private readonly Lazy<ICostDocumentStore> _store;
        private readonly Lazy<CostConnectionService> _service;
        public AppConfig Config { get; }
        public string Callback { get; }
        public string Availability { get; }
        public ICostDocumentStore Store { get { RequireAvailable(); return _store.Value; } }
        public CostConnectionService Service { get { RequireAvailable(); return _service.Value; } }

        public CostConnectionRuntime(AppConfig config)
        {
            Config = config;
            Availability = GetAvailability(config.CopilotCostConnectionEnabled && config.ImportJobSettings.CopilotCostManagement ? "true" : "false",
                config.ConnectionStrings?.StorageConnectionString, config.WebAppURL);
            if (Availability != "available") return;
            Callback = new Uri(new Uri(config.WebAppURL), CallbackPath).AbsoluteUri;
            _store = new Lazy<ICostDocumentStore>(() => CreateStore(config), LazyThreadSafetyMode.PublicationOnly);
            _service = new Lazy<CostConnectionService>(() => new CostConnectionService(Store,
                new CostMsalIdentity(config, Callback), config.TenantGUID.ToString(), config.ClientID, Callback,
                publicationFence: CostCollectorRuntime.CreateHistoryStore(config)),
                LazyThreadSafetyMode.PublicationOnly);
        }

        private static ICostDocumentStore CreateStore(AppConfig config)
        {
            try
            {
                var options = new TableClientOptions();
                options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
                options.Retry.MaxRetries = 1;
                return new TableCostDocumentStore(
                    Common.Entities.State.StorageTableClientFactory.CreateAndEnsureTable(config.ConnectionStrings.StorageConnectionString,
                        "CopilotCostConnections", config, NullLogger.Instance, "Copilot cost connection", options),
                    config.TenantGUID.ToString(), config.ClientID);
            }
            catch (RequestFailedException) { throw new CostConnectionException("storageUnavailable"); }
            catch (AuthenticationFailedException) { throw new CostConnectionException("storageUnavailable"); }
            catch (ArgumentException) { throw new CostConnectionException("configurationRequired"); }
            catch (InvalidOperationException) { throw new CostConnectionException("configurationRequired"); }
        }

        public void RequireAvailable()
        {
            if (Availability != "available") throw new CostConnectionException(Availability);
        }

        public static string GetAvailability(string enabled, string storage, string webUrl)
        {
            if (!bool.TryParse(enabled, out var value) || !value) return "disabled";
            if (string.IsNullOrWhiteSpace(storage)) return "storageRequired";
            if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != ""
                || uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath != "/")
                return "configurationRequired";
            return "available";
        }

        public static bool IsOperator(ClaimsPrincipal principal, string tenant, string client)
        {
            return principal?.Identity?.IsAuthenticated == true
                && (principal.FindFirst("tid")?.Value ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value) == tenant
                && principal.HasClaim("aud", client)
                && Guid.TryParse(OperatorId(principal), out _)
                && (principal.IsInRole(OperatorRole) || principal.HasClaim("roles", OperatorRole));
        }

        public static string OperatorId(ClaimsPrincipal principal) =>
            principal?.FindFirst("oid")?.Value ?? principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
    }
}
