using Azure.Core;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Doesn't create a RG, just checks if it can see/query for one
    /// </summary>
    public class ResourceGroupTestOnlyInstallJob : AzureInstallJob
    {
        private ResourceGroupResource _resourceGroupFound = null;

        public ResourceGroupTestOnlyInstallJob(ILogger logger, AzureLocation location, SubscriptionResource subscription, string resourceGroupName)
            : base(logger, location, new Dictionary<string, string>(), subscription, resourceGroupName)
        {
        }

        public override async Task Install()
        {
            var t = new GetOrCreateResourceGroupTask(TaskConfig.GetConfigForName(ResourceGroupName), Logger, Location, new Dictionary<string, string>(), _subscription);

            // Remember group found
            _resourceGroupFound = await t.GetOrCreateResourceGroup(false);
        }

        public ResourceGroupResource ResourceGroupFound => _resourceGroupFound;
    }
}
