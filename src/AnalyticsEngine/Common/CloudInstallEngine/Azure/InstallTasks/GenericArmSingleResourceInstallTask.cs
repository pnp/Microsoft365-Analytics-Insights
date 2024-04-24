using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Generic ARM deployment helper class for a single resource. 
    /// </summary>
    public abstract class GenericArmSingleResourceInstallTask<TASKRESULTINGRESOURCE> : InstallTaskInAzResourceGroup<TASKRESULTINGRESOURCE>
    {
        public GenericArmSingleResourceInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) 
            : base(config, logger, azureLocation, tags)
        {
        }

        public async Task<GetOrCreateArmOperationResult> GetOrCreateGenericAzResource(string name, string type, object newCreationParams, string templateJson)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));
            }

            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException($"'{nameof(type)}' cannot be null or empty.", nameof(type));
            }

            if (newCreationParams is null)
            {
                throw new ArgumentNullException(nameof(newCreationParams));
            }

            if (string.IsNullOrEmpty(templateJson))
            {
                throw new ArgumentException($"'{nameof(templateJson)}' cannot be null or empty.", nameof(templateJson));
            }

            var resource = GetGenericAzResource(name, type);

            var createdNew = false;
            if (resource == null)
            {
                await CreateGenericAzResourceAndEnsureTags(name, type, newCreationParams, templateJson);
                resource = GetGenericAzResource(name, type);
                createdNew = true;
            }
            else
            {
                await base.EnsureTagsOnExisting(resource.Data.Tags, resource.GetTagResource());
            }

            return new GetOrCreateArmOperationResult { CreatedNew = createdNew, ResourceId = resource.Id };
        }


        public async Task<ArmOperationResult> CreateGenericAzResourceAndEnsureTags(string name, string type, object newCreationParams, string templateJson)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));
            if (string.IsNullOrEmpty(type)) throw new ArgumentException($"'{nameof(type)}' cannot be null or empty.", nameof(type));
            if (newCreationParams is null) throw new ArgumentNullException(nameof(newCreationParams));
            if (string.IsNullOrEmpty(templateJson)) throw new ArgumentException($"'{nameof(templateJson)}' cannot be null or empty.", nameof(templateJson));
            
            var resource = GetGenericAzResource(name, type);

            if (resource == null)
            {
                var armDeploymentInfo = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
                {
                    Template = BinaryData.FromString(templateJson),
                    Parameters = BinaryData.FromObjectAsJson(newCreationParams)
                });

                await Container.GetArmDeployments().CreateOrUpdateAsync(WaitUntil.Completed, name + DateTime.Now.Ticks, armDeploymentInfo);

                _logger.LogInformation($"Created {name}");

                resource = GetGenericAzResource(name, type);
                await base.EnsureTagsOnExisting(resource.Data.Tags, resource.GetTagResource());
            }

            return new ArmOperationResult { ResourceId = resource.Id };
        }

        public async Task DeleteIfExistsGenericResource(string name, string type)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));
            }

            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException($"'{nameof(type)}' cannot be null or empty.", nameof(type));
            }

            var resource = GetGenericAzResource(name, type);

            if (resource != null)
            {
                await resource.DeleteAsync(WaitUntil.Completed);
                _logger.LogInformation($"Deleted {name}");
            }
        }

        private GenericResource GetGenericAzResource(string name, string type)
        {
            return Container.GetGenericResources($"resourceType eq '{type}'", "Properties")
                .Where(r => r.Data.Name == name).SingleOrDefault();
        }
    }

    public class ArmOperationResult
    {
        public string ResourceId { get; set; } = string.Empty;
    }

    public class GetOrCreateArmOperationResult : ArmOperationResult
    {
        public bool CreatedNew { get; set; } = false;
    }
}
