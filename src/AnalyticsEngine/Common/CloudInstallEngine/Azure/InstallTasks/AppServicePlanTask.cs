using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class AppServicePlanTask : InstallTaskInAzResourceGroup<AppServicePlanResource>
    {
        public const string PERF_TIER_BASIC1 = "B1";
        public const string PERF_TIER_BASIC2 = "B2";
        public const string CONFIG_KEY_PERF_TIER = "tier";

        /// <summary>
        /// Optional: the solution's web app. When it already exists, the task returns the plan the web app is actually
        /// hosted on, whatever the configured plan name, so an existing deployment is never moved or given a second plan.
        /// </summary>
        public const string CONFIG_KEY_WEB_APP_NAME = "webAppName";

        public AppServicePlanTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create App Service plan";


        public override async Task<AppServicePlanResource> ExecuteTaskReturnResult(object contextArg)
        {
            // Installers before this one ignored the configured plan name and named the plan after the web app, so most
            // existing web apps are hosted on a plan with the web app's name. Keep them exactly where they are.
            var webAppName = _config.GetOptionalConfigValue(CONFIG_KEY_WEB_APP_NAME);
            AppServicePlanResource hostingPlan = null;
            try
            {
                hostingPlan = await FindHostingPlanAsync(Container, webAppName, CancellationToken);
            }
            catch (RequestFailedException ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning($"Could not read the plan App Service '{webAppName}' runs on ({ex.Status} {ex.ErrorCode}); " +
                    $"looking for the configured plan '{_config.ResourceName}' instead. The web app is not moved either way.");
            }
            if (hostingPlan != null)
            {
                if (!string.Equals(hostingPlan.Data.Name, _config.ResourceName, System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"App Service '{webAppName}' already runs on plan '{hostingPlan.Data.Name}', so that plan is kept. " +
                        $"The configured plan name '{_config.ResourceName}' is only used when the installer creates the web app; it never moves an existing web app to another plan.");
                }
                await UseExistingPlan(hostingPlan);
                return hostingPlan;
            }

            // Get/create plan
            var plan = Container.GetAppServicePlans().GetAll(cancellationToken: CancellationToken).Where(p => p.Data.Name == _config.ResourceName).SingleOrDefault();

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
                var newPlanReq = await Container.GetAppServicePlans().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newPlanInfo, cancellationToken: CancellationToken);
                plan = newPlanReq.Value;

                _logger.LogInformation($"Created App Service plan '{plan.Data.Name}' at service level '{newPlanInfo.Sku.Name}'.");

                if (!string.IsNullOrWhiteSpace(webAppName) && !string.Equals(webAppName, _config.ResourceName, System.StringComparison.OrdinalIgnoreCase) &&
                    Container.GetAppServicePlans().GetAll(cancellationToken: CancellationToken).Any(p => string.Equals(p.Data.Name, webAppName, System.StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning($"The resource group also has an App Service plan named '{webAppName}', after the web app. Earlier installers created the plan under that name. " +
                        "If it no longer hosts anything, delete it so it stops being billed.");
                }
            }
            else
            {
                _logger.LogInformation($"Using existing App Service plan '{_config.ResourceName}'.");
                await UseExistingPlan(plan);
            }

            return plan;

        }

        private async Task UseExistingPlan(AppServicePlanResource plan)
        {
            await base.EnsureTagsOnExisting(plan.Data.Tags, plan.GetTagResource());     // Add configured tags
            if (!AppServicePlanCapabilities.SupportsAlwaysOn(plan.Data.Sku))
            {
                _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(plan.Data.Name, plan.Data.Sku));
            }
        }

        /// <summary>
        /// The plan <paramref name="webAppName"/> is hosted on, or null when the web app does not exist yet (or no name
        /// is given). Also used by Test Configuration, so it checks the plan the installer will really use.
        /// </summary>
        public static async Task<AppServicePlanResource> FindHostingPlanAsync(ResourceGroupResource resourceGroup, string webAppName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (resourceGroup == null || string.IsNullOrWhiteSpace(webAppName)) return null;

            var site = resourceGroup.GetWebSites().GetAll(cancellationToken: cancellationToken)
                .Where(s => string.Equals(s.Data.Name, webAppName, System.StringComparison.OrdinalIgnoreCase)).SingleOrDefault();
            var planId = site?.Data.AppServicePlanId;
            if (planId == null) return null;

            // The plan can live in another resource group, so read it by its full ID.
            var arm = resourceGroup.GetCachedClient(client => client);
            return (await arm.GetAppServicePlanResource(planId).GetAsync(cancellationToken)).Value;
        }
    }
}
