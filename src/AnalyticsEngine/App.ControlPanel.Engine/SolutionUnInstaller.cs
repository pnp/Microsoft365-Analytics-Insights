
using App.ControlPanel.Engine.SPO.Auth;
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

            SiteListInstallResult result;
            using (var authenticator = new InteractiveSpoAuthenticator(Config.SharePointConfig, logger))
            {
                var siteInstaller = new SpoSiteListInstaller(authenticator, logger);
                result = await siteInstaller.UninstallFromSites(Config.SharePointConfig.TargetSites, Config.SharePointConfig.DestinationDocLibTitle);
            }

            // Sites that couldn't be done have already been reported as errors
            logger.LogInformation((result.AllSucceeded ? "Uninstall complete. " : "Uninstall finished, but not on every site. ") +
                $"IMPORTANT: Please remember to uninstall the SPFx solution from '{Config.SharePointConfig.AppCatalogueURL}' manually.");
        }
    }
}
