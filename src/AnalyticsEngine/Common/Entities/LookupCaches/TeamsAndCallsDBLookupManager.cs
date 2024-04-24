using Common.DataUtils;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.LookupCaches;
using Common.Entities.Teams;
using System;
using System.Threading.Tasks;

namespace Common.Entities
{
    public class TeamsAndCallsDBLookupManager
    {
        private readonly TeamsAddOnCache _teamAddOnDefCache = null;
        private readonly TeamsCache _teamsCache;
        private readonly TeamsReactionTypeCache _teamsReactionCache;
        private readonly TeamsChannelCache _teamsChannelCache;
        private readonly TeamsTabCache _teamsTabsCache;
        private readonly CallModalityCache _callModalityCache;
        private readonly CallTypeCache _callTypeCache;
        private readonly UserCache _userCache;

        private readonly LanguageCache _langCache;
        private readonly KeywordCache _keywordCache;
        private readonly AnalyticsEntitiesContext _db;

        public AnalyticsEntitiesContext Database => _db;

        public TeamsAndCallsDBLookupManager(AnalyticsEntitiesContext db)
        {
            _teamsChannelCache = new TeamsChannelCache(db);
            _teamsTabsCache = new TeamsTabCache(db);
            _callTypeCache = new CallTypeCache(db);
            _userCache = new UserCache(db);
            _teamsReactionCache = new TeamsReactionTypeCache(db);

            _callModalityCache = new CallModalityCache(db);
            _teamsCache = new TeamsCache(db);
            _teamAddOnDefCache = new TeamsAddOnCache(db);
            _keywordCache = new KeywordCache(db);
            _langCache = new LanguageCache(db);
            _db = db;
        }

        public async Task<TeamDefinition> GetOrCreateTeam(string teamId, string name)
        {
            // Sanity
            if (string.IsNullOrEmpty(teamId)) throw new ArgumentNullException(nameof(teamId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));


            // Object in cache? Create new if not in cache or DB
            TeamDefinition lookup = _teamsCache.GetOrCreateNewResource(teamId,
                new TeamDefinition()
                {
                    Name = StringUtils.EnsureMaxLength(name, 100),
                    GraphID = teamId,
                    FirstDiscovered = DateTime.Now
                }).Result;

            if (!lookup.IsSavedToDB)
            {
                // Needed because LINQ blows up with "Only primitive types or enumeration types are supported in this context" for 'where' clauses passing objects.
                // So we need IDs instead, which means saving 1st
                await _db.SaveChangesAsync();
            }

            return lookup;
        }

        public async Task<TeamsReactionType> GetOrCreateTeamsReactionType(string name)
        {
            // Sanity
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            // Object in cache? Create new if not in cache or DB
            var lookup = await _teamsReactionCache.GetOrCreateNewResource(name, new TeamsReactionType() { Name = name });

            return lookup;
        }
        public async Task<CallModality> GetOrCreateCallModality(string name)
        {
            // Sanity
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            // Object in cache? Create new if not in cache or DB
            var lookup = await _callModalityCache.GetOrCreateNewResource(name, new CallModality() { Name = name });

            return lookup;
        }

        public async Task<CallType> GetOrCreateCallType(string name)
        {
            // Sanity
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            // Object in cache? Create new if not in cache or DB
            var lookup = await _callTypeCache.GetOrCreateNewResource(name, new CallType() { Name = name });

            return lookup;
        }

        public async Task<TeamChannel> GetTeamChannel(string teamId, string name, TeamDefinition parentTeam)
        {
            // Sanity
            if (string.IsNullOrEmpty(teamId)) throw new ArgumentNullException(nameof(teamId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));


            // Object in cache?
            var lookup = await _teamsChannelCache.GetOrCreateNewResource(teamId, new TeamChannel()
            {
                GraphID = teamId,
                Name = StringUtils.EnsureMaxLength(name, 100),
                Team = parentTeam
            });

            return lookup;
        }
        public async Task<TeamChannel> GetTeamChannel(string channelId)
        {
            if (string.IsNullOrEmpty(channelId))
            {
                throw new ArgumentException($"'{nameof(channelId)}' cannot be null or empty.", nameof(channelId));
            }

            return await _teamsChannelCache.GetResource(channelId.ToLower());
        }
        public async Task<TeamTabDefinition> GetOrCreateTeamTab(string tabId, string name, string webUrl, TeamAddOnDefinition associatedAddOn)
        {
            // Sanity
            if (string.IsNullOrEmpty(tabId))
            {
                return null;
            }

            name = StringUtils.EnsureMaxLength(name, 100);

            // Object in cache?
            var lookup = await _teamsTabsCache.GetOrCreateNewResource(tabId, new TeamTabDefinition()
            {
                Name = StringUtils.EnsureMaxLength(name, 100),
                GraphID = tabId,
                TeamAddOnDefinition = associatedAddOn,
                WebUrl = webUrl
            });

            // BugFix: Ensure app def
            if (lookup.TeamAddOnDefinition != associatedAddOn)
            {
                lookup.TeamAddOnDefinition = associatedAddOn;
            }

            return lookup;
        }

        public async Task<TeamAddOnDefinition> GetTeamAddOnDefinition(string addOnId, string name)
        {
            // Sanity
            if (string.IsNullOrEmpty(addOnId))
            {
                return null;
            }

            // Object in cache?
            var newAddInTemplate = new TeamAddOnDefinition()
            {
                GraphID = addOnId,
                Name = StringUtils.EnsureMaxLength(name, 100),
                AddOnType = 0  // Hardcode type
            };
            TeamAddOnDefinition lookup = await _teamAddOnDefCache.GetAndUpdateOrCreateNewResource(addOnId, newAddInTemplate);

            return lookup;
        }

        public async Task<Language> GetOrCreateLanguage(string name)
        {

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            var lookup = await _langCache.GetOrCreateNewResource(name, new Language { Name = name });
            return lookup;
        }
        public async Task<KeyWord> GetOrCreateKeyword(string name)
        {

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            var lookup = await _keywordCache.GetOrCreateNewResource(name, new KeyWord { Name = StringUtils.EnsureMaxLength(name, 100) });
            return lookup;
        }

        public async Task<User> GetOrCreateUser(string userPrincipalName, bool addNewToContextAndSave)
        {
            return await _userCache.GetOrCreateNewResource(userPrincipalName, new User { UserPrincipalName = userPrincipalName }, addNewToContextAndSave);
        }

        public async Task<User> GetOrCreateUnknownUser(bool addNewToContextAndSave)
        {
            // This is how Teams shows users that aren't found in AAD
            return await GetOrCreateUser("Unknown User", addNewToContextAndSave);
        }
    }
}
