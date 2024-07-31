﻿using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.SiteTrackerInstaller
{
    /// <summary>
    /// Installs AITracker for a list of site-collections
    /// </summary>
    public class SiteAITrackerInstaller<WEBTYPE>
    {
        private readonly ISiteInstallAdaptor<WEBTYPE> _siteInstallAdaptor;
        private readonly ILogger _logger;

        public SiteAITrackerInstaller(ISiteInstallAdaptor<WEBTYPE> siteInstallAdaptor, ILogger logger)
        {
            _siteInstallAdaptor = siteInstallAdaptor ?? throw new ArgumentNullException(nameof(siteInstallAdaptor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (!StringUtils.IsValidAbsoluteUrl(_siteInstallAdaptor.SiteUrl))
            {
                throw new ArgumentException($"'{nameof(_siteInstallAdaptor.SiteUrl)}' needs to be a well-formed URL", nameof(_siteInstallAdaptor.SiteUrl));
            }
        }

        /// <summary>
        /// Install AITracker to site using chosen adaptor
        /// </summary>
        public async Task InstallWebComponentsToSite(TrackerInstallConfig trackerInstallConfig, string solutionWebsiteBaseUrl)
        {
            if (trackerInstallConfig is null)
            {
                throw new ArgumentNullException(nameof(trackerInstallConfig));
            }

            // Prep adapter
            var validSite = await _siteInstallAdaptor.Init();
            if (!validSite)
            {
                _logger.LogError($"{_siteInstallAdaptor.SiteUrl} does not appear to be a valid site, or you do not have permissions to access it with the user logged in");
                return;
            }

            _logger.LogInformation($"Installing AI tracker to {_siteInstallAdaptor.SiteUrl}");
            var docLibInfo = await _siteInstallAdaptor.ConfirmDocLibOnRootSite(trackerInstallConfig.DocLibTitle);
            if (!docLibInfo.CreatedNew) _logger.LogInformation($"{trackerInstallConfig.DocLibTitle} created");
            else _logger.LogInformation($"{trackerInstallConfig.DocLibTitle} already exists");

            // Remove previous
            var trackerExisted = await _siteInstallAdaptor.RemoveTrackerIfExistsOnRootSite(trackerInstallConfig.DocLibTitle);
            if (!trackerExisted) _logger.LogInformation($"AITracker.js doesn't exist! Let's upload it...");
            else _logger.LogInformation($"Removed previous AITracker");

            // Add file
            await _siteInstallAdaptor.AddTrackerToLibraryOnRootSite(trackerInstallConfig.DocLibTitle, trackerInstallConfig.AiTrackerContents, docLibInfo.EnableMinorVersions);

            if (docLibInfo.EnableMinorVersions) _logger.LogInformation($"Added AITracker and published");
            else _logger.LogInformation($"Added AITracker to library '{trackerInstallConfig.DocLibTitle}' on {_siteInstallAdaptor.GetUrl(_siteInstallAdaptor.RootWeb)}");

            // Secure from edits
            await _siteInstallAdaptor.SecureList(trackerInstallConfig.DocLibTitle);
            _logger.LogInformation($"Library '{trackerInstallConfig.DocLibTitle}' security configured for 'read-only' permissions for all users");

            // Register custom actions
            var aiTrackerFQDN = $"{_siteInstallAdaptor.GetUrl(_siteInstallAdaptor.RootWeb)}/{docLibInfo.ServerRelativeUrl}/AITracker.js";
            var cacheToken = DateTime.Now.Ticks.ToString();
            var aiTrackerUrlWithToken = $"{aiTrackerFQDN}?ver={cacheToken}";

            // Remove old custom actions & add new
            await _siteInstallAdaptor.RemoveAITrackerCustomActionFromWeb(_siteInstallAdaptor.RootWeb);
            await _siteInstallAdaptor.AddAITrackerCustomActionToWeb(_siteInstallAdaptor.RootWeb, new ClassicPageCustomAction(aiTrackerUrlWithToken, trackerInstallConfig.AppInsightsConnectionString, solutionWebsiteBaseUrl));
            await _siteInstallAdaptor.RemoveAITrackerCustomActionFromWeb(_siteInstallAdaptor.RootWeb);
            await _siteInstallAdaptor.AddModernUIAITrackerCustomActionToWeb(_siteInstallAdaptor.RootWeb, new ModernAppCustomAction(trackerInstallConfig.AppInsightsConnectionString, cacheToken, solutionWebsiteBaseUrl));
            foreach (var w in _siteInstallAdaptor.SubWebs)
            {
                var webUrl = _siteInstallAdaptor.GetUrl(w);
                _logger.LogInformation($"Registering AITracker to on web {webUrl}");

                await _siteInstallAdaptor.RemoveAITrackerCustomActionFromWeb(w);
                await _siteInstallAdaptor.AddAITrackerCustomActionToWeb(w, new ClassicPageCustomAction(aiTrackerUrlWithToken, trackerInstallConfig.AppInsightsConnectionString, solutionWebsiteBaseUrl));

                await _siteInstallAdaptor.RemoveAITrackerCustomActionFromWeb(w);
                await _siteInstallAdaptor.AddModernUIAITrackerCustomActionToWeb(w, new ModernAppCustomAction(trackerInstallConfig.AppInsightsConnectionString, cacheToken, solutionWebsiteBaseUrl));
            }
            _logger.LogInformation($"Registered AITracker to all sub-webs");
        }

        /// <summary>
        /// Install AITracker to site using chosen adaptor
        /// </summary>
        public async Task UninstallWebComponentsFromSite(string docLibTitle)
        {
            if (string.IsNullOrEmpty(docLibTitle))
            {
                throw new ArgumentException($"'{nameof(docLibTitle)}' cannot be null or empty.", nameof(docLibTitle));
            }

            // Prep adapter
            var validSite = await _siteInstallAdaptor.Init();
            if (!validSite)
            {
                _logger.LogError($"{_siteInstallAdaptor.SiteUrl} does not appear to be a valid site, or you do not have permissions to access it with the user logged in");
                return;
            }

            _logger.LogInformation($"Uninstalling AI tracker from {_siteInstallAdaptor.SiteUrl}");

            // Clean up doc lib
            await _siteInstallAdaptor.RemoveDocLibOnRootSite(docLibTitle);

            // Unregister custom actions
            await _siteInstallAdaptor.RemoveAITrackerCustomActionFromWeb(_siteInstallAdaptor.RootWeb);
            await _siteInstallAdaptor.RemoveModernUIAITrackerCustomActionFromWeb(_siteInstallAdaptor.RootWeb);

            foreach (var w in _siteInstallAdaptor.SubWebs)
            {
                await _siteInstallAdaptor.RemoveAITrackerCustomActionFromWeb(w);
                await _siteInstallAdaptor.RemoveModernUIAITrackerCustomActionFromWeb(w);
            }
            _logger.LogInformation($"Uninstalled AITracker from all sub-webs");
        }
    }
}
