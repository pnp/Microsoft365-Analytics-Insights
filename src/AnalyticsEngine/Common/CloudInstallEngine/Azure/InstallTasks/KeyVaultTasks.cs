using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.Security.KeyVault.Secrets;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
                    try
                    {
                        var updateResult = await r.UpdateAsync(patch);
                        r = updateResult.Value;
                    }
                    catch (RequestFailedException ex) when (IsDisallowedByPolicy(ex))
                    {
                        // Some tenants attach a "Not allowed resource types" Azure Policy to the
                        // management group that denies any write to Microsoft.KeyVault/vaults. The
                        // vault already exists and is usable, so this update is best-effort hardening
                        // (public network access + firewall) — reuse the vault as-is rather than abort
                        // an otherwise-successful install. Network hardening should stay best-effort here.
                        _logger.LogWarning($"Key vault '{r.Data.Name}' already exists but updating its network settings (public access '{desiredAccess}', firewall) was disallowed by Azure Policy. Reusing the existing vault as-is and skipping the network hardening. To apply these settings, grant a policy exemption for the vault and re-run the installer.");
                    }
                }

                try
                {
                    await EnsureTagsOnExisting(r.Data.Tags, r.GetTagResource());
                }
                catch (RequestFailedException ex) when (IsDisallowedByPolicy(ex))
                {
                    // Tagging is also a write to the vault and can be blocked by the same policy; tags
                    // are cosmetic, so skip them rather than fail the install.
                    _logger.LogWarning($"Could not tag existing key vault '{r.Data.Name}': the write was disallowed by Azure Policy. Continuing without updating tags.");
                }
            }
            return r;
        }

        /// <summary>
        /// True when an Azure Resource Manager write was rejected by an Azure Policy "deny" effect
        /// (e.g. a "Not allowed resource types" initiative that blocks Microsoft.KeyVault/vaults).
        /// Surfaces as HTTP 403 with error code "RequestDisallowedByPolicy".
        /// </summary>
        public static bool IsDisallowedByPolicy(RequestFailedException ex)
        {
            return ex.Status == 403 && string.Equals(ex.ErrorCode, "RequestDisallowedByPolicy", StringComparison.OrdinalIgnoreCase);
        }
    }

    public abstract class BaseKeyVaultAddPolicyTask : InstallTaskInAzResourceGroup<KeyVaultResource>
    {
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";
        public const string CONFIG_KEY_SECRET = "secret";
        public const string CONFIG_KEY_WEB_APP_NAME = "webAppName";

        // Built-in Key Vault data-plane roles, used when the vault has the RBAC permission model enabled.
        public const string ROLE_SECRETS_OFFICER = "Key Vault Secrets Officer";
        public const string ROLE_SECRETS_USER = "Key Vault Secrets User";
        public const string ROLE_CERTIFICATE_USER = "Key Vault Certificate User";

        /// <summary>
        /// Access-policy secret permissions that imply write access, and therefore need
        /// "Key Vault Secrets Officer" rather than the read-only "Key Vault Secrets User".
        /// </summary>
        private static readonly string[] SecretWritePermissions = { "set", "delete", "recover", "backup", "restore", "purge" };

        protected BaseKeyVaultAddPolicyTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        protected async Task AddPolicyForConfiguredAccount(KeyVaultResource vaultResource, Guid tenantId, string objectId, IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            // A vault using the RBAC permission model ignores access policies completely. Azure still
            // ACCEPTS the UpdateAccessPolicy call below and stores the entry, so this used to look like it
            // worked while granting nothing at all - the first data-plane call then failed with
            // 403 "ForbiddenByRbac" / "Assignment: (not found)". Note that even subscription Owner does not
            // help: Key Vault secret get/set are dataActions, which Owner does not include. Assign the
            // equivalent built-in role at the vault scope instead.
            if (vaultResource.Data.Properties?.EnableRbacAuthorization == true)
            {
                await AssignVaultRbacRoles(vaultResource, objectId, secretPerms, certPerms);
                return;
            }

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

        /// <summary>
        /// RBAC-permission-model equivalent of an access policy: translates the requested secret /
        /// certificate permissions into the matching built-in roles and assigns them at the vault scope.
        /// </summary>
        private async Task AssignVaultRbacRoles(KeyVaultResource vaultResource, string objectId, IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            if (!Guid.TryParse(objectId, out var principalId))
            {
                throw new InstallException($"Invalid object ID '{objectId}' for key vault '{vaultResource.Data.Name}' role assignment");
            }

            foreach (var roleName in MapPermissionsToRoles(secretPerms, certPerms))
            {
                await AssignVaultRole(vaultResource, principalId, roleName);
            }
        }

        /// <summary>
        /// Translates access-policy style permissions into the equivalent built-in Key Vault RBAC roles.
        /// </summary>
        /// <remarks>
        /// "Key Vault Certificate User" is chosen for certificate access rather than a certificates-only role
        /// because it also carries <c>secrets/getSecret</c>, which is what actually returns the certificate's
        /// private key via <c>CertificateClient.DownloadCertificate</c> — a bare certificates/read is not
        /// enough for <c>AuthHelper.RetrieveKeyVaultCertificate</c>.
        /// </remarks>
        public static IReadOnlyList<string> MapPermissionsToRoles(IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            var roleNames = new List<string>();

            var secrets = (secretPerms ?? Enumerable.Empty<string>()).ToList();
            if (secrets.Count > 0)
            {
                var needsWrite = secrets.Any(p => SecretWritePermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
                roleNames.Add(needsWrite ? ROLE_SECRETS_OFFICER : ROLE_SECRETS_USER);
            }

            if ((certPerms ?? Enumerable.Empty<string>()).Any())
            {
                roleNames.Add(ROLE_CERTIFICATE_USER);
            }

            return roleNames;
        }

        private async Task AssignVaultRole(KeyVaultResource vaultResource, Guid principalId, string roleName)
        {
            // Ask ARM for just this role rather than enumerating the several-hundred built-in definitions
            // once per role per task. Fall back to a full scan if the server-side filter returns nothing.
            var roleDefinitions = vaultResource.GetAuthorizationRoleDefinitions();
            var roleDefinition = roleDefinitions
                .GetAll($"roleName eq '{roleName}'")
                .FirstOrDefault(rd => string.Equals(rd.Data.RoleName, roleName, StringComparison.OrdinalIgnoreCase))
                ?? roleDefinitions.FirstOrDefault(rd => string.Equals(rd.Data.RoleName, roleName, StringComparison.OrdinalIgnoreCase));

            if (roleDefinition == null)
            {
                throw new InstallException($"Role definition '{roleName}' not found on scope '{vaultResource.Id}'");
            }

            var roleAssignments = vaultResource.GetRoleAssignments();
            if (HasVaultScopedAssignment(roleAssignments, vaultResource, principalId, roleDefinition))
            {
                _logger.LogInformation($"Key vault '{vaultResource.Data.Name}' uses the RBAC permission model; role '{roleName}' already assigned to principal '{principalId}'.");
                return;
            }

            _logger.LogInformation($"Key vault '{vaultResource.Data.Name}' uses the RBAC permission model (access policies are ignored) — assigning role '{roleName}' to principal '{principalId}' at vault scope...");

            var content = new RoleAssignmentCreateOrUpdateContent(roleDefinition.Id, principalId)
            {
                // Tells ARM not to fail the create while a just-created service principal is still
                // replicating through Entra ID.
                PrincipalType = new RoleManagementPrincipalType("ServicePrincipal")
            };

            // Deterministic name, so re-running the installer PUTs the same assignment instead of trying to
            // create a second one for the same (principal, role, scope) and getting 409 RoleAssignmentExists.
            var assignmentName = DeterministicRoleAssignmentName(vaultResource.Id.ToString(), principalId, roleDefinition.Id.Name);

            try
            {
                await roleAssignments.CreateOrUpdateAsync(WaitUntil.Completed, assignmentName, content);
                _logger.LogInformation($"Assigned role '{roleName}' on key vault '{vaultResource.Data.Name}' to principal '{principalId}'. Note: data-plane role assignments can take a minute to propagate.");
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || string.Equals(ex.ErrorCode, "RoleAssignmentExists", StringComparison.OrdinalIgnoreCase))
            {
                // Someone (a previous install with a random name, or an admin in the portal) already granted
                // this exact role at this scope under a different assignment name. That is the desired state.
                _logger.LogInformation($"Role '{roleName}' was already assigned on key vault '{vaultResource.Data.Name}' to principal '{principalId}' under a different assignment name; nothing to do.");
            }
            catch (RequestFailedException ex) when (ex.Status == 403)
            {
                // The installer account can reach the vault but cannot hand out roles. This MUST be fatal:
                // on an RBAC vault the access policies are ignored, so continuing would produce a deployment
                // that looks installed but whose web app and importer cannot read the vault at all - exactly
                // the silent failure this whole change exists to remove. Creating role assignments needs
                // 'Microsoft.Authorization/roleAssignments/write' (User Access Administrator or Owner), which
                // Contributor does NOT include.
                throw new InstallException(
                    $"Could not assign role '{roleName}' on key vault '{vaultResource.Data.Name}' to principal '{principalId}': " +
                    $"the installer account is not permitted to create role assignments ({ex.ErrorCode}). " +
                    $"This vault uses the RBAC permission model, so access policies will NOT work as a substitute. " +
                    $"Grant the installer account 'User Access Administrator' (or Owner) on the vault — Contributor is not enough — " +
                    $"or assign '{roleName}' to that principal by hand, then re-run.");
            }
        }

        /// <summary>
        /// True when the principal already holds the role at the vault itself (or an ancestor scope).
        /// Deliberately ignores assignments scoped to an individual secret/certificate inside the vault, and
        /// any assignment carrying an ABAC <c>Condition</c>: neither grants unconditional vault-wide access,
        /// so treating them as equivalent would skip a grant the install actually needs.
        /// </summary>
        private static bool HasVaultScopedAssignment(RoleAssignmentCollection roleAssignments, KeyVaultResource vaultResource,
            Guid principalId, AuthorizationRoleDefinitionResource roleDefinition)
        {
            var vaultScope = vaultResource.Id.ToString();

            foreach (var existing in roleAssignments)
            {
                if (existing.Data.PrincipalId != principalId) continue;

                // Compare the role definition GUID rather than the full resource id: the id returned when
                // listing definitions at a resource scope is not guaranteed to be the same string as the one
                // stored on an existing assignment (tenant-rooted vs subscription-rooted paths).
                if (!string.Equals(existing.Data.RoleDefinitionId?.Name, roleDefinition.Id.Name, StringComparison.OrdinalIgnoreCase)) continue;

                if (!string.IsNullOrEmpty(existing.Data.Condition)) continue;

                var existingScope = existing.Data.Scope?.ToString();
                if (string.IsNullOrEmpty(existingScope)) continue;

                // The vault itself, or an ancestor (resource group / subscription) that already covers it.
                if (vaultScope.StartsWith(existingScope, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Stable GUID for a (scope, principal, role) triple so repeated installs are idempotent.
        /// </summary>
        private static string DeterministicRoleAssignmentName(string scope, Guid principalId, string roleDefinitionGuid)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{scope}|{principalId}|{roleDefinitionGuid}".ToLowerInvariant()));
                var guidBytes = new byte[16];
                Array.Copy(hash, guidBytes, 16);
                return new Guid(guidBytes).ToString();
            }
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

            _logger.LogInformation($"Granting Azure AD application with client ID '{clientId}' access to key vault {vaultResource.Data.Name} for {DescribePermissions(secretPerms, certPerms)}");

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

        /// <summary>
        /// Renders the requested permissions for logging, e.g. "secrets: Get, List; certificates: Get".
        /// Previously every call logged a hard-coded "secret read &amp; list; certificate read", which was
        /// actively misleading on the task that grants Set - the log claimed read-only access moments
        /// before failing to write a secret.
        /// </summary>
        protected static string DescribePermissions(IEnumerable<string> secretPerms, IEnumerable<string> certPerms)
        {
            var parts = new List<string>();
            var secrets = (secretPerms ?? Enumerable.Empty<string>()).ToList();
            if (secrets.Count > 0)
            {
                parts.Add($"secrets: {string.Join(", ", secrets)}");
            }
            var certs = (certPerms ?? Enumerable.Empty<string>()).ToList();
            if (certs.Count > 0)
            {
                parts.Add($"certificates: {string.Join(", ", certs)}");
            }
            return parts.Count > 0 ? string.Join("; ", parts) : "no permissions";
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
            _logger.LogInformation($"Granted web-app '{webAppName}' access to key vault {vaultResource.Data.Name} for {DescribePermissions(secretPerms, certPerms)}");
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

        /// <summary>Backoff schedule (seconds) for retrying the secret write on a 403 — absorbs typical access-policy propagation lag (~30–60s).</summary>
        private static readonly int[] _accessPolicyRetryDelaysSeconds = new[] { 10, 20, 30 };

        /// <summary>
        /// Backoff schedule (seconds) for vaults using the RBAC permission model. Role assignments replicate
        /// through Entra ID and the Key Vault data plane far more slowly than access policies — Azure documents
        /// up to 5 minutes — and the installer will usually have created the assignment moments earlier, so the
        /// first write lands squarely inside that window. The access-policy schedule (~60s total) is not enough.
        /// </summary>
        private static readonly int[] _rbacRetryDelaysSeconds = new[] { 10, 20, 30, 45, 60, 60, 60 };

        private static int[] RetryDelaysFor(KeyVaultResource vault) =>
            vault.Data.Properties?.EnableRbacAuthorization == true ? _rbacRetryDelaysSeconds : _accessPolicyRetryDelaysSeconds;

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

            // Retry on 403/Forbidden: the permission granting the InstallerAccount Set access was just added by
            // KeyVaultAddSecretAllPermissionsForAppRegistrationTask in the same task batch, and Entra ID takes
            // time to propagate before it is enforceable from the data plane — 30-60s for an access policy, and
            // materially longer for a role assignment on an RBAC-model vault (hence the wider schedule).
            var retryDelaysSeconds = RetryDelaysFor(vault);
            RequestFailedException lastForbidden = null;
            for (var attempt = 0; attempt <= retryDelaysSeconds.Length; attempt++)
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

                    // Key Vault's message is the only thing that says WHICH kind of 403 this is, and it
                    // used to be discarded - leaving the final error listing both possible causes without
                    // choosing. Logged once, on the first attempt, so the retry lines stay readable.
                    if (attempt == 0)
                    {
                        _logger.LogInformation($"Key vault said: {KeyVaultForbiddenClassifier.FirstLine(ex.Message)}");
                    }

                    // A networking refusal (vault firewall, public access disabled) will still be a refusal
                    // in five minutes - the installer's own address is not going to change mid-run. Only
                    // permission/unknown 403s are worth waiting out for propagation, so stop retrying here
                    // and let the classification below report it. Without this the RBAC schedule would sit
                    // through its full backoff on a failure that could never succeed.
                    if (KeyVaultForbiddenClassifier.Classify(ex) == KeyVaultForbiddenReason.NetworkBlocked)
                    {
                        break;
                    }

                    if (attempt < retryDelaysSeconds.Length)
                    {
                        var delaySeconds = retryDelaysSeconds[attempt];
                        _logger.LogInformation($"Key vault secret write got 403/Forbidden (attempt {attempt + 1} of {retryDelaysSeconds.Length + 1}). " +
                            $"This usually means the access policy or role assignment added moments earlier has not yet propagated through Entra ID. Waiting {delaySeconds}s and retrying...");
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                    }
                }
            }

            // All retries exhausted. Decide from Key Vault's own message whether the caller was refused by
            // networking or by permissions - the two need opposite remedies. This is deliberately checked
            // BEFORE the ARM re-read below: in a tenant where Azure Policy forces publicNetworkAccess back
            // to 'Disabled', the re-read races the policy's remediation and can still report 'Enabled',
            // which is exactly how a policy-blocked write got reported as a hard error.
            var vaultSaid = KeyVaultForbiddenClassifier.FirstLine(lastForbidden?.Message);
            var reason = KeyVaultForbiddenClassifier.Classify(lastForbidden);

            if (reason == KeyVaultForbiddenReason.NetworkBlocked)
            {
                _logger.LogWarning(
                    $"Key vault '{vault.Data.Name}' secret '{name}' was not updated: the vault refused the installer on networking grounds, not permissions. " +
                    $"Key vault said: {vaultSaid} " +
                    $"This is expected where public network access is switched off (often enforced by Azure Policy, which can revert the setting even after the installer changes it) or where the vault firewall does not list this host's address. " +
                    $"Any existing value in the vault remains valid, so this only matters if the runtime app-registration secret has actually changed. " +
                    $"To update it, run the installer from inside the VNet / over the private endpoint, or temporarily allow public access and re-run.");
                return vault;
            }

            if (reason == KeyVaultForbiddenReason.PermissionDenied)
            {
                var usesRbac = vault.Data.Properties?.EnableRbacAuthorization == true;
                _logger.LogError(
                    $"Could not add secret '{name}' to key vault '{vault.Data.Name}' after {retryDelaysSeconds.Length + 1} attempts: the installer reached the vault but is not permitted to write secrets. " +
                    $"Key vault said: {vaultSaid} " +
                    (usesRbac
                        ? $"This vault uses the RBAC permission model, so the Access policies blade has no effect — and note that Owner/Contributor do NOT grant secret access either, because Key Vault secret operations are dataActions. " +
                          $"Assign the installer's app registration the 'Key Vault Secrets Officer' role on the vault and re-run. " +
                          $"(The installer tries to assign this automatically; if it could not, it needs 'User Access Administrator' or Owner on the vault.) "
                        : $"Grant the installer's app registration 'Set' on secrets in the vault's Access policies blade and re-run. ") +
                    $"App-registration secrets in the vault may now be out of date.");
                return vault;
            }

            // Unrecognised 403. Re-read the vault's current PublicNetworkAccess from ARM as a second
            // opinion, so a policy-blocked write is still reported as a soft warning rather than an error.
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
                $"Could not add secret '{name}' to key vault '{vault.Data.Name}' after {retryDelaysSeconds.Length + 1} attempts (last error: 403 Forbidden, ErrorCode='{lastForbidden?.ErrorCode}'). " +
                (vaultSaid != null ? $"Key vault said: {vaultSaid} " : string.Empty) +
                $"Likely causes: access policy / RBAC propagation lag (longer than the {(retryDelaysSeconds.Length + 1)}-attempt retry window) or a vault firewall rule rejecting the runner IP — check the vault's Networking blade. " +
                $"App-registration secrets in the vault may now be out of date — re-run the installer once the underlying cause is resolved.");
            return vault;
        }
    }
}
