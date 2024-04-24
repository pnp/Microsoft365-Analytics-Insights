using App.ControlPanel.Engine.InstallerTasks.Adoptify.Models;
using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    /// <summary>
    /// Just loads SPO schema for next task
    /// </summary>
    public class AdoptifyLoadSiteSchemaTask : BaseInstallTask
    {
        private readonly ClientContext _clientContext;

        public AdoptifyLoadSiteSchemaTask(ILogger logger, ClientContext clientContext) : base(TaskConfig.NoConfig, logger)
        {
            _clientContext = clientContext;
        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            if (_clientContext == null)
            {
                throw new AdoptifyInstallException("No client context");
            }

            _clientContext.Load(_clientContext.Web, w => w.Url);
            _clientContext.Load(_clientContext.Site, s => s.Url);
            await _clientContext.ExecuteQueryAsync();
            _logger.LogInformation($"Loading SPO schema at {_clientContext.Web.Url}");


            // Return Ids
            var siteInfo = await AdoptifySiteListInfo.GetFromSite(_clientContext, _clientContext.Site.Url);

            return siteInfo;
        }
    }
}
