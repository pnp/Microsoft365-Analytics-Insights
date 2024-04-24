using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.SPO.SiteTrackerInstaller;
using Microsoft.Extensions.Logging;
using OfficeDevPnP.Core.ALM;
using System;
using System.IO;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// SharePoint specific installation tasks
    /// </summary>
    public class SharePointWebComponentsInstallJob : BaseInstallProcess
    {

        public SharePointWebComponentsInstallJob(SolutionInstallConfig config, ILogger logger) : base(config, logger)
        {
        }

        internal async Task InstallAITracker(SharePointInstallConfig sharePointInstallConfig, LocalStorageBlobInfo aiTrackerZipFile, string appInsightsConnectionString)
        {
            Console.WriteLine("Extracting AITracker from downloaded zip...");
            var zipContentsDir = ZipFileTasks.Unzip(aiTrackerZipFile, _logger);

            // Find AI tracker
            FileInfo aiTrackerTempFile = null, aiTrackerSPFx = null;
            foreach (var file in zipContentsDir.GetFiles("*.*"))
            {
                if (file.Name.ToLower() == InstallerConstants.AI_TRACKER_FILE_TITLE.ToLower())
                {
                    aiTrackerTempFile = file;
                }
                else if (file.Name.ToLower() == InstallerConstants.AI_TRACKER_SPFX_FILE_TITLE.ToLower())
                {
                    aiTrackerSPFx = file;
                }
            }
            if (aiTrackerTempFile == null)
            {
                throw new UnexpectedInstallException("Can't find '" + InstallerConstants.AI_TRACKER_FILE_TITLE + "' in downloaded package");
            }
            if (aiTrackerSPFx == null)
            {
                throw new UnexpectedInstallException("Can't find '" + InstallerConstants.AI_TRACKER_SPFX_FILE_TITLE + "' in downloaded package");
            }

            // Upload SPFx solution
            await ExecuteReportFailureAndThrowExceptionIfCritical("Upload SPFx solution",
                        () => UploadApp(sharePointInstallConfig, aiTrackerSPFx.FullName));

            // Install into sites. Hard-code library name "SPOInsights" for now
            var siteInstaller = new SpoSiteListInstaller(_logger);
            await siteInstaller.InstallToSites(sharePointInstallConfig.TargetSites, aiTrackerTempFile, appInsightsConnectionString, "SPOInsights");

            _logger.LogInformation("Installed AITracker to target SharePoint sites via CSOM.");
        }

        private async Task UploadApp(SharePointInstallConfig sharePointInstallConfig, string packagePath)
        {
            _logger.LogInformation($"Installing SPFX solution to '{sharePointInstallConfig.AppCatalogueURL}'...");

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();

            bool success = false;
            using (var context = authManager.GetWebLoginClientContext(sharePointInstallConfig.AppCatalogueURL))
            {
                if (context == null)
                {
                    _logger.LogInformation($"Unsuccessful login to {sharePointInstallConfig.AppCatalogueURL}. Aborting SPFX install - please do this step manually", true);
                    return;
                }

                // Test-load the web & check template
                var web = context.Web;
                context.Load(web);
                try
                {
                    await context.ExecuteQueryAsync();
                }
                catch (System.Net.WebException ex)
                {
                    Console.WriteLine(ex);
                    _logger.LogInformation($"Can't find SPO tenant app-catalog @ {sharePointInstallConfig.AppCatalogueURL}. Verify it exists and try again.", true);
                    return;
                }
                if (web.WebTemplate != InstallerConstants.TEMPLATE_APPSTORE)
                {
                    _logger.LogInformation($"Site-collection @ {sharePointInstallConfig.AppCatalogueURL} doesn't appear to be an app-catalog. " +
                        $"Template for this this site is '{web.WebTemplate}' but expected '{InstallerConstants.TEMPLATE_APPSTORE}'", true);
                    return;
                }

                var manager = new AppManager(context);

                // Add modern UI package
                AppMetadata addedModernUIApp = null;
                try
                {
                    addedModernUIApp = manager.Add(packagePath, true);
                    manager.Deploy(addedModernUIApp, true);
                    success = true;
                }
                catch (Exception)
                {
                    // Perform step manually
                    success = false;
                }

            }
            if (success)
            {
                _logger.LogInformation($"Installed Modern UI extension to app-catalog {sharePointInstallConfig.AppCatalogueURL} & deployed to tenant.");
            }
            else
            {
                _logger.LogError($"Failed to install & deploy Modern UI extension to app-catalog {sharePointInstallConfig.AppCatalogueURL} - access denied? Check site permissions for authenticated user to this site and/or perform step manually", true);
            }
        }
    }
}
