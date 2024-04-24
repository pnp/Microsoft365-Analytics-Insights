using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Install an ARM template of some kind
    /// </summary>
    public abstract class GenericARMInstallTask : InstallTaskInAzResourceGroup<ArmDeploymentResource>
    {
        protected GenericARMInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public async Task<ArmDeploymentResource> ApplyTemplate(object newCreationParams, string templateJson)
        {
            if (newCreationParams is null)
            {
                throw new ArgumentNullException(nameof(newCreationParams));
            }

            if (string.IsNullOrEmpty(templateJson))
            {
                throw new ArgumentException($"'{nameof(templateJson)}' cannot be null or empty.", nameof(templateJson));
            }

            var armDeploymentInfo = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(templateJson),
                Parameters = BinaryData.FromObjectAsJson(newCreationParams)
            });

            try
            {
                var result = await Container.GetArmDeployments()
                    .CreateOrUpdateAsync(WaitUntil.Completed, this.GetType().Name + DateTime.Now.Ticks, armDeploymentInfo);
                return result.Value;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"{this.TaskName} threw error {ex.Message}");
                throw;
            }
        }
    }
}
