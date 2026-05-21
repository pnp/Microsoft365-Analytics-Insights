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

        // Mirrors GraphUserLoader's deferred-commit pattern so tests can verify
        // that the new delta is only persisted after a successful import.
        private string _pendingDeltaToken;
        private bool _hasPendingDeltaToken;

        /// <summary>
        /// Delta token that LoadAllActiveUsers will buffer (and CommitDeltaTokenAsync
        /// will persist). Defaults to "fake-new-delta" so tests can distinguish a
        /// committed vs uncommitted import without extra setup.
        /// </summary>
        public string SimulatedNewDeltaToken { get; set; } = "fake-new-delta";

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

        /// <summary>
        /// Optional hook invoked by LoadUsersBySku before returning. Tests can
        /// set this to throw an exception, simulating a Graph error mid-import.
        /// </summary>
        public Func<Guid, Task> OnLoadUsersBySku { get; set; }

        public IDeltaValueProvider DeltaValueProvider => _deltaProvider;

        public Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Simulate GraphUserLoader behavior: buffer the new delta token in
            // memory; only CommitDeltaTokenAsync persists it.
            _pendingDeltaToken = SimulatedNewDeltaToken;
            _hasPendingDeltaToken = true;

            // Return a defensive copy so callers that Clear() the result
            // do not wipe the original list (InsertAndUpdateDatabaseFromExternalUsers does this).
            return Task.FromResult(new List<GraphUser>(_fakeUsers));
        }

        public Task<IGraphServiceSubscribedSkusCollectionPage> LoadTenantSkus()
        {
            return Task.FromResult(_fakeSkus);
        }

        public async Task<List<Microsoft.Graph.User>> LoadUsersBySku(Guid skuId)
        {
            if (OnLoadUsersBySku != null)
            {
                await OnLoadUsersBySku(skuId);
            }

            if (_fakeUsersBySku.ContainsKey(skuId))
            {
                return _fakeUsersBySku[skuId];
            }
            return new List<Microsoft.Graph.User>();
        }

        public Task<IUserLicenseDetailsCollectionPage> LoadUserLicenseDetails(string userId)
        {
            if (_fakeLicenseDetails.ContainsKey(userId))
            {
                return Task.FromResult(_fakeLicenseDetails[userId]);
            }
            return Task.FromResult<IUserLicenseDetailsCollectionPage>(null);
        }

        public async Task CommitDeltaTokenAsync()
        {
            if (_hasPendingDeltaToken)
            {
                await _deltaProvider.SetDeltaToken(_pendingDeltaToken);
                _pendingDeltaToken = null;
                _hasPendingDeltaToken = false;
            }
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
