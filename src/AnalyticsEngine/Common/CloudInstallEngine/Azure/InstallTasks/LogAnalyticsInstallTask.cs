using Azure;
using Azure.Core;
using Azure.ResourceManager.OperationalInsights;
using Azure.ResourceManager.OperationalInsights.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class LogAnalyticsInstallTask : InstallTaskInAzResourceGroup<LogWorkspaceInfo>
    {
        private readonly bool _allowPublicAccess;

        public LogAnalyticsInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create Log Analytics workspace";

        public async override Task<LogWorkspaceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();
            var desiredAccess = _allowPublicAccess ? OperationalInsightsPublicNetworkAccessType.Enabled : OperationalInsightsPublicNetworkAccessType.Disabled;
            var insightsLogs = Container.GetOperationalInsightsWorkspaces()
                            .Where(r => r.Data.Name == name).SingleOrDefault();

            if (insightsLogs == null)
            {
                _logger.LogInformation($"Creating log-analytics {name} (public access: {(_allowPublicAccess ? "enabled" : "disabled")})...");

                var newWsInfo = new OperationalInsightsWorkspaceData(AzureLocation)
                {
                    PublicNetworkAccessForIngestion = desiredAccess,
                    PublicNetworkAccessForQuery = desiredAccess
                };

                base.EnsureTagsOnNew(newWsInfo.Tags);     // Add configured tags
                var result = await Container.GetOperationalInsightsWorkspaces().CreateOrUpdateAsync(WaitUntil.Completed, name, newWsInfo);

                insightsLogs = result.Value;
            }
            else
            {
                _logger.LogInformation($"Found existing log-analytics {name}");
                await base.EnsureTagsOnExisting(insightsLogs.Data.Tags, insightsLogs.GetTagResource());

                var needsUpdate = false;
                var updateData = new OperationalInsightsWorkspaceData(AzureLocation)
                {
                    PublicNetworkAccessForIngestion = insightsLogs.Data.PublicNetworkAccessForIngestion,
                    PublicNetworkAccessForQuery = insightsLogs.Data.PublicNetworkAccessForQuery
                };

                if (insightsLogs.Data.PublicNetworkAccessForIngestion == null || insightsLogs.Data.PublicNetworkAccessForIngestion.Value != desiredAccess)
                {
                    updateData.PublicNetworkAccessForIngestion = desiredAccess;
                    needsUpdate = true;
                }
                if (insightsLogs.Data.PublicNetworkAccessForQuery == null || insightsLogs.Data.PublicNetworkAccessForQuery.Value != desiredAccess)
                {
                    updateData.PublicNetworkAccessForQuery = desiredAccess;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    _logger.LogInformation($"Updating log-analytics {name} public network access to '{desiredAccess}'...");
                    var result = await Container.GetOperationalInsightsWorkspaces().CreateOrUpdateAsync(WaitUntil.Completed, name, updateData);
                    insightsLogs = result.Value;
                }
            }

            return new LogWorkspaceInfo() { AzureID = insightsLogs.Id, WorkspaceID = insightsLogs.Data.CustomerId.ToString() };
        }
    }
}
