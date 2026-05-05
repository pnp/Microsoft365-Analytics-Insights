using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class AppServiceWebsiteTask : InstallTaskInAzResourceGroup<WebSiteResource>
    {
        public const string CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID = "vnetIntegrationSubnetId";

        public AppServiceWebsiteTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create App Service website";


        public override async Task<WebSiteResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<AppServicePlanResource>(contextArg);

            var appServicePlan = (AppServicePlanResource)contextArg;

            // Get/create app-service with plan
            var webApp = Container.GetWebSites().Where(s => s.Data.Name == _config.ResourceName).SingleOrDefault();
            if (webApp == null)
            {
                var newWebAppInfo = new WebSiteData(base.AzureLocation)
                {
                    AppServicePlanId = appServicePlan.Id,
                    IsHttpsOnly = true,
                    SiteConfig = new SiteConfigProperties
                    {
                        IsAlwaysOn = true,
                        FtpsState = AppServiceFtpsState.FtpsOnly,
                        MinTlsVersion = AppServiceSupportedTlsVersion.Tls1_2,
                        PublicNetworkAccess = "Enabled"  // Explicit: adding a PE later auto-disables this otherwise
                    },
                    Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned)
                };

                base.EnsureTagsOnNew(newWebAppInfo.Tags);     // Add configured tags
                _logger.LogInformation($"Creating App Service '{_config.ResourceName}' on plan '{appServicePlan.Data.Name}'...");
                var newWebAppReq = await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newWebAppInfo);
                webApp = newWebAppReq.Value;
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

                // Ensure minimum TLS version is 1.2
                var siteConfig = (await webApp.GetWebSiteConfig().GetAsync()).Value.Data;
                if (siteConfig.MinTlsVersion == null || !siteConfig.MinTlsVersion.Value.ToString().Equals(AppServiceSupportedTlsVersion.Tls1_2.ToString()))
                {
                    webAppUpdateInfo.SiteConfig = new SiteConfigProperties
                    {
                        MinTlsVersion = AppServiceSupportedTlsVersion.Tls1_2
                    };
                    _logger.LogInformation($"Updating App Service '{_config.ResourceName}' to enforce TLS 1.2...");
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    base.EnsureTagsOnNew(webAppUpdateInfo.Tags);
                    await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, webAppUpdateInfo);
                }

                await base.EnsureTagsOnExisting(webApp.Data.Tags, webApp.GetTagResource());     // Add configured tags

                _logger.LogInformation($"Using existing App Service '{webApp.Data.DefaultHostName}'.");
            }

            // Enable basic publishing credentials for SCM and FTP if not already enabled
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
            if (ftpPolicy.Value.Data.Allow != true)
            {
                _logger.LogInformation($"Enabling basic publishing credentials (FTP) for '{_config.ResourceName}'...");
                await webApp.GetWebSiteFtpPublishingCredentialsPolicy().CreateOrUpdateAsync(
                    WaitUntil.Completed, publishingCredentialsPolicyData);
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
    }
}
