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
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>The site and plan selected by read-only discovery for one deployment.</summary>
    public sealed class AppServiceHostingDiscovery
    {
        internal AppServiceHostingDiscovery(AppServicePlanResource plan, bool legacyNameFound)
        {
            Plan = plan;
            LegacyNameFound = legacyNameFound;
        }

        internal AppServiceHostingDiscovery(WebSiteResource site, AppServicePlanResource plan, SiteConfigData siteConfig, bool ftpAllowed)
            : this(plan, false)
        {
            Site = site;
            SiteConfig = siteConfig;
            FtpBasicPublishingAllowed = ftpAllowed;
        }

        public WebSiteResource Site { get; }
        public AppServicePlanResource Plan { get; }
        internal bool LegacyNameFound { get; }
        internal SiteConfigData SiteConfig { get; }
        internal bool? FtpBasicPublishingAllowed { get; }

        public void ObserveSiteAfterTeardown(WebSiteResource verifiedSite)
        {
            // Teardown changes only networking. Keep the discovered identity, and don't send another site
            // PATCH (or rely on a fresh lookup) for changes the teardown already read back and confirmed.
            if (Site == null || !Site.Id.Equals(verifiedSite.Id)) return;
            Site.Data.PublicNetworkAccess = verifiedSite.Data.PublicNetworkAccess;
            Site.Data.VirtualNetworkSubnetId = verifiedSite.Data.VirtualNetworkSubnetId;
        }
    }

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

        public AppServiceHostingDiscovery Preflight { private get; set; }

        /// <summary>Reads only. A confirmed missing resource group has neither a site nor a plan.</summary>
        public Task<AppServiceHostingDiscovery> DiscoverAsync(ResourceGroupResource existingGroup, CancellationToken token) =>
            AppServiceResourceLookup.DiscoverHostingAsync(existingGroup, _config.GetOptionalConfigValue(CONFIG_KEY_WEB_APP_NAME), _config.ResourceName, token);

        /// <summary>Returns the existing site's verified hosting plan, or null for a confirmed missing site or an unspecified name.</summary>
        public static async Task<AppServicePlanResource> FindHostingPlanAsync(ResourceGroupResource resourceGroup, string webAppName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(webAppName)) return null;
            if (resourceGroup == null) throw new ArgumentNullException(nameof(resourceGroup));
            return (await AppServiceResourceLookup.DiscoverExistingSiteAsync(resourceGroup, webAppName, cancellationToken))?.Plan;
        }

        public override async Task<AppServicePlanResource> ExecuteTaskReturnResult(object contextArg)
        {
            // Installers before this one ignored the configured plan name and named the plan after the web app, so most
            // existing web apps are hosted on a plan with the web app's name. Keep them exactly where they are.
            var webAppName = _config.GetOptionalConfigValue(CONFIG_KEY_WEB_APP_NAME);
            CancellationToken.ThrowIfCancellationRequested();
            var discovery = Preflight ?? await DiscoverAsync(Container, CancellationToken);
            if (discovery.Site != null)
            {
                var hostingPlan = discovery.Plan;
                if (!string.Equals(hostingPlan.Data.Name, _config.ResourceName, System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"App Service '{webAppName}' already runs on plan '{hostingPlan.Data.Name}', so that plan is kept. " +
                        $"The configured plan name '{_config.ResourceName}' is only used when the installer creates the web app; it never moves an existing web app to another plan.");
                }

                await UseExistingPlan(hostingPlan);
                return hostingPlan;
            }

            // Get/create plan
            var plan = discovery.Plan;

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

                if (discovery.LegacyNameFound)
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
            if (Container?.Id == null || plan.Id?.Parent == null)
                throw new InstallException("Could not verify the selected plan's resource group. Verify the deployment scope and retry.");
            if (Container.Id.Equals(plan.Id.Parent))
                await base.EnsureTagsOnExisting(plan.Data.Tags, plan.GetTagResource());
            else
                _logger.LogInformation($"Plan '{plan.Data.Name}' is in resource group '{plan.Id.ResourceGroupName}', outside this deployment's, " +
                    "so it is used as it is: the installer doesn't add its tags to it.");
            WarnIfAlwaysOnUnsupported(plan);
        }

        private void WarnIfAlwaysOnUnsupported(AppServicePlanResource plan)
        {
            if (!AppServicePlanCapabilities.SupportsAlwaysOn(plan.Data.Sku))
            {
                _logger.LogWarning(AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning(plan.Data.Name, plan.Data.Sku));
            }
        }
    }
}
