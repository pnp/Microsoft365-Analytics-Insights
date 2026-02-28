using Azure.Core;
using Azure.Identity;
using CloudInstallEngine.Models;
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
        /// <summary>
        /// Gets the object ID of a service principal by authenticating with client credentials and reading the "oid" claim from the resulting JWT.
        /// </summary>
        public static async Task<string> GetObjectIdFromClientCredentials(string tenantId, string clientId, string clientSecret)
        {
            var creds = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var credTokenResponse = await creds.GetTokenAsync(new TokenRequestContext(new string[] { "https://management.core.windows.net/.default" }, null));
            var handler = new JwtSecurityTokenHandler();
            var jwtSecurityToken = handler.ReadJwtToken(credTokenResponse.Token);
            var objectIdClaim = jwtSecurityToken.Claims.Where(c => c.Type == "oid").FirstOrDefault();
            if (objectIdClaim == null)
            {
                throw new InstallException("No object ID found in token for the given client credentials");
            }
            return objectIdClaim.Value;
        }
    }
}
