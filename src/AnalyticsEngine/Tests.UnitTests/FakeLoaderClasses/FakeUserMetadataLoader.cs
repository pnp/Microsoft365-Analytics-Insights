using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// Fake implementation of IUserMetadataLoader for testing
    /// </summary>
    public class FakeUserMetadataLoader : IUserMetadataLoader
    {
        private readonly List<GraphUser> _fakeUsers;
        private readonly IGraphServiceSubscribedSkusCollectionPage _fakeSkus;
        private readonly Dictionary<Guid, List<Microsoft.Graph.User>> _fakeUsersBySku;
        private readonly Dictionary<string, IUserLicenseDetailsCollectionPage> _fakeLicenseDetails;
        private readonly FakeDeltaValueProvider _deltaProvider;

        public FakeUserMetadataLoader(
            List<GraphUser> fakeUsers = null,
            IGraphServiceSubscribedSkusCollectionPage fakeSkus = null,
            Dictionary<Guid, List<Microsoft.Graph.User>> fakeUsersBySku = null,
            Dictionary<string, IUserLicenseDetailsCollectionPage> fakeLicenseDetails = null)
        {
            _fakeUsers = fakeUsers ?? new List<GraphUser>();
            _fakeSkus = fakeSkus;
            _fakeUsersBySku = fakeUsersBySku ?? new Dictionary<Guid, List<Microsoft.Graph.User>>();
            _fakeLicenseDetails = fakeLicenseDetails ?? new Dictionary<string, IUserLicenseDetailsCollectionPage>();
            _deltaProvider = new FakeDeltaValueProvider();
        }

        public IDeltaValueProvider DeltaValueProvider => _deltaProvider;

        public Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Return a defensive copy so callers that Clear() the result
            // do not wipe the original list (InsertAndUpdateDatabaseFromExternalUsers does this).
            return Task.FromResult(new List<GraphUser>(_fakeUsers));
        }

        public Task<IGraphServiceSubscribedSkusCollectionPage> LoadTenantSkus()
        {
            return Task.FromResult(_fakeSkus);
        }

        public Task<List<Microsoft.Graph.User>> LoadUsersBySku(Guid skuId)
        {
            if (_fakeUsersBySku.ContainsKey(skuId))
            {
                return Task.FromResult(_fakeUsersBySku[skuId]);
            }
            return Task.FromResult(new List<Microsoft.Graph.User>());
        }

        public Task<IUserLicenseDetailsCollectionPage> LoadUserLicenseDetails(string userId)
        {
            if (_fakeLicenseDetails.ContainsKey(userId))
            {
                return Task.FromResult(_fakeLicenseDetails[userId]);
            }
            return Task.FromResult<IUserLicenseDetailsCollectionPage>(null);
        }
    }

    /// <summary>
    /// Fake implementation of IDeltaValueProvider for testing
    /// </summary>
    public class FakeDeltaValueProvider : IDeltaValueProvider
    {
        private string _deltaToken;

        public Task ClearDeltaToken()
        {
            _deltaToken = null;
            return Task.CompletedTask;
        }

        public Task<string> GetDeltaToken()
        {
            return Task.FromResult(_deltaToken);
        }

        public Task SetDeltaToken(string deltaToken)
        {
            _deltaToken = deltaToken;
            return Task.CompletedTask;
        }
    }
}
