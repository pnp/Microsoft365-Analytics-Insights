using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.SiteTrackerInstaller
{
    public abstract class AbstractSiteListInstaller<WEBTYPE>
    {
        protected readonly ILogger _logger;

        public AbstractSiteListInstaller(ILogger logger)
        {
            _logger = logger;
        }

        public async Task InstallToSites(IEnumerable<string> siteUrls, FileInfo aiTrackerFileName, string appInsightsConnectionString, string docLibTitle, string solutionWebsiteBaseUrl)
        {
            if (!aiTrackerFileName.Exists)
                throw new ArgumentException($"Can't find AITracker file '{aiTrackerFileName}'", nameof(aiTrackerFileName));
            var fileContents = System.IO.File.ReadAllBytes(aiTrackerFileName.FullName);

            foreach (var siteUrl in siteUrls)
            {
                using (var adaptor = GetAdaptor(siteUrl))
                {
                    var cfg = new TrackerInstallConfig(appInsightsConnectionString, docLibTitle, fileContents);
                    var installer = new SiteAITrackerInstaller<WEBTYPE>(adaptor, _logger);

                    await installer.InstallWebComponentsToSite(cfg, solutionWebsiteBaseUrl);
                }
            }
        }

        public async Task UninstallFromSites(IEnumerable<string> siteUrls, string docLibTitle)
        {
            foreach (var siteUrl in siteUrls)
            {
                using (var adaptor = GetAdaptor(siteUrl))
                {
                    var installer = new SiteAITrackerInstaller<WEBTYPE>(adaptor, _logger);

                    await installer.UninstallWebComponentsFromSite(docLibTitle);
                }
            }
        }

        public abstract ISiteInstallAdaptor<WEBTYPE> GetAdaptor(string siteUrl);
    }

    /// <summary>
    /// Installs AI tracker to list of SPO sites
    /// </summary>
    public class SpoSiteListInstaller : AbstractSiteListInstaller<Web>
    {
        public SpoSiteListInstaller(ILogger logger) : base(logger)
        {
        }

        public override ISiteInstallAdaptor<Web> GetAdaptor(string siteUrl)
        {
            return new SpoSiteInstallAdaptor(siteUrl, _logger);
        }
    }
}
