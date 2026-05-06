using Azure;
using Azure.Core;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class ServiceBusNamespaceInstallTask : InstallTaskInAzResourceGroup<ServiceBusNamespaceResource>
    {
        private readonly bool _requirePremiumSku;
        private readonly bool _allowPublicAccess;

        public ServiceBusNamespaceInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool requirePremiumSku = false, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _requirePremiumSku = requirePremiumSku;
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create Service-bus namespace";

        public async override Task<ServiceBusNamespaceResource> ExecuteTaskReturnResult(object contextArg)
        {
            var allNSs = Container.GetServiceBusNamespaces();
            var name = base._config.GetNameConfigValue();
            var desiredAccess = _allowPublicAccess ? ServiceBusPublicNetworkAccess.Enabled : ServiceBusPublicNetworkAccess.Disabled;

            var sbNS = allNSs.Where(ns => ns.Data.Name.ToLower() == name.ToLower()).SingleOrDefault();

            if (sbNS == null)
            {
                var skuLabel = _requirePremiumSku ? "premium" : "basic";
                _logger.LogInformation($"Creating new service-bus namespace '{name}' at {skuLabel} SKU (public access: {(_allowPublicAccess ? "enabled" : "disabled")}). This may take several minutes...");

                var newResourceData = new ServiceBusNamespaceData(AzureLocation);
                newResourceData.MinimumTlsVersion = ServiceBusMinimumTlsVersion.Tls1_2;
                newResourceData.PublicNetworkAccess = desiredAccess;
                if (_requirePremiumSku)
                {
                    newResourceData.Sku = new ServiceBusSku(ServiceBusSkuName.Premium);
                }
                base.EnsureTagsOnNew(newResourceData.Tags);
                var operation = await allNSs.CreateOrUpdateAsync(WaitUntil.Completed, name, newResourceData);

                sbNS = operation.Value;


                _logger.LogInformation($"Created service-bus namespace '{sbNS.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing service-bus namespace '{sbNS.Data.Name}'.");
                await base.EnsureTagsOnExisting(sbNS.Data.Tags, sbNS.GetTagResource());

                bool needsUpdate = false;
                var updateData = new ServiceBusNamespaceData(sbNS.Data.Location);
                if (sbNS.Data.Sku != null)
                    updateData.Sku = sbNS.Data.Sku;
                updateData.MinimumTlsVersion = sbNS.Data.MinimumTlsVersion;
                updateData.PublicNetworkAccess = sbNS.Data.PublicNetworkAccess;

                // Enforce TLS 1.2 minimum
                if (sbNS.Data.MinimumTlsVersion == null || sbNS.Data.MinimumTlsVersion != ServiceBusMinimumTlsVersion.Tls1_2)
                {
                    _logger.LogInformation($"Updating service-bus namespace '{sbNS.Data.Name}' to enforce TLS 1.2 minimum...");
                    updateData.MinimumTlsVersion = ServiceBusMinimumTlsVersion.Tls1_2;
                    needsUpdate = true;
                }

                if (sbNS.Data.PublicNetworkAccess == null || sbNS.Data.PublicNetworkAccess != desiredAccess)
                {
                    _logger.LogInformation($"Updating service-bus namespace '{sbNS.Data.Name}' public network access to '{desiredAccess}'...");
                    updateData.PublicNetworkAccess = desiredAccess;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    var operation = await allNSs.CreateOrUpdateAsync(WaitUntil.Completed, name, updateData);
                    sbNS = operation.Value;
                }

                // Upgrade to Premium if required for private endpoints and not already Premium
                if (_requirePremiumSku && sbNS.Data.Sku.Name != ServiceBusSkuName.Premium)
                {
                    _logger.LogError($"Service Bus namespace '{sbNS.Data.Name}' is on {sbNS.Data.Sku.Name} SKU but private endpoints require Premium. " +
                        $"Azure does not support in-place SKU upgrades to Premium. Please manually migrate to a Premium namespace " +
                        $"(see https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-migrate-standard-premium) " +
                        $"and re-run the installer. Continuing installation...");
                }
            }

            return sbNS;
        }
    }
}
