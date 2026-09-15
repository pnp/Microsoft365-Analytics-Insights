using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Context for Graph calls
    /// </summary>
    public class GraphAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public GraphAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://graph.microsoft.com/.default";
    }

    /// <summary>
    /// Context for Activity API calls
    /// </summary>
    public class ActivityAPIAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public ActivityAPIAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://manage.office.com/.default";
    }

    /// <summary>
    /// Context for Power Platform API calls (Copilot Studio credit consumption).
    /// </summary>
    /// <remarks>
    /// A token for this audience is not enough on its own: the service principal must also hold a Power
    /// Platform RBAC role (Power Platform reader is the least-privilege option) assigned at tenant scope.
    /// Without it the licensing routes answer 401/403 even though the token itself is valid, which is why the
    /// credit importer turns those two status codes into an explicit instruction rather than a generic error.
    /// </remarks>
    public class PowerPlatformAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public PowerPlatformAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://api.powerplatform.com/.default";
    }

    /// <summary>
    /// Context for Azure Resource Manager calls (Microsoft Cost Management).
    /// </summary>
    /// <remarks>
    /// As with Power Platform, the token is only half the story: the service principal needs the Cost
    /// Management Reader role at the scope being queried. That is an Azure RBAC assignment for subscription
    /// and management-group scopes, but is granted in the billing portal for EA and MCA billing-account
    /// scopes.
    /// </remarks>
    public class AzureManagementAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public AzureManagementAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://management.azure.com/.default";
    }

    /// <summary>
    /// The permission state of an app token, keeping "we could not tell" distinct from "not granted".
    /// </summary>
    public enum AppTokenPermissionAccess
    {
        Granted,
        NotGranted,
        Unknown,
        NoIdentityToInspect,
    }

    /// <summary>
    /// Verifies application permissions by inspecting the roles carried by the token issued for an API audience.
    /// </summary>
    public static class AppTokenPermissionVerifier
    {
        public static async Task<AppTokenPermissionAccess> GetAccessAsync(
            ImportAppIndentityOAuthContext appIdentity,
            IReadOnlyCollection<string> acceptablePermissions,
            ILogger logger,
            string permissionName)
        {
            if (appIdentity == null)
            {
                return AppTokenPermissionAccess.NoIdentityToInspect;
            }

            try
            {
                var token = await appIdentity.GetAccessToken();

                if (!AccessTokenPermissions.TryExtract(token.Token, out var permissions))
                {
                    logger?.LogWarning(
                        $"Could not read the access token's permissions, so the {permissionName} grant could not be confirmed either way.");
                    return AppTokenPermissionAccess.Unknown;
                }

                var accepted = new HashSet<string>(acceptablePermissions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                return permissions.Any(p => accepted.Contains(p))
                    ? AppTokenPermissionAccess.Granted
                    : AppTokenPermissionAccess.NotGranted;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Could not verify {permissionName} on the access token: {ex.Message}.");
                return AppTokenPermissionAccess.Unknown;
            }
        }
    }

    /// <summary>
    /// Reads the permissions an app-only access token actually carries.
    /// </summary>
    /// <remarks>
    /// The signature is not validated - the token was just issued to us by Entra ID, and this is only used to
    /// produce a clearer "you haven't consented to X yet" message than a wall of 403s would.
    /// </remarks>
    public static class AccessTokenPermissions
    {
        public static IReadOnlyCollection<string> Extract(string jwt)
        {
            TryExtract(jwt, out var permissions);
            return permissions;
        }

        /// <summary>
        /// Reads the permissions off an app-only token, reporting separately whether the token could be
        /// parsed at all.
        /// </summary>
        /// <returns>
        /// True when the payload was decoded (even if it carried no permissions), false when the token was
        /// missing, malformed or not decodable.
        /// </returns>
        /// <remarks>
        /// The distinction matters to callers that report on a permission: "the token parsed and carries no
        /// roles" is a definite, actionable absence of consent, whereas "the token could not be read" proves
        /// nothing. <see cref="Extract"/> collapses both to an empty set, which is fine for a caller that
        /// only wants "does it have X?" but would make a diagnostic tell an admin to re-consent a permission
        /// they may already hold. See issue #329.
        /// </remarks>
        public static bool TryExtract(string jwt, out IReadOnlyCollection<string> permissions)
        {
            permissions = Array.Empty<string>();

            if (string.IsNullOrEmpty(jwt))
                return false;

            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return false;

            JObject payload;
            try
            {
                payload = JObject.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            }
            catch (Exception)
            {
                return false;
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Application permissions arrive as a "roles" array...
            if (payload["roles"] is JArray roles)
            {
                foreach (var r in roles)
                    found.Add(r.ToString());
            }

            // ...delegated ones as a space-separated "scp" string. The installer checks app-only tokens, but
            // both are collected so callers that just ask "does this token carry X?" behave sensibly.
            var scp = payload["scp"]?.ToString();
            if (!string.IsNullOrEmpty(scp))
            {
                foreach (var s in scp.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    found.Add(s);
            }

            permissions = found;
            return true;
        }

        private static byte[] Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
