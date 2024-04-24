using Azure.Core;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Common.DataUtils;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    #region Abstract

    /// <summary>
    /// Base ARM deployment for Adoptify
    /// </summary>
    public abstract class AdoptifySingleResourceArmInstallTask : GenericArmSingleResourceInstallTask<ArmOperationResult>
    {
        private readonly string _resourceName;
        private readonly string _resourceType;
        private readonly string _json;

        protected AdoptifySingleResourceArmInstallTask(string resourceName, TaskConfig configWithTemplateParams, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, string resourceType, string json) 
            : base(configWithTemplateParams, logger, azureLocation, tags)
        {
            _resourceName = resourceName;
            _resourceType = resourceType;
            _json = json;
        }

        /// <summary>
        /// Parameters the ARM template needs
        /// </summary>
        public abstract string[] ArmParamNames { get; }

        public async override Task<ArmOperationResult> ExecuteTaskReturnResult(object contextArg)
        {
            // Get or create from ARM template
            var armParams = _config.FilterParams(ArmParamNames).ToArmParamsObject();
            await DeleteIfExistsGenericResource(_resourceName, _resourceType);
            var resourceCreateInfo = await base.CreateGenericAzResourceAndEnsureTags(_resourceName, _resourceType, armParams, _json);

            return resourceCreateInfo;
        }
    }


    /// <summary>
    /// ARM deployment for Logic Apps. Resource-type is common. 
    /// </summary>
    public abstract class AdoptifySingleLogicAppInstallTask : AdoptifySingleResourceArmInstallTask
    {
        const string LOGIC_APP_TYPE = "Microsoft.Logic/workflows";

        protected AdoptifySingleLogicAppInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, string json)
            : base(resourceName, config, logger, azureLocation, tags, LOGIC_APP_TYPE, json)
        {
        }
    }
    #endregion

    public class ApiConnectionsAppInstallTask : GenericARMInstallTask
    {
        public ApiConnectionsAppInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public string[] ArmParamNames => new string[]
        {
            "subscriptionId", "location", "sqlservername", "sqldbname", "sqlusername", "sqlpassword",
            "keyvaultName", "appId", "appSecret", "tenantId"
        };

        public override async Task<ArmDeploymentResource> ExecuteTaskReturnResult(object contextArg)
        {
            // Todo: add tags to these templates
            _logger.LogInformation($"Creating API connections");

            var json = new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ApiConnections);
            var armParams = _config.FilterParams(ArmParamNames).ToArmParamsObject(new { tagsArray = new { value = Tags } });
            var created = await base.ApplyTemplate(armParams, json);

            return created;
        }
    }


    public class ProcessDeviceUsageInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessDeviceUsageInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessDeviceUsage)) { }

        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "badgesListId", "userBadgesListId", "userListId", "questsListId",
                "settingsListId", "userQuestsListId", "questProcessingListId", "cardsListId"
        };
    }
    public class ProcessCallModalityTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessCallModalityTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessCallModality)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "badgesListId", "userBadgesListId", "userListId", "questsListId",
                "settingsListId", "questsListId", "userQuestsListId", "questProcessingListId", "cardsListId"
        };
    }
    public class ProcessChatsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessChatsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessChats)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "badgesListId", "userBadgesListId", "userListId", "questsListId",
                "settingsListId", "questsListId", "userQuestsListId", "questProcessingListId", "cardsListId"
        };
    }
    public class ProcessCountsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessCountsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessCounts)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "badgesListId", "userBadgesListId", "userListId", "questsListId",
            "statsListId", "levelsListId", "userQuestsListId", "rewardsListId", "userRewardsListId", "userQuestsListId"
        };
    }
    public class ProcessMeetingsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessMeetingsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessMeetings)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "badgesListId", "userBadgesListId", "userListId", "questsListId",
                "settingsListId", "questsListId", "userQuestsListId", "questProcessingListId", "cardsListId"
        };
    }
    public class ProcessRedeemedRewardsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessRedeemedRewardsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessRedeemedRewards)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "userListId", "settingsListId",
                "userRewardsListId", "rewardsListId", "cardsListId", "userRewardProcessingListId"
        };
    }
    public class ProcessUsageRemindersInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessUsageRemindersInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessUsageReminders)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "userListId", "settingsListId", "rewardsListId", "cardsListId"
        };
    }
    public class ProcessQuestNotificationsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessQuestNotificationsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.QuestNotification)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "userListId", "questsListId", "settingsListId", "cardsListId"
        };
    }

    public class ProcessReactionsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessReactionsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessReactions)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "badgesListId", "levelsListId",
                "userListId", "userBadgesListId", "userQuestsListId", "userRewardsListId", "questsListId", "questProcessingListId",
                "settingsListId", "rewardsListId", "cardsListId"
        };
    }

    public class SyncTeamsAppsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public SyncTeamsAppsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.SyncTeamsApps)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "teamsAppsListId", "insightsAppId", "tenantId"
        };
    }

    public class ProcessTeamsAppsInstallTask : AdoptifySingleLogicAppInstallTask
    {
        public ProcessTeamsAppsInstallTask(string resourceName, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(resourceName, config, logger, azureLocation, tags, new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly()).ReadResourceStringFromExecutingAssembly(ResourceNameConstants.ProcessTeamsApps)) { }
        public override string[] ArmParamNames => new string[]
        {
            "resourceGroupName", "subscriptionId", "location", "adoptifySiteUrl", "questsListId", "questProcessingListId", "userListId", "userQuestsListId",
                "userBadgesListId", "badgesListId", "levelsListId", "settingsListId", "rewardsListId", "userRewardsListId", "cardsListId"
        };
    }
}
