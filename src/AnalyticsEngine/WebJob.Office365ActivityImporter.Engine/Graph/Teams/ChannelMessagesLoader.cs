using Common.DataUtils;
using Common.Entities.Redis;
using Common.Entities.Redis.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Teams Channel messages loader
    /// </summary>
    public class ChannelMessagesLoader
    {
        private readonly GraphServiceClient _client;
        private readonly CacheConnectionManager _cacheConnectionManager;
        private readonly ILogger _telemetry;

        public ChannelMessagesLoader(GraphServiceClient client, CacheConnectionManager cacheConnectionManager, ILogger telemetry)
        {
            this._client = client;
            this._cacheConnectionManager = cacheConnectionManager;
            this._telemetry = telemetry;
        }

        /// <summary>
        /// Load message & replies for a channel. Uses cached delta code if found for message loading
        /// </summary>
        public async Task<TeamsRedisManager.TeamChannelDeltaTokenInfo> LoadTeamMessagesAndReplies(ChannelWithReactions channel, string teamId)
        {
            if (string.IsNullOrEmpty(teamId)) throw new ArgumentException($"'{nameof(teamId)}' cannot be null or empty", nameof(teamId));

            // Do we have a delta token in redis?
            var delta = await _cacheConnectionManager.GetTeamChannelDeltaTokenInfo(teamId, channel.Id);

            // Get channel root msgs
            var req = _client.Teams[teamId].Channels[channel.Id].Messages.Delta().Request();
            var channelDeltaInfo = await _cacheConnectionManager.GetTeamChannelDeltaTokenInfo(teamId, channel.Id);

            // Add delta code from last time if we have one
            if (channelDeltaInfo != null)
            {
                req.QueryOptions.Add(new QueryOption("$deltatoken", channelDeltaInfo.Token));
            }

            // Load messages recursively & save delta token to redis for next time
            List<ChatMessage> rootMsgs = null;
            TeamsRedisManager.TeamChannelDeltaTokenInfo newDelta = null;
            try
            {
                rootMsgs = await LoadRootChannelMsgsFromGraphRecursive(req,
                    (deltaLink) =>
                    {
                        // Remember delta for return object
                        var lastPageDelta = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                        newDelta = new TeamsRedisManager.TeamChannelDeltaTokenInfo
                        {
                            Token = lastPageDelta,
                            LastUpdated = DateTime.Now
                        };

                        return Task.CompletedTask;
                    }, 0);
            }
            catch (ServiceException ex)
            {
                if (ex.Error.Code == "BadRequest" && channelDeltaInfo != null)
                {
                    await _cacheConnectionManager.RemoveTeamChannelDeltaToken(teamId, channel.Id, _telemetry);
                    _telemetry.LogError(ex, $"Got bad request using delta token for messages. Removing from cache & will try full read next time.");
                    rootMsgs = new List<ChatMessage>();
                }
                else throw;
            }

            // Load all replies
            foreach (var rootMsg in rootMsgs)
            {
                // Parse replies
                var msgReplies = await LoadMsgRepliesRecursive(_client.Teams[teamId].Channels[channel.Id].Messages[rootMsg.Id].Replies.Request());
                rootMsg.Replies = new ChatMessageRepliesCollectionPage();
                foreach (var r in msgReplies)
                {
                    rootMsg.Replies.Add(r);
                }
            }


            if (channelDeltaInfo != null)
            {
                _telemetry.LogInformation($"Loaded channel messages with last delta token for channel '{channel.DisplayName}' on Team '{teamId}'...");
            }
            else
            {
                _telemetry.LogInformation($"Loaded channel messages (all) for channel '{channel.DisplayName}' on Team '{teamId}'...");
            }

            // Set new msg & reaction data on channel
            channel.CalculateAndSetNewMessagesAndReactions(rootMsgs, channelDeltaInfo?.LastUpdated, _telemetry);

            return newDelta;
        }


        internal async Task<List<ChatMessage>> LoadMsgRepliesRecursive(IChatMessageRepliesCollectionRequest replyRequest)
        {
            var allReplies = new List<ChatMessage>();
            var replies = await replyRequest.GetAsync();

            foreach (var reply in replies)
            {
                allReplies.Add(reply);
            }
            if (replies.NextPageRequest != null)
            {
                var nextPageReplies = await LoadMsgRepliesRecursive(replies.NextPageRequest);
                nextPageReplies.AddRange(nextPageReplies);
            }

            return allReplies;
        }

        internal async Task<List<ChatMessage>> LoadRootChannelMsgsFromGraphRecursive(IChatMessageDeltaRequest msgsRequest, Func<string, Task> deltaTokenFunc, int pageNumber)
        {
            _telemetry.LogInformation($"Loading messages page {pageNumber}...");

            // https://docs.microsoft.com/en-us/graph/api/chatmessage-delta
            var channelRootMsgs = await msgsRequest.GetAsync();

            // Load more if there is any.
            // For some reason there can be a "next page" without any actual results, which if called, will fail. Don't bother if there's no results.
            if (channelRootMsgs.Count > 0 && channelRootMsgs.NextPageRequest != null)
            {
                var nextPageMsgs = await LoadRootChannelMsgsFromGraphRecursive(channelRootMsgs.NextPageRequest, deltaTokenFunc, ++pageNumber);

                // No AddRange
                foreach (var nextPageMsg in nextPageMsgs)
                {
                    channelRootMsgs.Add(nextPageMsg);
                }
            }
            else
            {
                const string DELTA_LINK = "@odata.deltaLink";

                // Last page of results. Do we have a delta link?
                if (channelRootMsgs.AdditionalData.ContainsKey(DELTA_LINK))
                {
                    var deltaLink = channelRootMsgs.AdditionalData[DELTA_LINK];
                    if (deltaLink != null && deltaTokenFunc != null)
                    {
                        await deltaTokenFunc(deltaLink.ToString());
                    }
                }

            }
            return channelRootMsgs.ToList();
        }

        public class ChannelChatInfo
        {
            public List<ChatMessage> NewMessages { get; set; }
            public List<ChatMessageReaction> NewReactions { get; set; }

            public TeamsRedisManager.TeamChannelDeltaTokenInfo DeltaInfo { get; set; }
        }
    }
}
