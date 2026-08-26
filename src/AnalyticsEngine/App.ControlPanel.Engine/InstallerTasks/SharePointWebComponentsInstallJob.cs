using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.SPO.AppCatalog;
using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Engine.SPO.SiteTrackerInstaller;
using Microsoft.Extensions.Logging;
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
        private readonly string _defaultHostName;

        public SharePointWebComponentsInstallJob(SolutionInstallConfig config, ILogger logger, string defaultHostName) : base(config, logger)
        {
            _defaultHostName = defaultHostName;
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

            // One interactive sign-in covers the app catalog & every target site - they're all in the same SPO tenant
            using (var authenticator = new InteractiveSpoAuthenticator(sharePointInstallConfig, _logger))
            {
                // Upload SPFx solution
                await ExecuteReportFailureAndThrowExceptionIfCritical("Upload SPFx solution",
                            () => UploadApp(sharePointInstallConfig, aiTrackerSPFx.FullName, authenticator));

                // Install into sites. Hard-code library name "SPOInsights" for now
                var siteInstaller = new SpoSiteListInstaller(authenticator, _logger);
                await siteInstaller.InstallToSites(sharePointInstallConfig.TargetSites, aiTrackerTempFile, appInsightsConnectionString,
                    "SPOInsights", "https://" + _defaultHostName);
            }

            _logger.LogInformation("Installed AITracker to target SharePoint sites via CSOM.");
        }

        private async Task UploadApp(SharePointInstallConfig sharePointInstallConfig, string packagePath, ISpoAuthenticator authenticator)
        {
            _logger.LogInformation($"Installing SPFX solution to '{sharePointInstallConfig.AppCatalogueURL}'...");

            bool success = false;
            try
            {
                // A sign-in failure here is fatal - SpoAuthenticationException is deliberately not caught,
                // so the whole SharePoint stage aborts instead of reporting a misleading success later.
                using (var context = authenticator.GetContext(sharePointInstallConfig.AppCatalogueURL))
                {
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
                        if (SpoSiteInstallAdaptor.IsAccessDenied(ex))
                        {
                            _logger.LogError($"Access denied to the app-catalog @ {sharePointInstallConfig.AppCatalogueURL}. " +
                                "The signed-in account must be a SharePoint administrator. If you signed in as a guest (B2B) administrator, " +
                                "set the target tenant on the SharePoint tab so you are signed in against the right directory.", true);
                        }
                        else
                        {
                            _logger.LogError($"Can't find SPO tenant app-catalog @ {sharePointInstallConfig.AppCatalogueURL}. Verify it exists and try again.", true);
                        }
                        return;
                    }
                    if (web.WebTemplate != InstallerConstants.TEMPLATE_APPSTORE)
                    {
                        _logger.LogInformation($"Site-collection @ {sharePointInstallConfig.AppCatalogueURL} doesn't appear to be an app-catalog. " +
                            $"Template for this this site is '{web.WebTemplate}' but expected '{InstallerConstants.TEMPLATE_APPSTORE}'", true);
                        return;
                    }
                }

                // Add modern UI package
                using (var appCatalog = new TenantAppCatalogManager(authenticator, _logger))
                {
                    var appId = await appCatalog.AddAsync(sharePointInstallConfig.AppCatalogueURL, packagePath);
                    await appCatalog.DeployAsync(sharePointInstallConfig.AppCatalogueURL, appId);
                    success = true;
                }
            }
            catch (SpoAppCatalogException ex)
            {
                // Recoverable: tell the admin what happened & let them do this step by hand.
                _logger.LogError(ex.Message);
                success = false;
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
