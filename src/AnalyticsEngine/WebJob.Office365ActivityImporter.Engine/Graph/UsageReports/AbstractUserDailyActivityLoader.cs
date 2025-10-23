using Common.Entities.ActivityReports;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// Generic Graph report loader for users. 
    /// </summary>
    public abstract class AbstractUserDailyActivityLoader<TReportDbType, TUserActivityUserDetail> : AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, Common.Entities.User, UserCache>
        where TReportDbType : AbstractUsageActivityLog, new()
        where TUserActivityUserDetail : AbstractActivityRecord<Common.Entities.User>
    {
        private readonly UserGroupsCache _graphUserGroupsCache;
        private readonly UserGroupsFilterModel _userGroupsFilterModel;

        internal AbstractUserDailyActivityLoader(ManualGraphCallClient client, UserGroupsCache graphUserGroupsCache, UserGroupsFilterModel userGroupsFilterModel, ILogger telemetry) : base(client, telemetry)
        {
            _graphUserGroupsCache = graphUserGroupsCache;
            _userGroupsFilterModel = userGroupsFilterModel;
        }

        protected override async Task<bool> IdInScope(string upn)
        {
            return await _graphUserGroupsCache.IsInGroupsFilter(upn, _userGroupsFilterModel);
        }
    }
}
