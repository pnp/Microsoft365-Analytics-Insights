using Azure;
using Azure.Core;
using Azure.ResourceManager.RedisEnterprise;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Grants the runtime service principal data-plane access on the Azure Managed Redis
    /// database when the database is configured for RBAC / Entra ID authentication
    /// (<see cref="RedisInstallResult.UseRbacAuth"/> == true).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Azure Managed Redis (Redis Enterprise) RBAC works via per-database
    /// <c>accessPolicyAssignments</c>. Today only the built-in <c>"default"</c> access policy
    /// is supported (full data-plane access — read, write, eviction, etc., equivalent to what
    /// an access-key-holder used to have).
    /// </para>
    /// <para>
    /// We upsert a single assignment named <c>runtime</c> per database, targeting the runtime
    /// service principal's Entra ID object ID. Re-runs are idempotent
    /// (<c>CreateOrUpdateAsync</c>); if the runtime SP is rotated, the assignment is updated to
    /// the new OID and the old principal loses data-plane access.
    /// </para>
    /// <para>
    /// For a pre-existing classic Azure Cache for Redis (legacy reuse path) or a Managed Redis
    /// database whose access keys are still enabled (existing pre-RBAC install we don't want to
    /// break), this task no-ops and logs why.
    /// </para>
    /// </remarks>
    public class RedisAccessPolicyAssignmentTask : InstallTaskInAzResourceGroup<RedisInstallResult>
    {
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_CLIENT_SECRET = "clientSecret";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";
        public const string CONFIG_KEY_INSTALLER_CLIENT_ID = "installerClientId";
        public const string CONFIG_KEY_INSTALLER_CLIENT_SECRET = "installerClientSecret";
        public const string CONFIG_KEY_INSTALLER_TENANT_ID = "installerTenantId";
        public const string CONFIG_KEY_ACCESS_POLICY_NAME = "accessPolicyName";

        /// <summary>Built-in data-plane access policy name. Only "default" is supported by Managed Redis today.</summary>
        private const string ACCESS_POLICY_NAME = "default";

        /// <summary>Fixed assignment name so re-runs are idempotent (and re-targetable to a rotated runtime SP).</summary>
        private const string ASSIGNMENT_NAME = "runtime";

        public RedisAccessPolicyAssignmentTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "assign Redis access policy";

        public override async Task<RedisInstallResult> ExecuteTaskReturnResult(object contextArg)
        {
            var redis = contextArg as RedisInstallResult;
            if (redis == null)
            {
                throw new InstallException("RedisAccessPolicyAssignmentTask requires a RedisInstallResult as context");
            }

            if (redis.IsLegacyClassicCache)
            {
                _logger.LogInformation($"Skipping Redis access policy assignment: reusing legacy classic Azure Cache for Redis '{redis.ResourceName}' which already has its own access configuration from the previous install.");
                return redis;
            }

            if (!redis.UseRbacAuth)
            {
                _logger.LogInformation(
                    $"Skipping Redis access policy assignment: existing Azure Managed Redis database '{redis.ResourceName}' has access keys enabled and the runtime is configured to use key auth. " +
                    "To switch this cache to RBAC/Entra ID auth, delete the resource in the portal and re-run the installer — the new database will default to RBAC-only.");
                return redis;
            }

            // RBAC path: look up the runtime SP's OID and upsert the access policy assignment
            // on the cluster's "default" database. ResourceId is the cluster resource ID
            // (private endpoint tasks rely on that); the database resource is obtained from it.
            var runtimeTenantId = _config[CONFIG_KEY_TENANT_ID];
            var runtimeClientId = _config[CONFIG_KEY_CLIENT_ID];
            var runtimeClientSecret = _config[CONFIG_KEY_CLIENT_SECRET];

            string runtimeOid;
            try
            {
                runtimeOid = await ServicePrincipalResolver.GetObjectIdFromClientCredentials(runtimeTenantId, runtimeClientId, runtimeClientSecret);
            }
            catch (Exception ex)
            {
                throw new InstallException(
                    $"Couldn't resolve the runtime service principal's Entra ID object ID (clientId '{runtimeClientId}') needed for Redis RBAC assignment: " +
                    ExceptionMessages.Format(ex));
            }

            var clusterResp = await base.Container.GetRedisEnterpriseClusterAsync(redis.ResourceName);
            var cluster = clusterResp.Value;
            var databaseResp = await cluster.GetRedisEnterpriseDatabaseAsync("default");
            var database = databaseResp.Value;
            var assignments = database.GetAccessPolicyAssignments();

            // If an assignment already exists, log whether the OID matches so re-targets are visible.
            try
            {
                var existing = await assignments.GetAsync(ASSIGNMENT_NAME);
                var existingOid = existing.Value.Data.UserObjectId?.ToString();
                if (!string.Equals(existingOid, runtimeOid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        $"Updating Redis access policy assignment '{ASSIGNMENT_NAME}' on '{redis.ResourceName}/default' — previously assigned to object ID '{existingOid}', re-assigning to runtime object ID '{runtimeOid}'.");
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // No existing assignment — first run, no-op.
            }

            var assignmentData = new AccessPolicyAssignmentData
            {
                AccessPolicyName = ACCESS_POLICY_NAME,
                UserObjectId = Guid.Parse(runtimeOid),
            };

            try
            {
                await assignments.CreateOrUpdateAsync(WaitUntil.Completed, ASSIGNMENT_NAME, assignmentData);
            }
            catch (RequestFailedException ex) when (ex.Status == 403 || ex.Status == 401)
            {
                throw new InstallException(
                    $"Installer is not authorised to create the Redis access policy assignment on '{redis.ResourceName}/default'. " +
                    "The installer service principal needs an Azure RBAC role on the Managed Redis cluster that includes the action " +
                    "'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments/write' — e.g. 'Redis Cache Contributor' or a custom role. " +
                    $"Details: {ExceptionMessages.Format(ex)}");
            }

            _logger.LogInformation(
                $"Assigned Redis '{ACCESS_POLICY_NAME}' access policy to runtime service principal (object ID '{runtimeOid}') on '{redis.ResourceName}/default'. " +
                "Note: data-plane access policy assignments can take up to a minute to propagate after creation.");

            return redis;
        }
    }
}

