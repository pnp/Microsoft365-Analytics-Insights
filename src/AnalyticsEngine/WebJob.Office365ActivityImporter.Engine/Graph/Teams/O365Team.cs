using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Redis.Teams;
using Common.Entities.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
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
            this.OwnerUserAccounts = new List<Microsoft.Graph.User>();
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
            this.InstalledApps = team.InstalledApps;
        }


        #endregion

        #region Properties

        public IEnumerable<TeamsAppInstallation> InstalledApps { get; set; }
        public List<ChannelWithReactions> Channels { get; set; } = new List<ChannelWithReactions>();

        public string DisplayName { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Graph users in the Team. 
        /// </summary>
        public List<BaseUser> Users { get; set; }
        public List<Microsoft.Graph.User> OwnerUserAccounts { get; set; }

        public List<UserReaction> AllReactions { get; set; } = new List<UserReaction>();

        public bool HasRefreshToken { get; set; }
        public DateTime? LastRefreshed { get; set; }

        #endregion

        public async Task<TeamDefinition> SaveToSQL(TeamsAndCallsDBLookupManager lookupManager, ILogger telemetry)
        {
            if (lookupManager is null) throw new ArgumentNullException(nameof(lookupManager));
            if (telemetry is null) throw new ArgumentNullException(nameof(telemetry));

            telemetry.LogInformation($"Saving Team '{this.DisplayName}' to SQL...");

            // Save to SQL if doesn't exist already
            var dbTeam = await lookupManager.GetOrCreateTeam(this.Id, this.DisplayName);

            if (!dbTeam.IsSavedToDB)
            {
                // Save so the lookup manager can find it later by ID
                await lookupManager.Database.SaveChangesAsync();
            }

            // Add owners
            foreach (var graphUser in this.OwnerUserAccounts)
            {
                var dbUser = await lookupManager.GetOrCreateUser(graphUser.UserPrincipalName, true);

                var teamOwnerRecord = lookupManager.Database.TeamOwners.Where(to => to.TeamID == dbTeam.ID && to.OwnerID == dbUser.ID).SingleOrDefault();
                if (teamOwnerRecord == null)
                {
                    lookupManager.Database.TeamOwners.Add(new Common.Entities.Entities.Teams.TeamOwners() { Owner = dbUser, Team = dbTeam, Discovered = DateTime.Now });
                }
            }
            // Save members in each team
            await this.Users.SaveStatsForToday(this, lookupManager);

            // Team apps
            await this.InstalledApps.SaveStatsForToday(lookupManager, dbTeam);

            // Channels
            var dbChannels = new List<TeamChannel>();

            var teamTokenManager = new TeamTokenManager(this);
            foreach (var channel in this.Channels)
            {
                var dbChannel = await channel.SaveToSql(lookupManager, dbTeam);
            }

            // Get & save channel stats for relevant chats found
            var channelMessageStats = await this.Channels.GetMessagesStats(telemetry);
            foreach (var channelStat in channelMessageStats)
            {
                await channelStat.InsertOrAppendSqlStats(lookupManager, dbTeam);
            }

            // Parse reactions
            foreach (var r in AllReactions)
            {
                var reaction = await lookupManager.GetOrCreateTeamsReactionType(r.Reaction);
                Common.Entities.User user = null;

                // It's possible there was no user in AAD for this reaction
                if (r.GraphUser != null)
                {
                    user = await lookupManager.GetOrCreateUser(r.GraphUser.UserPrincipalName, false);
                }
                else
                {
                    user = await lookupManager.GetOrCreateUnknownUser(false);
                }

                // We would've added it to cache previously
                var channel = await lookupManager.GetTeamChannel(r.ChannelId);

                lookupManager.Database.TeamsUserReactions.Add(
                    new Common.Entities.Entities.Teams.TeamsUserReaction
                    {
                        Reaction = reaction,
                        User = user,
                        Channel = channel,
                        Date = r.When
                    }
                );
            }

            // Update access-token stats for Team
            dbTeam.HasRefreshToken = this.HasRefreshToken;
            dbTeam.LastRefreshed = this.LastRefreshed;

            // Save all changes
            lookupManager.Database.ChangeTracker.DetectChanges();
            try
            {
                await lookupManager.Database.SaveChangesAsync();
            }
            catch (System.Data.Entity.Infrastructure.DbUpdateException ex)
            {
                telemetry.LogError(ex, $"Got SQL exception saving Team: {ex.Message}. Will try again next cycle.");
                await ClearChannelDeltaTokens(telemetry);
                return null;
            }

            return dbTeam;
        }

        async Task ClearChannelDeltaTokens(ILogger telemetry)
        {
            var teamTokenManager = new TeamTokenManager(this);
            foreach (var c in this.Channels)
            {
                await teamTokenManager.CacheConnectionManager.RemoveTeamChannelDeltaToken(this.Id, c.Id, telemetry);
            }
        }

        /// <summary>
        /// Loads all the Team child data, plus messages from the last channel log in the DB
        /// </summary>
        public static async Task<O365Team> LoadTeamFull(Group parentGroup, TeamsLoadContext context, ILogger telemetry, AnalyticsEntitiesContext db)
        {
            var teamId = parentGroup.Id;
            // Get Team details from Graph and convert to our own class
            var team = await context.GraphClient.Teams[teamId].Request().GetAsync();
            var fullTeam = new O365Team(team);

            // Populate new Teams definition with graph data
            var parentGroupFull = await context.GraphClient.Groups[teamId].Request().Expand("Owners").GetAsync();

            // Add owners
            foreach (var groupOwner in parentGroupFull.Owners)
            {
                var graphUser = await context.UserCache.GetResource(groupOwner.Id);
                if (graphUser != null)
                {
                    fullTeam.OwnerUserAccounts.Add(graphUser);
                }
                else
                {
                    telemetry.LogInformation($"Couldn't find owner with user ID '{groupOwner.Id}' for Team {fullTeam.DisplayName}");
                }
            }

#if DEBUG
            Console.WriteLine($"\nReading team '{parentGroup.DisplayName}':");
#endif
            var members = await LoadMembers(context.GraphClient.Groups[parentGroup.Id].Members.Request(), telemetry);

            foreach (var member in members)
            {
                // Multiple accounts can appear in users table if they have several logins on several domains. Pick 1st one
                var dbUser = await db.users.Where(u => u.AzureAdId == member.Id).FirstOrDefaultAsync();
                if (dbUser == null)
                {
                    // Load direct from Graph
                    var userInfo = await context.UserCache.GetResource(member.Id);

                    if (userInfo != null)
                        fullTeam.Users.Add(new BaseUser { UserPrincipalName = userInfo.UserPrincipalName });
                }
                else
                {
                    fullTeam.Users.Add(new BaseUser { UserPrincipalName = dbUser.UserPrincipalName });
                }
            }

            // Load apps
            var apps = await context.GraphClient.Teams[teamId].InstalledApps.Request().Expand("TeamsAppDefinition,TeamsApp").GetAsync();
            fullTeam.InstalledApps = apps;

            // Channels and tabs:
            var channelsLoaded = await context.GraphClient.Teams[teamId].Channels.Request().GetAsync();
            foreach (var channel in channelsLoaded)
                fullTeam.Channels.Add(new ChannelWithReactions(channel));


            var teamTokenManager = new TeamTokenManager(fullTeam);

            // Load tabs in each channel
            foreach (var channel in fullTeam.Channels)
            {
                var dbChannel = await db.TeamChannels.Where(c => c.GraphID == channel.Id).SingleOrDefaultAsync();
                if (dbChannel == null)
                {
                    // Clear delta cache if new channel in DB. Mainly for debug reasons but also if there's no channel, we need to make sure we ignore any delta code (just in case)
                    await teamTokenManager.CacheConnectionManager.RemoveTeamChannelDeltaToken(fullTeam.Id, channel.Id, telemetry);
                }
                channel.Tabs = await context.GraphClient.Teams[teamId].Channels[channel.Id].Tabs.Request().Expand("teamsApp").GetAsync();
            }

            // Get token for Team
            var teamRefreshOAuthToken = await teamTokenManager.GetRefreshToken(telemetry);

            // Update access-token stats for Team
            if (teamRefreshOAuthToken == null)
            {
                fullTeam.HasRefreshToken = false;
            }
            else
            {
                // We have a token. Load channel msgs/replies/reactions
                try
                {
                    await fullTeam.Channels.PopulateNewMessagesAndReactions(team, teamRefreshOAuthToken, teamTokenManager.CacheConnectionManager, telemetry);
                }
                catch (ChannelMessagesReadException ex)
                {
                    telemetry.LogError(ex, $"Couldn't get channel messages via cached token. '{ex.Message}'. Deleting token.");
                    await teamTokenManager.CacheConnectionManager.RemoveTeamAuthToken(team.Id);
                    teamRefreshOAuthToken = null;
                }

                fullTeam.HasRefreshToken = true;
                fullTeam.LastRefreshed = DateTime.Now;
            }

            // Load reactions + users from messages found
            await fullTeam.BuildAllReactionsFromMessages(context);

            return fullTeam;
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
                if (r.User.User?.Id != null && r.CreatedDateTime.HasValue)
                {
                    var graphUser = await context.UserCache.GetResource(r.User.User.Id);

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

        private static async Task<IGroupMembersCollectionWithReferencesPage> LoadMembers(IGroupMembersCollectionWithReferencesRequest req, ILogger telemetry)
        {
            return await LoadMembers(req, telemetry, 0);
        }
        private static async Task<IGroupMembersCollectionWithReferencesPage> LoadMembers(IGroupMembersCollectionWithReferencesRequest req, ILogger telemetry, int page)
        {
            var results = await req.GetAsync();
            if (results.NextPageRequest != null)
            {
                telemetry.LogInformation($"Load members page {++page}");

                var nextResults = await LoadMembers(results.NextPageRequest, telemetry, page++);
                foreach (var item in nextResults)
                {
                    results.Add(item);
                }
            }

            return results;
        }
    }
}
