using Azure.Core;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Configures and gets Application Insights API details.
    /// </summary>
    public class AppInsightsConfigureApiTask : AppInsightsTask<AppInsightsInfoWithApiAccess>
    {
        public AppInsightsConfigureApiTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, TokenCredential creds, string subscriptionID, string resourceGroupName)
            : base(config, logger, azureLocation, new Dictionary<string, string>(), resourceGroupName, subscriptionID, creds)
        {
        }

        public override string TaskName => "configure Application Insights API access";

        public async override Task<AppInsightsInfoWithApiAccess> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<AppInsightsInfo>(contextArg);

            var name = _config.GetNameConfigValue();

            var appInfo = (AppInsightsInfo)contextArg;

            // Add API key
            const string KEY_NAME = "O365 Adv Analytics";

            var appInsightsManager = new AppInsightsManager(_credential, _subscriptionId, _resourceGroupName, name);
            var apiKeyInfo = await appInsightsManager.EnsureKey(KEY_NAME, _logger);
            if (apiKeyInfo == null)
            {
                _logger.LogError("Failed to configure Application Insights API key. You'll need to do this manually if you need to read App Insights data");
                return null;
            }

            var appProps = await appInsightsManager.GetAppInsightsInstanceProperties(_logger);
            return new AppInsightsInfoWithApiAccess(appInfo.ID, appInfo.Name, appInfo.ConnectionString, apiKeyInfo.ApiKey, appProps.AppId);
        }
    }
}
