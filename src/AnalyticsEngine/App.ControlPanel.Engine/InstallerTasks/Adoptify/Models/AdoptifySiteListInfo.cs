using CloudInstallEngine;
using Microsoft.SharePoint.Client;
using System;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify.Models
{
    /// <summary>
    /// Schema info for Adoptiy SP site
    /// </summary>
    public class AdoptifySiteListInfo
    {
        public static async Task<AdoptifySiteListInfo> GetFromSite(ClientContext clientContext, string siteCollectionUrl)
        {
            var badgesList = clientContext.Web.GetListByTitle("Badges");
            var userBadgesList = clientContext.Web.GetListByTitle("User Badges");
            var userList = clientContext.Web.GetListByTitle("Users");
            var questsList = clientContext.Web.GetListByTitle("Quests");
            var userQuestsList = clientContext.Web.GetListByTitle("User Quests");
            var settingsList = clientContext.Web.GetListByTitle("Settings");
            var levelsList = clientContext.Web.GetListByTitle("Levels");
            var statsList = clientContext.Web.GetListByTitle("Stats");
            var assetsList = clientContext.Web.GetListByTitle("Site Assets");
            var userRewardsList = clientContext.Web.GetListByTitle("User Rewards");
            var rewardsList = clientContext.Web.GetListByTitle("Rewards");
            var userRewardProcessingList = clientContext.Web.GetListByTitle("User Reward Processing");
            var questProcessingList = clientContext.Web.GetListByTitle("User Quest Processing");
            var teamsAppsList = clientContext.Web.GetListByTitle("Teams Apps");
            var cardsList = clientContext.Web.GetListByTitle("Cards");

            if (badgesList == null) throw new AdoptifyInstallException("Can't find badges list in target site");
            if (userBadgesList == null) throw new AdoptifyInstallException("Can't find user badges list in target site");
            if (userList == null) throw new AdoptifyInstallException("Can't find users list in target site");
            if (questsList == null) throw new AdoptifyInstallException("Can't find quests list in target site");
            if (questProcessingList == null) throw new AdoptifyInstallException("Can't find quests processing list in target site");
            if (userQuestsList == null) throw new AdoptifyInstallException("Can't find user quests list in target site");
            if (settingsList == null) throw new AdoptifyInstallException("Can't find settings list in target site");
            if (levelsList == null) throw new AdoptifyInstallException("Can't find levels list in target site");
            if (statsList == null) throw new AdoptifyInstallException("Can't find stats list in target site");
            if (assetsList == null) throw new AdoptifyInstallException("Can't find assets list in target site");
            if (userRewardsList == null) throw new AdoptifyInstallException("Can't find user rewards list in target site");
            if (rewardsList == null) throw new AdoptifyInstallException("Can't find rewards list in target site");
            if (teamsAppsList == null) throw new AdoptifyInstallException("Can't find teams apps list in target site");
            if (cardsList == null) throw new AdoptifyInstallException("Can't find teams cards list in target site");
            if (userRewardProcessingList == null) throw new AdoptifyInstallException("Can't find user reward Processing list in target site");

            clientContext.Load(badgesList, l => l.Id);
            clientContext.Load(userBadgesList, l => l.Id);
            clientContext.Load(userList, l => l.Id);
            clientContext.Load(questsList, l => l.Id);
            clientContext.Load(questProcessingList, l => l.Id);
            clientContext.Load(userQuestsList, l => l.Id);
            clientContext.Load(settingsList, l => l.Id);
            clientContext.Load(levelsList, l => l.Id);
            clientContext.Load(statsList, l => l.Id);
            clientContext.Load(assetsList, l => l.Id);
            clientContext.Load(userRewardsList, l => l.Id);
            clientContext.Load(rewardsList, l => l.Id);
            clientContext.Load(teamsAppsList, l => l.Id);
            clientContext.Load(cardsList, l => l.Id);
            clientContext.Load(userRewardProcessingList, l => l.Id);
            await clientContext.ExecuteQueryAsync();

            return new AdoptifySiteListInfo()
            {
                BadgesListId = badgesList.Id,
                QuestsListId = questsList.Id,
                UserQuestsListId = userQuestsList.Id,
                QuestProcessingListId = questProcessingList.Id,
                SettingsListId = settingsList.Id,
                UserBadgesListId = userBadgesList.Id,
                UserListId = userList.Id,
                LevelsListId = levelsList.Id,
                StatsListId = statsList.Id,
                AssetsListId = assetsList.Id,
                AdoptifySiteUrl = siteCollectionUrl,
                UserRewardsListId = userRewardsList.Id,
                RewardsListId = rewardsList.Id,
                TeamsAppsListId = teamsAppsList.Id,
                CardsListId = cardsList.Id,
                UserRewardProcessingListId = userRewardProcessingList.Id
            };
        }

        public string AdoptifySiteUrl { get; set; } = string.Empty;
        public Guid SettingsListId { get; set; } = Guid.Empty;
        public Guid QuestsListId { get; set; } = Guid.Empty;
        public Guid QuestProcessingListId { get; set; } = Guid.Empty;
        public Guid UserQuestsListId { get; set; } = Guid.Empty;
        public Guid UserListId { get; set; } = Guid.Empty;
        public Guid UserBadgesListId { get; set; } = Guid.Empty;
        public Guid BadgesListId { get; set; } = Guid.Empty;
        public Guid LevelsListId { get; set; } = Guid.Empty;
        public Guid StatsListId { get; set; } = Guid.Empty;
        public Guid AssetsListId { get; set; } = Guid.Empty;
        public Guid UserRewardsListId { get; set; } = Guid.Empty;
        public Guid UserRewardProcessingListId { get; set; } = Guid.Empty;
        public Guid RewardsListId { get; set; } = Guid.Empty;
        public Guid TeamsAppsListId { get; set; } = Guid.Empty;
        public Guid CardsListId { get; set; } = Guid.Empty;

        public TaskConfig ToConfig()
        {
            var c = new TaskConfig();

            c.AddSetting("adoptifySiteUrl", AdoptifySiteUrl);
            c.AddSetting("badgesListId", BadgesListId.ToString());
            c.AddSetting("userBadgesListId", UserBadgesListId.ToString());
            c.AddSetting("userListId", UserListId.ToString());
            c.AddSetting("questsListId", QuestsListId.ToString());
            c.AddSetting("userQuestsListId", UserQuestsListId.ToString());
            c.AddSetting("questProcessingListId", QuestProcessingListId.ToString());

            c.AddSetting("userRewardsListId", UserRewardsListId.ToString());
            c.AddSetting("rewardsListId", RewardsListId.ToString());
            c.AddSetting("userRewardProcessingListId", UserRewardProcessingListId.ToString());

            c.AddSetting("statsListId", StatsListId.ToString());
            c.AddSetting("settingsListId", SettingsListId.ToString());
            c.AddSetting("levelsListId", LevelsListId.ToString());
            c.AddSetting("assetsListId", AssetsListId.ToString());
            c.AddSetting("teamsAppsListId", TeamsAppsListId.ToString());
            c.AddSetting("cardsListId", CardsListId.ToString());

            return c;
        }
    }

    public class AdoptifySiteListInfoWithAssetsUrl : AdoptifySiteListInfo
    {
        public AdoptifySiteListInfoWithAssetsUrl(AdoptifySiteListInfo adoptifySiteListInfo)
        {
            this.AdoptifySiteUrl = adoptifySiteListInfo.AdoptifySiteUrl;
            this.AssetsListId = adoptifySiteListInfo.AssetsListId;
            this.BadgesListId = adoptifySiteListInfo.BadgesListId;
            this.SettingsListId = adoptifySiteListInfo.SettingsListId;
            this.StatsListId = adoptifySiteListInfo.StatsListId;
            this.QuestsListId = adoptifySiteListInfo.QuestsListId;
            this.LevelsListId = adoptifySiteListInfo.LevelsListId;
            this.UserListId = adoptifySiteListInfo.UserListId;
            this.UserBadgesListId = adoptifySiteListInfo.UserBadgesListId;
            this.CardsListId = adoptifySiteListInfo.CardsListId;

        }

        public string AssetsBaseUrl { get; set; } = string.Empty;
    }
}
