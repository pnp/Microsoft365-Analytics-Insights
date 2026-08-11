using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.PrivateDns;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Service Bus-aware wrapper around <see cref="PrivateEndpointInstallTask"/>.
    /// Private endpoints for Service Bus require the Premium SKU, and Azure has no in-place upgrade to Premium,
    /// so a pre-existing Standard namespace can never get one. Attempting it fails the task with an opaque Azure
    /// error; instead we skip it and explain the consequence for the Teams calls import. See issue #228.
    /// </summary>
    public class ServiceBusPrivateEndpointInstallTask : InstallTaskInAzResourceGroup<PrivateEndpointResource>
    {
        private readonly ServiceBusNamespaceInstallTask _namespaceTask;

        public ServiceBusPrivateEndpointInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, ServiceBusNamespaceInstallTask namespaceTask)
            : base(config, logger, azureLocation, tags)
        {
            _namespaceTask = namespaceTask;
        }

        public override string TaskName => "get/create Service Bus private endpoint";

        public override async Task<PrivateEndpointResource> ExecuteTaskReturnResult(object contextArg)
        {
            if (_namespaceTask?.IsPrivateEndpointCapable == false)
            {
                _logger.LogWarning("Skipping Service Bus private endpoint creation: " + ServiceBusNamespaceInstallTask.NOT_PRIVATE_CAPABLE_WARNING);
                return null;
            }

            var innerTask = new PrivateEndpointInstallTask(_config, _logger, AzureLocation, Tags)
            {
                Container = base.Container,
            };
            return await innerTask.ExecuteTaskReturnResult(contextArg);
        }
    }

    /// <summary>
    /// Service Bus-aware wrapper around <see cref="PrivateDnsZoneInstallTask"/>.
    /// Skips the zone when the namespace can't have a private endpoint. Creating a VNet-linked
    /// <c>privatelink.servicebus.windows.net</c> zone with no endpoint behind it is worse than doing nothing: it
    /// can hijack resolution of the public hostname and leave a confusing orphan behind. See issues #228 / #229.
    /// </summary>
    public class ServiceBusPrivateDnsZoneInstallTask : InstallTaskInAzResourceGroup<PrivateDnsZoneResource>
    {
        private readonly ServiceBusNamespaceInstallTask _namespaceTask;

        public ServiceBusPrivateDnsZoneInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, ServiceBusNamespaceInstallTask namespaceTask)
            : base(config, logger, azureLocation, tags)
        {
            _namespaceTask = namespaceTask;
        }

        public override string TaskName => "get/create Service Bus private DNS zone";

        public override async Task<PrivateDnsZoneResource> ExecuteTaskReturnResult(object contextArg)
        {
            if (_namespaceTask?.IsPrivateEndpointCapable == false)
            {
                _logger.LogInformation("Skipping Service Bus private DNS zone creation: the namespace has no private endpoint (Premium SKU required), "
                    + "so the zone would not resolve to anything and could break access to the public endpoint.");
                return null;
            }

            var innerTask = new PrivateDnsZoneInstallTask(_config, _logger, AzureLocation, Tags)
            {
                Container = base.Container,
            };
            return await innerTask.ExecuteTaskReturnResult(contextArg);
        }
    }
}
