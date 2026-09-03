using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Email;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Orchestrates the sent-emails import: pulls messages from an
    /// <see cref="ISentEmailSourceLoader"/>, optionally scores sentiment,
    /// and persists one <see cref="SentEmail"/> row per message with a
    /// <see cref="SentEmailRecipient"/> child per recipient.
    /// </summary>
    /// <remarks>
    /// The importer parallelises Graph load and sentiment scoring across small user chunks
    /// but writes are serialised on a single connection using multi-row <c>INSERT ... VALUES</c>
    /// statements. Email address inserts in particular must remain single-threaded because
    /// concurrent threads would each see "does not exist" and race to insert the same row,
    /// hitting the unique index on <c>email_addresses.address</c>.
    /// </remarks>
    public class SentEmailImporter : AbstractApiLoader
    {
        // Tunables - kept conservative to avoid hammering Graph throttling limits and SQL.
        private const int DefaultUserChunkSize = 25;
        private const int DefaultGraphLoadParallelism = 8;

        private readonly ISentEmailSourceLoader _sourceLoader;
        private readonly ISentEmailSentimentScorer _sentimentScorer;
        private readonly IAnalyticsDbContextFactory _dbContextFactory;
        private readonly int _userChunkSize;
        private readonly int _graphLoadParallelism;
        private readonly ISentEmailMailboxSkipList _mailboxSkipList;
        private readonly int _noMailboxRetryHours;

        // UPNs discovered during this run to have no mailbox at all (Graph 404). Concurrent because the
        // Graph load phase populates it from parallel worker threads.
        private readonly ConcurrentDictionary<string, byte> _noMailboxUpns =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Stats collected across the whole run. All updated via Interlocked from worker threads.
        private int _mailboxesScanned;
        private int _mailboxesFailed;
        private int _mailboxesNotFound;
        private int _mailboxesSkipped;
        private int _messagesSeen;
        private int _messagesInserted;
        private int _recipientsInserted;
        private int _deltaTokenReads;
        private int _deltaTokenWrites;

        public SentEmailImporter(
            AnalyticsLogger logger,
            AppConfig settings,
            ISentEmailSourceLoader sourceLoader,
            ISentEmailSentimentScorer sentimentScorer,
            IAnalyticsDbContextFactory dbContextFactory = null,
            int userChunkSize = DefaultUserChunkSize,
            int graphLoadParallelism = DefaultGraphLoadParallelism,
            ISentEmailMailboxSkipList mailboxSkipList = null,
            int noMailboxRetryHours = 0)
            : base(logger, settings)
        {
            _sourceLoader = sourceLoader ?? throw new ArgumentNullException(nameof(sourceLoader));
            _sentimentScorer = sentimentScorer ?? NullSentEmailSentimentScorer.Instance;
            _dbContextFactory = dbContextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _userChunkSize = userChunkSize > 0 ? userChunkSize : DefaultUserChunkSize;
            _graphLoadParallelism = graphLoadParallelism > 0 ? graphLoadParallelism : DefaultGraphLoadParallelism;
            _mailboxSkipList = mailboxSkipList;
            _noMailboxRetryHours = noMailboxRetryHours;
        }

        /// <summary>
        /// Backward-compatible constructor that wires the default Graph-backed loader and the
        /// default Azure AI Language sentiment scorer (or a no-op when not configured).
        /// </summary>
        public SentEmailImporter(
            AnalyticsLogger logger,
            AppConfig settings,
            ManualGraphCallClient httpClient,
            IDeltaTokenStore deltaTokenStore,
            DataUtils.Http.ImportAppIndentityOAuthContext appIdentity,
            ISentEmailMailboxSkipList mailboxSkipList = null)
            : this(
                logger,
                settings,
                new GraphSentEmailSourceLoader(httpClient, deltaTokenStore, appIdentity, logger),
                SentEmailSentimentScorerFactory.Create(settings, logger),
                mailboxSkipList: mailboxSkipList,
                noMailboxRetryHours: settings?.SentEmailNoMailboxRetryHours ?? 0)
        {
        }

        public async Task ImportSentEmails()
        {
            _logger.LogInformation("Starting sent emails import...");
            var swTotal = Stopwatch.StartNew();

            if (!await _sourceLoader.HasMailReadAccessAsync())
            {
                _logger.LogWarning(
                    "Skipping sent emails import: the configured identity does not have Mail.Read permission. " +
                    "Grant Mail.Read (application) to the app registration to enable this import.");
                return;
            }

            var users = await LoadUsersWithMailAsync();
            if (users.Count == 0)
            {
                _logger.LogWarning("No users found with email addresses to scan for sent items.");
                return;
            }

            // Users with no Exchange mailbox (unlicensed, on-premises, inactive, or guest accounts) 404 on
            // every call, forever. Skip the ones we already know about, and re-sweep the whole directory
            // periodically so a newly-licensed user is picked up. A retry interval of 0 disables the cache.
            var skipList = _mailboxSkipList != null ? await _mailboxSkipList.LoadAsync() : MailboxSkipList.Empty();
            var fullSweepDue = ImportCadenceGate.ShouldRun(skipList.GeneratedUtc, _noMailboxRetryHours, force: false, nowUtc: DateTime.UtcNow);

            var knownNoMailbox = fullSweepDue ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : skipList.UpnSet;
            if (fullSweepDue && _mailboxSkipList != null && _noMailboxRetryHours > 0)
            {
                _logger.LogInformation(
                    $"Sent emails: re-checking every mailbox (last full sweep {skipList.GeneratedUtc?.ToString("u") ?? "never"}, " +
                    $"interval {_noMailboxRetryHours}h).");
            }

            var usersToScan = knownNoMailbox.Count == 0
                ? users
                : users.Where(u => string.IsNullOrEmpty(u.UserPrincipalName) || !knownNoMailbox.Contains(u.UserPrincipalName)).ToList();

            _mailboxesSkipped = users.Count - usersToScan.Count;
            if (_mailboxesSkipped > 0)
            {
                _logger.LogInformation(
                    $"Sent emails: skipping {_mailboxesSkipped} of {users.Count} users already known to have no mailbox. " +
                    $"They'll be re-checked after {skipList.GeneratedUtc?.AddHours(_noMailboxRetryHours):u} UTC.");
            }

            if (usersToScan.Count == 0)
            {
                _logger.LogInformation("No users left to scan for sent items once mailbox-less users are excluded.");
                return;
            }

            _logger.LogInformation(
                $"Found {usersToScan.Count} users with email addresses to scan for sent items. " +
                $"Processing in chunks of {_userChunkSize} (Graph load parallelism: {_graphLoadParallelism}; " +
                $"persistence uses single-connection multi-row SQL inserts to avoid unique-index deadlocks).");

            for (int i = 0; i < usersToScan.Count; i += _userChunkSize)
            {
                // GetRange instead of Skip().Take() - Skip() on a List walks past every
                // prior element, so chunking N users in slices of K costs O(N^2/K). At
                // 200k users with K=25 that's ~800M wasted iterations just for slicing.
                var chunk = usersToScan.GetRange(i, Math.Min(_userChunkSize, usersToScan.Count - i));
                _logger.LogInformation(
                    $"Processing user chunk {(i / _userChunkSize) + 1} of " +
                    $"{(usersToScan.Count + _userChunkSize - 1) / _userChunkSize} ({chunk.Count} users).");

                await ProcessUserChunkAsync(chunk);
            }

            await PersistMailboxSkipListAsync(skipList, knownNoMailbox, fullSweepDue);

            swTotal.Stop();
            LogRunSummary(swTotal.Elapsed);
        }

        /// <summary>
        /// Writes back the no-mailbox negative cache. A full sweep replaces the set outright (so users who
        /// have since been licensed drop out of it); an incremental run only adds newly-discovered entries
        /// and keeps the original sweep timestamp, so the next full sweep still happens on schedule.
        /// </summary>
        private async Task PersistMailboxSkipListAsync(MailboxSkipList previous, HashSet<string> skippedThisRun, bool fullSweepDue)
        {
            if (_mailboxSkipList == null || _noMailboxRetryHours <= 0)
                return;

            var updated = BuildUpdatedSkipList(previous, _noMailboxUpns.Keys, skippedThisRun, fullSweepDue, DateTime.UtcNow);
            await _mailboxSkipList.SaveAsync(updated);
        }

        /// <summary>
        /// Pure decision logic for the no-mailbox negative cache, factored out so it can be unit tested
        /// without Redis or a database (mirrors <see cref="ImportCadenceGate"/>).
        /// </summary>
        /// <param name="previous">The skip list as loaded at the start of the run.</param>
        /// <param name="discoveredNoMailbox">UPNs that returned a Graph 404 during this run.</param>
        /// <param name="skippedThisRun">UPNs that weren't checked because they were already in the list.</param>
        /// <param name="fullSweepDue">True when every user was checked, so the set can be rebuilt outright.</param>
        internal static MailboxSkipList BuildUpdatedSkipList(
            MailboxSkipList previous,
            IEnumerable<string> discoveredNoMailbox,
            HashSet<string> skippedThisRun,
            bool fullSweepDue,
            DateTime nowUtc)
        {
            var updated = new HashSet<string>(discoveredNoMailbox ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (!fullSweepDue && skippedThisRun != null)
            {
                // Incremental run: the users we skipped weren't re-checked, so carry them forward.
                updated.UnionWith(skippedThisRun);
            }

            return new MailboxSkipList
            {
                // Only a full sweep moves the timestamp - otherwise an incremental run every 10 minutes
                // would keep pushing the next sweep out and users would never get re-checked.
                GeneratedUtc = fullSweepDue ? nowUtc : previous?.GeneratedUtc ?? nowUtc,
                Upns = updated.ToList(),
            };
        }

        /// <summary>
        /// Per-user processing path retained for unit tests and ad-hoc callers. Internally this
        /// just dispatches the user as a one-element chunk so the same parallel pipeline runs.
        /// </summary>
        internal Task ImportSentEmailsForUser(Common.Entities.User user)
        {
            return ProcessUserChunkAsync(new List<Common.Entities.User> { user });
        }

        // For test-only access to the HTML stripper - kept on this class for backwards compatibility.
        internal static string StripHtml(string html) => AzureLanguageSentEmailSentimentScorer.StripHtml(html);

        #region Chunk pipeline

        /// <summary>
        /// Run the full pipeline for a chunk of users:
        ///   1. Parallel Graph load (per-user, throttled).
        ///   2. Single-threaded bulk address lookup-table reconciliation (avoids unique-index races).
        ///   3. Single-threaded bulk existing-key check across the chunk.
        ///   4. Parallel sentiment scoring (no DB writes).
        ///   5. Single-connection two-phase multi-row INSERTs of SentEmail and SentEmailRecipient rows.
        /// </summary>
        private async Task ProcessUserChunkAsync(List<Common.Entities.User> users)
        {
            // ---- 1. Parallel Graph load -------------------------------------------------
            var swPhase = Stopwatch.StartNew();
            var loaded = await LoadChunkInParallelAsync(users);
            swPhase.Stop();
            _logger.LogInformation(
                $"  [chunk] graph load: {users.Count} users -> {loaded.Count} loaded in {swPhase.ElapsedMilliseconds}ms.");
            if (loaded.Count == 0)
                return;

            // Build candidates for the whole chunk and collect distinct addresses.
            var perUser = new List<UserChunkResult>(loaded.Count);
            var allDistinctAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in loaded)
            {
                if (entry.Messages.Count == 0)
                    continue;

                Interlocked.Add(ref _messagesSeen, entry.Messages.Count);

                var candidates = BuildCandidates(entry.Messages, out var distinctAddresses);
                if (candidates.Count == 0)
                    continue;

                foreach (var addr in distinctAddresses)
                    allDistinctAddresses.Add(addr);

                perUser.Add(new UserChunkResult
                {
                    User = entry.User,
                    Candidates = candidates
                });
            }

            if (perUser.Count == 0)
                return;

            int totalCandidates = perUser.Sum(u => u.Candidates.Count);
            _logger.LogInformation(
                $"  [chunk] built {totalCandidates} candidate messages across {perUser.Count} users; " +
                $"distinct addresses: {allDistinctAddresses.Count}.");

            // ---- 2. Bulk address resolution (single-threaded) --------------------------
            // Doing this on one connection avoids races: parallel inserts of the same
            // address would each see "doesn't exist" and then collide on the unique index.
            swPhase.Restart();
            Dictionary<string, int> addressIds;
            using (var db = _dbContextFactory.Create())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                db.Configuration.ValidateOnSaveEnabled = false;
                addressIds = await BulkResolveAddressIdsAsync(db, allDistinctAddresses);
            }
            swPhase.Stop();
            _logger.LogInformation(
                $"  [chunk] resolved {addressIds.Count} address ids in {swPhase.ElapsedMilliseconds}ms.");

            // ---- 3. Bulk existing-key check across the chunk ---------------------------
            swPhase.Restart();
            var allKeys = perUser
                .SelectMany(u => u.Candidates.Select(c => c.GraphMessageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            HashSet<string> existingKeys;
            using (var db = _dbContextFactory.Create())
            {
                db.Configuration.AutoDetectChangesEnabled = false;
                existingKeys = await FindExistingKeysAsync(db, allKeys);
            }
            swPhase.Stop();
            _logger.LogInformation(
                $"  [chunk] existing-key check: {allKeys.Count} keys, {existingKeys.Count} already present, " +
                $"in {swPhase.ElapsedMilliseconds}ms.");

            // Filter each user's candidate list to the genuinely-new ones.
            int totalToInsert = 0;
            foreach (var u in perUser)
            {
                u.ToInsert = u.Candidates
                    .Where(c => !existingKeys.Contains(c.GraphMessageId))
                    .ToList();
                totalToInsert += u.ToInsert.Count;
            }

            // ---- 4. Sentiment scoring (parallel-safe; no DB writes) --------------------
            var sentimentByMessageId = await ScoreSentimentAcrossChunkAsync(perUser);

            // ---- 5. Single-connection persistence of SentEmail + recipient rows -------
            swPhase.Restart();
            _logger.LogInformation(
                $"  [chunk] persisting {totalToInsert} new messages across {perUser.Count} users " +
                "(serial multi-row INSERTs on one connection to avoid unique-index races)...");
            await PersistChunkAsync(perUser, addressIds, sentimentByMessageId);
            swPhase.Stop();
            _logger.LogInformation(
                $"  [chunk] persistence done in {swPhase.ElapsedMilliseconds}ms.");
        }

        private async Task<List<LoadedUser>> LoadChunkInParallelAsync(List<Common.Entities.User> users)
        {
            var includeBody = _sentimentScorer.IsEnabled;
            var loaded = new ConcurrentBag<LoadedUser>();

            using (var sem = new SemaphoreSlim(_graphLoadParallelism))
            {
                var tasks = users.Select(async user =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var load = await _sourceLoader.LoadSentEmailsForUserAsync(user, includeBody);
                        Interlocked.Add(ref _deltaTokenReads, load.DeltaTokenReads);
                        Interlocked.Add(ref _deltaTokenWrites, load.DeltaTokenWrites);
                        loaded.Add(new LoadedUser { User = user, Messages = load.Messages });
                    }
                    catch (GraphResourceNotFoundException ex)
                    {
                        // The mailbox genuinely doesn't exist - the user is unlicensed, on-premises,
                        // inactive, or an external/guest account. Permanent, so record it and stop
                        // re-checking every cycle. Not an error: nothing is wrong and nothing is lost.
                        Interlocked.Increment(ref _mailboxesNotFound);
                        if (!string.IsNullOrEmpty(user.UserPrincipalName))
                            _noMailboxUpns[user.UserPrincipalName] = 0;

                        _logger.LogDebug(
                            $"User '{user.UserPrincipalName}' has no mailbox ({ex.GraphErrorCode ?? "unknown"}); " +
                            "skipping and adding to the no-mailbox list.");
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        Interlocked.Increment(ref _mailboxesFailed);
                        _logger.LogWarning(
                            $"Could not access sent items for user '{user.UserPrincipalName}': {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _mailboxesFailed);
                        _logger.LogError(ex,
                            $"Error loading sent emails for user '{user.UserPrincipalName}': {ex.Message}");
                    }
                    finally
                    {
                        sem.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }

            return loaded.ToList();
        }

        private async Task<Dictionary<string, double?>> ScoreSentimentAcrossChunkAsync(List<UserChunkResult> perUser)
        {
            if (!_sentimentScorer.IsEnabled)
                return null;

            // Deduplicate messages across the whole chunk: a message is identified by GraphSentMessage.Id.
            var uniqueMessagesWithBody = perUser
                .SelectMany(u => u.ToInsert.Select(c => c.Message))
                .Where(m => !string.IsNullOrEmpty(m.Body?.Content))
                .GroupBy(m => m.Id)
                .Select(g => g.First())
                .ToList();

            if (uniqueMessagesWithBody.Count == 0)
                return null;

            return await _sentimentScorer.ScoreAsync(uniqueMessagesWithBody);
        }

        /// <summary>
        /// Persist all <see cref="SentEmail"/> rows for the chunk in two deterministic phases on
        /// a single connection, using multi-row <c>INSERT ... VALUES (...), (...), ...</c> SQL so
        /// each round-trip writes hundreds of rows instead of one. EF6's normal SaveChanges path
        /// issues one INSERT per row, which is fine for small workloads but ~200 rows/sec for
        /// many thousands of rows. Multi-row inserts get us closer to 5-10k rows/sec on the same
        /// connection.
        ///
        /// Phase A also returns the generated <c>id</c> for each row via <c>OUTPUT INSERTED.id</c>
        /// keyed on the unique <c>graph_message_id</c>, so phase B can set
        /// <c>SentEmailRecipient.SentEmailID</c> without an extra round-trip per row.
        /// </summary>
        private async Task PersistChunkAsync(
            List<UserChunkResult> perUser,
            Dictionary<string, int> addressIds,
            Dictionary<string, double?> sentimentByMessageId)
        {
            // SQL Server allows up to 2100 parameters per command. Parents have 6 params/row, so
            // 300 rows = 1800 params (safe margin). Recipients have 2 params/row, so 1000 rows
            // = 2000 params.
            const int parentBatchSize = 300;
            const int recipientBatchSize = 1000;

            // Flatten work to a single ordered list so we can batch and report progress.
            var work = new List<(Common.Entities.User User, Candidate Candidate, SentEmail Row)>();
            foreach (var u in perUser)
            {
                if (u.ToInsert.Count == 0)
                {
                    Interlocked.Increment(ref _mailboxesScanned);
                    continue;
                }

                foreach (var c in u.ToInsert)
                {
                    var row = BuildSentEmailRow(u.User, c, addressIds, sentimentByMessageId);
                    work.Add((u.User, c, row));
                }
                Interlocked.Increment(ref _mailboxesScanned);
            }

            if (work.Count == 0)
                return;

            using (var db = _dbContextFactory.Create())
            {
                var conn = db.Database.Connection;
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync();

                // ---- Phase A: SentEmail parents ----------------------------------------
                var swA = Stopwatch.StartNew();
                int parentsSaved = 0;
                for (int i = 0; i < work.Count; i += parentBatchSize)
                {
                    int take = Math.Min(parentBatchSize, work.Count - i);
                    try
                    {
                        await BulkInsertSentEmailsAsync(conn, work, i, take);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            $"  [persist] phase A failed at batch starting {i} (size {take}): {ex.GetBaseException().Message}");
                        throw;
                    }

                    parentsSaved += take;
                    Interlocked.Add(ref _messagesInserted, take);
                    _logger.LogInformation(
                        $"  [persist] phase A: saved {parentsSaved}/{work.Count} parent rows " +
                        $"({swA.ElapsedMilliseconds}ms elapsed).");
                }
                swA.Stop();

                // ---- Phase B: SentEmailRecipient rows ----------------------------------
                var swB = Stopwatch.StartNew();
                int recipientsSaved = 0;

                // Flatten recipient pairs (sentEmailId, recipientAddressId).
                var recipientPairs = new List<(int SentEmailId, int RecipientAddressId)>(
                    work.Sum(w => w.Candidate.RecipientAddresses.Count));
                foreach (var w in work)
                {
                    foreach (var addr in w.Candidate.RecipientAddresses)
                    {
                        if (!addressIds.TryGetValue(addr, out var addrId))
                            continue;
                        recipientPairs.Add((w.Row.ID, addrId));
                    }
                }

                int totalRecipients = recipientPairs.Count;
                for (int i = 0; i < recipientPairs.Count; i += recipientBatchSize)
                {
                    int take = Math.Min(recipientBatchSize, recipientPairs.Count - i);
                    try
                    {
                        await BulkInsertSentEmailRecipientsAsync(conn, recipientPairs, i, take);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            $"  [persist] phase B failed at batch starting {i} (size {take}): {ex.GetBaseException().Message}");
                        throw;
                    }

                    recipientsSaved += take;
                    Interlocked.Add(ref _recipientsInserted, take);
                    _logger.LogInformation(
                        $"  [persist] phase B: saved {recipientsSaved}/{totalRecipients} recipient rows " +
                        $"({swB.ElapsedMilliseconds}ms elapsed).");
                }
                swB.Stop();

                _logger.LogInformation(
                    $"  [persist] phase A: {parentsSaved} parents in {swA.ElapsedMilliseconds}ms; " +
                    $"phase B: {recipientsSaved} recipients in {swB.ElapsedMilliseconds}ms.");
            }
        }

        /// <summary>
        /// Multi-row insert of <see cref="SentEmail"/> rows. Uses
        /// <c>OUTPUT INSERTED.id, INSERTED.graph_message_id</c> to map the generated identity
        /// back onto each in-memory row in a single round-trip.
        /// </summary>
        private static async Task BulkInsertSentEmailsAsync(
            System.Data.Common.DbConnection conn,
            List<(Common.Entities.User User, Candidate Candidate, SentEmail Row)> work,
            int offset,
            int count)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 0;
                cmd.CommandText = BuildSentEmailsInsertSql(count);

                int p = 0;
                for (int j = 0; j < count; j++)
                {
                    var row = work[offset + j].Row;
                    AddParam(cmd, "@p" + p++, (object)row.Subject ?? DBNull.Value);
                    AddParam(cmd, "@p" + p++, row.SentDate);
                    AddParam(cmd, "@p" + p++, row.GraphMessageId);
                    AddParam(cmd, "@p" + p++,
                        row.CognitiveScore.HasValue ? (object)row.CognitiveScore.Value : DBNull.Value);
                    AddParam(cmd, "@p" + p++, row.FromAddressID);
                    AddParam(cmd, "@p" + p++, row.UserID);
                }

                // Map the OUTPUT clause back onto each row by GraphMessageId.
                var idsByKey = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        int id = reader.GetInt32(0);
                        string key = reader.GetString(1);
                        idsByKey[key] = id;
                    }
                }

                for (int j = 0; j < count; j++)
                {
                    var row = work[offset + j].Row;
                    if (idsByKey.TryGetValue(row.GraphMessageId, out var id))
                        row.ID = id;
                }
            }
        }

        /// <summary>
        /// Build the parameterised multi-row INSERT SQL for <c>sent_emails</c>. Pure function so
        /// it is unit-testable in isolation.
        /// </summary>
        internal static string BuildSentEmailsInsertSql(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count must be > 0");

            var sb = new StringBuilder(count * 60 + 256);
            sb.Append(
                "INSERT INTO sent_emails " +
                "(subject, sent_date, graph_message_id, cognitive_score, from_address_id, user_id) " +
                "OUTPUT INSERTED.id, INSERTED.graph_message_id VALUES ");

            int p = 0;
            for (int j = 0; j < count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "(@p{0},@p{1},@p{2},@p{3},@p{4},@p{5})",
                    p, p + 1, p + 2, p + 3, p + 4, p + 5);
                p += 6;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Multi-row insert of <see cref="SentEmailRecipient"/> rows with explicit FKs.
        /// </summary>
        private static async Task BulkInsertSentEmailRecipientsAsync(
            System.Data.Common.DbConnection conn,
            List<(int SentEmailId, int RecipientAddressId)> pairs,
            int offset,
            int count)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 0;
                cmd.CommandText = BuildSentEmailRecipientsInsertSql(count);

                int p = 0;
                for (int j = 0; j < count; j++)
                {
                    AddParam(cmd, "@p" + p++, pairs[offset + j].SentEmailId);
                    AddParam(cmd, "@p" + p++, pairs[offset + j].RecipientAddressId);
                }

                await cmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Build the parameterised multi-row INSERT SQL for <c>sent_email_recipients</c>. Pure
        /// function so it is unit-testable in isolation.
        /// </summary>
        internal static string BuildSentEmailRecipientsInsertSql(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count must be > 0");

            var sb = new StringBuilder(count * 24 + 128);
            sb.Append("INSERT INTO sent_email_recipients (sent_email_id, recipient_address_id) VALUES ");

            int p = 0;
            for (int j = 0; j < count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.AppendFormat(CultureInfo.InvariantCulture, "(@p{0},@p{1})", p, p + 1);
                p += 2;
            }
            return sb.ToString();
        }

        private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        #endregion

        #region Private helpers

        private async Task<List<Common.Entities.User>> LoadUsersWithMailAsync()
        {
            using (var db = _dbContextFactory.Create())
            {
                return await db.users.Where(u => u.Mail != null && u.Mail != "").ToListAsync();
            }
        }

        private void LogRunSummary(TimeSpan elapsed)
        {
            _logger.LogInformation(
                $"Finished sent emails import in {elapsed:hh\\:mm\\:ss}. " +
                $"Mailboxes scanned: {_mailboxesScanned} (failed: {_mailboxesFailed}, " +
                $"no mailbox: {_mailboxesNotFound}, skipped as already known to have no mailbox: {_mailboxesSkipped}). " +
                $"Messages seen: {_messagesSeen}. Messages inserted: {_messagesInserted} " +
                $"(recipient rows: {_recipientsInserted}). " +
                $"Delta tokens read: {_deltaTokenReads}, written: {_deltaTokenWrites}.");
        }

        internal static List<Candidate> BuildCandidates(
            IReadOnlyList<GraphSentMessage> messages,
            out HashSet<string> distinctAddresses)
        {
            var candidates = new List<Candidate>(messages.Count);
            var seenMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            distinctAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var msg in messages)
            {
                if (string.IsNullOrEmpty(msg.Id))
                    continue;

                var fromAddr = msg.From?.EmailAddress?.Address;
                if (string.IsNullOrEmpty(fromAddr))
                    continue;

                var toRecipients = msg.ToRecipients;
                if (toRecipients == null || toRecipients.Count == 0)
                    continue;

                // Deduplicate the recipient list per message (the same mailbox may appear
                // multiple times in a Graph payload).
                var recipientAddresses = new List<string>(toRecipients.Count);
                var recipientSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var recipient in toRecipients)
                {
                    var toAddr = recipient?.EmailAddress?.Address;
                    if (string.IsNullOrEmpty(toAddr))
                        continue;

                    toAddr = toAddr.ToLowerInvariant();
                    if (recipientSet.Add(toAddr))
                    {
                        recipientAddresses.Add(toAddr);
                        distinctAddresses.Add(toAddr);
                    }
                }

                if (recipientAddresses.Count == 0)
                    continue;

                // Skip the same Graph message ID showing up twice within the same load.
                if (!seenMessageIds.Add(msg.Id))
                    continue;

                fromAddr = fromAddr.ToLowerInvariant();
                distinctAddresses.Add(fromAddr);

                candidates.Add(new Candidate
                {
                    Message = msg,
                    FromAddress = fromAddr,
                    RecipientAddresses = recipientAddresses,
                    GraphMessageId = msg.Id
                });
            }

            return candidates;
        }

        private static async Task<HashSet<string>> FindExistingKeysAsync(
            AnalyticsEntitiesContext db, List<string> allKeys)
        {
            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allKeys.Count == 0)
                return existingKeys;

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

        /// <summary>
        /// Resolve all distinct addresses to <c>email_addresses.ID</c> in bulk: query existing
        /// rows in IN-clause batches, then insert any missing rows in a single SaveChanges.
        /// Must run on a single thread/context because parallel insert of the same address
        /// would race the unique index on <c>address</c>.
        /// </summary>
        private static async Task<Dictionary<string, int>> BulkResolveAddressIdsAsync(
            AnalyticsEntitiesContext db, HashSet<string> distinctAddresses)
        {
            var addressIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (distinctAddresses.Count == 0)
                return addressIds;

            var pending = new List<string>(distinctAddresses);

            // 1) Look up the addresses we already have, in IN-clause-friendly chunks.
            foreach (var batch in Chunk(pending, 1000))
            {
                var hits = await db.EmailAddresses
                    .Where(e => batch.Contains(e.Address))
                    .Select(e => new { e.ID, e.Address })
                    .ToListAsync();

                foreach (var h in hits)
                    addressIds[h.Address] = h.ID;
            }

            // 2) Insert the missing ones in a single SaveChanges per chunk.
            var missing = pending.Where(a => !addressIds.ContainsKey(a)).ToList();
            if (missing.Count == 0)
                return addressIds;

            foreach (var batch in Chunk(missing, 1000))
            {
                var newEntities = batch.Select(a => new EmailAddress { Address = a }).ToList();
                db.EmailAddresses.AddRange(newEntities);

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (System.Data.Entity.Infrastructure.DbUpdateException)
                {
                    // Another importer instance / process inserted some of these in parallel.
                    // Detach the failed batch and re-query to populate the dictionary with whichever
                    // IDs the database now holds.
                    foreach (var e in newEntities)
                        db.Entry(e).State = EntityState.Detached;

                    var reread = await db.EmailAddresses
                        .Where(e => batch.Contains(e.Address))
                        .Select(e => new { e.ID, e.Address })
                        .ToListAsync();

                    foreach (var h in reread)
                        addressIds[h.Address] = h.ID;
                    continue;
                }

                foreach (var e in newEntities)
                    addressIds[e.Address] = e.ID;
            }

            return addressIds;
        }

        internal static SentEmail BuildSentEmailRow(
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
                UserID = user.ID
            };

            // Recipients are persisted in a separate phase via explicit FKs - see PersistUserAsync.
            // Do not populate row.Recipients here: with AutoDetectChangesEnabled = false EF6 will
            // not fix up the child FK reliably and parallel writers can deadlock on the unique
            // index over (sent_email_id, recipient_address_id).

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

        private sealed class LoadedUser
        {
            public Common.Entities.User User;
            public IReadOnlyList<GraphSentMessage> Messages;
        }

        private sealed class UserChunkResult
        {
            public Common.Entities.User User;
            public List<Candidate> Candidates;
            public List<Candidate> ToInsert = new List<Candidate>();
        }

        internal sealed class Candidate
        {
            public GraphSentMessage Message;
            public string FromAddress;
            public List<string> RecipientAddresses;
            public string GraphMessageId;
        }

        #endregion
    }
}
