using App.ControlPanel.Engine.Models;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using CloudInstallEngine.Models;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Authenticates the installer's requests to an App Service's Kudu (SCM) site.
    /// </summary>
    /// <remarks>
    /// Kudu accepts a Microsoft Entra token for Azure Resource Manager, and Microsoft recommends it over basic
    /// authentication (https://learn.microsoft.com/azure/app-service/deploy-zip). The installer used basic publishing
    /// credentials, and switched SCM basic authentication back on to get them, which undid an operator's decision (and a
    /// common Defender for Cloud recommendation) to turn it off. Now the token is used, and the site's basic credentials
    /// only when Kudu refuses the token AND the operator has left SCM basic authentication on. The installer never turns
    /// it on. A token is fetched for every request, so a long deployment poll never outlives it (Azure.Identity caches it).
    /// </remarks>
    internal sealed class KuduAccess : DelegatingHandler
    {
        internal const string ArmTokenScope = "https://management.azure.com/.default";

        private readonly WebSiteResource _site;
        private readonly TokenCredential _credential;
        private AuthenticationHeaderValue _basic;

        internal KuduAccess(WebSiteResource site, TokenCredential credential, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            _site = site ?? throw new ArgumentNullException(nameof(site));
            _credential = credential;
        }

        /// <summary>The Kudu site's root, for example https://contoso.scm.azurewebsites.net.</summary>
        internal string ScmRootUrl { get; private set; }

        /// <summary>True while requests carry a Microsoft Entra token rather than basic publishing credentials.</summary>
        internal bool UsesEntraToken => _basic == null;

        /// <summary>
        /// Uses a Microsoft Entra token when there is a credential and the site lists its SCM host name, and the site's
        /// basic publishing credentials otherwise.
        /// </summary>
        internal async Task InitialiseAsync(CancellationToken cancellationToken)
        {
            var scmHost = FindScmHostName(_site.Data);
            if (_credential != null && scmHost != null)
            {
                ScmRootUrl = "https://" + scmHost;
                return;
            }
            await UseBasicCredentialsAsync(cancellationToken, _credential == null
                ? "No Microsoft Entra credential was supplied for Kudu"
                : $"App Service '{_site.Data.Name}' does not list its SCM host name");
        }

        /// <summary>
        /// Switches to the site's basic publishing credentials. Throws when SCM basic authentication is turned off,
        /// rather than turning it on.
        /// </summary>
        internal async Task UseBasicCredentialsAsync(CancellationToken cancellationToken,
            string whyNotEntra = "Kudu did not accept the installer's Microsoft Entra token")
        {
            var policy = await _site.GetScmSiteBasicPublishingCredentialsPolicy().GetAsync(cancellationToken);
            if (policy.Value.Data.Allow != true)
            {
                throw new InstallException(
                    $"{whyNotEntra}, and SCM basic authentication is turned off on App Service '{_site.Data.Name}', so the installer cannot " +
                    "reach its Kudu (SCM) site. The installer does not turn SCM basic authentication back on. Give the installer account a " +
                    "role that allows publishing to the web app (Website Contributor or Contributor on the web app or its resource group), " +
                    "then re-run the installer.");
            }

            var profile = await _site.GetPublishingProfileXmlWithSecretsAsync(
                new CsmPublishingProfile { Format = PublishingProfileFormat.WebDeploy }, cancellationToken);
            KuduPublishInfo publishInfo;
            using (var reader = new StreamReader(profile.Value))
            {
                publishInfo = publishData.FromXml(reader).GetKuduPublishInfo();
            }

            _basic = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publishInfo.Username}:{publishInfo.Password}")));
            if (ScmRootUrl == null)
            {
                ScmRootUrl = publishInfo.RootUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? publishInfo.RootUrl
                    : "https://" + publishInfo.RootUrl;
            }
        }

        /// <summary>Whether Kudu refused the caller, as opposed to failing for another reason.</summary>
        internal static bool IsAuthRejection(HttpStatusCode status) =>
            status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden;

        /// <summary>The SCM host name App Service lists for the site (its "Repository" host), or null.</summary>
        internal static string FindScmHostName(WebSiteData site)
        {
            return site?.HostNameSslStates?
                .FirstOrDefault(h => h != null && h.HostType == AppServiceHostType.Repository && !string.IsNullOrWhiteSpace(h.Name))?
                .Name;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_basic != null)
            {
                request.Headers.Authorization = _basic;
            }
            else
            {
                var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { ArmTokenScope }), cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
