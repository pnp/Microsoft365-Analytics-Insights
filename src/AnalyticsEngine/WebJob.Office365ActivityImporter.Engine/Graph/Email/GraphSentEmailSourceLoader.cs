using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Loads sent emails from a user's mailbox via the Microsoft Graph
    /// /messages/delta endpoint, persisting per-user delta tokens.
    /// </summary>
    public class GraphSentEmailSourceLoader : ISentEmailSourceLoader
    {
        // Graph defaults to 10 messages per page on /messages/delta - explicitly request more.
        public const int GraphPageSize = 200;

        // Graph permission names that grant the ability to read user mail.
        // Application permissions appear in the "roles" claim, delegated permissions in "scp".
        private static readonly HashSet<string> MailReadPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Mail.Read",
            "Mail.ReadBasic",
            "Mail.ReadBasic.All",
            "Mail.ReadWrite"
        };

        private readonly ManualGraphCallClient _httpClient;
        private readonly IDeltaTokenStore _deltaTokenStore;
        private readonly ImportAppIndentityOAuthContext _appIdentity;
        private readonly AnalyticsLogger _telemetry;

        public GraphSentEmailSourceLoader(
            ManualGraphCallClient httpClient,
            IDeltaTokenStore deltaTokenStore,
            ImportAppIndentityOAuthContext appIdentity,
            AnalyticsLogger telemetry)
        {
            _httpClient = httpClient;
            _deltaTokenStore = deltaTokenStore;
            _appIdentity = appIdentity;
            _telemetry = telemetry;
        }

        public async Task<bool> HasMailReadAccessAsync()
        {
            if (_appIdentity == null)
            {
                // No identity context to inspect - fail open and let the per-user calls surface errors.
                return true;
            }

            try
            {
                var token = await _appIdentity.GetAccessToken();
                var permissions = ExtractPermissionsFromJwt(token.Token);
                return permissions.Any(p => MailReadPermissions.Contains(p));
            }
            catch (Exception ex)
            {
                _telemetry.LogWarning($"Could not verify Mail.Read permission on access token: {ex.Message}. Assuming access is not granted.");
                return false;
            }
        }

        public async Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(Common.Entities.User user, bool includeBody)
        {
            var deltaKey = BuildDeltaKey(user);
            var deltaToken = await _deltaTokenStore.GetDeltaToken(deltaKey);
            var reads = 1;
            var writes = 0;

            var url = BuildDeltaUrl(user, deltaToken, includeBody);

            var messages = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphSentMessage>(
                url, _telemetry,
                async (deltaLink) =>
                {
                    var thisPageDelta = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    await _deltaTokenStore.SetDeltaToken(deltaKey, thisPageDelta);
                    writes++;
                });

            return new SentEmailLoadResult
            {
                Messages = messages ?? new List<GraphSentMessage>(),
                DeltaTokenReads = reads,
                DeltaTokenWrites = writes
            };
        }

        internal static string BuildDeltaKey(Common.Entities.User user)
            => $"SentEmails-{user.UserPrincipalName}";

        internal static string BuildDeltaUrl(Common.Entities.User user, string deltaToken, bool includeBody)
        {
            // Only request the message body when sentiment scoring is enabled - it's the biggest field.
            var select = includeBody
                ? "id,subject,from,toRecipients,sentDateTime,body"
                : "id,subject,from,toRecipients,sentDateTime";

            var url = $"https://graph.microsoft.com/v1.0/users/{user.UserPrincipalName}/mailFolders/sentitems/messages/delta" +
                      $"?$select={select}&$top={GraphPageSize}";

            if (!string.IsNullOrEmpty(deltaToken))
            {
                url += $"&$deltatoken={deltaToken}";
            }

            return url;
        }

        /// <summary>
        /// Decode the JWT payload and return the union of the application <c>roles</c> claim
        /// and the delegated <c>scp</c> claim. Signature is not validated - we trust the token
        /// because it was just issued to us by AAD.
        /// </summary>
        internal static IReadOnlyCollection<string> ExtractPermissionsFromJwt(string jwt)
        {
            if (string.IsNullOrEmpty(jwt))
                return Array.Empty<string>();

            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return Array.Empty<string>();

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var payload = JObject.Parse(payloadJson);

            var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Application permissions: "roles": ["Mail.Read", ...]
            if (payload["roles"] is JArray roles)
            {
                foreach (var r in roles)
                    permissions.Add(r.ToString());
            }

            // Delegated permissions: "scp": "Mail.Read User.Read ..."
            var scp = payload["scp"]?.ToString();
            if (!string.IsNullOrEmpty(scp))
            {
                foreach (var s in scp.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    permissions.Add(s);
            }

            return permissions;
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
