using Common.Entities.ActivityReports;
using Common.Entities.LookupCaches;
using Common.Entities.UserScope;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// Generic Graph report loader for users. 
    /// </summary>
    public abstract class AbstractUserDailyActivityLoader<TReportDbType, TUserActivityUserDetail> : AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, Common.Entities.User, UserCache>
        where TReportDbType : AbstractUsageActivityLog, new()
        where TUserActivityUserDetail : AbstractActivityRecord<Common.Entities.User>
    {
        private readonly UserImportScope _userScope;

        /// <param name="userScope">
        /// The <c>UserGroupsFilter</c> scope for this cycle. Null means unfiltered, which is what every row passes.
        /// </param>
        internal AbstractUserDailyActivityLoader(ManualGraphCallClient client, UserImportScope userScope, ILogger logger) : base(client, logger)
        {
            _userScope = userScope ?? UserImportScope.Unfiltered;
        }

        /// <summary>
        /// A hash lookup against the scope resolved for this cycle. It used to be a Graph <c>memberOf</c> call per
        /// user, which let a user through whenever Graph could not answer.
        /// </summary>
        protected override Task<bool> IdInScope(string upn)
        {
            return Task.FromResult(_userScope.IsInScope(upn));
        }
    }
}
