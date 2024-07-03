using App.ControlPanel.Engine.InstallerTasks.Adoptify.Models;
using App.ControlPanel.Engine.SharePointModelBuilder;
using CloudInstallEngine;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    /// <summary>
    /// Inserts list-items from Json files
    /// </summary>
    public class ListItemsInstallTask : BaseInstallTask
    {
        public const string PROP_NAME_LANG = "lang";
        private readonly ClientContext _clientContext;

        public ListItemsInstallTask(TaskConfig config, ILogger logger, ClientContext clientContext) : base(config, logger)
        {
            _clientContext = clientContext;
        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            base.EnsureContextArgType<AdoptifySiteListInfoWithAssetsUrl>(contextArg);

            var siteInfo = (AdoptifySiteListInfoWithAssetsUrl)contextArg;

            var code = _config[PROP_NAME_LANG];
            _clientContext.Load(_clientContext.Web, w => w.Url);
            await _clientContext.ExecuteQueryAsync();

            _logger.LogInformation($"Installing default content in language-code '{code}' to {_clientContext.Web.Url}");

            var rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            var defaultDataQuestsJson = string.Empty;
            var defaultDataBadgesJson = string.Empty;
            var defaultDataLevelsJson = string.Empty;
            var defaultDataStatsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultStats);
            var defaultDataSettingsJson = string.Empty;
            var defaultDataCardsJson = string.Empty;
            if (code == TargetSolutionConfig.LANG_ESPAÑOL)
            {
                defaultDataSettingsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultSettingsES);
                defaultDataQuestsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultQuestsES);
                defaultDataLevelsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultLevelsES);
                defaultDataBadgesJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultBadgesES);
                defaultDataCardsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultCardsES);
            }
            else
            {
                defaultDataSettingsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultSettingsEN);
                defaultDataQuestsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultQuestsEN);
                defaultDataLevelsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultLevelsEN);
                defaultDataBadgesJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultBadgesEN);
                defaultDataCardsJson = rr.ReadResourceString(ResourceNameConstants.SPDataDefaultCardsEN);
            }

            // Update field placerholders
            defaultDataQuestsJson = defaultDataQuestsJson.Replace("{siteAssetsBaseUrl}", siteInfo.AssetsBaseUrl);
            defaultDataBadgesJson = defaultDataBadgesJson.Replace("{siteAssetsBaseUrl}", siteInfo.AssetsBaseUrl);
            defaultDataLevelsJson = defaultDataLevelsJson.Replace("{siteAssetsBaseUrl}", siteInfo.AssetsBaseUrl);


            var newLevels = await SiteBuilder.ApplyListData(defaultDataLevelsJson, _clientContext, siteInfo.LevelsListId);
            _logger.LogInformation($"Inserted {newLevels} new levels");

            var newBadges = await SiteBuilder.ApplyListData(defaultDataBadgesJson, _clientContext, siteInfo.BadgesListId);
            _logger.LogInformation($"Inserted {newBadges} new badges");

            var newStats = await SiteBuilder.ApplyListData(defaultDataStatsJson, _clientContext, siteInfo.StatsListId);
            _logger.LogInformation($"Inserted {newLevels} default stats");

            var newSettings = await SiteBuilder.ApplyListData(defaultDataSettingsJson, _clientContext, siteInfo.SettingsListId);
            _logger.LogInformation($"Inserted {newSettings} default settings");

            var newQuests = await SiteBuilder.ApplyListData(defaultDataQuestsJson, _clientContext, siteInfo.QuestsListId);
            _logger.LogInformation($"Inserted {newQuests} new quests");

            var newCards = await SiteBuilder.ApplyListData(defaultDataCardsJson, _clientContext, siteInfo.CardsListId);
            _logger.LogInformation($"Inserted {newCards} new cards");

            // Pass to next task
            return siteInfo;
        }
    }
}
