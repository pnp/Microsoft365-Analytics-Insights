using Azure;
using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Gets or creates a private endpoint for a given Azure resource.
    /// </summary>
    public class PrivateEndpointInstallTask : InstallTaskInAzResourceGroup<PrivateEndpointResource>
    {
        public const string CONFIG_KEY_TARGET_RESOURCE_ID = "targetResourceId";
        public const string CONFIG_KEY_GROUP_ID = "groupId";
        public const string CONFIG_KEY_SUBNET_ID = "subnetId";

        // ARM reads should normally return in a few seconds. When they take longer than this
        // we emit a WARN so unexpectedly-slow ARM calls (regional issues, throttling, etc.) are
        // visible in the install log instead of silently inflating the run duration.
        private const int SlowArmReadWarningSeconds = 20;

        public PrivateEndpointInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create private endpoint";

        public override async Task<PrivateEndpointResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = _config.GetNameConfigValue();
            var targetResourceId = _config.GetConfigValue(CONFIG_KEY_TARGET_RESOURCE_ID);
            var groupId = _config.GetConfigValue(CONFIG_KEY_GROUP_ID);
            var subnetId = _config.GetConfigValue(CONFIG_KEY_SUBNET_ID);
            var targetShortName = ShortResourceName(targetResourceId);

            PrivateEndpointResource pe = null;
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await Container.GetPrivateEndpointAsync(name);
                pe = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }
            WarnIfSlow(sw, $"reading private endpoint '{name}'");

            // Check if existing PE is disconnected (e.g. target resource was deleted and recreated) and needs to be recreated
            if (pe != null)
            {
                var connection = pe.Data.PrivateLinkServiceConnections?.FirstOrDefault();
                var connectionState = connection?.ConnectionState?.Status;
                if (!string.IsNullOrEmpty(connectionState) &&
                    !string.Equals(connectionState, "Approved", System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Existing private endpoint '{name}' has connection status '{connectionState}'. Deleting and recreating for '{targetShortName}'...");
                    await pe.DeleteAsync(WaitUntil.Completed);
                    pe = null;
                }
            }

            if (pe == null)
            {
                _logger.LogInformation($"Creating private endpoint '{name}' for '{targetShortName}'...");

                var peData = new PrivateEndpointData()
                {
                    Location = AzureLocation,
                    Subnet = new SubnetData() { Id = new ResourceIdentifier(subnetId) },
                };
                peData.PrivateLinkServiceConnections.Add(new NetworkPrivateLinkServiceConnection()
                {
                    Name = name,
                    PrivateLinkServiceId = new ResourceIdentifier(targetResourceId),
                    GroupIds = { groupId },
                });

                EnsureTagsOnNew(peData.Tags);
                var operation = await Container.GetPrivateEndpoints().CreateOrUpdateAsync(WaitUntil.Completed, name, peData);
                pe = operation.Value;

                _logger.LogInformation($"Created private endpoint '{pe.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing private endpoint '{pe.Data.Name}' for '{targetShortName}'. No changes made.");
                await EnsureTagsOnExisting(pe.Data.Tags, pe.GetTagResource());
            }

            return pe;
        }

        private void WarnIfSlow(Stopwatch sw, string operation)
        {
            sw.Stop();
            if (sw.Elapsed.TotalSeconds >= SlowArmReadWarningSeconds)
            {
                _logger.LogWarning($"ARM operation '{operation}' took {(int)sw.Elapsed.TotalSeconds}s — slower than expected ({SlowArmReadWarningSeconds}s threshold). Could indicate ARM regional latency or throttling.");
            }
        }

        /// <summary>
        /// Returns just the trailing resource name from an ARM resource ID, e.g.
        /// "/subscriptions/.../servers/foo" → "foo". Keeps install logs scannable.
        /// </summary>
        private static string ShortResourceName(string resourceId)
        {
            if (string.IsNullOrEmpty(resourceId)) return resourceId;
            var idx = resourceId.LastIndexOf('/');
            return idx < 0 ? resourceId : resourceId.Substring(idx + 1);
        }
    }
}
