using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Loads resource-group only. 
    /// </summary>
    public class ResourceGroupContainerLoader : ResourceInstallTask<ResourceGroupResource>
    {
        private readonly SubscriptionResource _subscription;
        private readonly AzureLocation _location;
        private readonly Dictionary<string, string> _tags;

        public ResourceGroupContainerLoader(TaskConfig config, ILogger logger, SubscriptionResource subscription, AzureLocation location, Dictionary<string, string> tags) : base(config, logger)
        {
            _subscription = subscription;
            _location = location;
            _tags = tags;
        }

        public override string TaskName => "Resource group creator";

        public override async Task<ResourceGroupResource> ExecuteTaskReturnResult(object contextArg)
        {
            var rgTask = new GetOrCreateResourceGroupTask(_config, _logger, _location, _tags, _subscription);
            var rg = await rgTask.GetOrCreateResourceGroup(true);

            return rg;
        }
    }

    public class GetOrCreateResourceGroupTask : BaseInstallTask
    {
        private readonly SubscriptionResource _subscription;
        private readonly AzureLocation _location;
        private readonly Dictionary<string, string> _tags;

        public GetOrCreateResourceGroupTask(TaskConfig config, ILogger logger, AzureLocation location, Dictionary<string, string> tags, SubscriptionResource subscription) : base(config, logger)
        {
            _subscription = subscription;
            _location = location;
            _tags = tags;
        }


        public override string TaskName => "get/create resource group";

        public override async Task<object> ExecuteTask(object contextArg)
        {
            return await GetOrCreateResourceGroup(true);
        }

        public async Task<ResourceGroupResource> GetOrCreateResourceGroup(bool createIfNotExists)
        {
            var resourceGroups = _subscription.GetResourceGroups();
            ResourceGroupResource resourceGroup = null;
            try
            {
                resourceGroup = await resourceGroups.GetAsync(_config.ResourceName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Ignore
            }

            if (resourceGroup != null)
            {
                _logger.LogInformation($"Have already resource-group '{_config.ResourceName}'.");
                if (createIfNotExists)
                {
                    await base.EnsureTagsOnExisting(resourceGroup.Data.Tags, _tags, resourceGroup.GetTagResource());
                }
                return resourceGroup;
            }

            // Create
            if (createIfNotExists)
            {
                Console.WriteLine($"Creating resource-group '{_config.ResourceName}'...");

                var resourceGroupData = new ResourceGroupData(_location);
                base.EnsureTagsOnNew(resourceGroupData.Tags, _tags);
                var operation = await resourceGroups.CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, resourceGroupData);

                _logger.LogInformation($"Created resource-group '{_config.ResourceName}'.");
                return operation.Value;
            }
            else
            {
                _logger.LogInformation($"Can't find resource-group '{_config.ResourceName}', but no errors in calling Azure APIs.");
                return null;
            }

        }
    }
}
