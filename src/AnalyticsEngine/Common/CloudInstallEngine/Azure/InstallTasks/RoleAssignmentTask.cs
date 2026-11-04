using Azure;
using Azure.Core;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Assigns an RBAC role to an Azure resource within a resource group.
    /// </summary>
    /// <remarks>
    /// Without <see cref="CONFIG_KEY_SCOPE_SUFFIX"/> the role is assigned on the resource group, as every role was before.
    /// With it, the role is assigned on that one resource, under a deterministic assignment name and an installer
    /// description, and an assignment of the same role to the same principal on the whole resource group (which older
    /// installers granted) is reported with guidance. It is never removed: nothing records that the installer, rather
    /// than an administrator, created it.
    /// </remarks>
    public class RoleAssignmentTask : InstallTaskInAzResourceGroup<RoleAssignmentResource>
    {
        public const string CONFIG_KEY_ROLE_NAME = "roleName";
        public const string CONFIG_KEY_PRINCIPAL_TYPE = "principalType";
        public const string CONFIG_KEY_PRINCIPAL_ID = "principalId";
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_CLIENT_SECRET = "clientSecret";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";

        /// <summary>
        /// Optional: the resource to assign the role on, relative to the resource group, for example
        /// <c>providers/Microsoft.Storage/storageAccounts/contoso</c>.
        /// </summary>
        public const string CONFIG_KEY_SCOPE_SUFFIX = "scopeSuffix";

        /// <summary>Description stamped on every resource-scoped assignment the installer creates.</summary>
        public const string INSTALLER_ASSIGNMENT_DESCRIPTION = "Granted to the runtime account by the Microsoft 365 Analytics & Insights installer.";

        public RoleAssignmentTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "assign RBAC role";

        /// <summary>
        /// A resource-scoped assignment that fails (for example because the resource is missing) is logged and the next
        /// role is still assigned. A resource-group assignment keeps the historic behaviour.
        /// </summary>
        public override bool IsCritical => !_config.ContainsKey(CONFIG_KEY_SCOPE_SUFFIX);

        public override async Task<RoleAssignmentResource> ExecuteTaskReturnResult(object contextArg)
        {
            var roleName = _config.GetConfigValue(CONFIG_KEY_ROLE_NAME);

            Guid principalId;
            // If a pre-resolved principal ID is provided, use it directly (e.g. for managed identities)
            if (_config.ContainsKey(CONFIG_KEY_PRINCIPAL_ID))
            {
                var principalIdStr = _config.GetConfigValue(CONFIG_KEY_PRINCIPAL_ID);
                if (!Guid.TryParse(principalIdStr, out principalId))
                {
                    throw new InstallException($"Invalid principal ID '{principalIdStr}'");
                }
                _logger.LogInformation($"Using pre-resolved principal ID '{principalId}'");
            }
            else
            {
                var clientId = _config.GetConfigValue(CONFIG_KEY_CLIENT_ID);
                var clientSecret = _config.GetConfigValue(CONFIG_KEY_CLIENT_SECRET);
                var tenantId = _config.GetConfigValue(CONFIG_KEY_TENANT_ID);

                // Resolve the service principal object ID from client credentials. Only log the
                // resolution on the first lookup; subsequent role assignments for the same SP would
                // otherwise print an identical "Resolved client ID..." line for every role.
                var (objectIdStr, wasCached) = await ServicePrincipalResolver.GetObjectIdFromClientCredentialsWithCacheInfo(tenantId, clientId, clientSecret, cancellationToken: CancellationToken);
                if (!wasCached)
                {
                    _logger.LogInformation($"Resolved client ID '{clientId}' to object ID '{objectIdStr}'");
                }

                if (!Guid.TryParse(objectIdStr, out principalId))
                {
                    throw new InstallException($"Invalid object ID '{objectIdStr}' resolved for client ID '{clientId}'");
                }
            }

            // Find role definition by name on the resource group scope
            var scope = Container.Id;
            AuthorizationRoleDefinitionResource roleDefinition = null;
            foreach (var rd in Container.GetAuthorizationRoleDefinitions().GetAll(cancellationToken: CancellationToken))
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

            if (_config.ContainsKey(CONFIG_KEY_SCOPE_SUFFIX))
            {
                return await AssignOnResourceAsync(roleName, roleDefinition, principalId);
            }

            // Check for existing assignment
            foreach (var existing in Container.GetRoleAssignments().GetAll(cancellationToken: CancellationToken))
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
                WaitUntil.Completed, roleAssignmentId, content, cancellationToken: CancellationToken);

            _logger.LogInformation($"Role '{roleName}' assigned successfully.");
            return result.Value;
        }

        /// <summary>
        /// Assigns the role on the one resource named by <see cref="CONFIG_KEY_SCOPE_SUFFIX"/>, unless the principal
        /// already holds it exactly there. An assignment inherited from the resource group does not count, so the
        /// narrower one exists before anyone removes the broad one.
        /// </summary>
        private async Task<RoleAssignmentResource> AssignOnResourceAsync(string roleName, AuthorizationRoleDefinitionResource roleDefinition, Guid principalId)
        {
            var resourceScope = new ResourceIdentifier(Container.Id + "/" + _config.GetConfigValue(CONFIG_KEY_SCOPE_SUFFIX).Trim('/'));
            var assignments = Container.GetCachedClient(client => client).GetRoleAssignments(resourceScope);

            RoleAssignmentResource atResource = null;
            RoleAssignmentResource onResourceGroup = null;
            foreach (var existing in assignments.GetAll(cancellationToken: CancellationToken))
            {
                if (existing.Data.PrincipalId != principalId || !SameId(existing.Data.RoleDefinitionId?.ToString(), roleDefinition.Id.ToString()))
                    continue;
                if (SameId(existing.Data.Scope, resourceScope.ToString())) atResource = existing;
                else if (SameId(existing.Data.Scope, Container.Id.ToString())) onResourceGroup = existing;
            }

            if (atResource != null)
            {
                _logger.LogInformation($"Role '{roleName}' already assigned to principal '{principalId}' on '{resourceScope.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Assigning role '{roleName}' to principal '{principalId}' on '{resourceScope.Name}' only...");
                var content = new RoleAssignmentCreateOrUpdateContent(roleDefinition.Id, principalId)
                {
                    Description = INSTALLER_ASSIGNMENT_DESCRIPTION
                };
                if (_config.ContainsKey(CONFIG_KEY_PRINCIPAL_TYPE))
                {
                    content.PrincipalType = new RoleManagementPrincipalType(_config.GetConfigValue(CONFIG_KEY_PRINCIPAL_TYPE));
                }
                var name = DeterministicAssignmentName(resourceScope.ToString(), roleDefinition.Id.ToString(), principalId).ToString();
                atResource = (await assignments.CreateOrUpdateAsync(WaitUntil.Completed, name, content, cancellationToken: CancellationToken)).Value;
                _logger.LogInformation($"Role '{roleName}' assigned successfully on '{resourceScope.Name}'.");
            }

            if (onResourceGroup != null)
            {
                _logger.LogWarning(BuildBroadAssignmentGuidance(roleName, Container.Id.Name, resourceScope.Name, onResourceGroup.Id.ToString()));
            }
            return atResource;
        }

        /// <summary>
        /// What an operator reads when the runtime account still holds a role on the whole resource group that it now
        /// holds on the one resource that needs it.
        /// </summary>
        public static string BuildBroadAssignmentGuidance(string roleName, string resourceGroupName, string resourceName, string assignmentId)
        {
            return $"The runtime account also holds '{roleName}' on the whole resource group '{resourceGroupName}' (role assignment '{assignmentId}'), " +
                $"which earlier installers granted. It now has the same role on '{resourceName}' alone, which is all the solution needs, so the " +
                "resource-group assignment gives it access to every other resource of that type in the group. The installer does not remove it, " +
                "because nothing records whether the installer or an administrator created it. Role changes can take several minutes to apply: " +
                $"once the solution has been running normally on the narrower assignment, remove it with 'az role assignment delete --ids {assignmentId}' " +
                "or in the resource group's Access control (IAM) blade.";
        }

        /// <summary>
        /// The same assignment name on every run for one scope, role and principal, so a re-run never adds a duplicate
        /// and an assignment the installer created can be recognised later.
        /// </summary>
        public static Guid DeterministicAssignmentName(string scope, string roleDefinitionId, Guid principalId)
        {
            var key = $"{scope}|{roleDefinitionId}|{principalId}".ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                var guid = new byte[16];
                Array.Copy(bytes, guid, 16);
                guid[7] = (byte)((guid[7] & 0x0F) | 0x50);
                guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
                return new Guid(guid);
            }
        }

        private static bool SameId(string a, string b) =>
            string.Equals(a?.TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
