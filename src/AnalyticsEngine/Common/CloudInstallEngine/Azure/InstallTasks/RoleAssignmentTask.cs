using Azure;
using Azure.Core;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Assigns an RBAC role to an Azure resource within a resource group.
    /// </summary>
    public class RoleAssignmentTask : InstallTaskInAzResourceGroup<RoleAssignmentResource>
    {
        public const string CONFIG_KEY_ROLE_NAME = "roleName";
        public const string CONFIG_KEY_PRINCIPAL_TYPE = "principalType";
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_CLIENT_SECRET = "clientSecret";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";

        public RoleAssignmentTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "assign RBAC role";

        public override async Task<RoleAssignmentResource> ExecuteTaskReturnResult(object contextArg)
        {
            var roleName = _config.GetConfigValue(CONFIG_KEY_ROLE_NAME);
            var clientId = _config.GetConfigValue(CONFIG_KEY_CLIENT_ID);
            var clientSecret = _config.GetConfigValue(CONFIG_KEY_CLIENT_SECRET);
            var tenantId = _config.GetConfigValue(CONFIG_KEY_TENANT_ID);

            // Resolve the service principal object ID from client credentials
            var objectIdStr = await ServicePrincipalResolver.GetObjectIdFromClientCredentials(tenantId, clientId, clientSecret);
            _logger.LogInformation($"Resolved client ID '{clientId}' to object ID '{objectIdStr}'");

            if (!Guid.TryParse(objectIdStr, out var principalId))
            {
                throw new InstallException($"Invalid object ID '{objectIdStr}' resolved for client ID '{clientId}'");
            }

            // Find role definition by name on the resource group scope
            var scope = Container.Id;
            AuthorizationRoleDefinitionResource roleDefinition = null;
            foreach (var rd in Container.GetAuthorizationRoleDefinitions())
            {
                if (string.Equals(rd.Data.RoleName, roleName, StringComparison.OrdinalIgnoreCase))
                {
                    roleDefinition = rd;
                    break;
                }
            }
            if (roleDefinition == null)
            {
                throw new InstallException($"Role definition '{roleName}' not found on scope '{scope}'");
            }

            // Check for existing assignment
            foreach (var existing in Container.GetRoleAssignments())
            {
                if (existing.Data.PrincipalId == principalId &&
                    existing.Data.RoleDefinitionId == roleDefinition.Id)
                {
                    _logger.LogInformation($"Role '{roleName}' already assigned to principal '{principalId}'.");
                    return existing;
                }
            }

            // Create new role assignment
            _logger.LogInformation($"Assigning role '{roleName}' to principal '{principalId}'...");
            var roleAssignmentId = Guid.NewGuid().ToString();
            var content = new RoleAssignmentCreateOrUpdateContent(roleDefinition.Id, principalId);

            if (_config.ContainsKey(CONFIG_KEY_PRINCIPAL_TYPE))
            {
                content.PrincipalType = new RoleManagementPrincipalType(_config.GetConfigValue(CONFIG_KEY_PRINCIPAL_TYPE));
            }

            var result = await Container.GetRoleAssignments().CreateOrUpdateAsync(
                WaitUntil.Completed, roleAssignmentId, content);

            _logger.LogInformation($"Role '{roleName}' assigned successfully.");
            return result.Value;
        }
    }
}
