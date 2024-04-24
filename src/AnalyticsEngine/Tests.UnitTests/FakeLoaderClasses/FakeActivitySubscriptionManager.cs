using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests.FakeLoaderClasses
{
    internal class FakeActivitySubscriptionManager : IActivitySubscriptionManager
    {
        public Task CreateInactiveSubcriptions(List<string> active)
        {
            throw new NotImplementedException();
        }

        public Task<List<string>> EnsureActiveSubscriptionContentTypesActive()
        {
            return Task.FromResult(new List<string>() { "Testing" });
        }

        public Task<List<string>> GetActiveSubscriptionContentTypes()
        {
            throw new NotImplementedException();
        }

        public Task<ApiSubscription[]> GetActiveSubscriptions()
        {
            throw new NotImplementedException();
        }
    }
}
