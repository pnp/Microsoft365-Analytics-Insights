using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Engine.SPO.Rest;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.SiteTrackerInstaller
{
    /// <summary>
    /// Installs / removes the AITracker on a SharePoint Online site collection using the SharePoint REST API.
    ///
    /// Previously this used CSOM (Microsoft.SharePointOnline.CSOM). REST does everything needed here and drops
    /// eleven SharePoint client assemblies from the installer - nine of which were also being copied into the
    /// website, which uses none of them.
    /// </summary>
    public class SpoSiteInstallAdaptor : ISiteInstallAdaptor<SpoWeb>
    {
        const string FILENAME = "AITracker.js";

        /// <summary>SP.RoleType.Reader</summary>
        const int ROLETYPE_READER = 2;

        /// <summary>SP.ListTemplateType.DocumentLibrary</summary>
        const int LISTTEMPLATE_DOCLIB = 101;

        /// <summary>Claim for "everyone who is signed in", as used by the previous CSOM EnsureUser call.</summary>
        const string CLAIM_ALL_AUTHENTICATED_USERS = "c:0(.s|true";

        readonly ISpoAuthenticator _authenticator;
        readonly string _siteUrl;
        readonly ILogger _logger;
        SpoRestClient _rest;
        SpoWeb _rootWeb;
        List<SpoWeb> _subWebs = new List<SpoWeb>();

        public SpoSiteInstallAdaptor(string siteUrl, ISpoAuthenticator authenticator, ILogger logger)
        {
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _siteUrl = siteUrl;
            _logger = logger;
        }

        string SiteRoot => _siteUrl.TrimEnd('/');

        /// <summary>Web-scoped API root. Sub-webs have their own _api endpoint.</summary>
        static string Api(SpoWeb web) => web.Url.TrimEnd('/') + "/_api";

        public async Task<bool> Init()
        {
            // A sign-in failure is fatal for the whole SharePoint stage, not a per-site problem, so let
            // SpoAuthenticationException propagate rather than reporting this site as merely inaccessible.
            _rest = new SpoRestClient(_authenticator, _logger);

            try
            {
                var rootWebJson = await _rest.GetAsync($"{SiteRoot}/_api/web?$select=Url,Title,ServerRelativeUrl");
                _rootWeb = SpoWeb.FromJson(rootWebJson);

                var subWebsJson = await _rest.GetCollectionAsync($"{SiteRoot}/_api/web/webs?$select=Url,Title,ServerRelativeUrl");
                _subWebs = SpoWeb.ListFromJson(subWebsJson);
            }
            catch (SpoRestException ex)
            {
                if (ex.IsAccessDenied)
                {
                    _logger.LogError($"Access denied to '{_siteUrl}'. The signed-in account must be a site collection administrator on this site.");
                }
                else
                {
                    _logger.LogError(ex.Message);
                }
                return false;
            }
            return true;
        }

        public List<SpoWeb> SubWebs => _subWebs;

        public SpoWeb RootWeb => _rootWeb;

        public string SiteUrl => _siteUrl;

        public string GetUrl(SpoWeb web) => web.Url;

        #region Custom actions

        public async Task AddAITrackerCustomActionToWeb(SpoWeb web, ClassicPageCustomAction classicPageCustomAction)
        {
            var payload = new JObject
            {
                ["Description"] = classicPageCustomAction.Description,
                ["Location"] = classicPageCustomAction.Location,
                ["ScriptBlock"] = classicPageCustomAction.ScriptBlock,
                ["Sequence"] = 1000
            };

            try
            {
                await _rest.PostAsync($"{Api(web)}/web/UserCustomActions", payload.ToString());
                _logger.LogInformation($"Inserted custom-action into web: '{web.Url}'");
            }
            catch (SpoRestException ex) when (ex.IsAccessDenied)
            {
                _logger.LogWarning($"Run 'Set-SPOsite {SiteRoot} -DenyAddAndCustomizePages 0' to enable AITracker custom actions for classic pages. " +
                    "The site currently has custom scripts disabled, so the installer could not register the per-site custom action.");
            }
        }

        public async Task AddModernUIAITrackerCustomActionToWeb(SpoWeb web, ModernAppCustomAction modernAppCustomAction)
        {
            var payload = new JObject
            {
                ["Name"] = modernAppCustomAction.Name,
                ["Title"] = modernAppCustomAction.Title,
                ["Description"] = modernAppCustomAction.Description,
                ["Location"] = modernAppCustomAction.Location,
                ["ClientSideComponentId"] = modernAppCustomAction.ClientSideComponentId.ToString(),
                ["ClientSideComponentProperties"] = modernAppCustomAction.ClientSideComponentProperties,
                ["Sequence"] = 1000
            };

            await _rest.PostAsync($"{Api(web)}/web/UserCustomActions", payload.ToString());
        }

        public Task RemoveAITrackerCustomActionFromWeb(SpoWeb web)
        {
            return DeleteAction(web, ModernAppCustomAction.DESCRIPTION, ModernAppCustomAction.LOCATION);
        }

        public Task RemoveModernUIAITrackerCustomActionFromWeb(SpoWeb web)
        {
            return DeleteAction(web, ClassicPageCustomAction.DESCRIPTION, ClassicPageCustomAction.LOCATION);
        }

        async Task DeleteAction(SpoWeb web, string description, string location)
        {
            var loopActionCheck = true;
            while (loopActionCheck)
            {
                var actions = await _rest.GetCollectionAsync($"{Api(web)}/web/UserCustomActions?$select=Id,Description,Location");

                var deletedAction = false;
                foreach (var action in actions)
                {
                    if (action["Description"]?.ToString() == description && action["Location"]?.ToString() == location)
                    {
                        var id = action["Id"]?.ToString();
                        await _rest.DeleteAsync($"{Api(web)}/web/UserCustomActions(guid'{id}')");

                        _logger.LogInformation($"Removed user action ID {id} from location {location} on {web.Url}");
                        deletedAction = true;

                        // Give SPO a chance to remove the custom action
                        Thread.Sleep(1000);
                        break;
                    }
                }
                loopActionCheck = deletedAction;
            }
        }

        #endregion

        #region Library & file

        public async Task<ListInfo> ConfirmDocLibOnRootSite(string listTitle)
        {
            var listUrl = ListUrl(listTitle);
            var list = await _rest.GetOrNullAsync($"{listUrl}?$select=Id,Title,EnableMinorVersions,ParentWebUrl,RootFolder/ServerRelativeUrl&$expand=RootFolder");

            var createdNew = false;
            if (list == null)
            {
                createdNew = true;
                var payload = new JObject
                {
                    ["Title"] = listTitle,
                    ["BaseTemplate"] = LISTTEMPLATE_DOCLIB,
                    ["AllowContentTypes"] = true,
                    ["ContentTypesEnabled"] = false
                };
                await _rest.PostAsync($"{SiteRoot}/_api/web/lists", payload.ToString());

                list = await _rest.GetAsync($"{listUrl}?$select=Id,Title,EnableMinorVersions,ParentWebUrl,RootFolder/ServerRelativeUrl&$expand=RootFolder");
            }

            var versioning = list["EnableMinorVersions"]?.Value<bool>() ?? false;
            var rootFolderUrl = list["RootFolder"]?["ServerRelativeUrl"]?.ToString() ?? string.Empty;
            var parentWebUrl = list["ParentWebUrl"]?.ToString() ?? string.Empty;

            // Preserve the previous behaviour: the library URL relative to its parent web.
            var siteRelativeUrl = rootFolderUrl;
            if (!string.IsNullOrEmpty(parentWebUrl) && rootFolderUrl.StartsWith(parentWebUrl, StringComparison.OrdinalIgnoreCase))
            {
                siteRelativeUrl = rootFolderUrl.Substring(parentWebUrl.Length);
            }
            siteRelativeUrl = siteRelativeUrl.TrimStart('/');

            return new ListInfo { CreatedNew = createdNew, EnableMinorVersions = versioning, SiteRelativeUrl = siteRelativeUrl };
        }

        public async Task AddTrackerToLibraryOnRootSite(string listTitle, byte[] aiTrackerContents, bool publish)
        {
            var list = await _rest.GetAsync($"{ListUrl(listTitle)}?$select=RootFolder/ServerRelativeUrl&$expand=RootFolder");
            var folder = list["RootFolder"]?["ServerRelativeUrl"]?.ToString();
            if (string.IsNullOrEmpty(folder))
            {
                throw new SpoRestException($"Library '{listTitle}' on {_siteUrl} has no root folder to upload '{FILENAME}' into.");
            }

            var url = $"{SiteRoot}/_api/web/GetFolderByServerRelativeUrl('{SpoRestClient.ODataLiteral(folder)}')" +
                      $"/Files/add(url='{SpoRestClient.ODataLiteral(FILENAME)}',overwrite=true)";
            await _rest.PostBytesAsync(url, aiTrackerContents);
        }

        public async Task<bool> RemoveTrackerIfExistsOnRootSite(string listTitle)
        {
            var items = await _rest.GetCollectionAsync($"{ListUrl(listTitle)}/items?$select=Id,FileLeafRef");

            foreach (var item in items)
            {
                var fileName = item["FileLeafRef"]?.ToString();
                if (fileName != null && string.Equals(fileName, FILENAME, StringComparison.OrdinalIgnoreCase))
                {
                    await _rest.DeleteAsync($"{ListUrl(listTitle)}/items({item["Id"]})");
                    return true;
                }
            }
            return false;
        }

        public async Task RemoveDocLibOnRootSite(string listTitle)
        {
            var list = await _rest.GetOrNullAsync($"{ListUrl(listTitle)}?$select=Id");
            if (list != null)
            {
                await _rest.DeleteAsync(ListUrl(listTitle));
            }
        }

        public async Task SecureList(string listTitle)
        {
            var listUrl = ListUrl(listTitle);

            // Stop inheriting, keeping no copied assignments and clearing sub-scopes - as the CSOM
            // BreakRoleInheritance(false, true) call did.
            await _rest.PostAsync($"{listUrl}/breakroleinheritance(copyRoleAssignments=false,clearSubscopes=true)");

            var readerRole = await _rest.GetAsync($"{SiteRoot}/_api/web/roledefinitions/getbytype({ROLETYPE_READER})");
            var roleDefId = readerRole["Id"]?.ToString();

            var user = await _rest.PostAsync($"{SiteRoot}/_api/web/ensureuser",
                new JObject { ["logonName"] = CLAIM_ALL_AUTHENTICATED_USERS }.ToString());
            var principalId = user["Id"]?.ToString();

            if (string.IsNullOrEmpty(roleDefId) || string.IsNullOrEmpty(principalId))
            {
                throw new SpoRestException($"Couldn't resolve the reader role or the all-authenticated-users principal on {_siteUrl}, so '{listTitle}' was not secured.");
            }

            await _rest.PostAsync($"{listUrl}/roleassignments/addroleassignment(principalid={principalId},roledefid={roleDefId})");
        }

        string ListUrl(string listTitle) => $"{SiteRoot}/_api/web/lists/getbytitle('{SpoRestClient.ODataLiteral(listTitle)}')";

        #endregion

        public void Dispose()
        {
            _rest?.Dispose();
        }
    }
}
