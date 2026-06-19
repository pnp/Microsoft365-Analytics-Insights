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
        private readonly bool _allowPublicAccess;

        public KeyVaultTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
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
                _logger.LogInformation($"Creating new key vault '{name}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")})...");

                var props = new KeyVaultProperties(tenantId, new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard))
                {
                    PublicNetworkAccess = _allowPublicAccess ? "Enabled" : "Disabled",
                    // Enable the vault firewall up-front so the create itself complies with Azure
                    // policies that require it (e.g. "Azure Key Vault should have firewall enabled or
                    // public network access disabled"). KeyVaultFirewallConfigTask then allow-lists the
                    // installer + App Service IPs so the vault stays reachable. See issue #136.
                    NetworkRuleSet = KeyVaultFirewallConfigTask.BuildFirewallRuleSet(null, null, null)
                };
                var newKeyVaultInfo = new KeyVaultCreateOrUpdateContent(AzureLocation, props);
                EnsureTagsOnNew(newKeyVaultInfo.Tags);

                var serverCreateResult = await Container.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, name, newKeyVaultInfo);
                r = serverCreateResult.Value;
            }
            else
            {
                _logger.LogInformation($"Found existing key vault '{r.Data.Name}'.");

                var desiredAccess = _allowPublicAccess ? "Enabled" : "Disabled";
                var publicAccessChanged = !string.Equals(r.Data.Properties.PublicNetworkAccess, desiredAccess, StringComparison.OrdinalIgnoreCase);
                var firewallEnabled = r.Data.Properties.NetworkRuleSet?.DefaultAction == KeyVaultNetworkRuleAction.Deny;
                if (publicAccessChanged || !firewallEnabled)
                {
                    _logger.LogInformation($"Updating key vault '{r.Data.Name}': public network access '{desiredAccess}', firewall default action 'Deny'...");

                    // PATCH (not a full CreateOrUpdate) so existing access policies and other vault
                    // settings are preserved while we set public access and enable the firewall. The
                    // firewall must be enabled here (not just by KeyVaultFirewallConfigTask) so this
                    // update itself satisfies Azure Key Vault firewall policies. See issue #136.
                    var patch = new KeyVaultPatch
                    {
                        Properties = new KeyVaultPatchProperties
                        {
                            PublicNetworkAccess = desiredAccess,
                            NetworkRuleSet = KeyVaultFirewallConfigTask.BuildFirewallRuleSet(r.Data.Properties.NetworkRuleSet, null, null)
                        }
                    };
                    var updateResult = await r.UpdateAsync(patch);
                    r = updateResult.Value;
                }

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

            // Extract object Id by getting a token from the credentials passed. Only log the
            // resolution on the first lookup; subsequent KV access-policy adds for the same SP would
            // otherwise print an identical "Detected client ID..." line.
            var (objectIdValue, wasCached) = await ServicePrincipalResolver.GetObjectIdFromClientCredentialsWithCacheInfo(tenantId.ToString(), clientId, secret);
            if (!wasCached)
            {
                _logger.LogInformation($"Detected client ID '{clientId}' has object ID '{objectIdValue}'");
            }

            await AddPolicyForConfiguredAccount(vaultResource, tenantId, objectIdValue, secretPerms, certPerms);
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

        /// <summary>Backoff schedule (seconds) for retrying the secret write on a 403 — absorbs typical AAD policy propagation lag (~30–60s).</summary>
        private static readonly int[] _retryDelaysSeconds = new[] { 10, 20, 30 };

        public KeyVaultSecretAddTask(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        /// <summary>
        /// Writing the runtime app-registration secret to Key Vault is best-effort: a transient
        /// network/DNS/permission failure here must not abort an otherwise-successful install. The
        /// existing vault value (if any) remains valid and the secret can be re-written on a later run.
        /// </summary>
        public override bool IsCritical => false;

        public override async Task<object> ExecuteTask(object contextArg)
        {
            base.EnsureContextArgType<KeyVaultResource>(contextArg);
            var vault = (KeyVaultResource)contextArg;

            var name = _config.GetNameConfigValue();
            try
            {
                return await AddRuntimeSecretAsync(vault, name);
            }
            catch (Exception ex) when (TransportFailureDetector.IsTransportOrDnsFailure(ex, out var leafMessage))
            {
                // DNS/network transport failure reaching the Key Vault data-plane endpoint
                // (e.g. "The remote name could not be resolved: '<vault>.vault.azure.net'"), as opposed
                // to an HTTP error response. Writing the secret is best-effort (see IsCritical), so log
                // an actionable warning and let the install continue instead of aborting everything.
                _logger.LogWarning(
                    $"Could not reach key vault '{vault.Data.Name}' over the network to update secret '{name}': {leafMessage} " +
                    $"This is usually a DNS / network / firewall issue resolving '{vault.Data.Name}.vault.azure.net' from the installer host " +
                    $"(for example when public network access is disabled and the host is not on the VNet). " +
                    $"The secret was not written; any existing value in the vault remains valid. " +
                    $"Re-run the installer once connectivity is restored if the secret needs updating.");
                return vault;
            }
        }

        private async Task<object> AddRuntimeSecretAsync(KeyVaultResource vault, string name)
        {
            var val = _config.GetConfigValue(CONFIG_KEY_SECRET_VAL);


            var credClientId = _config.GetConfigValue(CONFIG_KEY_CRED_CLIENT_ID);
            var credTenantId = _config.GetConfigValue(CONFIG_KEY_CRED_TENANT_ID);
            var credSecret = _config.GetConfigValue(CONFIG_KEY_CRED_SECRET);

            var kvUri = "https://" + vault.Data.Name + ".vault.azure.net";
            var client = new SecretClient(new Uri(kvUri), new ClientSecretCredential(credTenantId, credClientId, credSecret));

            // Try to read the existing secret first. If it already matches what we'd write, there
            // is nothing to do — skip silently. This is the common re-run case.
            //
            // Important: do NOT short-circuit on a 403 here. The access policy granting Get/Set was
            // added moments earlier in the same task batch and AAD propagation lag (~30-60s) can
            // cause this first data-plane call to 403 even on a perfectly healthy vault. The write
            // retry loop below absorbs that lag, so fall through. We only treat 403 as "intentional"
            // when we've separately confirmed the vault's PublicNetworkAccess is Disabled, which is
            // handled after the write retries are exhausted.
            try
            {
                var existing = await client.GetSecretAsync(name);
                if (existing?.Value != null && string.Equals(existing.Value.Value, val, StringComparison.Ordinal))
                {
                    _logger.LogInformation($"Key vault secret '{name}' in '{vault.Data.Name}' is already up-to-date; skipping write.");
                    return vault;
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // No existing secret — proceed to write.
            }
            catch (RequestFailedException)
            {
                // Read failed for some other reason (403 propagation lag, transient network, etc.).
                // Don't treat this as a definitive answer — fall through to the write retry loop,
                // which has the propagation-lag back-off + a final accurate diagnostic that
                // distinguishes "policy-blocked public access" from "something else is wrong".
            }

            _logger.LogInformation($"Updating secret '{name}' in key vault '{vault.Data.Name}'...");

            // Retry on 403/Forbidden: the access policy granting the InstallerAccount Set permission was just
            // added by KeyVaultAddSecretAllPermissionsForAppRegistrationTask in the same task batch, and AAD
            // typically takes 30-60s to propagate before the policy is enforceable from the data plane.
            RequestFailedException lastForbidden = null;
            for (var attempt = 0; attempt <= _retryDelaysSeconds.Length; attempt++)
            {
                try
                {
                    await client.SetSecretAsync(new KeyVaultSecret(name, val));
                    if (attempt > 0)
                    {
                        _logger.LogInformation($"Secret '{name}' written to '{vault.Data.Name}' on retry attempt {attempt + 1}.");
                    }
                    else
                    {
                        _logger.LogInformation($"Updated key vault secret '{name}'.");
                    }
                    return vault;
                }
                catch (RequestFailedException ex) when (ex.Status == 403 && ex.ErrorCode == "Forbidden")
                {
                    lastForbidden = ex;
                    if (attempt < _retryDelaysSeconds.Length)
                    {
                        var delaySeconds = _retryDelaysSeconds[attempt];
                        _logger.LogInformation($"Key vault secret write got 403/Forbidden (attempt {attempt + 1} of {_retryDelaysSeconds.Length + 1}). " +
                            $"This usually means the access policy added moments earlier has not yet propagated through AAD. Waiting {delaySeconds}s and retrying...");
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                    }
                }
            }

            // All retries exhausted. Re-read the vault's current PublicNetworkAccess from ARM so we can
            // distinguish "policy intentionally blocks public access" (soft warning — only matters on secret
            // rotation) from "something else is genuinely wrong" (error worth investigating).
            string publicAccess = null;
            try
            {
                var fresh = (await vault.GetAsync()).Value;
                publicAccess = fresh.Data.Properties?.PublicNetworkAccess;
            }
            catch (Exception probeEx)
            {
                _logger.LogWarning($"Could not re-read key vault state to diagnose 403: {probeEx.Message}");
            }

            if (string.Equals(publicAccess, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                // Policy-blocked write: not fatal. Only matters if the secret needed updating.
                _logger.LogWarning($"Key vault '{vault.Data.Name}' secret '{name}' was not updated: vault PublicNetworkAccess is 'Disabled' (likely enforced by Azure policy). " +
                    $"If the runtime app-registration secret has been rotated, run the installer from inside the private network (or temporarily allow public access) and re-run; otherwise the existing vault value is still valid and this can be ignored.");
                return vault;
            }

            // Other 403 (policy lag past retry window, network ACL deny, etc.) — surface as Error.
            _logger.LogError(
                $"Could not add secret '{name}' to key vault '{vault.Data.Name}' after {_retryDelaysSeconds.Length + 1} attempts (last error: 403 Forbidden, ErrorCode='{lastForbidden?.ErrorCode}'). " +
                $"Likely causes: access policy / RBAC propagation lag (longer than the {(_retryDelaysSeconds.Length + 1)}-attempt retry window) or a vault firewall rule rejecting the runner IP — check the vault's Networking blade. " +
                $"App-registration secrets in the vault may now be out of date — re-run the installer once the underlying cause is resolved.");
            return vault;
        }
    }
}
