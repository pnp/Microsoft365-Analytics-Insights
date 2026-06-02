using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Threading.Tasks;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    public abstract class GraphCache<T> : ObjectByIdCache<T> where T : class
    {
        protected readonly GraphServiceClient _graphServiceClient;

        protected GraphCache(GraphServiceClient graphServiceClient)
        {
            _graphServiceClient = graphServiceClient;
        }
    }
    public class SiteGraphCache : GraphCache<Site>
    {
        public SiteGraphCache(GraphServiceClient graphServiceClient) : base(graphServiceClient)
        {
        }

        public override async Task<Site> Load(string id)
        {
            return await _graphServiceClient.Sites[id].GetAsync();
        }
    }
    public class UserGraphCache : GraphCache<User>
    {
        public UserGraphCache(GraphServiceClient graphServiceClient) : base(graphServiceClient)
        {
        }

        public override async Task<User> Load(string id)
        {
            return await _graphServiceClient.Users[id].GetAsync();
        }
    }
}
