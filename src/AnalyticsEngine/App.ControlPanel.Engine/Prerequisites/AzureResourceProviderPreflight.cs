using Azure;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.Prerequisites
{
    internal static class AzureResourceProviderPreflight
    {
        public static IReadOnlyList<string> RequiredProviders(SolutionInstallConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var providers = new List<string>
            {
                "Microsoft.Web", "Microsoft.Sql", "Microsoft.Storage", "Microsoft.KeyVault",
                "Microsoft.Insights", "Microsoft.OperationalInsights"
            };
            if (config.ServiceBusEnabled || config.SolutionConfig?.ImportTaskSettings?.Calls == true) providers.Add("Microsoft.ServiceBus");
            if (config.CognitiveServicesEnabled || config.FoundryPromptEnabled) providers.Add("Microsoft.CognitiveServices");
            if (config.SolutionConfig?.ImportTaskSettings?.GraphUsageReports == true) providers.Add("Microsoft.Automation");
            if (config.NetworkConfig?.Enabled == true) providers.Add("Microsoft.Network");
            return providers;
        }

        /// <summary>
        /// Read registration states without registering providers or requiring subscription-wide write access.
        /// Only a confirmed unavailable provider blocks deployment; an inconclusive check warns.
        /// </summary>
        public static async Task<bool> CheckAsync(SubscriptionResource subscription, SolutionInstallConfig config,
            ILogger logger, CancellationToken cancellationToken = default(CancellationToken))
        {
            var canDeploy = true;
            logger.LogInformation("Checking Azure resource-provider registration for the selected services (read-only)...");
            foreach (var provider in RequiredProviders(config))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string state;
                try
                {
                    var response = await subscription.GetResourceProviders().GetAsync(provider, cancellationToken: cancellationToken);
                    state = response.Value.Data.RegistrationState;
                }
                catch (RequestFailedException ex)
                {
                    logger.LogWarning($"Could not verify Azure resource provider '{provider}' (HTTP {ex.Status}, {ex.ErrorCode}). " +
                        "Registration is unverified, not missing. Have a subscription administrator check Subscription > Resource providers. " +
                        "Reading this status requires Microsoft.Resources/subscriptions/providers/read at subscription scope; " +
                        "resource-group access alone may not include it. Continuing without changing registration.");
                    continue;
                }

                if (string.Equals(state, "Registered", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation($"Azure resource provider '{provider}': Registered.");
                }
                else if (string.Equals(state, "Registering", StringComparison.OrdinalIgnoreCase))
                {
                    // Azure registers each region separately; global Registering does not mean this region is unavailable.
                    logger.LogWarning($"Azure resource provider '{provider}': Registering. Continuing because registration may already " +
                        "be complete in the deployment region. If Azure rejects resource creation, allow registration to finish and retry.");
                }
                else if (string.Equals(state, "NotRegistered", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Unregistered", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Unregistering", StringComparison.OrdinalIgnoreCase))
                {
                    canDeploy = false;
                    logger.LogError($"Required Azure resource provider '{provider}' is {state}. Before deploying, have an administrator " +
                        $"with subscription-scope '{provider}/register/action' permission register it under Subscription > Resource providers, " +
                        $"or run: az provider register --namespace {provider} --subscription {subscription.Id.SubscriptionId}. " +
                        "Then retry the installer. Resource-group Contributor alone cannot register subscription providers; " +
                        "the installer does not register them automatically.");
                }
                else
                {
                    logger.LogWarning($"Azure resource provider '{provider}' returned an unrecognised registration state '{state ?? "(empty)"}'. " +
                        "Registration is unverified. Check Subscription > Resource providers before deploying; continuing without changing registration.");
                }
            }
            return canDeploy;
        }
    }
}
