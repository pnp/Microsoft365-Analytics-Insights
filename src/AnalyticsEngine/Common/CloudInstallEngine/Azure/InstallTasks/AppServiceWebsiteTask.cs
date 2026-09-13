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
            var webApp = Container.GetWebSites().AsEnumerable().Where(s => s.Data.Name == _config.ResourceName).SingleOrDefault();
            if (webApp == null)
            {
                var newWebAppInfo = BuildNewWebSiteData(base.AzureLocation, appServicePlan.Id, appServicePlanSku, _allowPublicAccess);

                base.EnsureTagsOnNew(newWebAppInfo.Tags);     // Add configured tags
                if (!supportsAlwaysOn)
                    _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(appServicePlan.Data.Name, appServicePlanSku));

                _logger.LogInformation($"Creating App Service '{_config.ResourceName}' on plan '{appServicePlan.Data.Name}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")})...");
                try
                {
                    var newWebAppReq = await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newWebAppInfo);
                    webApp = newWebAppReq.Value;
                }
                catch (RequestFailedException ex) when (AppServicePlanCapabilities.IsPlanCapabilityConflict(ex))
                {
                    _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(appServicePlan.Data.Name, appServicePlanSku));
                    newWebAppInfo.SiteConfig = BuildSecureSiteConfig(appServicePlanSku, includeAlwaysOn: false);
                    var newWebAppReq = await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newWebAppInfo);
                    webApp = newWebAppReq.Value;
                }
            }
            else
            {
                var needsUpdate = false;
                var webAppUpdateInfo = new WebSiteData(base.AzureLocation);

                // Ensure app has system assigned identity
                if (webApp.HasData && webApp.Data.Identity == null)
                {
                    webAppUpdateInfo.Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned);
                    _logger.LogInformation($"Updating App Service '{_config.ResourceName}' to use System Assigned identity...");
                    needsUpdate = true;
                }

                // Ensure minimum TLS version is 1.2 and enable Always On when the plan supports it.
                var siteConfig = (await webApp.GetWebSiteConfig().GetAsync()).Value.Data;
                var needsTlsUpdate = siteConfig.MinTlsVersion == null || !siteConfig.MinTlsVersion.Value.ToString().Equals(AppServiceSupportedTlsVersion.Tls1_2.ToString());
                var needsAlwaysOnUpdate = supportsAlwaysOn && siteConfig.IsAlwaysOn != true;
                var needsFtpsDisable = siteConfig.FtpsState != AppServiceFtpsState.Disabled;
                var needsPublicAccessUpdate = !string.Equals(webApp.Data.PublicNetworkAccess, desiredAccess, System.StringComparison.OrdinalIgnoreCase);
                if (needsTlsUpdate || needsAlwaysOnUpdate || needsFtpsDisable || needsPublicAccessUpdate)
                {
                    webAppUpdateInfo.PublicNetworkAccess = desiredAccess;
                    webAppUpdateInfo.SiteConfig = BuildSecureSiteConfig(appServicePlanSku, supportsAlwaysOn);
                    if (needsTlsUpdate)
                        _logger.LogInformation($"Updating App Service '{_config.ResourceName}' to enforce TLS 1.2...");
                    if (needsAlwaysOnUpdate)
                        _logger.LogInformation($"Updating App Service '{_config.ResourceName}' to enable Always On...");
                    if (needsFtpsDisable)
                        _logger.LogInformation($"Disabling FTP/FTPS on App Service '{_config.ResourceName}' because deployment uses SCM HTTPS...");
                    if (needsPublicAccessUpdate)
                        _logger.LogInformation($"Updating App Service '{_config.ResourceName}' public network access to '{desiredAccess}'...");
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    base.EnsureTagsOnNew(webAppUpdateInfo.Tags);
                    try
                    {
                        await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, webAppUpdateInfo);
                    }
                    catch (RequestFailedException ex) when (AppServicePlanCapabilities.IsPlanCapabilityConflict(ex))
                    {
                        _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(appServicePlan.Data.Name, appServicePlanSku));
                        if (webAppUpdateInfo.SiteConfig != null)
                            webAppUpdateInfo.SiteConfig.IsAlwaysOn = null;
                        await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, webAppUpdateInfo);
                    }
                }

                await base.EnsureTagsOnExisting(webApp.Data.Tags, webApp.GetTagResource());     // Add configured tags

                _logger.LogInformation($"Using existing App Service '{webApp.Data.DefaultHostName}'.");
            }

            // Kudu ZIP deployment requires SCM publishing credentials.
            var scmPolicy = await webApp.GetScmSiteBasicPublishingCredentialsPolicy().GetAsync();
            var ftpPolicy = await webApp.GetWebSiteFtpPublishingCredentialsPolicy().GetAsync();

            var publishingCredentialsPolicyData = new CsmPublishingCredentialsPoliciesEntityData()
            {
                Allow = true,
            };

            if (scmPolicy.Value.Data.Allow != true)
            {
                _logger.LogInformation($"Enabling basic publishing credentials (SCM) for '{_config.ResourceName}'...");
                await webApp.GetScmSiteBasicPublishingCredentialsPolicy().CreateOrUpdateAsync(
                    WaitUntil.Completed, publishingCredentialsPolicyData);
            }
            if (ftpPolicy.Value.Data.Allow != false)
            {
                _logger.LogInformation($"Disabling FTP basic publishing credentials for '{_config.ResourceName}'...");
                await webApp.GetWebSiteFtpPublishingCredentialsPolicy().CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    new CsmPublishingCredentialsPoliciesEntityData { Allow = false });
            }
            // Configure VNet integration if a subnet ID is provided
            var vnetSubnetId = _config.ContainsKey(CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID) ? _config.GetConfigValue(CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID) : null;
            if (!string.IsNullOrWhiteSpace(vnetSubnetId))
            {
                var currentSubnetId = webApp.Data.VirtualNetworkSubnetId?.ToString();
                if (string.IsNullOrWhiteSpace(currentSubnetId) || !currentSubnetId.Equals(vnetSubnetId, System.StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _logger.LogInformation($"Configuring VNet integration for App Service '{_config.ResourceName}'...");
                        var vnetUpdateData = new WebSiteData(base.AzureLocation)
                        {
                            VirtualNetworkSubnetId = new global::Azure.Core.ResourceIdentifier(vnetSubnetId),
                            SiteConfig = new SiteConfigProperties
                            {
                                IsVnetRouteAllEnabled = true
                            }
                        };
                        await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, vnetUpdateData);
                        _logger.LogInformation($"VNet integration configured for App Service '{_config.ResourceName}' with route-all enabled.");
                    }
                    catch (RequestFailedException ex)
                    {
                        _logger.LogWarning($"Failed to configure VNet integration for App Service: {ex.Message}. " +
                            $"Ensure the integration subnet '{vnetSubnetId}' exists and is delegated to Microsoft.Web/serverFarms. " +
                            $"Without VNet integration, the App Service will use public outbound IPs and Redis must allow public access with firewall rules.");
                    }
                }
                else
                {
                    _logger.LogInformation($"App Service '{_config.ResourceName}' already has VNet integration configured.");
                }
            }

            return webApp;

        }

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
    }
}
