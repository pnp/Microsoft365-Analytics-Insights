using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.Teams;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// A per-save adapter over the existing per-chunk lookup manager. Disabling automatic detection
    /// avoids quadratic EF tracking on large Teams; each existing flush explicitly detects changes.
    /// Dispose restores the caller's tracking setting, including on failure. No new transaction.
    /// </summary>
    public sealed class SqlTeamsPersistenceStore : ITeamsPersistenceStore, IDisposable
    {
        private readonly TeamsAndCallsDBLookupManager _lookup;
        private readonly bool _autoDetectWasEnabled;
        private readonly System.Data.Entity.Core.Objects.ObjectContext _objectContext;

        public SqlTeamsPersistenceStore(TeamsAndCallsDBLookupManager lookup)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _autoDetectWasEnabled = lookup.Database.Configuration.AutoDetectChangesEnabled;
            _objectContext = ((IObjectContextAdapter)lookup.Database).ObjectContext;
            lookup.Database.Configuration.AutoDetectChangesEnabled = false;
            // Lookup caches flush newly discovered Teams/users themselves. Detect at those same
            // boundaries too, so pending relationship changes still join that implicit commit.
            _objectContext.SavingChanges += DetectChangesBeforeSave;
        }

        private void DetectChangesBeforeSave(object sender, EventArgs args) => _lookup.Database.ChangeTracker.DetectChanges();

        public void Dispose()
        {
            _objectContext.SavingChanges -= DetectChangesBeforeSave;
            _lookup.Database.Configuration.AutoDetectChangesEnabled = _autoDetectWasEnabled;
        }

        public async Task<TeamDefinition> GetOrCreateTeam(O365Team team)
        {
            var dbTeam = await _lookup.GetOrCreateTeam(team.Id, team.DisplayName);
            if (!dbTeam.IsSavedToDB)
            {
                _lookup.Database.ChangeTracker.DetectChanges();
                await _lookup.Database.SaveChangesAsync();
            }
            return dbTeam;
        }

        public async Task SaveOwners(List<Microsoft.Graph.Models.User> owners, TeamDefinition team)
        {
            foreach (var graphUser in owners)
            {
                var dbUser = await _lookup.GetOrCreateUser(graphUser.UserPrincipalName, true);
                var teamOwnerRecord = _lookup.Database.TeamOwners.Where(to => to.TeamID == team.ID && to.OwnerID == dbUser.ID).SingleOrDefault();
                if (teamOwnerRecord == null)
                {
                    _lookup.Database.TeamOwners.Add(new TeamOwners { Owner = dbUser, Team = team, Discovered = DateTime.Now });
                }
            }
        }

        public async Task SaveMembers(List<BaseUser> members, O365Team team)
        {
            var dbTeam = await _lookup.GetOrCreateTeam(team.Id, team.DisplayName);
            var today = DateTime.UtcNow.Date;
            int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;
            foreach (var member in members)
            {
                var user = await _lookup.GetOrCreateUser(member.UserPrincipalName, true);
                TeamMembershipLog todaysUserLog = null;
                if (user.IsSavedToDB)
                {
                    todaysUserLog = await _lookup.Database.TeamMembershipLogs.SingleOrDefaultAsync(t =>
                        t.Team.ID == dbTeam.ID && t.UserID == user.ID &&
                        t.Date.Year == todayYear && t.Date.Month == todayMonth && t.Date.Day == todayDay);
                }
                if (todaysUserLog == null)
                {
                    _lookup.Database.TeamMembershipLogs.Add(new TeamMembershipLog { Team = dbTeam, User = user, Date = today });
                }
            }
        }

        public async Task<TeamChannel> SaveChannel(ChannelWithReactions channel, TeamDefinition team)
        {
            var existingChannelSQL = await _lookup.GetTeamChannel(channel.Id, channel.DisplayName, team);
            if (!existingChannelSQL.IsSavedToDB)
            {
                _lookup.Database.TeamChannels.Add(existingChannelSQL);
            }
            if (channel.Tabs != null)
            {
                foreach (var tab in channel.Tabs)
                {
                    var tabDB = await _lookup.GetOrCreateTeamTab(tab.Id, tab.DisplayName, tab.WebUrl);
                    var today = DateTime.UtcNow.Date;
                    int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;
                    var tabLog = await _lookup.Database.ChannelTabLogs.SingleOrDefaultAsync(l =>
                        l.Date.Year == todayYear && l.Date.Month == todayMonth && l.Date.Day == todayDay &&
                        l.Channel.GraphID == existingChannelSQL.GraphID && l.TabDefinition.GraphID == tab.Id);
                    if (tabLog == null)
                    {
                        _lookup.Database.ChannelTabLogs.Add(new ChannelTabLog { Channel = existingChannelSQL, Date = today, TabDefinition = tabDB });
                    }
                }
            }
            return existingChannelSQL;
        }

        public async Task SaveStats(MessageCognitiveStats stats, TeamDefinition team)
        {
            var dbChannel = await _lookup.GetTeamChannel(stats.Channel.Id, stats.Channel.DisplayName, team);
            var existingLog = await _lookup.Database.TeamChannelStats
                .Where(s => s.Date == stats.ForDate.Date && s.ChannelID == dbChannel.ID).SingleOrDefaultAsync();
            if (existingLog == null)
            {
                existingLog = new ChannelStatsLog { Channel = dbChannel, ChatsCount = stats.ChatsCount, SentimentScore = stats.Sentiment, Date = stats.ForDate.Date };
                _lookup.Database.TeamChannelStats.Add(existingLog);
            }
            else
            {
                stats.IncrementMessageStatsWithThis(existingLog);
            }
            foreach (var kw in stats.KeyWords.Keys)
            {
                var kwDef = await _lookup.GetOrCreateKeyword(kw);
                var addNew = !existingLog.IsSavedToDB;
                if (existingLog.IsSavedToDB)
                {
                    var existingKwLookup = await _lookup.Database.TeamChannelStatKeywords
                        .Where(s => s.ChannelStatsLogID == existingLog.ID && s.KeyWordID == kwDef.ID).FirstOrDefaultAsync();
                    addNew = existingKwLookup == null;
                }
                if (addNew) existingLog.KeywordLookups.Add(new ChannelLogKeyword { KeyWord = kwDef, ChannelStatsLog = existingLog });
            }
            foreach (var lang in stats.Languages)
            {
                var langDef = await _lookup.GetOrCreateLanguage(lang);
                var addNew = !existingLog.IsSavedToDB;
                if (existingLog.IsSavedToDB)
                {
                    var existingLangLookup = await _lookup.Database.TeamChannelStatLanguages
                        .Where(s => s.ChannelStatsLogID == existingLog.ID && s.LanguageID == langDef.ID).SingleOrDefaultAsync();
                    addNew = existingLangLookup == null;
                }
                if (addNew) existingLog.LanguageLookups.Add(new ChannelLogLanguage { Language = langDef, ChannelStatsLog = existingLog });
            }
        }

        public async Task SaveReactions(List<UserReaction> reactions)
        {
            foreach (var r in reactions)
            {
                var reaction = await _lookup.GetOrCreateTeamsReactionType(r.Reaction);
                var user = r.GraphUser != null
                    ? await _lookup.GetOrCreateUser(r.GraphUser.UserPrincipalName, false)
                    : await _lookup.GetOrCreateUnknownUser(false);
                var channel = await _lookup.GetTeamChannel(r.ChannelId);
                _lookup.Database.TeamsUserReactions.Add(new TeamsUserReaction { Reaction = reaction, User = user, Channel = channel, Date = r.When });
            }
        }

        public async Task SaveChanges(O365Team source, TeamDefinition team)
        {
            team.HasRefreshToken = source.HasRefreshToken;
            team.LastRefreshed = source.LastRefreshed;
            _lookup.Database.ChangeTracker.DetectChanges();
            await _lookup.Database.SaveChangesAsync();
        }
    }
}
