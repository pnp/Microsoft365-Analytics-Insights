using DataUtils;
using Microsoft.Graph.Models;
using System.Threading.Tasks;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    public abstract class GraphCache<T> : ObjectByIdCache<T> where T : class
    {
        protected readonly ISpoGraphClient _spoGraphClient;

        protected GraphCache(ISpoGraphClient spoGraphClient)
        {
            _spoGraphClient = spoGraphClient;
        }
    }
    public class SiteGraphCache : GraphCache<Site>
    {
        public SiteGraphCache(ISpoGraphClient spoGraphClient) : base(spoGraphClient)
        {
        }

        public override async Task<Site> Load(string id)
        {
            return await _spoGraphClient.GetSiteAsync(id);
        }
    }
    public class UserGraphCache : GraphCache<User>
    {
        public UserGraphCache(ISpoGraphClient spoGraphClient) : base(spoGraphClient)
        {
        }

        public override async Task<User> Load(string id)
        {
            return await _spoGraphClient.GetUserAsync(id);
        }
    }
}
