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

            var analytics = Container.GetCognitiveServicesAccounts().Where(s => s.Data.Name == name).SingleOrDefault();

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

            var keysResponse = await analytics.GetKeysAsync();

            var cognitiveServicesInfo = new CognitiveServicesInfo
            {
                Endpoint = $"https://{analytics.Data.Location.Name}.api.cognitive.microsoft.com/",
                Key = keysResponse.Value.Key1
            };



            _logger.LogInformation($"{logMsg}");
            return cognitiveServicesInfo;
        }
    }
}
