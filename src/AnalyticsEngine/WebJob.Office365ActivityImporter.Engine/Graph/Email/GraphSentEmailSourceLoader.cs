using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Loads sent emails from a user's mailbox via the Microsoft Graph
    /// /messages/delta endpoint, persisting per-user delta tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bounded reads.</b> A first read - or a re-read after Graph rejects a token - returns the whole Sent Items
    /// folder, which for a long-serving account is tens of thousands of messages (with bodies, when sentiment scoring
    /// is on). It used to be read in one go, held in memory with up to 24 other mailboxes, and saved in one
    /// transaction; a failure anywhere threw it all away and the next cycle started again from page one. Each read
    /// now stops after <see cref="MaxMessagesPerMailboxPerCycle"/> messages (fewer with bodies) and reports the Graph
    /// <c>@odata.nextLink</c> it stopped at. The importer saves what was read and only then commits that link, so the
    /// next cycle continues from it. A crash between the two replays at most those pages, and the unique
    /// <c>graph_message_id</c> makes the replay harmless. An incremental read normally fits in one page and is
    /// unaffected: it still ends with a <c>@odata.deltaLink</c> whose token replaces the previous one. The token and the
    /// continuation are never stored together - committing a continuation retires the token - so a mailbox in steady
    /// state costs one state read and one write a cycle, exactly as before.
    /// </para>
    /// <para>
    /// <b>Rejected tokens.</b> When Graph refuses the stored delta token or continuation link - expired, or sync
    /// reset (<see cref="GraphDeltaTokenRejection"/>; for a continuation, which Graph minted itself, any 400 or 410) -
    /// both are discarded and the mailbox is read again from the start, once, in the same call. Replaying a dead token
    /// can never succeed, and nothing else ever cleared it, so the mailbox used to fail on every cycle, for good; a 404
    /// <c>syncStateNotFound</c> was worse, being taken for "no mailbox" and the user skipped. Only a failure of the
    /// first request - the one that carried the stored checkpoint - can reject it; a later page, and 401/403/429/5xx,
    /// timeouts and transport failures, keep it.
    /// </para>
    /// </remarks>
    public class GraphSentEmailSourceLoader : ISentEmailSourceLoader, ISentEmailDeltaTokenCommitter, ISentEmailSyncContinuationCommitter
    {
        // Graph defaults to 10 messages per page on /messages/delta - explicitly request more.
        public const int GraphPageSize = 200;

        /// <summary>Default bound on the messages one mailbox read returns per cycle, without message bodies.</summary>
        public const int DefaultMaxMessagesPerMailboxPerCycle = 1000;

        /// <summary>Default bound when bodies are read for sentiment scoring: each message is many times larger.</summary>
        public const int DefaultMaxMessagesWithBodyPerMailboxPerCycle = GraphPageSize;

        /// <summary>
        /// Bound on the messages one mailbox read returns per cycle, checked after each page. With up to 25 mailboxes
        /// in a chunk this bounds the chunk's memory and its save transaction too.
        /// </summary>
        internal int MaxMessagesPerMailboxPerCycle { get; set; } = DefaultMaxMessagesPerMailboxPerCycle;

        /// <summary>As <see cref="MaxMessagesPerMailboxPerCycle"/>, when message bodies are requested.</summary>
        internal int MaxMessagesWithBodyPerMailboxPerCycle { get; set; } = DefaultMaxMessagesWithBodyPerMailboxPerCycle;

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

        // Mailboxes (by token key) whose load resumed from a stored continuation, so completing the round deletes it.
        // Only mailboxes part-way through a bounded read are ever in here.
        private readonly ConcurrentDictionary<string, byte> _mailboxesWithStoredContinuation =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

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
            var continuationKey = BuildContinuationKey(user);

            // A round starts from the stored delta token, or from scratch when there is none; a read that stopped at
            // its bound last cycle continues from its saved nextLink, which already encodes the query. The two are never
            // both stored - committing a continuation retires the token - so the continuation is only looked for when
            // there is no token, and a mailbox in steady state costs one state read a cycle, not two.
            var deltaToken = await _deltaTokenStore.GetDeltaToken(deltaKey);
            var stateReads = 1;
            string continuation = null;
            if (string.IsNullOrEmpty(deltaToken))
            {
                continuation = await _deltaTokenStore.GetDeltaToken(continuationKey);
                stateReads++;
                if (!string.IsNullOrEmpty(continuation))
                {
                    _mailboxesWithStoredContinuation[deltaKey] = 0;
                }
            }

            var firstUrl = !string.IsNullOrEmpty(continuation) ? continuation : BuildDeltaUrl(user, deltaToken, includeBody);
            var firstUrlCarriesStoredCheckpoint = !string.IsNullOrEmpty(continuation) || !string.IsNullOrEmpty(deltaToken);

            var checkpointWasReset = false;
            MailboxRead read;
            try
            {
                read = await ReadPagesAsync(firstUrl, includeBody);
            }
            catch (Exception ex) when (IsRejectedStoredCheckpoint(ex, firstUrl, firstUrlCarriesStoredCheckpoint, firstUrlIsStoredContinuation: !string.IsNullOrEmpty(continuation)))
            {
                _logger.LogWarning($"Sent emails: Microsoft Graph rejected the stored sync checkpoint for '{user.UserPrincipalName}' ({DescribeFailure(ex)}): " +
                    "it has expired, or Graph has reset synchronisation. Discarding it and reading this mailbox's sent items again from the start; " +
                    "messages already imported are recognised by their Graph message id and skipped.");

                await _deltaTokenStore.ClearDeltaToken(continuationKey);
                await _deltaTokenStore.ClearDeltaToken(deltaKey);
                _mailboxesWithStoredContinuation.TryRemove(deltaKey, out _);
                checkpointWasReset = true;

                // Once, and with no token at all. This request carries no checkpoint, so its failure can never be
                // taken for a rejection, and the recovery cannot loop.
                read = await ReadPagesAsync(BuildDeltaUrl(user, null, includeBody), includeBody);
            }

            return new SentEmailLoadResult
            {
                Messages = read.Messages,
                DeltaTokenReads = stateReads,
                DeltaTokenWrites = 0,
                NextDeltaToken = read.NextDeltaToken,
                NextPageLink = read.NextPageLink,
                CheckpointWasReset = checkpointWasReset,
            };
        }

        /// <summary>
        /// Strict paging: any failure throws, so a partial read is never mistaken for a complete one. A mailbox-less
        /// user's 404 surfaces as <see cref="GraphResourceNotFoundException"/>, so the importer can tell "no mailbox"
        /// apart from "mailbox with no sent mail" and stop re-checking the user every cycle.
        /// </summary>
        private async Task<MailboxRead> ReadPagesAsync(string firstUrl, bool includeBody)
        {
            var maxMessages = Math.Max(1, includeBody ? MaxMessagesWithBodyPerMailboxPerCycle : MaxMessagesPerMailboxPerCycle);
            var read = new MailboxRead();
            var url = firstUrl;
            var pageNumber = 0;
            while (!string.IsNullOrEmpty(url))
            {
                pageNumber++;
                var page = await _httpClient.GetAsyncWithThrottleRetries<PageableGraphResponseWithDelta<GraphSentMessage>>(url);

                // A page that could not be parsed (ManualGraphCallClient has logged its body) or that came without its
                // "value" collection has unknown messages, not none. Stepping over it carried the read on to the deltaLink
                // and advanced the token past messages never read - lost for good. So the mailbox fails this cycle:
                // nothing is committed, and the next cycle retries from the same checkpoint. No URL in the message: the
                // link carries the mailbox's UPN and its sync token.
                if (page == null)
                {
                    throw new InvalidOperationException($"Graph returned sent-items page {pageNumber} in a form that could not be read.");
                }
                if (!page.HasValueCollection)
                {
                    throw new InvalidOperationException($"Graph returned sent-items page {pageNumber} without its \"value\" collection.");
                }

                read.Messages.AddRange(page.PageResults);

                if (!string.IsNullOrEmpty(page.OdataNextLink))
                {
                    if (read.Messages.Count >= maxMessages)
                    {
                        read.NextPageLink = page.OdataNextLink;
                        break;
                    }
                    url = page.OdataNextLink;
                }
                else
                {
                    if (!string.IsNullOrEmpty(page.DeltaLink))
                    {
                        read.NextDeltaToken = StringUtils.ExtractCodeFromGraphUrl(page.DeltaLink);
                    }
                    url = null;
                }
            }

            return read;
        }

        /// <summary>
        /// Whether <paramref name="failure"/> means the stored checkpoint is dead. Only the first request carried it, so
        /// the failed request must be that one (Graph's paging links carry their own <c>$skiptoken</c>, never the
        /// stored one); the status and error code are then judged by the shared <see cref="GraphDeltaTokenRejection"/>.
        /// </summary>
        /// <remarks>
        /// A stored continuation is also dead on any 400 or 410, whatever the error code. Graph minted that link itself,
        /// so unlike a delta token - which is appended to a query this loader builds - a refusal cannot be a query we
        /// got wrong. Requiring a recognised code there would leave a mailbox whose continuation expired while the
        /// importer was stopped failing on every cycle.
        /// </remarks>
        internal static bool IsRejectedStoredCheckpoint(Exception failure, string firstUrl, bool firstUrlCarriesStoredCheckpoint, bool firstUrlIsStoredContinuation)
        {
            var graphError = failure as GraphHttpException;
            var failedOnFirstRequest = graphError != null && string.Equals(graphError.Url, firstUrl, StringComparison.Ordinal);

            if (GraphDeltaTokenRejection.IsRejectedDeltaToken(failure, firstUrlCarriesStoredCheckpoint && failedOnFirstRequest))
            {
                return true;
            }

            return firstUrlIsStoredContinuation && failedOnFirstRequest
                && (graphError.StatusCode == System.Net.HttpStatusCode.BadRequest || graphError.StatusCode == System.Net.HttpStatusCode.Gone);
        }

        // The status and Graph error code only: the URL carries the mailbox's UPN and the token.
        private static string DescribeFailure(Exception failure)
            => failure is GraphHttpException graphError ? graphError.SummaryWithoutUrl : failure.Message;

        public async Task CommitDeltaTokenAsync(Common.Entities.User user, string deltaToken)
        {
            // The round is complete: the next one starts from this token, not from a continuation. Only a mailbox that
            // resumed from a continuation has one to delete - not a Table delete for every mailbox on every cycle.
            var deltaKey = BuildDeltaKey(user);
            await _deltaTokenStore.SetDeltaToken(deltaKey, deltaToken);
            if (_mailboxesWithStoredContinuation.TryRemove(deltaKey, out _))
            {
                await _deltaTokenStore.ClearDeltaToken(BuildContinuationKey(user));
            }
        }

        public async Task CommitContinuationAsync(Common.Entities.User user, string nextPageLink)
        {
            await _deltaTokenStore.SetDeltaToken(BuildContinuationKey(user), nextPageLink);

            // A load only looks for a continuation when no token is stored, so a round that began from a token and
            // outgrew one cycle must retire it, or every later cycle would restart that round and never finish it.
            // Deleted after the continuation is written: a failure in between leaves both, and the next load replays
            // the round from the token - harmless - instead of losing its place. Only mailboxes part-way through a
            // bounded read get here.
            await _deltaTokenStore.ClearDeltaToken(BuildDeltaKey(user));
        }

        internal static string BuildDeltaKey(Common.Entities.User user)
            => $"SentEmails-{user.UserPrincipalName}";

        // A different prefix, not a suffix on the token key, so it can never collide with another user's token key.
        internal static string BuildContinuationKey(Common.Entities.User user)
            => $"SentEmailsNextPage-{user.UserPrincipalName}";

        internal static string BuildDeltaUrl(Common.Entities.User user, string deltaToken, bool includeBody)
        {
            // Only request the message body when sentiment scoring is enabled - it's the biggest field.
            var select = includeBody
                ? "id,subject,from,toRecipients,sentDateTime,body"
                : "id,subject,from,toRecipients,sentDateTime";

            var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(user.UserPrincipalName)}/mailFolders/sentitems/messages/delta" +
                      $"?$select={select}&$top={GraphPageSize}";

            if (!string.IsNullOrEmpty(deltaToken))
            {
                url += $"&$deltatoken={deltaToken}";
            }

            return url;
        }

        private sealed class MailboxRead
        {
            public List<GraphSentMessage> Messages { get; } = new List<GraphSentMessage>();
            public string NextDeltaToken { get; set; }
            public string NextPageLink { get; set; }
        }
    }
}
