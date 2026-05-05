using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Redis;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Creates a Redis Access Policy Assignment to grant data-plane access to a service principal.
    /// This is required for AAD/RBAC-based Redis authentication (when key-based auth is disabled).
    /// Uses ARM REST API because the SDK version (1.1.0) does not expose access policy types.
    /// </summary>
    public class RedisAccessPolicyAssignmentTask : InstallTaskInAzResourceGroup<RedisResource>
    {
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_CLIENT_SECRET = "clientSecret";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";
        public const string CONFIG_KEY_INSTALLER_CLIENT_ID = "installerClientId";
        public const string CONFIG_KEY_INSTALLER_CLIENT_SECRET = "installerClientSecret";
        public const string CONFIG_KEY_INSTALLER_TENANT_ID = "installerTenantId";
        public const string CONFIG_KEY_ACCESS_POLICY_NAME = "accessPolicyName";

        private static readonly HttpClient _httpClient = new HttpClient();

        public RedisAccessPolicyAssignmentTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "assign Redis access policy";

        public override async Task<RedisResource> ExecuteTaskReturnResult(object contextArg)
        {
            var redisResource = (RedisResource)contextArg;
            if (redisResource == null)
            {
                throw new InstallException("RedisAccessPolicyAssignmentTask requires a RedisResource as context");
            }

            var tenantId = _config.GetConfigValue(CONFIG_KEY_TENANT_ID);
            var clientId = _config.GetConfigValue(CONFIG_KEY_CLIENT_ID);
            var clientSecret = _config.GetConfigValue(CONFIG_KEY_CLIENT_SECRET);
            var accessPolicyName = _config.ContainsKey(CONFIG_KEY_ACCESS_POLICY_NAME)
                ? _config.GetConfigValue(CONFIG_KEY_ACCESS_POLICY_NAME)
                : "Data Owner";

            // Use installer credentials for ARM API calls (runtime account may not have control-plane access)
            var installerTenantId = _config.ContainsKey(CONFIG_KEY_INSTALLER_TENANT_ID)
                ? _config.GetConfigValue(CONFIG_KEY_INSTALLER_TENANT_ID) : tenantId;
            var installerClientId = _config.ContainsKey(CONFIG_KEY_INSTALLER_CLIENT_ID)
                ? _config.GetConfigValue(CONFIG_KEY_INSTALLER_CLIENT_ID) : clientId;
            var installerClientSecret = _config.ContainsKey(CONFIG_KEY_INSTALLER_CLIENT_SECRET)
                ? _config.GetConfigValue(CONFIG_KEY_INSTALLER_CLIENT_SECRET) : clientSecret;

            // Get the object ID of the runtime service principal
            var objectId = await ServicePrincipalResolver.GetObjectIdFromClientCredentials(tenantId, clientId, clientSecret);
            _logger.LogInformation($"Resolved service principal object ID: {objectId}");

            // Get a token for ARM API calls using the installer account
            var credential = new ClientSecretCredential(installerTenantId, installerClientId, installerClientSecret);
            var tokenResponse = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }), default);

            var redisResourceId = redisResource.Id.ToString();
            var assignmentName = $"sp-{objectId.Substring(0, 8)}"; // Short unique name based on principal

            var apiVersion = "2023-08-01";

            // Step 1: Ensure Microsoft Entra (AAD) authentication is enabled on the Redis instance
            await EnsureAadEnabled(redisResourceId, apiVersion, tokenResponse.Token);

            // Step 2: Check if access policy assignment already exists
            var listUrl = $"https://management.azure.com{redisResourceId}/accessPolicyAssignments?api-version={apiVersion}";

            var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResponse.Token);
            var listResponse = await _httpClient.SendAsync(request);

            if (listResponse.IsSuccessStatusCode)
            {
                var listContent = await listResponse.Content.ReadAsStringAsync();
                var listJson = JObject.Parse(listContent);
                var assignments = listJson["value"] as JArray;
                if (assignments != null)
                {
                    foreach (var assignment in assignments)
                    {
                        var props = assignment["properties"];
                        if (props != null &&
                            string.Equals(props["objectId"]?.ToString(), objectId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(props["accessPolicyName"]?.ToString(), accessPolicyName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation($"Redis access policy assignment already exists for principal {objectId} with policy '{accessPolicyName}'.");
                            return redisResource;
                        }
                    }
                }
            }

            // Create the access policy assignment
            var createUrl = $"https://management.azure.com{redisResourceId}/accessPolicyAssignments/{assignmentName}?api-version={apiVersion}";
            var body = new
            {
                properties = new
                {
                    accessPolicyName = accessPolicyName,
                    objectId = objectId,
                    objectIdAlias = clientId
                }
            };

            var createRequest = new HttpRequestMessage(HttpMethod.Put, createUrl);
            createRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResponse.Token);
            createRequest.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            _logger.LogInformation($"Creating Redis access policy assignment '{assignmentName}' with policy '{accessPolicyName}' for principal {objectId}...");
            var createResponse = await _httpClient.SendAsync(createRequest);
            var responseContent = await createResponse.Content.ReadAsStringAsync();

            if (!createResponse.IsSuccessStatusCode)
            {
                // If it's a conflict (already exists), that's fine
                if (createResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    _logger.LogInformation($"Redis access policy assignment already exists (conflict).");
                    return redisResource;
                }
                _logger.LogWarning($"Failed to create Redis access policy assignment. Status: {createResponse.StatusCode}. Response: {responseContent}");
                throw new InstallException($"Failed to create Redis access policy assignment: {createResponse.StatusCode} - {responseContent}");
            }

            _logger.LogInformation($"Successfully created Redis access policy assignment '{assignmentName}' for data-plane access.");
            return redisResource;
        }

        private async Task EnsureAadEnabled(string redisResourceId, string apiVersion, string bearerToken)
        {
            // GET current Redis configuration to check if aad-enabled is already set
            var getUrl = $"https://management.azure.com{redisResourceId}?api-version={apiVersion}";
            var getRequest = new HttpRequestMessage(HttpMethod.Get, getUrl);
            getRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

            var getResponse = await _httpClient.SendAsync(getRequest);
            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to read Redis resource for AAD check. Status: {getResponse.StatusCode}");
                return;
            }

            var getContent = await getResponse.Content.ReadAsStringAsync();
            var redisJson = JObject.Parse(getContent);
            var redisConfig = redisJson["properties"]?["redisConfiguration"];
            var aadEnabledValue = redisConfig?["aad-enabled"]?.ToString();

            if (string.Equals(aadEnabledValue, "true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Microsoft Entra (AAD) authentication is already enabled on Redis.");
                return;
            }

            // PATCH to enable aad-enabled via the update API
            _logger.LogInformation("Enabling Microsoft Entra (AAD) authentication on Redis cache...");
            var patchUrl = $"https://management.azure.com{redisResourceId}?api-version={apiVersion}";
            var patchBody = new
            {
                properties = new
                {
                    redisConfiguration = new
                    {
                        aad_enabled = "true" // JSON key uses hyphen but we serialize; use raw JSON below
                    }
                }
            };

            // Build raw JSON to use hyphenated key name
            var patchJson = $"{{\"properties\":{{\"redisConfiguration\":{{\"aad-enabled\":\"true\"}}}}}}";
            var patchRequest = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl);
            patchRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            patchRequest.Content = new StringContent(patchJson, Encoding.UTF8, "application/json");

            var patchResponse = await _httpClient.SendAsync(patchRequest);
            var patchContent = await patchResponse.Content.ReadAsStringAsync();

            if (!patchResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to enable AAD authentication on Redis. Status: {patchResponse.StatusCode}. Response: {patchContent}");
                throw new InstallException($"Failed to enable AAD authentication on Redis: {patchResponse.StatusCode} - {patchContent}");
            }

            _logger.LogInformation("Microsoft Entra (AAD) authentication enabled on Redis. Note: Redis nodes may reboot (up to 30 minutes).");
        }
    }
}
