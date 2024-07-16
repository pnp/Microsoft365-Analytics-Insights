using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.Security.KeyVault.Secrets;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Get/create keyvault
    /// </summary>
    public class KeyVaultTask : InstallTaskInAzResourceGroup<KeyVaultResource>
    {
        public const string CONFIG_KEY_TENANT_ID = "tenantId";
        public KeyVaultTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create key vault";

        public override async Task<KeyVaultResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();
            var tenantIdStr = base._config.GetConfigValue(CONFIG_KEY_TENANT_ID);
            var tenantId = Guid.Empty;
            if (!Guid.TryParse(tenantIdStr, out tenantId))
                throw new InstallException($"Invalid tenant ID '{tenantIdStr}' given to {nameof(KeyVaultTask)}");

            KeyVaultResource r = null;
            foreach (var server in Container.GetKeyVaults())
            {
                if (server.Data.Name == name)
                {
                    r = server;
                    break;
                }
            }
            if (r == null)
            {
                _logger.LogInformation($"Creating new key vault '{name}'...");

                var newKeyVaultInfo = new KeyVaultCreateOrUpdateContent(AzureLocation, new KeyVaultProperties(tenantId, new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard)));
                EnsureTagsOnNew(newKeyVaultInfo.Tags);

                var serverCreateResult = await Container.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, name, newKeyVaultInfo);
                r = serverCreateResult.Value;
            }
            else
            {
                _logger.LogInformation($"Found existing key vault '{r.Data.Name}'.");
                await EnsureTagsOnExisting(r.Data.Tags, r.GetTagResource());
            }
            return r;
        }
    }

    public abstract class BaseKeyVaultAddPolicyTask : InstallTaskInAzResourceGroup<KeyVaultResource>
    {
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";
        public const string CONFIG_KEY_SECRET = "secret";
        public const string CONFIG_KEY_WEB_APP_NAME = "webAppName";

        protected BaseKeyVaultAddPolicyTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        protected async Task AddPolicyForConfiguredAccount(KeyVaultResource vaultResource, Guid tenantId, string objectId, IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            var access = new IdentityAccessPermissions();
            foreach (var perm in secretPerms)
            {
                access.Secrets.Add(new IdentityAccessSecretPermission(perm));
            }
            foreach (var perm in certPerms)
            {
                access.Certificates.Add(new IdentityAccessCertificatePermission(perm));
            }
            var pol = new KeyVaultAccessPolicyParameters(new KeyVaultAccessPolicyProperties(new List<KeyVaultAccessPolicy>()
            {
                new KeyVaultAccessPolicy(tenantId, objectId, access)
            }));
            await vaultResource.UpdateAccessPolicyAsync(AccessPolicyUpdateKind.Add, pol);
        }
        protected async Task AddPolicyForConfiguredRuntimeAccount(KeyVaultResource vaultResource, IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            // https://azidentity.azurewebsites.net/post/2019/05/17/getting-it-right-key-vault-access-policies
            var clientId = _config.GetConfigValue(CONFIG_KEY_CLIENT_ID);
            var secret = _config.GetConfigValue(CONFIG_KEY_SECRET);
            var tenantId = TenantGuidFromConfig();

            // Only support adding accounts from same tenant as KV
            if (tenantId != vaultResource.Data.Properties.TenantId)
            {
                _logger.LogError($"Key Vault permissions configuration error: Entra ID application ID {clientId} does not exist in the Key Vault tenant ID {vaultResource.Data.Properties.TenantId}");
                _logger.LogError("Continuing anyway, but your Key Vault is NOT configured due to an unsupported setup - both Office 365 and Azure should be in the same Entra ID tenant");
                return;
            }

            _logger.LogInformation($"Adding Azure AD application with client ID '{clientId}' to key vault {vaultResource.Data.Name} for secret read & list; certificate read");

            // Extract object Id by getting a token from the credentials passed
            var creds = new ClientSecretCredential(tenantId.ToString(), clientId, secret);
            var credTokenResponse = await creds.GetTokenAsync(new TokenRequestContext(new string[] { "https://management.core.windows.net/.default" }, null));
            var handler = new JwtSecurityTokenHandler();
            var jwtSecurityToken = handler.ReadJwtToken(credTokenResponse.Token);
            var objectId = jwtSecurityToken.Claims.Where(c => c.Type == "oid").FirstOrDefault();
            if (objectId == null)
            {
                throw new InstallException($"No object ID found for client credentials");
            }
            _logger.LogInformation($"Detected client ID '{clientId}' has object ID '{objectId.Value}'");

            await AddPolicyForConfiguredAccount(vaultResource, tenantId, objectId.Value, secretPerms, certPerms);
        }

        protected Guid TenantGuidFromConfig()
        {
            var tenantIdStr = _config.GetConfigValue(CONFIG_KEY_TENANT_ID);
            var tenantId = Guid.Empty;
            Guid.TryParse(tenantIdStr, out tenantId);
            if (tenantId == Guid.Empty)
            {
                throw new InstallException($"No valid tenant ID found");
            }
            return tenantId;
        }
    }

    public class KeyVaultAddWebAppPermissionsTask : BaseKeyVaultAddPolicyTask
    {
        public KeyVaultAddWebAppPermissionsTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }
        async Task AddPolicyForConfiguredAppServiceManagedIdentity(KeyVaultResource vaultResource, IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            var webAppName = _config.GetConfigValue(CONFIG_KEY_WEB_APP_NAME);
            var tenantId = TenantGuidFromConfig();

            var webAppWithManagedIdentity = Container.GetWebSites().Where(s => s.Data.Name == webAppName).SingleOrDefault();
            if (webAppWithManagedIdentity == null)
                throw new InstallException($"Can't find web-app with name '{webAppName}'");

            await AddPolicyForConfiguredAccount(vaultResource, tenantId, webAppWithManagedIdentity.Data.Identity.PrincipalId.ToString(), secretPerms, certPerms);
            _logger.LogInformation($"Added web-app '{webAppName}' to key vault {vaultResource.Data.Name} for secret read & list; certificate read");
        }

        public override async Task<KeyVaultResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<KeyVaultResource>(contextArg);
            var vault = (KeyVaultResource)contextArg;

            await AddPolicyForConfiguredAppServiceManagedIdentity(vault, new string[] { "Get" }, new string[] { "Get" });

            return vault;
        }
    }

    public class KeyVaultAddSecretReadPolicyForAppRegistrationTask : BaseKeyVaultAddPolicyTask
    {
        public KeyVaultAddSecretReadPolicyForAppRegistrationTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public async override Task<KeyVaultResource> ExecuteTaskReturnResult(object contextArg)
        {

            base.EnsureContextArgType<KeyVaultResource>(contextArg);
            var vault = (KeyVaultResource)contextArg;

            await AddPolicyForConfiguredRuntimeAccount(vault, new string[] { "Get", "List" }, new string[] { "Get" });

            return vault;
        }
    }

    public class KeyVaultAddSecretAllPermissionsForAppRegistrationTask : BaseKeyVaultAddPolicyTask
    {
        public KeyVaultAddSecretAllPermissionsForAppRegistrationTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public async override Task<KeyVaultResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<KeyVaultResource>(contextArg);
            var vault = (KeyVaultResource)contextArg;
            await AddPolicyForConfiguredRuntimeAccount(vault, new string[] { "Get", "List", "Set", "Delete", "Recover", "Backup", "Restore" }, new string[] { "Get" });

            return vault;
        }
    }

    public class KeyVaultSecretAddTask : BaseInstallTask
    {
        public const string CONFIG_KEY_SECRET_VAL = "secretval";
        public const string CONFIG_KEY_CRED_TENANT_ID = "tenantId";
        public const string CONFIG_KEY_CRED_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_CRED_SECRET = "secret";

        public KeyVaultSecretAddTask(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            base.EnsureContextArgType<KeyVaultResource>(contextArg);
            var vault = (KeyVaultResource)contextArg;

            var name = _config.GetNameConfigValue();
            var val = _config.GetConfigValue(CONFIG_KEY_SECRET_VAL);


            var credClientId = _config.GetConfigValue(CONFIG_KEY_CRED_CLIENT_ID);
            var credTenantId = _config.GetConfigValue(CONFIG_KEY_CRED_TENANT_ID);
            var credSecret = _config.GetConfigValue(CONFIG_KEY_CRED_SECRET);

            _logger.LogInformation($"Adding secret name '{name}' to key vault {vault.Data.Name}.");

            var kvUri = "https://" + vault.Data.Name + ".vault.azure.net";
            var client = new SecretClient(new Uri(kvUri), new ClientSecretCredential(credTenantId, credClientId, credSecret));
            await client.SetSecretAsync(new KeyVaultSecret(name, val));
            return vault;
        }
    }
}
