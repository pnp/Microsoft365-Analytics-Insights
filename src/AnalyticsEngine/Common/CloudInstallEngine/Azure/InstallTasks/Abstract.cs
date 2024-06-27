using Azure.Core;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{

    /// <summary>
    /// Install task that creates a resource inside a container (resource group)
    /// </summary>
    public abstract class InstallTaskInAzResourceGroup<TASKRESULTINGRESOURCE> : ResourceInstallTaskInContainer<TASKRESULTINGRESOURCE, ResourceGroupResource>
    {
        protected InstallTaskInAzResourceGroup(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger)
        {
            AzureLocation = azureLocation;
            Tags = tags;
        }

        public AzureLocation AzureLocation { get; set; }
        public Dictionary<string, string> Tags { get; }

        protected async Task EnsureTagsOnExisting(IDictionary<string, string> existingTags, TagResource tagResource)
        {
            await base.EnsureTagsOnExisting(existingTags, Tags, tagResource);
        }

        protected void EnsureTagsOnNew(IDictionary<string, string> existingTags)
        {
            base.EnsureTagsOnNew(existingTags, Tags);
        }
    }

    public abstract class AppInsightsTask<TASKRESULTINGRESOURCE> : GenericArmSingleResourceInstallTask<TASKRESULTINGRESOURCE>
    {
        protected readonly string _resourceGroupName;
        protected readonly string _subscriptionId;
        protected readonly TokenCredential _credential;

        public AppInsightsTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, string resourceGroupName, string subscriptionId, TokenCredential credential) : base(config, logger, azureLocation, tags)
        {
            _resourceGroupName = resourceGroupName;
            _subscriptionId = subscriptionId;
            _credential = credential;
        }
    }
}
