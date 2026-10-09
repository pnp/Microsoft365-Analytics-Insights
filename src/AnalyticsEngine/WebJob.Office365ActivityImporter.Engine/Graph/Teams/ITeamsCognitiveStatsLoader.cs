using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public interface ITeamsCognitiveStatsLoader
    {
        Task<List<MessageCognitiveStats>> LoadStats(ChannelWithReactions channel, ILogger logger);
    }

    /// <summary>
    /// Owned by the importer, shared across its parallel Team saves. The thread-safe client is
    /// built once, lazily; message caching and key-auth/RBAC fallback remain in the existing helpers.
    /// </summary>
    public sealed class TeamsCognitiveStatsLoader : ITeamsCognitiveStatsLoader
    {
        private readonly AppConfig _config;
        private readonly Func<CognitiveServicesClient> _clientFactory;
        private readonly object _clientLock = new object();
        private CognitiveServicesClient _client;
        private bool _clientBuilt;

        public TeamsCognitiveStatsLoader(AppConfig config, ILogger logger, Func<CognitiveServicesClient> clientFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            _clientFactory = clientFactory ?? (() => config.CreateCognitiveServicesClient(logger));
        }

        private CognitiveServicesClient GetOrBuildClient()
        {
            lock (_clientLock)
            {
                if (!_clientBuilt)
                {
                    _client = _clientFactory();
                    _clientBuilt = true;
                }
                return _client;
            }
        }

        public async Task<List<MessageCognitiveStats>> LoadStats(ChannelWithReactions channel, ILogger logger)
        {
            var allStatsAllDays = new List<MessageCognitiveStats>();
            var msgDates = channel.Messages.GetUniqueDates();

            if (!_config.IsValidCognitiveConfig)
            {
                logger.LogWarning($"Cognitive config not valid. Cannot load cognitive stats for channel {channel.DisplayName} ({channel.Id}). Adding basic stats with no cognitive insights.");
                foreach (var uniqueMsgDate in msgDates)
                {
                    var msgsForDate = channel.Messages.GetByDate(uniqueMsgDate);
                    allStatsAllDays.Add(new MessageCognitiveStats(channel, uniqueMsgDate) { ChatsCount = msgsForDate.Count });
                }
                return allStatsAllDays;
            }

            var client = GetOrBuildClient();
            if (client == null)
            {
                logger.LogWarning($"Could not build cognitive client for channel {channel.DisplayName} ({channel.Id}). Adding basic stats with no cognitive insights.");
                foreach (var uniqueMsgDate in msgDates)
                {
                    var msgsForDate = channel.Messages.GetByDate(uniqueMsgDate);
                    allStatsAllDays.Add(new MessageCognitiveStats(channel, uniqueMsgDate) { ChatsCount = msgsForDate.Count });
                }
                return allStatsAllDays;
            }

            foreach (var uniqueMsgDate in msgDates)
            {
                var msgsForDate = channel.Messages.GetByDate(uniqueMsgDate);
                allStatsAllDays.Add(await msgsForDate.LoadSameDayCognitiveDataStats(client, logger, channel));
            }
            return allStatsAllDays;
        }
    }
}
