using Azure.Identity;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Base implementation of any install job in the analytics engine installer. Creates own subscription & ARM client from config.
    /// </summary>
    public abstract class BaseAnalyticsSolutionInstallJob : AzureInstallJob
    {
        protected readonly SolutionInstallConfig _config;

        /// <summary>
        /// Create new job with new Arm client & resource-group loader, using solution config
        /// </summary>
        public BaseAnalyticsSolutionInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription)
            : base(logger, config.AzureLocation, config.Tags.ToDictionary(), subscription, config.ResourceGroupName)
        {
            _config = config;
        }

        public static SubscriptionResource FromConfig(SolutionInstallConfig config)
        {
            var azureSub = FromTokenCredential(new ClientSecretCredential(config.InstallerAccount.DirectoryId, config.InstallerAccount.ClientId,
                config.InstallerAccount.Secret), config.Subscription.SubId);


            return azureSub;
        }
    }
}
