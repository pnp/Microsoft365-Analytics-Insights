using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Base Team + members/users. Reads & saves data for a single Team. 
    /// </summary>
	public class O365Team
    {
        #region Constructors

        public O365Team()
        {
            this.Users = new List<BaseUser>();
            this.OwnerUserAccounts = new List<Microsoft.Graph.Models.User>();
        }

        public O365Team(Team team) : this()
        {
            if (team == null)
            {
                throw new ArgumentNullException(nameof(team));
            }

            // Copy over all props
            this.DisplayName = team.DisplayName;

            if (team.Channels != null)
                foreach (var c in team.Channels)
                {
                    this.Channels.Add(new ChannelWithReactions(c));
                }
            this.Id = team.Id;
        }


        #endregion

        #region Properties

        public List<ChannelWithReactions> Channels { get; set; } = new List<ChannelWithReactions>();

        public string DisplayName { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Graph users in the Team. 
        /// </summary>
        public List<BaseUser> Users { get; set; }
        public List<Microsoft.Graph.Models.User> OwnerUserAccounts { get; set; }

        public List<UserReaction> AllReactions { get; set; } = new List<UserReaction>();

        public bool HasRefreshToken { get; set; }
        public DateTime? LastRefreshed { get; set; }
        public List<TeamChannelDeltaTokenCommit> PendingChannelDeltaTokenCommits { get; set; } = new List<TeamChannelDeltaTokenCommit>();
        public TeamsChannelBudgetProgress ChannelBudgetProgress { get; set; }

        /// <summary>What this Team's channel crawl read, skipped and failed; <c>null</c> when no channel messages were crawled.</summary>
        public TeamsChannelCrawlStats ChannelCrawlStats { get; set; }

        #endregion

        public Task<TeamDefinition> SaveToSQL(TeamsAndCallsDBLookupManager lookupManager, AppConfig appConfig, ILogger logger)
            => SaveToSQL(lookupManager, appConfig, logger, null);

        public async Task<TeamDefinition> SaveToSQL(TeamsAndCallsDBLookupManager lookupManager, AppConfig appConfig, ILogger logger,
            ITeamsCognitiveStatsLoader cognitiveStatsLoader)
        {
            if (lookupManager is null) throw new ArgumentNullException(nameof(lookupManager));
            if (logger is null) throw new ArgumentNullException(nameof(logger));

            using (var store = new SqlTeamsPersistenceStore(lookupManager))
            {
                return await SaveToSQLCore(store, cognitiveStatsLoader ?? new TeamsCognitiveStatsLoader(new AppConfig(), logger),
                    () => new TeamTokenManager(this, appConfig, logger).ChannelDeltaTokenStore, logger);
            }
        }

        /// <summary>
        /// The same staged save and checkpoint ordering, without constructing SQL, AI or state clients.
        /// </summary>
        public Task<TeamDefinition> SaveToSQL(ITeamsPersistenceStore store, ITeamsCognitiveStatsLoader cognitiveStatsLoader,
            ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger)
            => SaveToSQLCore(store, cognitiveStatsLoader, () => deltaTokenStore, logger);

        private async Task<TeamDefinition> SaveToSQLCore(ITeamsPersistenceStore store, ITeamsCognitiveStatsLoader cognitiveStatsLoader,
            Func<ITeamChannelDeltaTokenStore> deltaTokenStoreFactory, ILogger logger)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (cognitiveStatsLoader == null) throw new ArgumentNullException(nameof(cognitiveStatsLoader));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            logger.LogInformation($"Saving Team '{this.DisplayName}' to SQL...");

            var dbTeam = await store.GetOrCreateTeam(this);
            await store.SaveOwners(this.OwnerUserAccounts, dbTeam);
            await store.SaveMembers(this.Users, this);
            // Resolve state at the original point: its "No Storage" warning must follow member saves.
            var deltaTokenStore = deltaTokenStoreFactory();
            foreach (var channel in this.Channels)
            {
                await store.SaveChannel(channel, dbTeam);
            }

            var channelMessageStats = await this.Channels.GetMessagesStats(logger, cognitiveStatsLoader);
            foreach (var channelStat in channelMessageStats)
            {
                await store.SaveStats(channelStat, dbTeam);
            }
            await store.SaveReactions(AllReactions);

            try
            {
                await store.SaveChanges(this, dbTeam);
            }
            catch (System.Data.Entity.Infrastructure.DbUpdateException ex)
            {
                logger.LogError(ex, $"Got SQL exception saving Team: {ex.Message}. Will try again next cycle.");
                return null;
            }

            if (this.PendingChannelDeltaTokenCommits.Count > 0 && deltaTokenStore != null)
            {
                await TeamChannelDeltaTokenCommitter.CommitPendingTokens(deltaTokenStore, this.Id, this.PendingChannelDeltaTokenCommits);
            }
            if (this.ChannelBudgetProgress != null)
            {
                await this.ChannelBudgetProgress.CommitAfterPersistence();
            }

            return dbTeam;
        }

        /// <summary>
        /// Loads all the Team child data, plus messages from the last channel log in the DB
        /// </summary>
        public static async Task<O365Team> LoadTeamFull(Group parentGroup, TeamsLoadContext context, ILogger logger, AppConfig appConfig, AnalyticsEntitiesContext db)
        {
            var teamId = parentGroup.Id;
            // Get Team details from Graph and convert to our own class
            var team = await context.GraphClient.Teams[teamId].GetAsync();
            var fullTeam = new O365Team(team);

            // Populate new Teams definition with graph data
            var parentGroupFull = await context.GraphClient.Groups[teamId].GetAsync(rc =>
            {
                rc.QueryParameters.Expand = new[] { "Owners" };
            });

            // Add owners. Owners outside UserGroupsFilter are skipped before their Graph lookup, so they are never stored.
            foreach (var groupOwner in parentGroupFull.Owners)
            {
                if (!context.UserScope.IsInScope(groupOwner.Id))
                {
                    continue;
                }

                // Null when Entra ID can't find the owner - a deleted account, or an app that owns the group. That used to
                // throw, abandoning every Team left in this chunk.
                var graphUser = await context.UserCache.GetUserOrNullIfNotFound(groupOwner.Id);
                if (graphUser != null)
                {
                    fullTeam.OwnerUserAccounts.Add(graphUser);
                }
                else
                {
                    logger.LogInformation($"Couldn't find owner with user ID '{groupOwner.Id}' for Team {fullTeam.DisplayName}");
                }
            }

#if DEBUG
            Console.WriteLine($"\nReading team '{parentGroup.DisplayName}':");
#endif
            var members = await LoadAllGroupMembers(context.GraphClient, parentGroup.Id, logger);

            var membersNotFound = 0;
            foreach (var member in members)
            {
                // Membership of people outside UserGroupsFilter is not recorded.
                if (!context.UserScope.IsInScope(member.Id))
                {
                    continue;
                }

                var userPrincipalName = await FindKnownUserPrincipalName(member.Id, context, db);
                if (string.IsNullOrEmpty(userPrincipalName))
                {
                    // Load direct from Graph
                    var userInfo = await context.UserCache.GetUserOrNullIfNotFound(member.Id);
                    userPrincipalName = userInfo?.UserPrincipalName;
                }

                if (!string.IsNullOrEmpty(userPrincipalName))
                {
                    fullTeam.Users.Add(new BaseUser { UserPrincipalName = userPrincipalName });
                }
                else
                {
                    membersNotFound++;
                }
            }
            if (membersNotFound > 0)
            {
                logger.LogInformation($"Team '{fullTeam.DisplayName}': {membersNotFound:N0} member(s) weren't found in Entra ID (deleted, or not a user account), so their membership isn't recorded.");
            }

            // Channels and tabs:
            var channelsLoaded = await context.GraphClient.Teams[teamId].Channels.GetAsync();
            if (channelsLoaded?.Value != null)
            {
                foreach (var channel in channelsLoaded.Value)
                    fullTeam.Channels.Add(new ChannelWithReactions(channel));
            }


            var teamTokenManager = new TeamTokenManager(fullTeam, appConfig, logger);

            // Load tabs in each channel
            foreach (var channel in fullTeam.Channels)
            {
                var dbChannel = await db.TeamChannels.Where(c => c.GraphID == channel.Id).SingleOrDefaultAsync();
                if (dbChannel == null && teamTokenManager.ChannelDeltaTokenStore != null)
                {
                    // Clear delta cache if new channel in DB. Mainly for debug reasons but also if there's no channel, we need to make sure we ignore any delta code (just in case)
                    await teamTokenManager.ChannelDeltaTokenStore.RemoveDeltaToken(fullTeam.Id, channel.Id);
                }
                var tabsPage = await context.GraphClient.Teams[teamId].Channels[channel.Id].Tabs.GetAsync(rc =>
                {
                    rc.QueryParameters.Expand = new[] { "teamsApp" };
                });
                channel.Tabs = tabsPage?.Value ?? new List<TeamsTab>();
            }

            // Get token for Team
            var teamRefreshOAuthToken = await teamTokenManager.GetRefreshToken(logger);

            // Update access-token stats for Team
            if (teamRefreshOAuthToken == null)
            {
                // Still authorised when only this cycle's refresh failed (Entra ID unreachable, throttling...): the
                // stored token was kept, and the Team's status must say so.
                fullTeam.HasRefreshToken = teamTokenManager.Status == TeamTokenStatus.RefreshFailed;
            }
            else
            {
                // We have a token. Load channel msgs/replies/reactions
                var pendingDeltaTokenCommits = new List<TeamChannelDeltaTokenCommit>();
                var channelState = Common.Entities.State.StateStore.TryOpen(appConfig, Common.Entities.State.StatePartitions.TeamsChannels, logger);
                if (appConfig.TeamsMaxChannelsPerTeamPerCycle > 0)
                {
                    fullTeam.ChannelBudgetProgress = new TeamsChannelBudgetProgress(channelState, appConfig.TeamsMaxChannelsPerTeamPerCycle, logger);
                }
                var failureLedger = channelState == null ? null : new TeamsChannelFailureLedger(channelState, logger);
                fullTeam.ChannelCrawlStats = new TeamsChannelCrawlStats();
                try
                {
                    fullTeam.PendingChannelDeltaTokenCommits = await fullTeam.Channels.PopulateNewMessagesAndReactions(team, teamRefreshOAuthToken,
                        teamTokenManager.ChannelDeltaTokenStore, logger, pendingDeltaTokenCommits, fullTeam.ChannelBudgetProgress,
                        teamTokenManager.RedeemRefreshTokenAsync, failureLedger, fullTeam.ChannelCrawlStats);
                    fullTeam.HasRefreshToken = true;

                    // Refreshed means messages were read. A Team whose every channel failed has still had its token used and kept,
                    // but nothing was refreshed - and stamping it would hide that from the Teams explorer.
                    fullTeam.LastRefreshed = CrawlRefreshedTheTeam(fullTeam.ChannelCrawlStats) ? DateTime.Now : (DateTime?)null;
                }
                catch (ChannelMessagesReadException ex)
                {
                    // Channels read before the failure are still saved, and their delta tokens committed after SQL.
                    fullTeam.PendingChannelDeltaTokenCommits = pendingDeltaTokenCommits;
                    await RecordChannelReadFailure(fullTeam, ex, teamTokenManager, logger);
                }
            }

            // UserGroupsFilter: drop messages and reactions by people outside the scope before anything reads them,
            // so their text never reaches the channel statistics (or Cognitive Services) and their reactions are
            // never resolved or stored. The delta tokens are unaffected - those messages were still read.
            if (context.UserScope.IsFiltered)
            {
                var removed = 0;
                foreach (var channel in fullTeam.Channels)
                {
                    removed += ChannelMessageScopeRules.RestrictToUserScope(channel, context.UserScope);
                }
                if (removed > 0)
                {
                    logger.LogInformation($"Team '{fullTeam.DisplayName}': left out {removed:N0} channel message(s) and reaction(s) by people outside UserGroupsFilter.");
                }
            }

            // Load reactions + users from messages found
            await fullTeam.BuildAllReactionsFromMessages(context);

            return fullTeam;
        }

        /// <summary>
        /// What a channel read that stopped the Team's crawl means for its stored authorisation. Only evidence that the
        /// token can't read the Team (<see cref="ChannelReadFailureKind.TokenRejected"/>) deletes it; anything else - a
        /// throttled or failed request, an outage - used to delete it too, and an administrator had to authorise the Team
        /// again. Takes the Team's <see cref="TeamTokenManager"/> rather than the token store, so the delete is always the
        /// conditional one: an authorisation made while the crawl ran is never removed on evidence about an older token.
        /// </summary>
        internal static async Task RecordChannelReadFailure(O365Team fullTeam, ChannelMessagesReadException ex,
            TeamTokenManager tokens, ILogger logger)
        {
            if (ex.Kind == ChannelReadFailureKind.TokenRejected)
            {
                logger.LogError(ex, $"Couldn't get channel messages via cached token for Team '{fullTeam.DisplayName}': '{ex.Message}'.");
                var outcome = await tokens.RemoveRefusedRefreshTokenAsync(logger);

                // As the token store now says: still authorised only if an administrator authorised the Team again
                // meanwhile. Its last refresh time is left as it was; this cycle didn't read the Team with that token.
                fullTeam.HasRefreshToken = outcome == RefusedTokenOutcome.KeptNewAuthorisation;
                fullTeam.LastRefreshed = null;
            }
            else
            {
                logger.LogWarning(ex, $"Couldn't finish reading channel messages for Team '{fullTeam.DisplayName}' this cycle: '{ex.Message}' " +
                    "Its authorisation is kept: the channels already read are saved and the rest are read next cycle.");
                fullTeam.HasRefreshToken = true;

                // Refreshed only if something was read before the failure: a Team whose first channel failed was not refreshed.
                fullTeam.LastRefreshed = fullTeam.ChannelCrawlStats != null && fullTeam.ChannelCrawlStats.ChannelsRead > 0 ? DateTime.Now : (DateTime?)null;
            }
        }

        /// <summary>
        /// Whether a crawl that ended without stopping refreshed the Team. A Team with no channels, or none that failed, was; one
        /// where channels failed and none could be read was not - the crawl used its token, but no messages were read.
        /// </summary>
        internal static bool CrawlRefreshedTheTeam(TeamsChannelCrawlStats stats)
            => stats == null || stats.ChannelsFailed == 0 || stats.ChannelsRead > 0;

        /// <summary>
        /// The UPN SQL already holds for a member's Entra object id, or <c>null</c>. Several rows can share an object id
        /// (one person with logins on several domains); see <see cref="TeamsCrawlRules.BuildUpnByAzureAdId"/> for which wins.
        /// </summary>
        private static async Task<string> FindKnownUserPrincipalName(string azureAdId, TeamsLoadContext context, AnalyticsEntitiesContext db)
        {
            if (context.UpnByAzureAdId != null)
            {
                // Built once per crawl: azure_ad_id is nvarchar(max), so every per-member query was a scan of users.
                return context.UpnByAzureAdId.TryGetValue(azureAdId, out var mapped) ? mapped : null;
            }

            // No crawl-wide map from this caller: one query per member, as before. Pick 1st one
            var dbUser = await db.users.Where(u => u.AzureAdId == azureAdId).FirstOrDefaultAsync();
            return dbUser?.UserPrincipalName;
        }

        #region Reactions

        async Task BuildAllReactionsFromMessages(TeamsLoadContext context)
        {
            this.AllReactions.Clear();
            foreach (var c in this.Channels)
            {
                if (c.Messages != null) await ProcessAllReactionsFromMessages(context, c);
            }
        }
        public async Task ProcessAllReactionsFromMessages(TeamsLoadContext context, ChannelWithReactions channel)
        {
            foreach (var r in channel.Reactions)
            {
                if (r.User?.User?.Id != null && r.CreatedDateTime.HasValue)
                {
                    // Null when Entra ID can't find the person (deleted, or from another tenant): the reaction still
                    // happened, and is stored against "Unknown User" - how Teams itself shows them.
                    var graphUser = await context.UserCache.GetUserOrNullIfNotFound(r.User.User.Id);

                    this.AllReactions.Add(
                        new UserReaction
                        {
                            GraphUser = graphUser,
                            Reaction = r.ReactionType,
                            ChannelId = channel.Id,
                            When = r.CreatedDateTime.Value.DateTime
                        }
                    );
                }
            }
        }
        #endregion

        private static async Task<List<DirectoryObject>> LoadAllGroupMembers(GraphServiceClient client, string groupId, ILogger logger)
        {
            // Safety cap on paging: at 200k-user scale a single group rarely exceeds ~50k members
            // but a runaway nextLink shouldn't ever fill memory unbounded. 200k members per group
            // is comfortably above any realistic limit.
            var all = new List<DirectoryObject>();

            var firstPage = await client.Groups[groupId].Members.GetAsync();
            if (firstPage == null) return all;

            int loaded = 0;
            var iterator = PageIterator<DirectoryObject, DirectoryObjectCollectionResponse>
                .CreatePageIterator(client, firstPage, item =>
                {
                    all.Add(item);
                    loaded++;
                    return TeamsCrawlPagingPolicy.ShouldContinuePaging(loaded, TeamsCrawlPagingPolicy.MaxTeamMembers);
                });

            await iterator.IterateAsync();

            if (iterator.State == PagingState.Paused)
            {
                logger.LogWarning($"Group {groupId}: hit MAX_MEMBERS ({TeamsCrawlPagingPolicy.MaxTeamMembers:N0}). Returning partial list of {all.Count:N0} members.");
            }

            return all;
        }
    }
}
