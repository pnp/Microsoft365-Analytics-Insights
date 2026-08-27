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

        /// <summary>
        /// Standard warning text used when a private deployment can't make Service Bus private. Public so the
        /// installer UI can show the same wording before an install starts.
        /// </summary>
        public const string NOT_PRIVATE_CAPABLE_WARNING =
            "Service Bus is on the Standard SKU and cannot be made private (private endpoints require Premium). "
            + "With public network access disabled the namespace would be unreachable from the VNet and the TEAMS CALLS IMPORT WOULD NOT WORK "
            + "(the importer fails with 'Put token failed. status-code: 401 ... Ip has been prevented to connect to the endpoint'). "
            + "Options: (a) migrate the namespace to Premium and re-run the installer "
            + "(https://learn.microsoft.com/azure/service-bus-messaging/service-bus-migrate-standard-premium), "
            + "(b) keep public network access enabled on Service Bus, or (c) disable the Teams calls import.";

        /// <summary>
        /// The minimum TLS version every namespace must enforce.
        /// </summary>
        /// <remarks>
        /// Deliberately <c>Tls12</c> and NOT the near-identically named <c>Tls1_2</c>. In
        /// Azure.ResourceManager.ServiceBus 1.2.0 the underscored members are deprecated aliases whose
        /// underlying value is the EMPTY STRING, while the real API values ("1.0".."1.3") live on
        /// <c>Tls10</c>..<c>Tls13</c>. Comparing against the empty alias made the "needs updating" test
        /// true on every single run, so the installer logged "Updating service-bus namespace '...' to
        /// enforce TLS 1.2 minimum..." and re-PUT the namespace forever - while sending
        /// <c>minimumTlsVersion: ""</c>, so TLS 1.2 was never actually enforced either.
        ///
        /// The sibling Storage, Redis and App Service SDKs do not share the quirk, so only this task
        /// was affected. ServiceBusNamespaceInstallTaskTests pins the value so an SDK upgrade (or a
        /// well-meaning rename back to Tls1_2) can't silently reintroduce the bug.
        /// </remarks>
        public static readonly ServiceBusMinimumTlsVersion RequiredMinimumTlsVersion = ServiceBusMinimumTlsVersion.Tls12;

        public ServiceBusNamespaceInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool requirePremiumSku = false, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _requirePremiumSku = requirePremiumSku;
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create Service-bus namespace";

        /// <summary>
        /// True when an existing namespace still needs its minimum TLS version raising to
        /// <see cref="RequiredMinimumTlsVersion"/>. A namespace already at 1.2 must return false, or the
        /// installer re-writes it on every run.
        /// </summary>
        public static bool NeedsMinimumTlsUpdate(ServiceBusMinimumTlsVersion? current)
        {
            return current == null || current.Value != RequiredMinimumTlsVersion;
        }

        /// <summary>
        /// True when the namespace this task produced can host a private endpoint (i.e. it is Premium). False
        /// means a private deployment cannot make Service Bus private, so we deliberately leave public access on.
        /// Null until the task has run.
        /// </summary>
        public bool? IsPrivateEndpointCapable { get; private set; }

        /// <summary>
        /// True when a private deployment asked for public access to be disabled but we kept it enabled because
        /// the namespace can't have a private endpoint - disabling it would silently break the Teams calls import.
        /// </summary>
        public bool PublicAccessKeptOpenForReachability { get; private set; }

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
                newResourceData.MinimumTlsVersion = RequiredMinimumTlsVersion;
                newResourceData.PublicNetworkAccess = desiredAccess;
                if (_requirePremiumSku)
                {
                    newResourceData.Sku = new ServiceBusSku(ServiceBusSkuName.Premium);
                }
                base.EnsureTagsOnNew(newResourceData.Tags);
                var operation = await allNSs.CreateOrUpdateAsync(WaitUntil.Completed, name, newResourceData);

                sbNS = operation.Value;

                // A namespace we just created at the SKU we asked for is private-capable whenever we asked for Premium.
                IsPrivateEndpointCapable = IsPremium(sbNS) || _requirePremiumSku;

                _logger.LogInformation($"Created service-bus namespace '{sbNS.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing service-bus namespace '{sbNS.Data.Name}'.");
                await base.EnsureTagsOnExisting(sbNS.Data.Tags, sbNS.GetTagResource());

                // Azure has no in-place upgrade to Premium, so an existing non-Premium namespace in a private
                // deployment can never get a private endpoint. Work that out BEFORE touching public access:
                // disabling public access on a namespace that can't be reached privately makes it unreachable
                // altogether, which silently kills the Teams calls import. See issue #228.
                IsPrivateEndpointCapable = !_requirePremiumSku || IsPremium(sbNS);

                var effectiveDesiredAccess = desiredAccess;
                if (_requirePremiumSku && !IsPremium(sbNS))
                {
                    _logger.LogError($"Service Bus namespace '{sbNS.Data.Name}' is on the {sbNS.Data.Sku?.Name} SKU but private endpoints require Premium. "
                        + "Azure does not support in-place SKU upgrades to Premium. " + NOT_PRIVATE_CAPABLE_WARNING);

                    if (desiredAccess == ServiceBusPublicNetworkAccess.Disabled)
                    {
                        // Prefer "reachable and warned about" over "private and silently broken".
                        effectiveDesiredAccess = ServiceBusPublicNetworkAccess.Enabled;
                        PublicAccessKeptOpenForReachability = true;
                        _logger.LogWarning($"Service Bus: leaving public network access ENABLED on '{sbNS.Data.Name}' even though this is a private deployment, "
                            + "because the namespace cannot have a private endpoint and disabling public access would make the Teams calls import fail silently. "
                            + "Migrate the namespace to Premium and re-run the installer to make it private, or disable the Teams calls import if it isn't needed.");
                    }
                }

                bool needsUpdate = false;
                var updateData = new ServiceBusNamespaceData(sbNS.Data.Location);
                if (sbNS.Data.Sku != null)
                    updateData.Sku = sbNS.Data.Sku;
                updateData.MinimumTlsVersion = sbNS.Data.MinimumTlsVersion;
                updateData.PublicNetworkAccess = sbNS.Data.PublicNetworkAccess;

                // Carry the tags across. CreateOrUpdate is a PUT, so any tag missing from the payload is
                // dropped - which is why the namespace was the only untagged resource in an otherwise
                // fully tagged resource group. The read-back snapshot predates the EnsureTagsOnExisting
                // PATCH above, so re-apply the configured tags on top of it as well.
                foreach (var tag in sbNS.Data.Tags)
                {
                    updateData.Tags[tag.Key] = tag.Value;
                }
                base.EnsureTagsOnNew(updateData.Tags);

                // Enforce TLS 1.2 minimum
                if (NeedsMinimumTlsUpdate(sbNS.Data.MinimumTlsVersion))
                {
                    _logger.LogInformation($"Updating service-bus namespace '{sbNS.Data.Name}' to enforce TLS 1.2 minimum...");
                    updateData.MinimumTlsVersion = RequiredMinimumTlsVersion;
                    needsUpdate = true;
                }

                if (sbNS.Data.PublicNetworkAccess == null || sbNS.Data.PublicNetworkAccess != effectiveDesiredAccess)
                {
                    _logger.LogInformation($"Updating service-bus namespace '{sbNS.Data.Name}' public network access to '{effectiveDesiredAccess}'...");
                    updateData.PublicNetworkAccess = effectiveDesiredAccess;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    var operation = await allNSs.CreateOrUpdateAsync(WaitUntil.Completed, name, updateData);
                    sbNS = operation.Value;
                }
            }

            return sbNS;
        }

        private static bool IsPremium(ServiceBusNamespaceResource ns)
            => ns?.Data?.Sku != null && ns.Data.Sku.Name == ServiceBusSkuName.Premium;
    }
}
