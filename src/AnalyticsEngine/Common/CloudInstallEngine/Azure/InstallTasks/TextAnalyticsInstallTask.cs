using Azure;
using Azure.Core;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class TextAnalyticsInstallTask : InstallTaskInAzResourceGroup<CognitiveServicesInfo>
    {
        private readonly bool _allowPublicAccess;

        public TextAnalyticsInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create Cognitive Services (text analytics)";

        public async override Task<CognitiveServicesInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var name = _config.GetNameConfigValue();
            var desiredAccess = _allowPublicAccess ? ServiceAccountPublicNetworkAccess.Enabled : ServiceAccountPublicNetworkAccess.Disabled;

            var analytics = Container.GetCognitiveServicesAccounts().AsEnumerable().Where(s => s.Data.Name == name).SingleOrDefault();

            var logMsg = string.Empty;
            if (analytics == null)
            {
                var creationParams = new CognitiveServicesAccountData(AzureLocation)
                {
                    Sku = new CognitiveServicesSku("S"),
                    Kind = "TextAnalytics",
                    Properties = new CognitiveServicesAccountProperties
                    {
                        CustomSubDomainName = name,
                        PublicNetworkAccess = desiredAccess
                    }
                };
                base.EnsureTagsOnNew(creationParams.Tags);

                try
                {
                    var result = await Container.GetCognitiveServicesAccounts().CreateOrUpdateAsync(WaitUntil.Completed, name, creationParams);
                    analytics = result.Value;
                }
                catch (RequestFailedException ex) when (ex.ErrorCode == "ResourceKindRequireAcceptTerms")
                {
                    throw new InstallException(ex.Message);
                }

                logMsg = $"Created new Cognitive Service application '{analytics.Data.Name}' at 'Standard' SKU (public access: {(_allowPublicAccess ? "enabled" : "disabled")}).";
            }
            else
            {
                var needsUpdate = false;
                var updateProps = new CognitiveServicesAccountProperties
                {
                    CustomSubDomainName = analytics.Data.Properties?.CustomSubDomainName ?? name,
                    PublicNetworkAccess = analytics.Data.Properties?.PublicNetworkAccess
                };

                // Ensure custom subdomain is set (required for private endpoints)
                if (string.IsNullOrEmpty(analytics.Data.Properties?.CustomSubDomainName))
                {
                    updateProps.CustomSubDomainName = name;
                    needsUpdate = true;
                }

                if (analytics.Data.Properties?.PublicNetworkAccess == null || analytics.Data.Properties.PublicNetworkAccess.Value != desiredAccess)
                {
                    _logger.LogInformation($"Updating Cognitive Service '{name}' public network access to '{desiredAccess}'...");
                    updateProps.PublicNetworkAccess = desiredAccess;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    var updateParams = new CognitiveServicesAccountData(AzureLocation)
                    {
                        Sku = analytics.Data.Sku,
                        Kind = analytics.Data.Kind,
                        Properties = updateProps
                    };
                    var result = await Container.GetCognitiveServicesAccounts().CreateOrUpdateAsync(WaitUntil.Completed, name, updateParams);
                    analytics = result.Value;
                }

                logMsg = $"Found existing Cognitive Service '{name}'";
                await base.EnsureTagsOnExisting(analytics.Data.Tags, analytics.GetTagResource());
            }

            string accountKey = null;
            try
            {
                var keysResponse = await analytics.GetKeysAsync();
                accountKey = keysResponse.Value.Key1;
            }
            catch (RequestFailedException ex) when (ex.Status == 400 && ex.Message != null && ex.Message.IndexOf("disableLocalAuth", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Account has properties.disableLocalAuth = true (set by a previous install, by an admin,
                // or by Azure Policy). There are no keys to list — runtime must use RBAC instead.
                // Return an empty Key so the App Service connection-string write below stores no key.
                // The runtime CognitiveServicesClient (DataUtils.CognitiveExtensions) will detect the
                // missing key and fall back to ClientSecretCredential / RBAC automatically.
                _logger.LogWarning($"Cognitive Service '{name}' has local-auth (account keys) disabled. " +
                    "Skipping key retrieval — the runtime will authenticate against Cognitive Services using " +
                    "the configured runtime account (RBAC). Ensure the runtime account has the 'Cognitive Services User' " +
                    "role on this resource.");
            }

            // Always prefer the resource's own endpoint - the custom-subdomain form,
            // https://<name>.cognitiveservices.azure.com/. The regional endpoint
            // (https://<region>.api.cognitive.microsoft.com/) supports API-key auth ONLY: Azure rejects
            // Entra ID tokens against it with 400 "Please provide a custom subdomain for token
            // authentication, otherwise API key is required". Hard-coding the regional form therefore
            // broke the RBAC fallback taken just above when disableLocalAuth = true - the install
            // succeeded and sentiment/key-phrase enrichment then failed silently at runtime. It also
            // breaks private endpoints, because the privatelink.cognitiveservices.azure.com zone only
            // ever resolves the custom-subdomain name.
            var endpoint = ResolveEndpoint(
                analytics.Data.Properties?.Endpoint,
                analytics.Data.Properties?.CustomSubDomainName,
                analytics.Data.Location.Name,
                out var usedRegionalFallback);

            if (usedRegionalFallback)
            {
                _logger.LogWarning($"Cognitive Service '{name}' reports neither an endpoint nor a custom subdomain, so the regional endpoint '{endpoint}' will be used. " +
                    "Entra ID (RBAC) authentication does NOT work against regional endpoints — if account keys are disabled on this resource, " +
                    "set a custom subdomain on it and re-run the installer, or sentiment/key-phrase enrichment will fail with " +
                    "'Please provide a custom subdomain for token authentication'.");
            }

            var cognitiveServicesInfo = new CognitiveServicesInfo
            {
                Endpoint = endpoint,
                Key = accountKey ?? string.Empty
            };



            _logger.LogInformation($"{logMsg}");
            return cognitiveServicesInfo;
        }

        /// <summary>
        /// Chooses the Azure AI Language endpoint to hand to the runtime.
        /// </summary>
        /// <remarks>
        /// Order matters. The regional endpoint <c>https://{region}.api.cognitive.microsoft.com/</c> supports
        /// API-key authentication ONLY — Azure rejects Entra ID tokens against it with HTTP 400
        /// "Please provide a custom subdomain for token authentication, otherwise API key is required". Since
        /// the installer falls back to RBAC whenever the resource has <c>disableLocalAuth = true</c>, the
        /// custom-subdomain endpoint is the only form that works in that configuration. It is also the only
        /// form a <c>privatelink.cognitiveservices.azure.com</c> private DNS zone can resolve.
        /// </remarks>
        /// <param name="usedRegionalFallback">
        /// True when neither an endpoint nor a custom subdomain was available and the regional form was used
        /// as a last resort — the caller should warn, because RBAC auth cannot work against it.
        /// </param>
        public static string ResolveEndpoint(string reportedEndpoint, string customSubDomain, string location, out bool usedRegionalFallback)
        {
            usedRegionalFallback = false;

            if (!string.IsNullOrWhiteSpace(reportedEndpoint))
            {
                return reportedEndpoint;
            }

            // Every path in this task sets CustomSubDomainName, so this is the normal fallback for a freshly
            // created account whose Endpoint ARM has not populated yet.
            if (!string.IsNullOrWhiteSpace(customSubDomain))
            {
                return $"https://{customSubDomain}.cognitiveservices.azure.com/";
            }

            usedRegionalFallback = true;
            return $"https://{location}.api.cognitive.microsoft.com/";
        }
    }
}
