using Azure.Core;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class AppInsightsInstallTask : AppInsightsTask<AppInsightsInfo>
    {
        const string APP_INSIGHTS_RESOURCE_TYPE = "microsoft.insights/components";


        public AppInsightsInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, string resourceGroupName, string subscriptionId, TokenCredential credential)
                    : base(config, logger, azureLocation, tags, resourceGroupName, subscriptionId, credential)
        {
        }

        public override string TaskName => "get/create Application Insights";

        public async override Task<AppInsightsInfo> ExecuteTaskReturnResult(object contextArg)
        {
            // Ensure/get context & config
            base.EnsureContextArgType<LogWorkspaceInfo>(contextArg);
            var name = _config.GetNameConfigValue();
            var workspaceInfo = (LogWorkspaceInfo)contextArg;

            // Get or create from ARM template
            var createOrUpdateAppInsightsInfo = new
            {
                type = new { value = "web" },
                name = new { value = name },
                regionId = new { value = AzureLocation.Name },
                requestSource = new { value = "IbizaAIExtension" },
                workspaceResourceId = new
                {
                    value = workspaceInfo.AzureID
                },
                tagsArray = new { value = Tags }
            };
            var resourceCreateInfo = await base.GetOrCreateGenericAzResource(name, APP_INSIGHTS_RESOURCE_TYPE,
                createOrUpdateAppInsightsInfo, Properties.Resources.AppInsightsArmTemplate);

            // Read AppInsights info
            var appInsightsManager = new AppInsightsManager(_credential, _subscriptionId, _resourceGroupName, name);
            var appProps = await appInsightsManager.GetAppInsightsInstanceProperties(_logger);

            if (resourceCreateInfo.CreatedNew)
            {
                _logger.LogInformation($"Created new AppInsights with name {name} and instrumentation key {appProps.InstrumentationKey}");
            }
            else
            {
                _logger.LogInformation($"Found existing AppInsights with name {name} and instrumentation key {appProps.InstrumentationKey}");
            }

            return new AppInsightsInfo(resourceCreateInfo.ResourceId, name, appProps.ConnectionString);
        }
    }
}
