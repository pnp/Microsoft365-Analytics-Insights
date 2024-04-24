using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class AppServicePlanTask : InstallTaskInAzResourceGroup<AppServicePlanResource>
    {
        public const string PERF_TIER_BASIC1 = "B1";
        public const string PERF_TIER_BASIC2 = "B2";
        public const string CONFIG_KEY_PERF_TIER = "tier";

        public AppServicePlanTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create App Service plan";


        public override async Task<AppServicePlanResource> ExecuteTaskReturnResult(object contextArg)
        {
            // Get/create plan
            var plan = Container.GetAppServicePlans().Where(p => p.Data.Name == _config.ResourceName).SingleOrDefault();

            if (plan == null)
            {
                // Performance tier configured?
                var perfTier = PERF_TIER_BASIC1;
                if (_config.ContainsKey(CONFIG_KEY_PERF_TIER))
                    perfTier = _config.GetConfigValue(CONFIG_KEY_PERF_TIER);

                var newPlanInfo = new AppServicePlanData(base.AzureLocation)
                {
                    Sku = new AppServiceSkuDescription() { Name = perfTier, Family = "B" },
                    Kind = "app"
                };

                base.EnsureTagsOnNew(newPlanInfo.Tags);     // Add configured tags
                var newPlanReq = await Container.GetAppServicePlans().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newPlanInfo);
                plan = newPlanReq.Value;

                _logger.LogInformation($"Created App Service plan '{plan.Data.Name}' at service level '{newPlanInfo.Sku.Name}'.");
            }
            else
            {
                await base.EnsureTagsOnExisting(plan.Data.Tags, plan.GetTagResource());     // Add configured tags

                _logger.LogInformation($"Using existing App Service plan '{_config.ResourceName}'.");
            }

            return plan;

        }
    }
}
