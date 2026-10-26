using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using System;
using System.Collections.Generic;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// The settings the Teams calls queue must have, whatever Azure's defaults are. Pure, so the decision is testable
    /// without Azure; <see cref="ServiceBusQueueWithPolicyInstallTask"/> applies it on create and on every reinstall.
    /// </summary>
    /// <remarks>
    /// The queue used to be created with <c>new ServiceBusQueueData()</c> and never updated, so it had Azure's defaults: on
    /// the Basic tier a 14-day time to live, with expired messages dropped. An importer outage longer than that silently
    /// lost every queued call notification. Now:
    /// <list type="bullet">
    /// <item>Default message time to live is set explicitly to the tier's maximum: 14 days on Basic, unlimited
    /// (<see cref="TimeSpan.MaxValue"/>, Azure's own default) on Standard and Premium - "For the basic tier, the default (also
    /// maximum) expiration time is 14 days", https://learn.microsoft.com/azure/service-bus-messaging/message-expiration.
    /// An unknown tier gets 14 days, which every tier accepts.</item>
    /// <item>Dead-lettering on message expiration is on, so an expired notification is kept in the dead-letter queue -
    /// which the importer's <c>--replayCallDeadLetters</c> can replay - rather than dropped. It is a queue property with
    /// no tier restriction: https://learn.microsoft.com/azure/service-bus-messaging/enable-dead-letter.</item>
    /// </list>
    /// </remarks>
    public static class ServiceBusQueueSettings
    {
        public static readonly TimeSpan BasicTierMaxTimeToLive = TimeSpan.FromDays(14);

        public static TimeSpan RequiredTimeToLive(ServiceBusSkuName? tier)
        {
            return tier == ServiceBusSkuName.Standard || tier == ServiceBusSkuName.Premium
                ? TimeSpan.MaxValue
                : BasicTierMaxTimeToLive;
        }

        /// <summary>
        /// Sets what differs from the required settings on <paramref name="queue"/> and says what it changed - nothing when
        /// it already complies, so a reinstall does not rewrite a queue that is fine.
        /// </summary>
        public static IReadOnlyList<string> Apply(ServiceBusQueueData queue, ServiceBusSkuName? tier)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            var changes = new List<string>();
            var ttl = RequiredTimeToLive(tier);
            if (queue.DefaultMessageTimeToLive != ttl)
            {
                changes.Add($"default message time to live {DescribeTtl(queue.DefaultMessageTimeToLive)} -> {DescribeTtl(ttl)}");
                queue.DefaultMessageTimeToLive = ttl;
            }

            if (queue.DeadLetteringOnMessageExpiration != true)
            {
                changes.Add("dead-lettering on message expiration off -> on");
                queue.DeadLetteringOnMessageExpiration = true;
            }

            return changes;
        }

        public static string Describe(ServiceBusQueueData queue)
        {
            return $"default message time to live {DescribeTtl(queue?.DefaultMessageTimeToLive)}, dead-lettering on expiration " +
                (queue?.DeadLetteringOnMessageExpiration == true ? "on" : "off");
        }

        private static string DescribeTtl(TimeSpan? ttl)
        {
            if (!ttl.HasValue) return "unset";
            return ttl.Value == TimeSpan.MaxValue ? "unlimited" : $"{ttl.Value.TotalDays:0.##} days";
        }
    }
}
