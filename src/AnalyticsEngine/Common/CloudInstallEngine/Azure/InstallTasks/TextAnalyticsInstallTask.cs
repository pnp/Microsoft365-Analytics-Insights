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
        public TextAnalyticsInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create Cognitive Services (text analytics)";

        public async override Task<CognitiveServicesInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var name = _config.GetNameConfigValue();

            var analytics = Container.GetCognitiveServicesAccounts().Where(s => s.Data.Name == name).SingleOrDefault();

            var logMsg = string.Empty;
            if (analytics == null)
            {
                var creationParams = new CognitiveServicesAccountData(AzureLocation)
                {
                    Sku = new CognitiveServicesSku("S"),
                    Kind = "TextAnalytics"
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

                logMsg = $"Created new Cognitive Service application '{analytics.Data.Name}' at 'Standard' SKU.";
            }
            else
            {
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
