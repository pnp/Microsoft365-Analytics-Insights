using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests.FakeLoaderClasses
{
    public class MockUserGroupsCache : UserGroupsCache
    {
        private readonly Dictionary<string, List<string>> _mockGroups;

        public MockUserGroupsCache(Dictionary<string, List<string>> mockGroups, ILogger logger = null)
            : base(logger)
        {
            _mockGroups = mockGroups ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        protected override Task<List<string>> LoadGroupsFromGraphAsync(string upn)
        {
            if (_mockGroups.TryGetValue(upn, out var groups))
                return Task.FromResult(groups);
            return Task.FromResult(new List<string>());
        }
    }
}
