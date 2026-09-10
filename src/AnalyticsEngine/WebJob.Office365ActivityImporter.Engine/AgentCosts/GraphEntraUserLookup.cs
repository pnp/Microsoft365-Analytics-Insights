using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Looks a user up in Entra by object id, for per-user credit rows whose user is not in
    /// <c>dbo.users</c> yet.
    /// </summary>
    /// <remarks>
    /// Uses the same application permission the user import already holds (<c>User.Read.All</c>), so this
    /// adds no new consent requirement - worth knowing, because a feature that silently needed a new admin
    /// grant would fail on every existing deployment until someone noticed.
    /// </remarks>
    public class GraphEntraUserLookup : IEntraUserLookup
    {
        private readonly ManualGraphCallClient _graphClient;
        private readonly ILogger _logger;

        public GraphEntraUserLookup(ManualGraphCallClient graphClient, ILogger logger)
        {
            _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
            _logger = logger;
        }

        public async Task<EntraUserRef> GetUserByObjectIdAsync(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return null;

            // Guarded because the id is interpolated into a URL. In practice it is a GUID from Microsoft's
            // own billing API, but "in practice" is not a validation, and a stray '/' or '?' would otherwise
            // address a different resource entirely.
            if (!Guid.TryParse(objectId.Trim(), out var parsed))
            {
                _logger?.LogDebug(
                    $"Agent costs - user identifier '{objectId}' is not an object id, so it cannot be looked up "
                    + "in the directory. The credits are still recorded against the raw value.");
                return null;
            }

            var url = $"https://graph.microsoft.com/v1.0/users/{parsed:D}"
                + "?$select=id,userPrincipalName,mail,accountEnabled";

            try
            {
                var user = await _graphClient.GetAsyncWithThrottleRetries<GraphUserIdentity>(url);
                if (user == null || string.IsNullOrWhiteSpace(user.UserPrincipalName)) return null;

                return new EntraUserRef
                {
                    ObjectId = string.IsNullOrWhiteSpace(user.Id) ? parsed.ToString("D") : user.Id,
                    UserPrincipalName = user.UserPrincipalName,
                    Mail = user.Mail,
                    AccountEnabled = user.AccountEnabled,
                };
            }
            catch (GraphResourceNotFoundException)
            {
                // No such object. A deleted account is a normal, permanent state for a billing row from a
                // past period, so this is null rather than an exception - the caller stops asking.
                return null;
            }
        }

        /// <summary>The handful of directory fields linking a billing row needs.</summary>
        private class GraphUserIdentity
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("userPrincipalName")]
            public string UserPrincipalName { get; set; }

            [JsonProperty("mail")]
            public string Mail { get; set; }

            [JsonProperty("accountEnabled")]
            public bool? AccountEnabled { get; set; }
        }
    }
}
