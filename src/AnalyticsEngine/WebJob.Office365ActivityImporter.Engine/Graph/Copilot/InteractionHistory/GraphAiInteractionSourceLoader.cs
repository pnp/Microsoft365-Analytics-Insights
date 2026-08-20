using DataUtils;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Loads Copilot interaction history from
    /// <c>GET /copilot/users/{userId}/interactionHistory/getAllEnterpriseInteractions</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One HTTP call (plus paging) per user - there is no tenant-wide or delta form of this endpoint. That is
    /// the defining constraint of the whole feature and the reason the importer scopes to a pilot group and
    /// caps how many users it will touch per cycle.
    /// </para>
    /// <para>
    /// Requires the <c>AiEnterpriseInteraction.Read.All</c> <b>application</b> permission (there is no
    /// delegated form), and returns data only for users licensed with the <c>M365_COPILOT_BUSINESS_CHAT</c>
    /// service plan. Copilot Studio agents are excluded by the API itself, so agent conversations built in
    /// Copilot Studio will never appear here.
    /// </para>
    /// </remarks>
    public class GraphAiInteractionSourceLoader : IAiInteractionSourceLoader
    {
        /// <summary>
        /// Graph defaults to a small page size on this endpoint; ask for more so a chatty pilot user doesn't
        /// cost dozens of round trips.
        /// </summary>
        public const int GraphPageSize = 100;

        /// <summary>
        /// Safety cap on pages fetched for one user in one cycle. A single very heavy user must not be able
        /// to consume the whole import cycle; hitting the cap is reported as a truncated (not failed) load
        /// so the remainder resumes next cycle rather than being skipped.
        /// </summary>
        internal const int MaxPagesPerUser = 50;

        /// <summary>
        /// The only permission that grants this endpoint. Deliberately a single-entry set (unlike the mail
        /// import's several alternatives) because Graph offers no lesser-scoped equivalent.
        /// </summary>
        internal static readonly HashSet<string> InteractionReadPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AiEnterpriseInteraction.Read.All"
        };

        /// <summary>
        /// Graph error codes that mean "this user will never return interactions". Treated as a terminal,
        /// expected state - the user is put on the back-off list rather than counted as a failure.
        /// </summary>
        private static readonly HashSet<string> TerminalUserErrorCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Request_ResourceNotFound",
            "ResourceNotFound",
            "itemNotFound",
            "MailboxNotEnabledForRESTAPI",
            "NoCopilotLicense",
        };

        private readonly ManualGraphCallClient _httpClient;
        private readonly ImportAppIndentityOAuthContext _appIdentity;
        private readonly ILogger _logger;

        public GraphAiInteractionSourceLoader(ManualGraphCallClient httpClient, ImportAppIndentityOAuthContext appIdentity, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _appIdentity = appIdentity;
            _logger = logger;
        }

        public async Task<bool> HasInteractionReadAccessAsync()
        {
            if (_appIdentity == null)
            {
                // Nothing to inspect - fail open and let the per-user calls report the truth.
                return true;
            }

            try
            {
                var token = await _appIdentity.GetAccessToken();
                var permissions = GraphTokenPermissions.Extract(token.Token);
                return permissions.Any(p => InteractionReadPermissions.Contains(p));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not verify AiEnterpriseInteraction.Read.All on the access token: {ex.Message}. Assuming it has not been granted.");
                return false;
            }
        }

        public async Task<AiInteractionLoadResult> LoadInteractionsForUserAsync(Common.Entities.User user, DateTime fromUtc, DateTime toUtc)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            var userKey = GetUserKey(user);
            if (string.IsNullOrWhiteSpace(userKey))
            {
                // No addressable identifier - not an error, just nothing we can ask Graph about.
                return new AiInteractionLoadResult { UserNotAvailable = true };
            }

            // An inverted or zero-length window would make Graph return nothing while looking like a
            // successful empty result, which would then advance the watermark. Refuse it instead.
            if (toUtc <= fromUtc)
            {
                return AiInteractionLoadResult.Empty();
            }

            return await LoadAllPagesAsync(BuildInteractionsUrl(userKey, fromUtc, toUtc));
        }

        /// <summary>
        /// Reads every page for one user, all-or-nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This deliberately does not use the shared <c>LoadAllPagesWithThrottleRetries</c> helper, for two
        /// reasons that both matter a great deal for this endpoint specifically:
        /// </para>
        /// <para>
        /// <b>1. Partial results must not look like success.</b> The shared helper swallows an HTTP failure
        /// mid-paging and returns the pages it managed to collect. For most importers that is the right
        /// call. Here it would be silent data loss: the caller would treat the truncated list as a complete
        /// window and advance the user's watermark past interactions it never actually saw, which nothing
        /// would ever go back for. Any page failure here fails the whole user, leaving the watermark alone.
        /// </para>
        /// <para>
        /// <b>2. The response body must never be logged.</b> The shared client logs the raw response body on
        /// a deserialisation or HTTP error. For every other Graph call that is a useful diagnostic; for this
        /// one the body is a page of the user's literal prompts and Copilot's literal answers, so logging it
        /// would put the most sensitive data in the product into the console and Application Insights. This
        /// loop reads the body only to pull out Graph's machine-readable <c>error.code</c>, and reports
        /// status codes and page numbers instead.
        /// </para>
        /// </remarks>
        private async Task<AiInteractionLoadResult> LoadAllPagesAsync(string firstPageUrl)
        {
            var all = new List<AiInteraction>();
            var nextUrl = firstPageUrl;
            var page = 0;

            while (!string.IsNullOrEmpty(nextUrl))
            {
                page++;
                if (page > MaxPagesPerUser)
                {
                    // One pathological user must not be able to consume the whole cycle. Report what we
                    // have as truncated so the caller advances the watermark only as far as it actually
                    // read, and picks the rest up next cycle.
                    _logger.LogWarning(
                        $"Copilot interaction history: stopped after {MaxPagesPerUser} pages for a single user " +
                        $"({all.Count} interaction(s) read). The remainder will resume on the next cycle.");
                    return new AiInteractionLoadResult { Interactions = all, Truncated = true };
                }

                HttpResponseMessage response = null;
                try
                {
                    response = await _httpClient.GetAsyncWithThrottleRetries(nextUrl, _logger);

                    if (!response.IsSuccessStatusCode)
                    {
                        // Read the body only to extract Graph's error code - never to log or return it.
                        var errorCode = ExtractGraphErrorCode(await response.Content.ReadAsStringAsync());

                        if (IsTerminalForUser(response.StatusCode, errorCode))
                        {
                            _logger.LogDebug(
                                $"No Copilot interaction history available for a user (HTTP {(int)response.StatusCode}, " +
                                $"Graph code '{errorCode ?? "unknown"}'). Most likely they have no " +
                                "M365_COPILOT_BUSINESS_CHAT service plan. Backing off this user.");
                            return new AiInteractionLoadResult { UserNotAvailable = true };
                        }

                        return new AiInteractionLoadResult
                        {
                            Error = $"Graph returned HTTP {(int)response.StatusCode} (code '{errorCode ?? "unknown"}') on page {page}."
                        };
                    }

                    var body = await response.Content.ReadAsStringAsync();

                    PageableGraphResponse<AiInteraction> pageResult;
                    try
                    {
                        pageResult = JsonConvert.DeserializeObject<PageableGraphResponse<AiInteraction>>(body);
                    }
                    catch (JsonException)
                    {
                        // Note: neither the body nor the exception message is included - a deserialisation
                        // error message can quote the offending JSON value, which here is prompt text.
                        return new AiInteractionLoadResult
                        {
                            Error = $"Could not parse the Graph interaction-history response on page {page}."
                        };
                    }

                    if (pageResult == null)
                    {
                        return new AiInteractionLoadResult { Error = $"Graph returned an empty body on page {page}." };
                    }

                    if (pageResult.PageResults != null)
                        all.AddRange(pageResult.PageResults);

                    nextUrl = pageResult.OdataNextLink;
                }
                catch (Exception ex)
                {
                    // Only the exception type is reported: message text from an HTTP/JSON stack can embed
                    // response content, and this value is both logged and persisted to last_error.
                    _logger.LogWarning(
                        $"Copilot interaction history: failed to load page {page} for a user ({ex.GetType().Name}). " +
                        "The user's watermark is unchanged, so this window will be retried next cycle.");

                    return new AiInteractionLoadResult
                    {
                        Error = $"{ex.GetType().Name} loading page {page}."
                    };
                }
                finally
                {
                    response?.Dispose();
                }
            }

            return new AiInteractionLoadResult { Interactions = all };
        }

        /// <summary>
        /// Prefer the Entra object id, falling back to UPN. Both are accepted by Graph, but the object id is
        /// stable across renames and avoids escaping problems with unusual UPNs.
        /// </summary>
        internal static string GetUserKey(Common.Entities.User user)
        {
            if (!string.IsNullOrWhiteSpace(user.AzureAdId))
                return user.AzureAdId.Trim();

            return string.IsNullOrWhiteSpace(user.UserPrincipalName) ? null : user.UserPrincipalName.Trim();
        }

        /// <summary>
        /// Builds the request URL.
        /// </summary>
        /// <remarks>
        /// Graph only supports <c>$filter</c> on <c>createdDateTime</c> as a <b>range</b>, so both bounds are
        /// always sent even for an open-ended catch-up; <paramref name="toUtc"/> is simply "now" in that case.
        /// <c>sessionId</c> and <c>requestId</c> are not filterable, which is why incremental loading has to
        /// key off the timestamp.
        /// </remarks>
        internal static string BuildInteractionsUrl(string userKey, DateTime fromUtc, DateTime toUtc)
        {
            var filter = $"createdDateTime gt {ToGraphDateTime(fromUtc)} and createdDateTime lt {ToGraphDateTime(toUtc)}";

            var sb = new StringBuilder();
            sb.Append("https://graph.microsoft.com/v1.0/copilot/users/");
            sb.Append(Uri.EscapeDataString(userKey));
            sb.Append("/interactionHistory/getAllEnterpriseInteractions");
            sb.Append("?$filter=");
            sb.Append(Uri.EscapeDataString(filter));
            sb.Append("&$top=");
            sb.Append(GraphPageSize.ToString(CultureInfo.InvariantCulture));

            return sb.ToString();
        }

        /// <summary>ISO-8601 UTC literal, the form OData expects for a datetime comparison.</summary>
        internal static string ToGraphDateTime(DateTime value)
        {
            return InteractionStatsExtractor.ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether a failure means "give up on this user" rather than "retry later". A 404 always does; a 403
        /// does too, because at this point the tenant-wide permission check has already passed, so a per-user
        /// 403 means that specific user is out of scope (an access policy, or an unlicensed account).
        /// </summary>
        private static bool IsTerminalForUser(HttpStatusCode status, string graphCode)
        {
            if (graphCode != null && TerminalUserErrorCodes.Contains(graphCode))
                return true;

            return status == HttpStatusCode.NotFound || status == HttpStatusCode.Forbidden;
        }

        /// <summary>
        /// Best-effort pull of Graph's <c>error.code</c> out of text that may or may not contain a JSON body.
        /// Never throws - this only feeds logging and the back-off decision.
        /// </summary>
        internal static string ExtractGraphErrorCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;

            try
            {
                return JObject.Parse(text.Substring(start, end - start + 1))["error"]?["code"]?.ToString();
            }
            catch (Exception)
            {
                return null;
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
    public static class GraphTokenPermissions
    {
        public static IReadOnlyCollection<string> Extract(string jwt)
        {
            if (string.IsNullOrEmpty(jwt))
                return Array.Empty<string>();

            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return Array.Empty<string>();

            JObject payload;
            try
            {
                payload = JObject.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }

            var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Application permissions arrive as a "roles" array...
            if (payload["roles"] is JArray roles)
            {
                foreach (var r in roles)
                    permissions.Add(r.ToString());
            }

            // ...delegated ones as a space-separated "scp" string. This endpoint is app-only, but both are
            // collected so the check behaves sensibly if it is ever reused.
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
