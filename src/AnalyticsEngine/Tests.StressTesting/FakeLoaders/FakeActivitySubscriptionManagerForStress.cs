using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.StressTesting.FakeLoaders
{
    /// <summary>
    /// Fake subscription manager for stress testing
    /// </summary>
    public class FakeActivitySubscriptionManagerForStress : IActivitySubscriptionManager
    {
        private List<string> _activeTypes = new List<string> { "Audit.SharePoint", "Audit.Exchange" };

        public Task CreateInactiveSubcriptions(List<string> active)
        {
            return Task.CompletedTask;
        }

        public Task<List<string>> EnsureActiveSubscriptionContentTypesActive()
        {
            return Task.FromResult(_activeTypes);
        }

        public Task<List<string>> GetActiveSubscriptionContentTypes()
        {
            return Task.FromResult(_activeTypes);
        }

        public Task<ApiSubscription[]> GetActiveSubscriptions()
        {
            var subs = new ApiSubscription[]
            {
                new ApiSubscription
                {
                    contentType = "Audit.SharePoint",
                    status = "enabled",
                    webhook = null
                },
                new ApiSubscription
                {
                    contentType = "Audit.Exchange",
                    status = "enabled",
                    webhook = null
                }
            };
            return Task.FromResult(subs);
        }
    }
}
