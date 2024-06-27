using DataUtils;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps;

namespace Tests.UnitTests.FakeLoaderClasses
{
    internal class FakeUserAppLoader : AbstractUserAppLoader<FakeUserApp>
    {
        private readonly int _fakeUserCount;
        private bool _haveThrownUserNotFound = false;

        public FakeUserAppLoader(AnalyticsLogger telemetry, int fakeUserCount) : base(telemetry)
        {
            _fakeUserCount = fakeUserCount;
        }

        public override TimeSpan GetDelay(int throttledAttempts)
        {
            return TimeSpan.Zero;
        }

        public override Task<List<string>> GetUserEmailAddressesToFindAppsFor()
        {
            var l = new List<string>();
            for (int i = 0; i < _fakeUserCount; i++)
            {
                l.Add($"user{i}@unittests.local");
            }
            return Task.FromResult(l);
        }

        public override Task<Dictionary<string, UserAppsLoadState<FakeUserApp>>> LoadAppsForUsers(List<string> ids, int attemptCount)
        {
            var r = new Dictionary<string, UserAppsLoadState<FakeUserApp>>();
            foreach (var id in ids)
            {
                // Fail 1st time
                r.Add(id, new UserAppsLoadState<FakeUserApp>
                {
                    Results = attemptCount > 0 ? new List<FakeUserApp>() { new FakeUserApp() } : new List<FakeUserApp>(),
                    Retry = attemptCount > 0 ? false : true
                });
            }
            return Task.FromResult(r);
        }

        public override Task Save(Dictionary<string, List<FakeUserApp>> usersAndApps)
        {
            _telemetry.LogInformation($"Faking save for {usersAndApps.Count} users and apps");
            return Task.CompletedTask;
        }

        public override Task<bool> TestReadPermissionsForUser(string testUserEmail)
        {
            if (!_haveThrownUserNotFound)
            {
                // 1st time throw "user not found" to test retry logic
                _haveThrownUserNotFound = true;
                throw new UserNotFoundException();
            }

            return Task.FromResult(true);
        }
    }

    internal class FakeUserApp
    {

    }
}
