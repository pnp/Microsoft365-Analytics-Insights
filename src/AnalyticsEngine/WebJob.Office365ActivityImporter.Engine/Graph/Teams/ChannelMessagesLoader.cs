using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Teams.Item.Channels.Item.Messages.Delta;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Teams Channel messages loader
    /// </summary>
    public class ChannelMessagesLoader
    {
        private readonly IChannelMessagesPageReader _pageReader;
        private readonly ITeamChannelDeltaTokenStore _deltaTokenStore;
        private readonly ILogger _logger;

        /// <summary>
        /// Production constructor: delta tokens are cached in Redis.
        /// </summary>
        public ChannelMessagesLoader(GraphServiceClient client, CacheConnectionManager cacheConnectionManager, ILogger logger)
            : this(client, new RedisTeamChannelDeltaTokenStore(cacheConnectionManager, logger), logger) { }

        /// <summary>
        /// Constructor taking the delta-token store as a port, so the token handling can be exercised
        /// without Redis. See issue #377.
        /// </summary>
        public ChannelMessagesLoader(GraphServiceClient client, ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger)
            : this(new GraphChannelMessagesPageReader(client), deltaTokenStore, logger) { }

        internal ChannelMessagesLoader(IChannelMessagesPageReader pageReader, ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger)
        {
            this._pageReader = pageReader ?? throw new ArgumentNullException(nameof(pageReader));
            this._deltaTokenStore = deltaTokenStore ?? throw new ArgumentNullException(nameof(deltaTokenStore));
            this._logger = logger;
        }

        /// <summary>
        /// Load message & replies for a channel. Uses cached delta code if found for message loading
        /// </summary>
        public async Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> LoadTeamMessagesAndReplies(ChannelWithReactions channel, string teamId)
        {
            if (string.IsNullOrEmpty(teamId)) throw new ArgumentException($"'{nameof(teamId)}' cannot be null or empty", nameof(teamId));

            var channelDeltaInfo = await _deltaTokenStore.GetDeltaToken(teamId, channel.Id);

            var rootMsgs = new List<ChatMessage>();
            TeamsRedisManager.TeamChannelDeltaTokenInfo newDelta = null;
            var channelReadComplete = true;

            ChannelRootMessagesPageResult rootMessagesResult = null;
            try
            {
                rootMessagesResult = await _pageReader.LoadRootMessages(teamId, channel.Id, channelDeltaInfo);
            }
            catch (ODataError ex)
            {
                if (ex.Error?.Code == "BadRequest" && channelDeltaInfo != null)
                {
                    await _deltaTokenStore.RemoveDeltaToken(teamId, channel.Id);
                    _logger.LogError(ex, $"Got bad request using delta token for messages. Removing from cache & will try full read next time.");
                }
                else throw;
            }

            if (rootMessagesResult != null)
            {
                rootMsgs = rootMessagesResult.Messages;
                channelReadComplete = rootMessagesResult.Completed;

                if (!rootMessagesResult.Completed)
                {
                    _logger.LogWarning($"Channel '{channel.DisplayName}' on Team '{teamId}': hit MAX_MESSAGES_PER_CHANNEL ({TeamsCrawlPagingPolicy.MaxMessagesPerChannel:N0}). Returning partial set of {rootMsgs.Count:N0} root messages.");
                }

                if (!string.IsNullOrEmpty(rootMessagesResult.Deltalink))
                {
                    newDelta = new TeamsRedisManager.TeamChannelDeltaTokenInfo
                    {
                        Token = StringUtils.ExtractCodeFromGraphUrl(rootMessagesResult.Deltalink),
                        LastUpdated = DateTime.Now
                    };
                }
            }

            // Load all replies for each root message
            foreach (var rootMsg in rootMsgs)
            {
                var repliesResult = await LoadAllRepliesForMessage(teamId, channel.Id, rootMsg.Id);
                rootMsg.Replies = repliesResult.Replies;

                if (!repliesResult.Completed)
                {
                    channelReadComplete = false;
                }
            }

            if (!channelReadComplete && newDelta != null)
            {
                // Deliberately leave the stored token untouched when a cap stopped paging. The next
                // cycle re-reads from the previous token and may double-count additive Teams stats, but
                // a visible duplicate is safer than silently losing messages or replies.
                _logger.LogWarning($"Withholding Teams channel delta token for Team '{teamId}', channel '{channel.Id}' because at least one message or reply page hit a paging cap. The next import cycle will re-read from the previous token; additive Teams stats may be double-counted.");
                newDelta = null;
            }

            if (channelDeltaInfo != null)
            {
                _logger.LogInformation($"Loaded channel messages with last delta token for channel '{channel.DisplayName}' on Team '{teamId}'...");
            }
            else
            {
                _logger.LogInformation($"Loaded channel messages (all) for channel '{channel.DisplayName}' on Team '{teamId}'...");
            }

            // Set new msg & reaction data on channel
            channel.CalculateAndSetNewMessagesAndReactions(rootMsgs, channelDeltaInfo?.LastUpdated, _logger);

            return newDelta;
        }

        /// <summary>
        /// Walk all reply pages for a single message via <see cref="PageIterator{TEntity, TCollectionPage}"/>.
        /// </summary>
        internal async Task<ChannelRepliesPageResult> LoadAllRepliesForMessage(string teamId, string channelId, string messageId)
        {
            var repliesResult = await _pageReader.LoadReplies(teamId, channelId, messageId);
            if (!repliesResult.Completed)
            {
                _logger.LogWarning($"Channel {channelId} msg {messageId}: hit MAX_REPLIES_PER_MESSAGE ({TeamsCrawlPagingPolicy.MaxRepliesPerMessage:N0}). Returning partial reply list of {repliesResult.Replies.Count:N0}.");
            }

            return repliesResult;
        }

        public class ChannelChatInfo
        {
            public List<ChatMessage> NewMessages { get; set; }
            public List<ChatMessageReaction> NewReactions { get; set; }

            public TeamsRedisManager.TeamChannelDeltaTokenInfo DeltaInfo { get; set; }
        }
    }

    internal interface IChannelMessagesPageReader
    {
        Task<ChannelRootMessagesPageResult> LoadRootMessages(
            string teamId,
            string channelId,
            TeamsRedisManager.TeamChannelDeltaTokenInfo channelDeltaInfo);

        Task<ChannelRepliesPageResult> LoadReplies(string teamId, string channelId, string messageId);
    }

    internal class ChannelRootMessagesPageResult
    {
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        public string Deltalink { get; set; }

        public bool Completed { get; set; }
    }

    internal class ChannelRepliesPageResult
    {
        public List<ChatMessage> Replies { get; set; } = new List<ChatMessage>();

        public bool Completed { get; set; }
    }

    internal class GraphChannelMessagesPageReader : IChannelMessagesPageReader
    {
        private readonly GraphServiceClient _client;

        public GraphChannelMessagesPageReader(GraphServiceClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<ChannelRootMessagesPageResult> LoadRootMessages(
            string teamId,
            string channelId,
            TeamsRedisManager.TeamChannelDeltaTokenInfo channelDeltaInfo)
        {
            // v5+ removed QueryOption / $deltatoken support on the typed Delta request builder. To
            // keep using the SDK serialiser for ChatMessage we construct the full URL ourselves
            // (with $deltatoken appended when we have one) and instantiate a DeltaRequestBuilder
            // against the existing RequestAdapter, then walk pages via PageIterator.
            var baseUrl = $"{_client.RequestAdapter.BaseUrl}/teams/{teamId}/channels/{channelId}/messages/delta";
            var initialUrl = channelDeltaInfo != null
                ? $"{baseUrl}?$deltatoken={channelDeltaInfo.Token}"
                : baseUrl;
            var deltaBuilder = new DeltaRequestBuilder(initialUrl, _client.RequestAdapter);

            var firstPage = await deltaBuilder.GetAsDeltaGetResponseAsync();
            if (firstPage == null) return null;

            var rootMsgs = new List<ChatMessage>();
            int loaded = 0;
            var iterator = PageIterator<ChatMessage, DeltaGetResponse>
                .CreatePageIterator(_client, firstPage, msg =>
                {
                    rootMsgs.Add(msg);
                    loaded++;
                    return TeamsCrawlPagingPolicy.ShouldContinuePaging(loaded, TeamsCrawlPagingPolicy.MaxMessagesPerChannel);
                });

            await iterator.IterateAsync();

            return new ChannelRootMessagesPageResult
            {
                Messages = rootMsgs,
                Deltalink = iterator.Deltalink,
                Completed = iterator.State != PagingState.Paused
            };
        }

        public async Task<ChannelRepliesPageResult> LoadReplies(string teamId, string channelId, string messageId)
        {
            var allReplies = new List<ChatMessage>();

            var firstPage = await _client.Teams[teamId].Channels[channelId].Messages[messageId].Replies.GetAsync();
            if (firstPage == null)
            {
                return new ChannelRepliesPageResult { Replies = allReplies, Completed = true };
            }

            int loaded = 0;
            var iterator = PageIterator<ChatMessage, ChatMessageCollectionResponse>
                .CreatePageIterator(_client, firstPage, reply =>
                {
                    allReplies.Add(reply);
                    loaded++;
                    return TeamsCrawlPagingPolicy.ShouldContinuePaging(loaded, TeamsCrawlPagingPolicy.MaxRepliesPerMessage);
                });

            await iterator.IterateAsync();

            return new ChannelRepliesPageResult
            {
                Replies = allReplies,
                Completed = iterator.State != PagingState.Paused
            };
        }
    }
}
