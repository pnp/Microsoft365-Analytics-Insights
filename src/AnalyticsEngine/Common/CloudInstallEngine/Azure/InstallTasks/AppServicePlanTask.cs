using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
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
            var hostingPlan = await FindHostingPlanAsync(Container, webAppName, CancellationToken);
            if (hostingPlan != null)
            {
                if (!string.Equals(hostingPlan.Data.Name, _config.ResourceName, System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"App Service '{webAppName}' already runs on plan '{hostingPlan.Data.Name}', so that plan is kept. " +
                        $"The configured plan name '{_config.ResourceName}' is only used when the installer creates the web app; it never moves an existing web app to another plan.");
                }

                // A plan in another resource group (or subscription) is a shared one the operator chose: it is used as it is.
                // Installers before this one never touched it, and writing the solution's tags to it needs write access
                // outside the deployment's resource group, which the installer's identity need not have - a refused tag
                // write would otherwise abort an upgrade that has everything else it needs.
                if (IsInDeploymentResourceGroup(hostingPlan))
                {
                    await UseExistingPlan(hostingPlan);
                }
                else
                {
                    _logger.LogInformation($"Plan '{hostingPlan.Data.Name}' is in resource group '{hostingPlan.Id.ResourceGroupName}', outside this deployment's, " +
                        "so it is used as it is: the installer doesn't add its tags to it.");
                    WarnIfAlwaysOnUnsupported(hostingPlan);
                }
                return hostingPlan;
            }

            // Get/create plan
            var plan = FindConfiguredPlan(Container, _config.ResourceName, CancellationToken);

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
                _logger.LogInformation($"Using existing App Service plan '{plan.Data.Name}'.");
                await UseExistingPlan(plan);
            }

            return plan;

        }

        private async Task UseExistingPlan(AppServicePlanResource plan)
        {
            await base.EnsureTagsOnExisting(plan.Data.Tags, plan.GetTagResource());     // Add configured tags
            WarnIfAlwaysOnUnsupported(plan);
        }

        private void WarnIfAlwaysOnUnsupported(AppServicePlanResource plan)
        {
            if (!AppServicePlanCapabilities.SupportsAlwaysOn(plan.Data.Sku))
            {
                _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(plan.Data.Name, plan.Data.Sku));
            }
        }

        private bool IsInDeploymentResourceGroup(AppServicePlanResource plan)
        {
            var group = Container?.Id;
            var id = plan?.Id;
            if (group == null || id == null) return true;
            return string.Equals(id.SubscriptionId, group.SubscriptionId, System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(id.ResourceGroupName, group.ResourceGroupName, System.StringComparison.OrdinalIgnoreCase);
        }

        // A casing difference is not a new ARM resource: a create-or-update would resize the existing plan.
        public static AppServicePlanResource FindConfiguredPlan(ResourceGroupResource resourceGroup, string planName,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            AppServiceResourceLookup.FindPlan(resourceGroup, planName, cancellationToken);

        /// <summary>
        /// The plan <paramref name="webAppName"/> is hosted on, or null when the web app does not exist yet (or no name
        /// is given). An unreadable site or plan is not a confirmed absence. Test Configuration uses the same lookup.
        /// </summary>
        public static async Task<AppServicePlanResource> FindHostingPlanAsync(ResourceGroupResource resourceGroup, string webAppName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(webAppName)) return null;
            if (resourceGroup == null) throw new ArgumentNullException(nameof(resourceGroup));
            var site = AppServiceResourceLookup.FindSite(resourceGroup, webAppName, cancellationToken);
            if (site == null) return null;

            var id = site.Data.AppServicePlanId;
            if (id == null || !ResourceIdentifier.TryParse(id.ToString(), out var planId)
                || planId.ResourceType != AppServicePlanResource.ResourceType || planId.Parent?.ResourceType != ResourceGroupResource.ResourceType)
                throw new InstallException("Azure did not return a valid hosting plan ID (serverFarmId) for the existing App Service. " +
                    "Verify the site's App Service plan association and retry.");

            try
            {
                // A shared plan must be read by its full ID, not by name in the site's resource group.
                var arm = resourceGroup.GetCachedClient(client => client);
                var plan = (await arm.GetAppServicePlanResource(planId).GetAsync(cancellationToken))?.Value;
                cancellationToken.ThrowIfCancellationRequested();
                if (plan?.HasData != true || !planId.Equals(plan.Id) || string.IsNullOrWhiteSpace(plan.Data.Name))
                    throw new InvalidDataException();
                return plan;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw AppServiceResourceLookup.ReadFailure("read the existing App Service's hosting plan", ex,
                    "Verify that the plan still exists and the installer can read it in the plan's resource group, then retry.");
            }
        }
    }
}
