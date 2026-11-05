using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Engine.SPO.Rest;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.SiteTrackerInstaller
{
    /// <summary>
    /// What happened on each target site collection in one install or uninstall run.
    /// </summary>
    public class SiteListInstallResult
    {
        public List<string> SucceededSites { get; } = new List<string>();

        /// <summary>Sites that were not done, with the reason each one failed.</summary>
        public List<KeyValuePair<string, string>> FailedSites { get; } = new List<KeyValuePair<string, string>>();

        public int TotalSites => SucceededSites.Count + FailedSites.Count;

        public bool AllSucceeded => FailedSites.Count == 0;
    }

    public abstract class AbstractSiteListInstaller<WEBTYPE>
    {
        protected readonly ILogger _logger;
        public CancellationToken CancellationToken { get; set; }

        public AbstractSiteListInstaller(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Installs the AITracker on every site. A site that fails is logged as an error and skipped, so one site the
        /// signed-in account can't manage doesn't cost every site after it its tracking. A sign-in failure or a cancel
        /// still ends the whole run, as neither is about one site.
        /// </summary>
        public async Task<SiteListInstallResult> InstallToSites(IEnumerable<string> siteUrls, FileInfo aiTrackerFileName, string appInsightsConnectionString, string docLibTitle, string solutionWebsiteBaseUrl)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (!aiTrackerFileName.Exists)
                throw new ArgumentException($"Can't find AITracker file '{aiTrackerFileName}'", nameof(aiTrackerFileName));
            var fileContents = System.IO.File.ReadAllBytes(aiTrackerFileName.FullName);

            // Built once, outside the per-site error handling: a bad argument is the same for every site.
            var cfg = new TrackerInstallConfig(appInsightsConnectionString, docLibTitle, fileContents);

            var result = await ForEachSite(siteUrls, "install the AITracker on",
                installer => installer.InstallWebComponentsToSite(cfg, solutionWebsiteBaseUrl));

            if (!result.AllSucceeded)
            {
                _logger.LogError($"The AITracker was installed on {result.SucceededSites.Count} of {result.TotalSites} SharePoint site collection(s). " +
                    $"It was NOT installed on: {string.Join(", ", result.FailedSites.Select(f => f.Key))}. " +
                    "Fix the problem logged for each of these sites, then run the installer again.");
            }
            return result;
        }

        /// <summary>
        /// Removes the AITracker from every site, carrying on past a site that fails as <see cref="InstallToSites"/> does.
        /// </summary>
        public async Task<SiteListInstallResult> UninstallFromSites(IEnumerable<string> siteUrls, string docLibTitle)
        {
            if (string.IsNullOrEmpty(docLibTitle))
            {
                throw new ArgumentException($"'{nameof(docLibTitle)}' cannot be null or empty.", nameof(docLibTitle));
            }

            var result = await ForEachSite(siteUrls, "remove the AITracker from",
                installer => installer.UninstallWebComponentsFromSite(docLibTitle));

            if (!result.AllSucceeded)
            {
                _logger.LogError($"The AITracker was removed from {result.SucceededSites.Count} of {result.TotalSites} SharePoint site collection(s). " +
                    $"It was NOT removed from: {string.Join(", ", result.FailedSites.Select(f => f.Key))}. " +
                    "Fix the problem logged for each of these sites, then remove the SharePoint tracker again.");
            }
            return result;
        }

        async Task<SiteListInstallResult> ForEachSite(IEnumerable<string> siteUrls, string action, Func<SiteAITrackerInstaller<WEBTYPE>, Task<bool>> doSite)
        {
            var result = new SiteListInstallResult();
            foreach (var siteUrl in siteUrls)
            {
                CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using (var adaptor = GetAdaptor(siteUrl))
                    {
                        var installer = new SiteAITrackerInstaller<WEBTYPE>(adaptor, _logger);
                        if (await doSite(installer))
                        {
                            result.SucceededSites.Add(siteUrl);
                        }
                        else
                        {
                            // The installer has already logged why the site couldn't be opened.
                            result.FailedSites.Add(new KeyValuePair<string, string>(siteUrl, "the site couldn't be opened with the signed-in account"));
                        }
                    }
                }
                catch (Exception ex) when (IsSiteSpecificFailure(ex))
                {
                    var reason = ex is SpoRestException ? ex.Message : $"[{ex.GetType().Name}] {ex.Message}";
                    _logger.LogError($"Couldn't {action} SharePoint site '{siteUrl}': {reason} Carrying on with the remaining sites.");
                    result.FailedSites.Add(new KeyValuePair<string, string>(siteUrl, reason));
                }
            }
            return result;
        }

        /// <summary>
        /// A cancel and a failed sign-in end the SharePoint stage, as they always have. Anything else happened on
        /// one site, so the remaining sites are still worth trying.
        /// </summary>
        bool IsSiteSpecificFailure(Exception ex)
        {
            if (CancellationToken.IsCancellationRequested) return false;
            return !(ex is SpoAuthenticationException) && !(ex is Microsoft.Identity.Client.MsalException);
        }

        public abstract ISiteInstallAdaptor<WEBTYPE> GetAdaptor(string siteUrl);
    }

    /// <summary>
    /// Installs AI tracker to list of SPO sites. All sites share a single interactive sign-in, as a SharePoint
    /// access token covers the whole tenant.
    /// </summary>
    public class SpoSiteListInstaller : AbstractSiteListInstaller<SpoWeb>
    {
        private readonly ISpoAuthenticator _authenticator;

        public SpoSiteListInstaller(ISpoAuthenticator authenticator, ILogger logger) : base(logger)
        {
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        }

        public override ISiteInstallAdaptor<SpoWeb> GetAdaptor(string siteUrl)
        {
            return new SpoSiteInstallAdaptor(siteUrl, _authenticator, _logger) { CancellationToken = CancellationToken };
        }
    }
}
