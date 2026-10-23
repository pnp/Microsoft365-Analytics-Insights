using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// GraphServiceClient + caches
    /// </summary>
    public class TeamsLoadContext
    {
        public TeamsLoadContext(GraphServiceClient graphClient)
        {
            this.UserCache = new UserLookupCache(graphClient);
            this.GraphClient = graphClient;
        }
        public GraphServiceClient GraphClient { get; set; }
        public UserLookupCache UserCache { get; set; }

        /// <summary>
        /// The <c>UserGroupsFilter</c> scope for this crawl. People outside it are not stored as owners or members,
        /// their reactions are not stored, and their channel messages are left out of the channel statistics.
        /// </summary>
        public Common.Entities.UserScope.UserImportScope UserScope { get; set; } = Common.Entities.UserScope.UserImportScope.Unfiltered;

        /// <summary>
        /// Entra object id to UPN for the users already in SQL, built once per crawl by
        /// <see cref="Teams.TeamsImporter"/> and shared read-only by every parallel chunk, so resolving a Team's members
        /// costs no SQL. <c>null</c> - a caller that didn't build one - falls back to one SQL lookup per member, as before.
        /// See <see cref="Teams.TeamsCrawlRules.BuildUpnByAzureAdId"/>.
        /// </summary>
        public IReadOnlyDictionary<string, string> UpnByAzureAdId { get; set; }
    }

    /// <summary>
    /// A Graph cache for an abstract resource.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class GraphLookupCache<T> : ObjectByIdCache<T> where T : class
    {
        protected GraphServiceClient graphClient = null;
        public GraphLookupCache(GraphServiceClient graphClient)
        {
            this.graphClient = graphClient;
        }
    }

    /// <summary>
    /// Graph cache for users
    /// </summary>
    public class UserLookupCache : GraphLookupCache<Microsoft.Graph.Models.User>
    {
        // Ids Entra ID said don't exist, remembered for the life of this cache - one crawl. A deleted person's reactions
        // across a Team's channels would otherwise cost one Graph call each.
        private readonly ConcurrentDictionary<string, bool> _notFoundIds = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public UserLookupCache(GraphServiceClient graphClient) : base(graphClient) { }

        public override async Task<Microsoft.Graph.Models.User> Load(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            try
            {
                var user = await graphClient.Users[id].GetAsync();
                return user;
            }
            catch (ODataError ex) when (IsUserNotFound(ex))
            {
                Console.WriteLine($"Got {ex.Error?.Code ?? ex.ResponseStatusCode.ToString()} finding user by ID '{id}'");
                return null;
            }
            catch (ODataError ex)
            {
                Console.WriteLine($"Got unexepected exception {ex.Message} finding user by ID '{id}'");
                throw;
            }
        }

        /// <summary>
        /// The user, or <c>null</c> when Entra ID says there is no such user: a deleted account, someone from another
        /// tenant, or a directory object that isn't a user (an app can own a group). Any other failure - throttling,
        /// permissions, an outage - still throws, because it says nothing about whether the person exists.
        /// </summary>
        public async Task<Microsoft.Graph.Models.User> GetUserOrNullIfNotFound(string id)
        {
            if (string.IsNullOrEmpty(id) || _notFoundIds.ContainsKey(id))
            {
                return null;
            }

            var user = await GetResourceOrNullIfNotExists(id);
            if (user == null)
            {
                _notFoundIds.TryAdd(id, true);
            }
            return user;
        }

        /// <summary>
        /// Graph's answer for a directory object that doesn't exist: HTTP 404, normally with code
        /// <c>Request_ResourceNotFound</c>.
        /// </summary>
        internal static bool IsUserNotFound(ODataError ex)
        {
            return ex.ResponseStatusCode == (int)HttpStatusCode.NotFound
                || string.Equals(ex.Error?.Code, "Request_ResourceNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ex.Error?.Code, "ResourceNotFound", StringComparison.OrdinalIgnoreCase);
        }
    }
}
