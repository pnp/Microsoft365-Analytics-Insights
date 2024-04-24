using Common.Entities;
using Common.Entities.Entities;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammergroupsactivitydetail?view=graph-rest-beta
    /// </summary>
    public class YammerGroupActivityDetail : AbstractActivityRecord<YammerGroup>
    {

        [JsonProperty("postedCount")]
        public int? PostedCount { get; set; }

        [JsonProperty("readCount")]
        public int? ReadCount { get; set; }

        [JsonProperty("likedCount")]
        public int? LikedCount { get; set; }

        [JsonProperty("memberCount")]
        public int? MemberCount { get; set; }

        [JsonProperty("groupDisplayName")]
        public string GroupName { get; set; }

        public override string LookupFieldValue => GroupName;

        // This is the only activity report that doesn't correspond to a user, but instead a yammer group
        public override async Task<AbstractEFEntity> GetOrCreateLookup(DBLookupCache<YammerGroup> lookupCache)
        {
            return await lookupCache.GetOrCreateNewResource(GroupName, new YammerGroup { Name = GroupName }, true);
        }
    }
}
