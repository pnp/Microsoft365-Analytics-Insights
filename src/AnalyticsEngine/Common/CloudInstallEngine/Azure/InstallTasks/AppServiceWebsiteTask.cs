using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Models;
using CloudInstallEngine.Azure;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
// Azure.ResourceManager.AppService 1.5.0 added its own ManagedServiceIdentityType, which collides with
// the ARM-wide one. ManagedServiceIdentity's constructor takes the ARM-wide type, so pin the alias to it.
using ManagedServiceIdentityType = Azure.ResourceManager.Models.ManagedServiceIdentityType;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class AppServiceWebsiteTask : InstallTaskInAzResourceGroup<WebSiteResource>
    {
        public const string CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID = "vnetIntegrationSubnetId";
        private readonly bool _allowPublicAccess;

        public AppServiceWebsiteTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create App Service website";


        public override async Task<WebSiteResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<AppServicePlanResource>(contextArg);

            var appServicePlan = (AppServicePlanResource)contextArg;
            var desiredAccess = _allowPublicAccess ? "Enabled" : "Disabled";
            var appServicePlanSku = appServicePlan.Data.Sku;
            var supportsAlwaysOn = AppServicePlanCapabilities.SupportsAlwaysOn(appServicePlanSku);

            // Get/create app-service with plan
            var webApp = Container.GetWebSites().GetAll(cancellationToken: CancellationToken).Where(s => s.Data.Name == _config.ResourceName).SingleOrDefault();
            if (webApp == null)
            {
                var newWebAppInfo = BuildNewWebSiteData(base.AzureLocation, appServicePlan.Id, appServicePlanSku, _allowPublicAccess);

                base.EnsureTagsOnNew(newWebAppInfo.Tags);     // Add configured tags
                if (!supportsAlwaysOn)
                    _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(appServicePlan.Data.Name, appServicePlanSku));

                _logger.LogInformation($"Creating App Service '{_config.ResourceName}' on plan '{appServicePlan.Data.Name}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")})...");
                try
                {
                    var newWebAppReq = await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newWebAppInfo, cancellationToken: CancellationToken);
                    webApp = newWebAppReq.Value;
                }
                catch (RequestFailedException ex) when (AppServicePlanCapabilities.IsPlanCapabilityConflict(ex))
                {
                    _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(appServicePlan.Data.Name, appServicePlanSku));
                    newWebAppInfo.SiteConfig = BuildSecureSiteConfig(appServicePlanSku, includeAlwaysOn: false);
                    var newWebAppReq = await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newWebAppInfo, cancellationToken: CancellationToken);
                    webApp = newWebAppReq.Value;
                }
            }
            else
            {
                // PATCH, never a PUT of a fresh WebSiteData. That PUT carried only the identity, public access, site
                // config and the configured tags, so it removed every other tag on the site and risked resetting
                // anything else it left out. A PATCH changes only the properties it names.
                var patch = new SitePatchInfo();
                var needsUpdate = false;

                // Ensure app has system assigned identity
                if (webApp.HasData && webApp.Data.Identity == null)
                {
                    patch.Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned);
                    _logger.LogInformation($"Updating App Service '{_config.ResourceName}' to use System Assigned identity...");
                    needsUpdate = true;
                }

                // Raise a minimum TLS version below 1.2, disable FTP/FTPS and enable Always On when the plan supports it.
                // Only the settings that need changing are sent, so a stricter setting an operator chose (TLS 1.3) stays.
                var siteConfig = (await webApp.GetWebSiteConfig().GetAsync(cancellationToken: CancellationToken)).Value.Data;
                var configChanges = new List<string>();
                var siteConfigPatch = BuildSiteConfigChanges(siteConfig, supportsAlwaysOn, configChanges);
                if (siteConfigPatch != null)
                {
                    patch.SiteConfig = siteConfigPatch;
                    foreach (var change in configChanges)
                        _logger.LogInformation($"Updating App Service '{_config.ResourceName}': {change}...");
                    needsUpdate = true;
                }

                if (!string.Equals(webApp.Data.PublicNetworkAccess, desiredAccess, System.StringComparison.OrdinalIgnoreCase))
                {
                    patch.PublicNetworkAccess = desiredAccess;
                    _logger.LogInformation($"Updating App Service '{_config.ResourceName}' public network access to '{desiredAccess}'...");
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    try
                    {
                        webApp = (await webApp.UpdateAsync(patch, CancellationToken)).Value;
                    }
                    catch (RequestFailedException ex) when (AppServicePlanCapabilities.IsPlanCapabilityConflict(ex))
                    {
                        _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(appServicePlan.Data.Name, appServicePlanSku));
                        if (patch.SiteConfig != null)
                            patch.SiteConfig.IsAlwaysOn = null;
                        webApp = (await webApp.UpdateAsync(patch, CancellationToken)).Value;
                    }
                }

                await base.EnsureTagsOnExisting(webApp.Data.Tags, webApp.GetTagResource());     // Add configured tags

                _logger.LogInformation($"Using existing App Service '{webApp.Data.DefaultHostName}'.");
            }

            // Content is deployed through Kudu with a Microsoft Entra token (InstallAppServiceContentsTask), so the
            // installer no longer switches SCM basic publishing credentials on. A site whose operator turned them off
            // keeps them off. FTP basic credentials are still turned off, because nothing here uses FTP.
            var ftpPolicy = await webApp.GetWebSiteFtpPublishingCredentialsPolicy().GetAsync(cancellationToken: CancellationToken);
            if (ftpPolicy.Value.Data.Allow != false)
            {
                _logger.LogInformation($"Disabling FTP basic publishing credentials for '{_config.ResourceName}'...");
                await webApp.GetWebSiteFtpPublishingCredentialsPolicy().CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    new CsmPublishingCredentialsPoliciesEntityData { Allow = false }, cancellationToken: CancellationToken);
            }
            // Configure VNet integration if a subnet ID is provided
            var vnetSubnetId = _config.ContainsKey(CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID) ? _config.GetConfigValue(CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID) : null;
            if (!string.IsNullOrWhiteSpace(vnetSubnetId))
            {
                var currentSubnetId = webApp.Data.VirtualNetworkSubnetId?.ToString();
                if (string.IsNullOrWhiteSpace(currentSubnetId) || !currentSubnetId.Equals(vnetSubnetId, System.StringComparison.OrdinalIgnoreCase))
                {
                    var integration = new PrivateNetworkArtifact
                    {
                        Id = webApp.Id.ToString(), Kind = PrivateNetworkProvenance.Integration,
                        TargetId = webApp.Id.ToString(), SubnetId = vnetSubnetId,
                        PreviousRouteAllEnabled = (await webApp.GetWebSiteConfig().GetAsync(cancellationToken: CancellationToken)).Value.Data.IsVnetRouteAllEnabled == true
                    };
                    if (Networking != null)
                    {
                        if (!string.IsNullOrWhiteSpace(currentSubnetId))
                            throw PrivateNetworkProvenance.Blocked(webApp.Id.ToString(), "an existing App Service VNet integration uses another subnet");
                        Networking.ObserveMissing(integration.Id);
                        Networking.BeginCreate(integration);
                    }
                    try
                    {
                        _logger.LogInformation($"Configuring VNet integration for App Service '{_config.ResourceName}'...");
                        // PATCH: the PUT this replaced carried no tags, so it removed every tag on the site.
                        var vnetPatch = new SitePatchInfo
                        {
                            VirtualNetworkSubnetId = new global::Azure.Core.ResourceIdentifier(vnetSubnetId),
                            SiteConfig = new SiteConfigProperties
                            {
                                IsVnetRouteAllEnabled = true
                            }
                        };
                        webApp = (await webApp.UpdateAsync(vnetPatch, CancellationToken)).Value;
                        Networking?.ConfirmCreate(integration.Id);
                        _logger.LogInformation($"VNet integration configured for App Service '{_config.ResourceName}' with route-all enabled.");
                    }
                    catch (RequestFailedException ex)
                    {
                        if (Networking != null) throw;
                        _logger.LogWarning($"Failed to configure VNet integration for App Service: {ex.Message}. " +
                            $"Ensure the integration subnet '{vnetSubnetId}' exists and is delegated to Microsoft.Web/serverFarms. " +
                            $"Without VNet integration, the App Service will use public outbound IPs to reach public Azure PaaS endpoints.");
                    }
                }
                else
                {
                    Networking?.ObserveExisting(new PrivateNetworkArtifact
                    {
                        Id = webApp.Id.ToString(), Kind = PrivateNetworkProvenance.Integration,
                        TargetId = webApp.Id.ToString(), SubnetId = vnetSubnetId
                    });
                    _logger.LogInformation($"App Service '{_config.ResourceName}' already has VNet integration configured.");
                }
            }

            return webApp;

        }

        public PrivateNetworkProvenance Networking { get; set; }

        public static WebSiteData BuildNewWebSiteData(AzureLocation azureLocation, ResourceIdentifier appServicePlanId, AppServiceSkuDescription appServicePlanSku, bool allowPublicAccess)
        {
            return new WebSiteData(azureLocation)
            {
                AppServicePlanId = appServicePlanId,
                IsHttpsOnly = true,
                PublicNetworkAccess = allowPublicAccess ? "Enabled" : "Disabled",
                SiteConfig = BuildSecureSiteConfig(appServicePlanSku),
                Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned)
            };
        }

        public static SiteConfigProperties BuildSecureSiteConfig(AppServiceSkuDescription appServicePlanSku, bool includeAlwaysOn = true)
        {
            var siteConfig = new SiteConfigProperties
            {
                MinTlsVersion = AppServiceSupportedTlsVersion.Tls1_2,
                FtpsState = AppServiceFtpsState.Disabled
            };

            if (includeAlwaysOn && AppServicePlanCapabilities.SupportsAlwaysOn(appServicePlanSku))
            {
                siteConfig.IsAlwaysOn = true;
            }

            return siteConfig;
        }

        /// <summary>
        /// The site-config changes an existing site needs, or null when it needs none. Each setting is only ever
        /// tightened: a minimum TLS version below 1.2 is raised (1.2 and 1.3 are left alone), FTP/FTPS is turned off,
        /// and Always On is turned on when the plan supports it. Nothing else is sent.
        /// </summary>
        public static SiteConfigProperties BuildSiteConfigChanges(SiteConfigData current, bool supportsAlwaysOn, ICollection<string> changes = null)
        {
            SiteConfigProperties patch = null;
            SiteConfigProperties Patch() => patch ?? (patch = new SiteConfigProperties());

            if (TlsVersionPolicy.NeedsRaising(current?.MinTlsVersion?.ToString()))
            {
                Patch().MinTlsVersion = AppServiceSupportedTlsVersion.Tls1_2;
                changes?.Add($"raise the minimum TLS version from '{current?.MinTlsVersion?.ToString() ?? "not set"}' to 1.2");
            }
            if (current?.FtpsState != AppServiceFtpsState.Disabled)
            {
                Patch().FtpsState = AppServiceFtpsState.Disabled;
                changes?.Add("disable FTP/FTPS, because deployment uses SCM HTTPS");
            }
            if (supportsAlwaysOn && current?.IsAlwaysOn != true)
            {
                Patch().IsAlwaysOn = true;
                changes?.Add("enable Always On");
            }
            return patch;
        }
    }
}
