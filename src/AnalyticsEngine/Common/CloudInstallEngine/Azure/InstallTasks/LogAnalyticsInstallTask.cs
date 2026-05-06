using Azure;
using Azure.Core;
using Azure.ResourceManager.OperationalInsights;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class LogAnalyticsInstallTask : InstallTaskInAzResourceGroup<LogWorkspaceInfo>
    {
        // Note: Log Analytics public network access is intentionally not toggled by the installer.
        // Disabling public access on a workspace requires an Azure Monitor Private Link Scope (AMPLS)
        // plus the corresponding private DNS zones, which can affect Azure Monitor connectivity for
        // every VNet that resolves those zones (potentially across unrelated workloads/subscriptions).
        // Customers that need a private workspace must configure AMPLS manually after install.
        public LogAnalyticsInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create Log Analytics workspace";

        public async override Task<LogWorkspaceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();
            var insightsLogs = Container.GetOperationalInsightsWorkspaces()
                            .Where(r => r.Data.Name == name).SingleOrDefault();

            if (insightsLogs == null)
            {
                _logger.LogInformation($"Creating log-analytics {name}...");

                var newWsInfo = new OperationalInsightsWorkspaceData(AzureLocation);

                base.EnsureTagsOnNew(newWsInfo.Tags);     // Add configured tags
                var result = await Container.GetOperationalInsightsWorkspaces().CreateOrUpdateAsync(WaitUntil.Completed, name, newWsInfo);

                insightsLogs = result.Value;
            }
            else
            {
                _logger.LogInformation($"Found existing log-analytics {name}");
                await base.EnsureTagsOnExisting(insightsLogs.Data.Tags, insightsLogs.GetTagResource());
            }

            return new LogWorkspaceInfo() { AzureID = insightsLogs.Id, WorkspaceID = insightsLogs.Data.CustomerId.ToString() };
        }
    }
}
