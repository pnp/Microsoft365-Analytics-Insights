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
        // Safety cap for delta/replies paging at 200k-user scale: a single channel should
        // never legitimately return more than this many messages in one delta window, but a
        // misbehaving nextLink could otherwise loop forever or fill memory.
        private const int MAX_MESSAGES_PER_CHANNEL = 100_000;
        private const int MAX_REPLIES_PER_MESSAGE = 10_000;

        private readonly GraphServiceClient _client;
        private readonly CacheConnectionManager _cacheConnectionManager;
        private readonly ILogger _logger;

        public ChannelMessagesLoader(GraphServiceClient client, CacheConnectionManager cacheConnectionManager, ILogger logger)
        {
            this._client = client;
            this._cacheConnectionManager = cacheConnectionManager;
            this._logger = logger;
        }

        /// <summary>
        /// Load message & replies for a channel. Uses cached delta code if found for message loading
        /// </summary>
        public async Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> LoadTeamMessagesAndReplies(ChannelWithReactions channel, string teamId)
        {
            if (string.IsNullOrEmpty(teamId)) throw new ArgumentException($"'{nameof(teamId)}' cannot be null or empty", nameof(teamId));

            var channelDeltaInfo = await _cacheConnectionManager.GetTeamChannelDeltaTokenInfo(teamId, channel.Id);

            // v5+ removed QueryOption / $deltatoken support on the typed Delta request builder. To
            // keep using the SDK serialiser for ChatMessage we construct the full URL ourselves
            // (with $deltatoken appended when we have one) and instantiate a DeltaRequestBuilder
            // against the existing RequestAdapter, then walk pages via PageIterator.
            var baseUrl = $"{_client.RequestAdapter.BaseUrl}/teams/{teamId}/channels/{channel.Id}/messages/delta";
            var initialUrl = channelDeltaInfo != null
                ? $"{baseUrl}?$deltatoken={channelDeltaInfo.Token}"
                : baseUrl;
            var deltaBuilder = new DeltaRequestBuilder(initialUrl, _client.RequestAdapter);

            var rootMsgs = new List<ChatMessage>();
            TeamsRedisManager.TeamChannelDeltaTokenInfo newDelta = null;

            DeltaGetResponse firstPage = null;
            try
            {
                firstPage = await deltaBuilder.GetAsDeltaGetResponseAsync();
            }
            catch (ODataError ex)
            {
                if (ex.Error?.Code == "BadRequest" && channelDeltaInfo != null)
                {
                    await _cacheConnectionManager.RemoveTeamChannelDeltaToken(teamId, channel.Id, _logger);
                    _logger.LogError(ex, $"Got bad request using delta token for messages. Removing from cache & will try full read next time.");
                }
                else throw;
            }

            if (firstPage != null)
            {
                int loaded = 0;
                var iterator = PageIterator<ChatMessage, DeltaGetResponse>
                    .CreatePageIterator(_client, firstPage, msg =>
                    {
                        rootMsgs.Add(msg);
                        loaded++;
                        return loaded < MAX_MESSAGES_PER_CHANNEL;
                    });

                await iterator.IterateAsync();

                if (iterator.State == PagingState.Paused)
                {
                    _logger.LogWarning($"Channel '{channel.DisplayName}' on Team '{teamId}': hit MAX_MESSAGES_PER_CHANNEL ({MAX_MESSAGES_PER_CHANNEL:N0}). Returning partial set of {rootMsgs.Count:N0} root messages.");
                }

                if (!string.IsNullOrEmpty(iterator.Deltalink))
                {
                    newDelta = new TeamsRedisManager.TeamChannelDeltaTokenInfo
                    {
                        Token = StringUtils.ExtractCodeFromGraphUrl(iterator.Deltalink),
                        LastUpdated = DateTime.Now
                    };
                }
            }

            // Load all replies for each root message
            foreach (var rootMsg in rootMsgs)
            {
                rootMsg.Replies = await LoadAllRepliesForMessage(teamId, channel.Id, rootMsg.Id);
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
        internal async Task<List<ChatMessage>> LoadAllRepliesForMessage(string teamId, string channelId, string messageId)
        {
            var allReplies = new List<ChatMessage>();

            var firstPage = await _client.Teams[teamId].Channels[channelId].Messages[messageId].Replies.GetAsync();
            if (firstPage == null) return allReplies;

            int loaded = 0;
            var iterator = PageIterator<ChatMessage, ChatMessageCollectionResponse>
                .CreatePageIterator(_client, firstPage, reply =>
                {
                    allReplies.Add(reply);
                    loaded++;
                    return loaded < MAX_REPLIES_PER_MESSAGE;
                });

            await iterator.IterateAsync();

            if (iterator.State == PagingState.Paused)
            {
                _logger.LogWarning($"Channel {channelId} msg {messageId}: hit MAX_REPLIES_PER_MESSAGE ({MAX_REPLIES_PER_MESSAGE:N0}). Returning partial reply list of {allReplies.Count:N0}.");
            }

            return allReplies;
        }

        public class ChannelChatInfo
        {
            public List<ChatMessage> NewMessages { get; set; }
            public List<ChatMessageReaction> NewReactions { get; set; }

            public TeamsRedisManager.TeamChannelDeltaTokenInfo DeltaInfo { get; set; }
        }
    }
}
