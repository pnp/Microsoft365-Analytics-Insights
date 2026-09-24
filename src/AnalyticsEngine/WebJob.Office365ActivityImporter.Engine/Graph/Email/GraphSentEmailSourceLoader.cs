using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Loads sent emails from a user's mailbox via the Microsoft Graph
    /// /messages/delta endpoint, persisting per-user delta tokens.
    /// </summary>
    public class GraphSentEmailSourceLoader : ISentEmailSourceLoader, ISentEmailDeltaTokenCommitter
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
        private readonly AnalyticsLogger _logger;

        public GraphSentEmailSourceLoader(
            ManualGraphCallClient httpClient,
            IDeltaTokenStore deltaTokenStore,
            ImportAppIndentityOAuthContext appIdentity,
            AnalyticsLogger logger)
        {
            _httpClient = httpClient;
            _deltaTokenStore = deltaTokenStore;
            _appIdentity = appIdentity;
            _logger = logger;
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
                var permissions = AccessTokenPermissions.Extract(token.Token);
                return permissions.Any(p => MailReadPermissions.Contains(p));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not verify Mail.Read permission on access token: {ex.Message}. Assuming access is not granted.");
                return false;
            }
        }

        public async Task<SentEmailLoadResult> LoadSentEmailsForUserAsync(Common.Entities.User user, bool includeBody)
        {
            var deltaKey = BuildDeltaKey(user);
            var deltaToken = await _deltaTokenStore.GetDeltaToken(deltaKey);
            var reads = 1;
            string nextDeltaToken = null;

            var url = BuildDeltaUrl(user, deltaToken, includeBody);

            var messages = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphSentMessage>(
                url, _logger,
                (deltaLink) =>
                {
                    nextDeltaToken = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    return Task.CompletedTask;
                },
                // A mailbox-less user 404s here. Surface it instead of silently returning an empty list,
                // so the importer can tell "no mailbox" apart from "mailbox with no sent mail" and stop
                // re-checking the user every cycle.
                throwOnNotFound: true,
                throwOnHttpError: true);

            return new SentEmailLoadResult
            {
                Messages = messages ?? new List<GraphSentMessage>(),
                DeltaTokenReads = reads,
                DeltaTokenWrites = 0,
                NextDeltaToken = nextDeltaToken
            };
        }

        public Task CommitDeltaTokenAsync(Common.Entities.User user, string deltaToken)
        {
            return _deltaTokenStore.SetDeltaToken(BuildDeltaKey(user), deltaToken);
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
    }
}
