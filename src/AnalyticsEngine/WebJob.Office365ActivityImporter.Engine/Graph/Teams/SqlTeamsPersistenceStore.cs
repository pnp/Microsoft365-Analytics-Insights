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
            // Who is already recorded as an owner of this Team: one query for the Team, not one per owner.
            var recorded = new UsersAlreadyRecorded(team.IsSavedToDB
                ? await _lookup.Database.TeamOwners.Where(to => to.TeamID == team.ID).Select(to => to.OwnerID).ToListAsync()
                : new List<int>());
            foreach (var graphUser in owners)
            {
                var dbUser = await _lookup.GetOrCreateUser(graphUser.UserPrincipalName, true);
                if (recorded.IsNew(dbUser))
                {
                    _lookup.Database.TeamOwners.Add(new TeamOwners { Owner = dbUser, Team = team, Discovered = DateTime.Now });
                }
            }
        }

        /// <summary>
        /// Logs today's membership of the Team, once per person per day.
        /// </summary>
        /// <remarks>
        /// Who is already logged today is read in ONE query for the Team, rather than one per member, and with a date range rather
        /// than the date's year, month and day: those three were each a <c>DATEPART</c> over the column, which no index can seek,
        /// so every lookup read the member's whole membership history (an <c>IX_user_id</c> seek and a key lookup per row). The
        /// range seeks <c>IX_team_membership_log_date</c>, which carries <c>team_id</c> and <c>user_id</c>, so the query reads
        /// today's rows rather than the history. Measured on synthetic data with 365 days of history
        /// (<c>TeamMembershipLookupBenchmarkTests</c>): a 2,000-member Team's check went from 2,000 queries and 7.0M logical
        /// reads to one query and 59. Rows are written at midnight UTC, but the range also matches any time of day, as the old
        /// comparison did.
        /// </remarks>
        public async Task SaveMembers(List<BaseUser> members, O365Team team)
        {
            var dbTeam = await _lookup.GetOrCreateTeam(team.Id, team.DisplayName);
            var today = DateTime.UtcNow.Date;
            var tomorrow = today.AddDays(1);
            var loggedToday = new UsersAlreadyRecorded(dbTeam.IsSavedToDB
                ? await _lookup.Database.TeamMembershipLogs
                    .Where(t => t.TeamID == dbTeam.ID && t.Date >= today && t.Date < tomorrow)
                    .Select(t => t.UserID)
                    .ToListAsync()
                : new List<int>());
            foreach (var member in members)
            {
                var user = await _lookup.GetOrCreateUser(member.UserPrincipalName, true);
                if (loggedToday.IsNew(user))
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
                // The tabs already logged on this channel today: one query for the channel, not one per tab, and a date range
                // rather than the date's parts, for the reason given on SaveMembers.
                var today = DateTime.UtcNow.Date;
                var tomorrow = today.AddDays(1);
                var tabsLoggedToday = new HashSet<string>(existingChannelSQL.IsSavedToDB
                    ? await _lookup.Database.ChannelTabLogs
                        .Where(l => l.ChannelID == existingChannelSQL.ID && l.Date >= today && l.Date < tomorrow)
                        .Select(l => l.TabDefinition.GraphID)
                        .ToListAsync()
                    : new List<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var tab in channel.Tabs)
                {
                    var tabDB = await _lookup.GetOrCreateTeamTab(tab.Id, tab.DisplayName, tab.WebUrl);
                    if (tabsLoggedToday.Add(tab.Id))
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
                // Someone Entra ID couldn't find is stored as "Unknown User", as Teams shows them.
                var user = !string.IsNullOrEmpty(r.GraphUser?.UserPrincipalName)
                    ? await _lookup.GetOrCreateUser(r.GraphUser.UserPrincipalName, false)
                    : await _lookup.GetOrCreateUnknownUser(false);
                var channel = await _lookup.GetTeamChannel(r.ChannelId);
                _lookup.Database.TeamsUserReactions.Add(new TeamsUserReaction { Reaction = reaction, User = user, Channel = channel, Date = r.When });
            }
        }

        public async Task SaveChanges(O365Team source, TeamDefinition team)
        {
            team.HasRefreshToken = source.HasRefreshToken;

            // A Team still authorised, whose token just couldn't be refreshed this cycle, keeps its last refresh time.
            if (source.LastRefreshed.HasValue || !source.HasRefreshToken)
            {
                team.LastRefreshed = source.LastRefreshed;
            }
            _lookup.Database.ChangeTracker.DetectChanges();
            await _lookup.Database.SaveChangesAsync();
        }

        /// <summary>
        /// The users a Team already has a row for - their ids, read from SQL in one query - plus every user seen during this save,
        /// so a person listed twice still gets one row.
        /// </summary>
        private sealed class UsersAlreadyRecorded
        {
            private readonly HashSet<int> _recordedIds;

            // By reference: a user created during this save may not have an id yet. AbstractEFEntity doesn't override Equals.
            private readonly HashSet<User> _seenThisSave = new HashSet<User>();

            public UsersAlreadyRecorded(IEnumerable<int> recordedIds)
            {
                _recordedIds = new HashSet<int>(recordedIds);
            }

            /// <summary>True the first time a user is seen, unless SQL already has their row.</summary>
            public bool IsNew(User user) => _seenThisSave.Add(user) && (!user.IsSavedToDB || _recordedIds.Add(user.ID));
        }
    }
}
