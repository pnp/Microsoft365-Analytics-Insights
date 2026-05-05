using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Email;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Orchestrates the sent-emails import: pulls messages from an
    /// <see cref="ISentEmailSourceLoader"/>, optionally scores sentiment,
    /// and persists per-recipient rows into the analytics database.
    /// </summary>
    public class SentEmailImporter : AbstractApiLoader
    {
        private readonly ISentEmailSourceLoader _sourceLoader;
        private readonly ISentEmailSentimentScorer _sentimentScorer;
        private readonly Func<AnalyticsEntitiesContext> _dbContextFactory;

        // Stats collected across the whole run.
        private int _mailboxesScanned;
        private int _mailboxesFailed;
        private int _messagesSeen;
        private int _rowsInserted;
        private int _deltaTokenReads;
        private int _deltaTokenWrites;

        public SentEmailImporter(
            AnalyticsLogger telemetry,
            AppConfig settings,
            ISentEmailSourceLoader sourceLoader,
            ISentEmailSentimentScorer sentimentScorer,
            Func<AnalyticsEntitiesContext> dbContextFactory = null)
            : base(telemetry, settings)
        {
            _sourceLoader = sourceLoader ?? throw new ArgumentNullException(nameof(sourceLoader));
            _sentimentScorer = sentimentScorer ?? NullSentEmailSentimentScorer.Instance;
            _dbContextFactory = dbContextFactory ?? (() => new AnalyticsEntitiesContext());
        }

        /// <summary>
        /// Backward-compatible constructor that wires the default Graph-backed loader and the
        /// default Azure AI Language sentiment scorer (or a no-op when not configured).
        /// </summary>
        public SentEmailImporter(
            AnalyticsLogger telemetry,
            AppConfig settings,
            ManualGraphCallClient httpClient,
            IDeltaTokenStore deltaTokenStore,
            DataUtils.Http.ImportAppIndentityOAuthContext appIdentity)
            : this(
                telemetry,
                settings,
                new GraphSentEmailSourceLoader(httpClient, deltaTokenStore, appIdentity, telemetry),
                SentEmailSentimentScorerFactory.Create(settings, telemetry))
        {
        }

        public async Task ImportSentEmails()
        {
            _telemetry.LogInformation("Starting sent emails import...");
            var swTotal = Stopwatch.StartNew();

            if (!await _sourceLoader.HasMailReadAccessAsync())
            {
                _telemetry.LogWarning(
                    "Skipping sent emails import: the configured identity does not have Mail.Read permission. " +
                    "Grant Mail.Read (application) to the app registration to enable this import.");
                return;
            }

            var users = await LoadUsersWithMailAsync();
            if (users.Count == 0)
            {
                _telemetry.LogWarning("No users found with email addresses to scan for sent items.");
                return;
            }

            _telemetry.LogInformation($"Found {users.Count} users with email addresses to scan for sent items.");

            foreach (var user in users)
            {
                await ImportSafelyAsync(user);
            }

            swTotal.Stop();
            LogRunSummary(swTotal.Elapsed);
        }

        internal async Task ImportSentEmailsForUser(Common.Entities.User user)
        {
            var load = await _sourceLoader.LoadSentEmailsForUserAsync(user, includeBody: _sentimentScorer.IsEnabled);
            _deltaTokenReads += load.DeltaTokenReads;
            _deltaTokenWrites += load.DeltaTokenWrites;

            if (load.Messages.Count == 0)
                return;

            _messagesSeen += load.Messages.Count;
            _telemetry.LogInformation($"Found {load.Messages.Count} sent messages for user '{user.UserPrincipalName}'.");

            var candidates = BuildCandidates(load.Messages, out var distinctAddresses);
            if (candidates.Count == 0)
                return;

            using (var db = _dbContextFactory())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                db.Configuration.ValidateOnSaveEnabled = false;

                var existingKeys = await FindExistingKeysAsync(db, candidates);
                var toInsert = candidates.Where(c => !existingKeys.Contains(c.GraphMessageId)).ToList();
                if (toInsert.Count == 0)
                    return;

                var addressIds = await ResolveAddressIdsAsync(db, distinctAddresses);
                var sentimentByMessageId = await ScoreSentimentAsync(toInsert);

                foreach (var c in toInsert)
                {
                    db.SentEmails.Add(BuildSentEmailRow(user, c, addressIds, sentimentByMessageId));
                }

                await db.SaveChangesAsync();
                _rowsInserted += toInsert.Count;
            }
        }

        // For test-only access to the HTML stripper - kept on this class for backwards compatibility.
        internal static string StripHtml(string html) => AzureLanguageSentEmailSentimentScorer.StripHtml(html);

        #region Private helpers

        private async Task<List<Common.Entities.User>> LoadUsersWithMailAsync()
        {
            using (var db = _dbContextFactory())
            {
                return await db.users.Where(u => u.Mail != null && u.Mail != "").ToListAsync();
            }
        }

        private async Task ImportSafelyAsync(Common.Entities.User user)
        {
            try
            {
                await ImportSentEmailsForUser(user);
                _mailboxesScanned++;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _mailboxesFailed++;
                _telemetry.LogWarning($"Could not access sent items for user '{user.UserPrincipalName}': {ex.Message}");
            }
            catch (Exception ex)
            {
                _mailboxesFailed++;
                _telemetry.LogError(ex, $"Error importing sent emails for user '{user.UserPrincipalName}': {ex.Message}");
            }
        }

        private void LogRunSummary(TimeSpan elapsed)
        {
            _telemetry.LogInformation(
                $"Finished sent emails import in {elapsed:hh\\:mm\\:ss}. " +
                $"Mailboxes scanned: {_mailboxesScanned} (failed: {_mailboxesFailed}). " +
                $"Messages seen: {_messagesSeen}. Rows inserted: {_rowsInserted}. " +
                $"Delta tokens read: {_deltaTokenReads}, written: {_deltaTokenWrites}.");
        }

        private static List<Candidate> BuildCandidates(
            IReadOnlyList<GraphSentMessage> messages,
            out HashSet<string> distinctAddresses)
        {
            var candidates = new List<Candidate>(messages.Count);
            var pendingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            distinctAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var msg in messages)
            {
                if (string.IsNullOrEmpty(msg.Id))
                    continue;

                var fromAddr = msg.From?.EmailAddress?.Address;
                if (string.IsNullOrEmpty(fromAddr))
                    continue;

                fromAddr = fromAddr.ToLowerInvariant();
                distinctAddresses.Add(fromAddr);

                var toRecipients = msg.ToRecipients;
                if (toRecipients == null || toRecipients.Count == 0)
                    continue;

                foreach (var recipient in toRecipients)
                {
                    var toAddr = recipient?.EmailAddress?.Address;
                    if (string.IsNullOrEmpty(toAddr))
                        continue;

                    toAddr = toAddr.ToLowerInvariant();
                    var key = msg.Id + "_" + toAddr;
                    if (!pendingKeys.Add(key))
                        continue;

                    distinctAddresses.Add(toAddr);
                    candidates.Add(new Candidate
                    {
                        Message = msg,
                        FromAddress = fromAddr,
                        ToAddress = toAddr,
                        GraphMessageId = key
                    });
                }
            }

            return candidates;
        }

        private static async Task<HashSet<string>> FindExistingKeysAsync(
            AnalyticsEntitiesContext db, List<Candidate> candidates)
        {
            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allKeys = candidates.Select(c => c.GraphMessageId).ToList();

            foreach (var batch in Chunk(allKeys, 1000))
            {
                var hits = await db.SentEmails
                    .Where(s => batch.Contains(s.GraphMessageId))
                    .Select(s => s.GraphMessageId)
                    .ToListAsync();

                foreach (var h in hits)
                    existingKeys.Add(h);
            }

            return existingKeys;
        }

        private static async Task<Dictionary<string, int>> ResolveAddressIdsAsync(
            AnalyticsEntitiesContext db, HashSet<string> distinctAddresses)
        {
            var emailAddressCache = new EmailAddressCache(db);
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var addr in distinctAddresses)
            {
                var entity = await emailAddressCache.GetOrCreateEmailAddress(addr);
                addressIds[addr] = entity.ID;
            }
            return addressIds;
        }

        private async Task<Dictionary<string, double?>> ScoreSentimentAsync(List<Candidate> toInsert)
        {
            if (!_sentimentScorer.IsEnabled)
                return null;

            var uniqueMessagesWithBody = toInsert
                .Select(c => c.Message)
                .GroupBy(m => m.Id)
                .Select(g => g.First())
                .Where(m => !string.IsNullOrEmpty(m.Body?.Content))
                .ToList();

            return await _sentimentScorer.ScoreAsync(uniqueMessagesWithBody);
        }

        private static SentEmail BuildSentEmailRow(
            Common.Entities.User user,
            Candidate c,
            Dictionary<string, int> addressIds,
            Dictionary<string, double?> sentimentByMessageId)
        {
            var msg = c.Message;
            var row = new SentEmail
            {
                GraphMessageId = c.GraphMessageId,
                Subject = msg.Subject?.Length > 1000 ? msg.Subject.Substring(0, 1000) : msg.Subject,
                SentDate = msg.SentDateTime ?? DateTime.MinValue,
                FromAddressID = addressIds[c.FromAddress],
                ToAddressID = addressIds[c.ToAddress],
                UserID = user.ID
            };

            if (sentimentByMessageId != null
                && sentimentByMessageId.TryGetValue(msg.Id, out var score))
            {
                row.CognitiveScore = score;
            }

            return row;
        }

        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }

        private sealed class Candidate
        {
            public GraphSentMessage Message;
            public string FromAddress;
            public string ToAddress;
            public string GraphMessageId;
        }

        #endregion
    }
}
