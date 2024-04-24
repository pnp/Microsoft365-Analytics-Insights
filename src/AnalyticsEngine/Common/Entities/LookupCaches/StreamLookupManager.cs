using Common.Entities.Entities;
using System;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities
{

    public class StreamLookupManager
    {

        private StreamCache _streamCache = null;
        public StreamLookupManager(AnalyticsEntitiesContext db)
        {
            _streamCache = new StreamCache(db);

        }

        public async Task<StreamVideo> GetOrCreateStreamVideo(Guid streamId)
        {
            return await GetCreateOrUpdateStreamVideo(streamId, null);
        }
        public async Task<StreamVideo> GetCreateOrUpdateStreamVideo(Guid streamId, string videoName)
        {
            // Object in cache? Create new if not in cache or DB
            var lookup = await _streamCache.GetOrCreateNewResource(streamId.ToString(), new StreamVideo() { Name = videoName, StreamID = streamId });
            if (!string.IsNullOrEmpty(videoName) && string.IsNullOrEmpty(lookup.Name))
            {
                lookup.Name = videoName;
            }

            return lookup;
        }

        internal class StreamCache : DBLookupCache<StreamVideo>
        {
            public StreamCache(AnalyticsEntitiesContext context) : base(context) { }

            public override DbSet<StreamVideo> EntityStore => this.DB.Streams;

            public async override Task<StreamVideo> Load(string searchKey)
            {
                return await EntityStore.SingleOrDefaultAsync(t => t.StreamID == new Guid(searchKey));
            }

        }

    }
}
