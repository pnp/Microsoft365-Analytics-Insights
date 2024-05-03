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
                        FtpsState = AppServiceFtpsState.FtpsOnly
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
                // Ensure app has system assigned identity
                if (webApp.HasData && webApp.Data.Identity == null)
                {
                    webApp.Data.Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned);
                    _logger.LogInformation($"Updating App Service '{_config.ResourceName}' to use System Assigned identity...");

                    var webAppUpdateInfo = new WebSiteData(base.AzureLocation)
                    {
                        Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned)
                    };
                    base.EnsureTagsOnNew(webAppUpdateInfo.Tags);     // Add configured tags
                    var newWebAppReq = await Container.GetWebSites().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, webAppUpdateInfo);
                }

                await base.EnsureTagsOnExisting(webApp.Data.Tags, webApp.GetTagResource());     // Add configured tags

                _logger.LogInformation($"Using existing App Service '{webApp.Data.DefaultHostName}'.");
            }

            return webApp;

        }
    }
}
