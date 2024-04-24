
using App.ControlPanel.Engine.SPO.SiteTrackerInstaller;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Top-level uninstaller class. Removes from SP only.
    /// </summary>
    public class SolutionUninstaller : BaseInstallProcess
    {
        public SolutionUninstaller(SolutionInstallConfig config, ILogger logger) : base(config, logger)
        {
        }

        /// <summary>
        /// Remove AITracker from configured SP sites
        /// </summary>
        public async Task UninstallFromSharePoint(ILogger logger)
        {
            logger.LogInformation($"Uninstalling AITracker from {Config.SharePointConfig.TargetSites.Count} site-collections...");
            var siteInstaller = new SpoSiteListInstaller(logger);
            await siteInstaller.UninstallFromSites(Config.SharePointConfig.TargetSites, Config.SharePointConfig.DestinationDocLibTitle);

            logger.LogInformation($"Uninstall complete. IMPORTANT: Please remember to uninstall the SPFx solution from '{Config.SharePointConfig.AppCatalogueURL}' manually.");
        }
    }
}
