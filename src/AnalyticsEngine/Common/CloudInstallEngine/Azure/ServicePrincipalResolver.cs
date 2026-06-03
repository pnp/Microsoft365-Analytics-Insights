using Azure.Core;
using Azure.Identity;
using CloudInstallEngine.Models;
using System;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Resolves the Entra ID object ID (service principal) from app registration client credentials.
    /// </summary>
    public static class ServicePrincipalResolver
    {
        // Per-process cache. The (tenantId, clientId) → objectId mapping is immutable for the
        // lifetime of an installer run, and resolving it requires an interactive token request.
        // Without this cache the same client ID is resolved 4+ times across role assignment and
        // KV access policy tasks during a single install.
        //
        // NOTE: this cache is process-static. CloudInstallEngine is a .NET Standard 2.0 library
        // shared with the long-running web jobs. The installer is the only caller today; if any
        // long-running service ever starts calling GetObjectIdFromClientCredentials, consider
        // moving the cache to an instance held by the installer so credential rotation is picked
        // up across worker process lifetimes. ObjectIds themselves are stable for the principal,
        // so the cache content stays valid; only the failure to re-validate the secret is at risk.
        private static readonly ConcurrentDictionary<string, string> _cache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the object ID of a service principal by authenticating with client credentials and reading the "oid" claim from the resulting JWT.
        /// Cached per (tenantId, clientId) for the lifetime of the process.
        /// </summary>
        public static async Task<string> GetObjectIdFromClientCredentials(string tenantId, string clientId, string clientSecret)
        {
            var cacheKey = $"{tenantId ?? string.Empty}|{clientId ?? string.Empty}";
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var creds = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var credTokenResponse = await creds.GetTokenAsync(new TokenRequestContext(new string[] { "https://management.core.windows.net/.default" }, null));
            var handler = new JwtSecurityTokenHandler();
            var jwtSecurityToken = handler.ReadJwtToken(credTokenResponse.Token);
            var objectIdClaim = jwtSecurityToken.Claims.Where(c => c.Type == "oid").FirstOrDefault();
            if (objectIdClaim == null)
            {
                throw new InstallException("No object ID found in token for the given client credentials");
            }

            _cache[cacheKey] = objectIdClaim.Value;
            return objectIdClaim.Value;
        }
    }
}

