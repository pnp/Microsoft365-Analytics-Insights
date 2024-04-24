using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.SiteTrackerInstaller
{
    public class SpoSiteInstallAdaptor : ISiteInstallAdaptor<Web>
    {
        private readonly ClientContext _clientContext;
        private readonly string _siteUrl;
        private readonly ILogger _logger;
        const string FILENAME = "AITracker.js";

        public SpoSiteInstallAdaptor(string siteUrl, ILogger logger)
        {
            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            _clientContext = authManager.GetWebLoginClientContext(siteUrl);
            _siteUrl = siteUrl;
            _logger = logger;
        }

        public async Task<bool> Init()
        {
            if (_clientContext == null)
            {
                return false;
            }
            _clientContext.RequestTimeout = Timeout.Infinite;

            // Load webs
            _clientContext.Load(_clientContext.Site, s => s.Url);
            _clientContext.Load(_clientContext.Site.RootWeb);
            _clientContext.Load(_clientContext.Site.RootWeb.Webs);

            try
            {
                await _clientContext.ExecuteQueryAsync();
            }
            catch (WebException)
            {
                return false;
            }
            return true;
        }

        public List<Web> SubWebs => _clientContext.Site.RootWeb.Webs.ToList();

        public Web RootWeb => _clientContext.Site.RootWeb;

        public string SiteUrl => _siteUrl;

        public async Task AddAITrackerCustomActionToWeb(Web web, ClassicPageCustomAction classicPageCustomAction)
        {
            var newAction = web.UserCustomActions.Add();
            newAction.Description = classicPageCustomAction.Description;
            newAction.ScriptBlock = classicPageCustomAction.ScriptBlock;
            newAction.Location = classicPageCustomAction.Location;
            newAction.Update();

            var success = false;
            try
            {
                await _clientContext.ExecuteQueryAsync();
                success = true;
            }
            catch (ServerUnauthorizedAccessException)
            {
                success = false;
            }
            if (success)
            {
                _logger.LogInformation($"Inserted custom-action into web: '{web.Url}'");
            }
            else
            {
                _logger.LogWarning($"WARNING: Failed to configure custom actions for classic pages - custom scripts enabled? Run 'Set-SPOsite {_clientContext.Site.Url} -DenyAddAndCustomizePages 0' to enable customisations");
            }
        }

        public async Task AddModernUIAITrackerCustomActionToWeb(Web web, ModernAppCustomAction modernAppCustomAction)
        {
            var newAction = web.UserCustomActions.Add();
            newAction.Name = modernAppCustomAction.Name;
            newAction.Title = modernAppCustomAction.Title;
            newAction.Description = modernAppCustomAction.Description;
            newAction.Location = modernAppCustomAction.Location;
            newAction.ClientSideComponentId = modernAppCustomAction.ClientSideComponentId;
            newAction.ClientSideComponentProperties = modernAppCustomAction.ClientSideComponentProperties;
            newAction.Update();

            await _clientContext.ExecuteQueryAsync();
        }

        public async Task AddTrackerToLibraryOnRootSite(string listTitle, byte[] aiTrackerContents, bool publish)
        {
            var list = _clientContext.Web.GetListByTitle(listTitle);
            _clientContext.Load(list);
            await _clientContext.ExecuteQueryAsync();

            var fileCreationInfo = new FileCreationInformation
            {
                Content = aiTrackerContents,
                Overwrite = true,
                Url = FILENAME
            };

            var uploadFile = list.RootFolder.Files.Add(fileCreationInfo);
            _clientContext.Load(uploadFile);
            await _clientContext.ExecuteQueryAsync();
        }

        public async Task<ListInfo> ConfirmDocLibOnRootSite(string listTitle)
        {
            var list = _clientContext.Web.GetListByTitle(listTitle);
            bool createdNew = false, versioning = false;
            if (list == null)
            {
                createdNew = true;
                list = _clientContext.Web.CreateDocumentLibrary(listTitle);
                await _clientContext.ExecuteQueryAsync();
            }

            _clientContext.Load(list);
            _clientContext.Load(list, l => l.RootFolder.ServerRelativeUrl);
            await _clientContext.ExecuteQueryAsync();
            versioning = list.EnableMinorVersions;


            return new ListInfo { CreatedNew = createdNew, EnableMinorVersions = versioning, ServerRelativeUrl = list.RootFolder.ServerRelativeUrl };
        }

        public string GetUrl(Web web)
        {
            return web.Url;
        }
        public async Task<bool> RemoveTrackerIfExistsOnRootSite(string listTitle)
        {
            var list = _clientContext.Web.GetListByTitle(listTitle);
            _clientContext.Load(list);

            var allItems = list.GetItems(CamlQuery.CreateAllItemsQuery());
            _clientContext.Load(allItems);

            await _clientContext.ExecuteQueryAsync();

            foreach (var item in allItems)
            {
                var fileName = item.FieldValues["FileLeafRef"];
                if (fileName != null && fileName.ToString().ToLower() == FILENAME.ToLower())
                {
                    item.DeleteObject();
                    await _clientContext.ExecuteQueryAsync();

                    return true;
                }
            }

            return false;
        }

        public async Task SecureList(string listTitle)
        {
            var list = _clientContext.Web.GetListByTitle(listTitle);
            _clientContext.Load(list);
            await _clientContext.ExecuteQueryAsync();
            list.BreakRoleInheritance(false, true);

            var roleDefinition = _clientContext.Site.RootWeb.RoleDefinitions.GetByType(RoleType.Reader);
            var roleBindings = new RoleDefinitionBindingCollection(_clientContext) { roleDefinition };

            // All authenticated users
            var user = _clientContext.Web.EnsureUser("c:0(.s|true");

            list.RoleAssignments.Add(user, roleBindings);
            list.Update();

            await _clientContext.ExecuteQueryAsync();

        }

        public async Task RemoveDocLibOnRootSite(string listTitle)
        {
            var list = _clientContext.Web.GetListByTitle(listTitle);
            if (list != null)
            {
                _clientContext.Load(list);
                await _clientContext.ExecuteQueryAsync();

                list.DeleteObject();
                await _clientContext.ExecuteQueryAsync();
            }
        }

        public async Task RemoveAITrackerCustomActionFromWeb(Web web)
        {
            await DeleteAction(web, ModernAppCustomAction.DESCRIPTION, ModernAppCustomAction.LOCATION);
        }
        public async Task RemoveModernUIAITrackerCustomActionFromWeb(Web web)
        {
            await DeleteAction(web, ClassicPageCustomAction.DESCRIPTION, ClassicPageCustomAction.LOCATION);
        }

        async Task DeleteAction(Web web, string description, string location)
        {
            var loopActionCheck = true;
            while (loopActionCheck)
            {
                _clientContext.Load(web.UserCustomActions);
                await _clientContext.ExecuteQueryAsync();

                var deletedAction = false;
                foreach (var action in web.UserCustomActions)
                {
                    if (action.Description == description && action.Location == location)
                    {
                        action.DeleteObject();
                        await _clientContext?.ExecuteQueryAsync();

                        _logger.LogInformation($"Removed user action ID {action.Id} from location {location} on {web.Url}");
                        deletedAction = true;

                        // Give SPO a chance to remove the custom action
                        Thread.Sleep(1000);
                        break;
                    }
                }
                loopActionCheck = deletedAction;
            }
        }
        public void Dispose()
        {
            _clientContext?.Dispose();
        }

    }
}
