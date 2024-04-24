using Common.DataUtils;
using Microsoft.Graph;
using System;
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
    public class UserLookupCache : GraphLookupCache<Microsoft.Graph.User>
    {
        public UserLookupCache(GraphServiceClient graphClient) : base(graphClient) { }

        public override async Task<Microsoft.Graph.User> Load(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            try
            {
                var user = await graphClient.Users[id].Request().GetAsync();
                return user;
            }
            catch (ServiceException ex)
            {
                if (ex.Error.Code == "Request_ResourceNotFound")
                {
                    Console.WriteLine($"Got {ex.Error.Code} finding user by ID '{id}'");
                    return null;
                }
                else
                {
                    Console.WriteLine($"Got unexepected exception {ex.Message} finding user by ID '{id}'");
                    throw;
                }
            }
        }
    }
}
