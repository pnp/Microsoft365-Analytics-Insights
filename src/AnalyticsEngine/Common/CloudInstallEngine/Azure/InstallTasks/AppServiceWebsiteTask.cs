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
                        MinTlsVersion = AppServiceSupportedTlsVersion.Tls1_2
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
                var webAppData = (await webApp.GetAsync()).Value.Data;
                var currentTlsVersion = webAppData.SiteConfig?.MinTlsVersion;
                if (currentTlsVersion == null || !currentTlsVersion.Value.ToString().Equals(AppServiceSupportedTlsVersion.Tls1_2.ToString()))
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
            }

            // Enable basic publishing credentials for SCM and FTP
            var publishingCredentialsPolicyData = new CsmPublishingCredentialsPoliciesEntityData()
            {
                Allow = true,
            };

            _logger.LogInformation($"Enabling basic publishing credentials (SCM & FTP) for '{_config.ResourceName}'...");
            await webApp.GetScmSiteBasicPublishingCredentialsPolicy().CreateOrUpdateAsync(
                WaitUntil.Completed, publishingCredentialsPolicyData);
            await webApp.GetWebSiteFtpPublishingCredentialsPolicy().CreateOrUpdateAsync(
                WaitUntil.Completed, publishingCredentialsPolicyData);

            return webApp;

        }
    }
}
