using Azure;
using Azure.Core;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Configure a service-bus queue + an access policy
    /// </summary>
    public class ServiceBusQueueWithPolicyInstallTask : InstallTaskInAzResourceGroup<ServiceBusQueueResourceWithConnectionString>
    {
        public const string CONFIG_KEY_RULE_NAME = "policyname";

        public ServiceBusQueueWithPolicyInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation) : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
        }

        public override string TaskName => "get/create Service-bus queue with access policy";

        public async override Task<ServiceBusQueueResourceWithConnectionString> ExecuteTaskReturnResult(object contextArg)
        {
            // Get NS created by parent task
            base.EnsureContextArgType<ServiceBusNamespaceResource>(contextArg);
            var sbNS = (ServiceBusNamespaceResource)contextArg;

            var name = base._config.GetNameConfigValue();
            var policyName = base._config.GetConfigValue(CONFIG_KEY_RULE_NAME);

            ServiceBusQueueResource queue = null;
            try
            {
                var existingQueue = await sbNS.GetServiceBusQueueAsync(name);
                queue = existingQueue.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Ignore
            }

            if (queue == null)
            {
                var allQueues = sbNS.GetServiceBusQueues();

                var operation = await allQueues.CreateOrUpdateAsync(WaitUntil.Completed, name, new ServiceBusQueueData());
                queue = operation.Value;

                _logger.LogInformation($"Created service-bus queue '{name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing service-bus queue '{name}'.");
            }

            // TODO: create policy against the queue, not namespace. 
            ServiceBusNamespaceAuthorizationRuleResource rule = null;
            try
            {
                rule = await sbNS.GetServiceBusNamespaceAuthorizationRuleAsync(policyName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Ignore
            }

            if (rule == null)
            {
                var allRules = sbNS.GetServiceBusNamespaceAuthorizationRules();

                // Create the rule
                var ruleInfo = new ServiceBusAuthorizationRuleData();
                ruleInfo.Rights.Add(ServiceBusAccessRight.Send);
                ruleInfo.Rights.Add(ServiceBusAccessRight.Listen);
                var operation = await allRules.CreateOrUpdateAsync(WaitUntil.Completed, policyName, ruleInfo);
                rule = operation.Value;

                _logger.LogInformation($"Created service-bus access policy '{policyName}'.");
            }

            var key = await rule.GetKeysAsync();
            var connectionString = $"{key.Value.PrimaryConnectionString};EntityPath={name}";

            return new ServiceBusQueueResourceWithConnectionString { ConnectionString = connectionString, Queue = queue };
        }
    }


    public class ServiceBusQueueResourceWithConnectionString
    {
        public ServiceBusQueueResource Queue { get; set; } = null;
        public string ConnectionString { get; set; } = null;
    }
}
