using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    /// <summary>
    /// Updates "Title" default fields to not be required
    /// </summary>
    public class AdoptifySiteFieldUpdatesTask : BaseInstallTask
    {
        private readonly ClientContext _clientContext;

        public AdoptifySiteFieldUpdatesTask(TaskConfig config, ILogger logger, ClientContext clientContext) : base(config, logger)
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
            await _clientContext.ExecuteQueryAsync();
            _logger.LogInformation($"Updating default SPO schema at {_clientContext.Web.Url}");

            await UpdateTitleField("Users");
            await UpdateTitleField("User Quests");
            await UpdateTitleField("User Quest Processing");
            await UpdateTitleField("User Reward Processing");
            await UpdateTitleField("User Badges");
            await UpdateTitleField("Stats");

            _logger.LogInformation($"SharePoint default site schema modified");

            return null;
        }

        private async Task UpdateTitleField(string v)
        {
            _clientContext.Load(_clientContext.Web.Lists);
            await _clientContext.ExecuteQueryAsync();

            var list = _clientContext.Web.Lists.Where(l => l.Title == v).SingleOrDefault();
            if (list == null)
                throw new ArgumentOutOfRangeException(nameof(v), $"No list by title '{v}'");

            _clientContext.Load(list.Fields);
            await _clientContext.ExecuteQueryAsync();

            var titleField = list.Fields.Where(f => f.StaticName == "Title").SingleOrDefault();
            titleField.Required = false;
            titleField.UpdateAndPushChanges(true);
        }
    }
}
