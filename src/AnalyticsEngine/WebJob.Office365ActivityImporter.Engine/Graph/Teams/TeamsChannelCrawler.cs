using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq;
using Common.Entities.State;
using Microsoft.Extensions.Logging;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Walks a Team's channels, reading each one's new messages/reactions and storing the delta token
    /// that makes the next read incremental. Extracted from
    /// <c>TeamChannelExtensions.PopulateNewMessagesAndReactions</c> so the crawl can be tested with no
    /// Graph and no storage. See issue #377.
    /// </summary>
    public class TeamsChannelCrawler
    {
        /// <summary>
        /// A Team's crawl stops when this many channels in a row fail to read (<see cref="ChannelReadFailureKind.ChannelFailed"/>):
        /// one failing channel is that channel's problem, but three in a row is Graph being down, and waiting out a timeout on every
        /// remaining channel of every Team is what an outage would otherwise cost.
        /// </summary>
        public const int MaxConsecutiveChannelFailures = 3;

        private readonly IChannelMessagesSourceLoader _messagesSource;
        private readonly TeamsChannelBudgetProgress _budget;
        private readonly TeamsChannelFailureLedger _failures;
        private readonly TeamsChannelCrawlStats _stats;
        private readonly ILogger _logger;

        public TeamsChannelCrawler(IChannelMessagesSourceLoader messagesSource, TeamsChannelBudgetProgress budget = null,
            TeamsChannelFailureLedger failures = null, TeamsChannelCrawlStats stats = null, ILogger logger = null)
        {
            _messagesSource = messagesSource ?? throw new System.ArgumentNullException(nameof(messagesSource));
            _budget = budget;
            _failures = failures;
            _stats = stats ?? new TeamsChannelCrawlStats();
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        /// <summary>
        /// Sets the "Messages" prop on each channel by reading each channel's messages, then returns
        /// the delta tokens that may be committed after SQL persistence succeeds.
        /// </summary>
        /// <remarks>
        /// A channel that returns no delta token leaves the stored token untouched, so a read that
        /// couldn't produce one doesn't destroy the incremental position. Channels are crawled in
        /// source order by default, or in durable round-robin order when budgeting is enabled.
        /// A channel only this person's token can't read (<see cref="ChannelReadFailureKind.SkipChannel"/>) is
        /// skipped and the crawl carries on, and so is one whose read failed for a reason that isn't authentication or throttling
        /// (<see cref="ChannelReadFailureKind.ChannelFailed"/>): one bad channel must not stall the rest of its Team, as Graph's
        /// persistent <c>400 UnknownError</c> on a channel's delta did. That channel's delta token is left alone, its consecutive
        /// failures are counted, and once it has failed <see cref="TeamsChannelFailureLedger.MovePastAfterFailures"/> times the budget's
        /// cursor moves past it. Any other failure stops the Team's crawl and is rethrown for the caller to act on (see
        /// <see cref="ChannelReadFailureKind"/>), with the earlier channels' tokens still pending.
        /// </remarks>
        public async Task<List<TeamChannelDeltaTokenCommit>> PopulateNewMessagesAndReactions(
            List<ChannelWithReactions> channels,
            string teamId,
            List<TeamChannelDeltaTokenCommit> pendingDeltaTokenCommits = null)
        {
            pendingDeltaTokenCommits = pendingDeltaTokenCommits ?? new List<TeamChannelDeltaTokenCommit>();

            var selected = _budget == null ? channels : await _budget.SelectChannels(channels, teamId);
            _stats.ChannelsSelected = selected.Count;
            _stats.ChannelsDeferred = channels.Count - selected.Count;
            if (_failures != null)
            {
                await _failures.LoadAsync(teamId, channels.Select(c => c.Id));
            }

            var failedInARow = 0;
            try
            {
                foreach (var channel in selected)
                {
                    // Load stats. Throws ChannelMessagesReadException if the channel couldn't be read
                    TeamChannelDeltaTokenInfo channelDelta;
                    try
                    {
                        channelDelta = await _messagesSource.LoadMessagesAndReactions(channel, teamId);
                    }
                    catch (ChannelMessagesReadException ex) when (ex.Kind == ChannelReadFailureKind.SkipChannel)
                    {
                        // No delta token, so it is read again next cycle; the scheduling cursor still moves past it, or a
                        // budget of one would pick the same unreadable channel for ever.
                        failedInARow = 0;
                        _stats.ChannelsSkipped++;
                        _budget?.ReadSkipped(channel.Id);
                        continue;
                    }
                    catch (ChannelMessagesReadException ex) when (ex.Kind == ChannelReadFailureKind.ChannelFailed)
                    {
                        var consecutive = _failures?.RecordFailure(channel.Id) ?? 1;
                        _stats.RecordFailure(ex);
                        LogChannelReadFailed(teamId, channel.Id, ex, consecutive);
                        _budget?.ReadFailedButContinuing(channel.Id, consecutive);

                        if (++failedInARow >= MaxConsecutiveChannelFailures)
                        {
                            _stats.Stopped = true;
                            _budget?.ReadFailed();
                            throw;
                        }
                        continue;
                    }
                    catch (ChannelMessagesReadException ex)
                    {
                        _stats.Stopped = true;
                        _stats.RecordFailure(ex);
                        LogChannelReadFailed(teamId, channel.Id, ex, _failures?.ConsecutiveFailures(channel.Id) ?? 0);
                        _budget?.ReadFailed();
                        throw;
                    }

                    failedInARow = 0;
                    _failures?.RecordSuccess(channel.Id);
                    _stats.ChannelsRead++;
                    if (channel.DeltaTokenWasReset)
                    {
                        _stats.TokensReset++;
                    }
                    _budget?.ReadCompleted(channel.Id, channelDelta != null);

                    if (channelDelta != null)
                    {
                        _stats.TokensToCommit++;
                        pendingDeltaTokenCommits.Add(new TeamChannelDeltaTokenCommit(channel.Id, channelDelta));
                    }
                    else
                    {
                        _stats.TokensWithheld++;
                    }
                }
            }
            finally
            {
                if (_failures != null)
                {
                    await _failures.SaveAsync();
                }
            }

            _budget?.ReportCompleted();
            return pendingDeltaTokenCommits;
        }

        private void LogChannelReadFailed(string teamId, string channelId, ChannelMessagesReadException ex, int consecutive)
        {
            var inner = ex.InnerException ?? ex;
            var status = ChannelReadFailureRules.StatusCode(inner);
            var code = (inner as Microsoft.Graph.Models.ODataErrors.ODataError)?.Error?.Code;
            _logger.LogWarning("TeamsChannelReadFailed: teamId={TeamId}; channelId={ChannelId}; status={Status}; code={Code}; failure={Failure}; consecutive={Consecutive}; kind={Kind}.",
                teamId, channelId, status?.ToString() ?? "none", code ?? "none", FailureClassification.Describe(ex), consecutive, ex.Kind);
        }
    }

    /// <summary>
    /// A scheduling cursor, NOT a Graph checkpoint. Only channel delta tokens certify completed reads.
    /// Stops between channels so the budget itself never causes replay of additive statistics.
    /// </summary>
    public sealed class TeamsChannelBudgetProgress
    {
        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;
        private readonly int _maximum;
        private string _lastRead;
        private int _read;
        private string _teamKey;
        private int _deferred;
        private string _teamReference;

        public TeamsChannelBudgetProgress(IKeyValueStore store, int maximum, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            _maximum = maximum;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal async Task<List<ChannelWithReactions>> SelectChannels(List<ChannelWithReactions> channels, string teamId)
        {
            _teamKey = "budget|" + teamId;
            _teamReference = TeamReference(teamId);
            _read = 0;
            _lastRead = null;
            var after = await _store.GetStringAsync(_teamKey);
            var ordered = channels.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            var start = after == null ? 0 : ordered.FindIndex(c => StringComparer.Ordinal.Compare(c.Id, after) > 0);
            if (start < 0) start = 0;
            var count = Math.Min(_maximum, ordered.Count);
            var selected = new List<ChannelWithReactions>(count);
            for (int i = 0; i < count; i++) selected.Add(ordered[(start + i) % ordered.Count]);
            _deferred = ordered.Count - count;
            return selected;
        }

        internal void ReadCompleted(string channelId, bool completedDelta)
        {
            _lastRead = channelId;
            _read++;
            if (!completedDelta)
                _logger.LogWarning("TeamsChannelReadIncomplete: section=channelMessages; teamRef={TeamRef}; no completed delta; scheduling may continue but Graph checkpoint is unchanged. On retry, additive statistics may be counted again.", _teamReference);
        }

        /// <summary>
        /// A channel this authorisation can't read. Nothing was read from it, so nothing can be counted twice: scheduling
        /// moves past it and its Graph checkpoint is left as it was.
        /// </summary>
        internal void ReadSkipped(string channelId)
        {
            _lastRead = channelId;
            _read++;
            _logger.LogInformation("TeamsChannelReadSkipped: section=channelMessages; teamRef={TeamRef}; channel unreadable with this authorisation; scheduling continues past it and its Graph checkpoint is unchanged.", _teamReference);
        }

        internal void ReadFailed() => _logger.LogWarning("TeamsChannelGraphFailure: section=channelMessages; teamRef={TeamRef}; budget exhaustion is not the cause; failed read has no scheduling or Graph checkpoint.", _teamReference);

        /// <summary>
        /// A channel whose read failed but whose Team's crawl carries on. Its delta token is untouched, so nothing was certified
        /// and nothing can be counted twice. While it has failed fewer than <see cref="TeamsChannelFailureLedger.MovePastAfterFailures"/>
        /// times in a row the cursor stays put (a budget of one retries it, so a one-off failure costs a cycle); from then on the
        /// cursor moves past it, so a channel Graph persistently refuses can't hold the Team's other channels back.
        /// </summary>
        internal void ReadFailedButContinuing(string channelId, int consecutiveFailures)
        {
            if (consecutiveFailures < TeamsChannelFailureLedger.MovePastAfterFailures)
            {
                return;
            }

            _lastRead = channelId;
            _read++;
            _logger.LogWarning("TeamsChannelReadWithheld: section=channelMessages; teamRef={TeamRef}; consecutiveFailures={Consecutive}; scheduling moves past the channel and its Graph checkpoint is unchanged.",
                _teamReference, consecutiveFailures);
        }

        internal void ReportCompleted()
        {
            if (_deferred > 0)
                _logger.LogWarning("TeamsChannelBudgetExhausted: section=channelMessages; teamRef={TeamRef}; channelsRead={Read}; channelsDeferred={Deferred}; limit={Limit}. Deferred channels resume after SQL persistence.",
                    _teamReference, _read, _deferred, _maximum);
        }

        /// <summary>Called only after SQL and completed channel-token writes have succeeded.</summary>
        public async Task CommitAfterPersistence()
        {
            if (_lastRead != null)
            {
                await _store.SetStringAsync(_teamKey, _lastRead);
                _logger.LogInformation("TeamsChannelBudgetProgressSaved: section=channelMessages; channelsRead={Read}; SQL and completed Graph checkpoints persisted.", _read);
            }
        }

        private static string TeamReference(string teamId)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(teamId))).Replace("-", "").Substring(0, 16);
        }
    }
}
