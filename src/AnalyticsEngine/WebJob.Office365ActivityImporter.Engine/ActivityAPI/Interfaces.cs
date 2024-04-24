using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{

    public interface IActivityReportLoader<SUMMARYTYPE>
    {
        Task<ActivityReportSet> Load(SUMMARYTYPE metadata);
    }
    public interface IActivitySubscriptionManager
    {
        Task CreateInactiveSubcriptions(List<string> active);
        Task<List<string>> EnsureActiveSubscriptionContentTypesActive();
        Task<List<string>> GetActiveSubscriptionContentTypes();
        Task<ApiSubscription[]> GetActiveSubscriptions();
    }

    public interface IActivityReportPersistenceManager
    {
        Task<ImportStat> CommitAll(ActivityReportSet activities);
    }
}
