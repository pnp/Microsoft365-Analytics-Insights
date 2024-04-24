using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Install something into Azure, into a resource group,
    /// </summary>
    public abstract class AzureInstallJob : InstallJobInContainerJob<ResourceGroupResource>
    {
        protected readonly SubscriptionResource _subscription;
        public SubscriptionResource Subscription { get { return _subscription; } }

        public AzureLocation Location { get; }
        public string ResourceGroupName { get; }

        /// <summary>
        /// Create new job & instantiate new ResourceGroupContainerLoader
        /// </summary>
        public AzureInstallJob(ILogger logger, AzureLocation location, Dictionary<string, string> tags, SubscriptionResource subscription, string resourceGroupName)
            : base(logger, new ResourceGroupContainerLoader(TaskConfig.GetConfigForName(resourceGroupName), logger, subscription, location, tags))
        {
            Location = location;
            _subscription = subscription;
            ResourceGroupName = resourceGroupName;
        }

        public static SubscriptionResource FromTokenCredential(TokenCredential credential, string subscriptionId)
        {
            if (subscriptionId is null)
            {
                throw new ArgumentNullException(nameof(subscriptionId));
            }

            var client = new ArmClient(credential);
            var allSubs = client.GetSubscriptions().ToList();
            var subscription = allSubs.Where(sub => sub.Data.SubscriptionId == subscriptionId).SingleOrDefault();
            if (subscription == null)
            {
                throw new InstallException($"Can't find subscription with Id '{subscriptionId}' - check account has read access to the subscription");
            }

            return subscription;
        }
    }
}
