using Microsoft.Graph;
using Microsoft.Graph.Models;
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
        private List<GraphUser> _fakeUsers;
        private List<SubscribedSku> _fakeSkus;
        private Dictionary<Guid, List<Microsoft.Graph.Models.User>> _fakeUsersBySku;
        private readonly Dictionary<string, List<LicenseDetails>> _fakeLicenseDetails;
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

        /// <summary>
        /// When non-null AND the delta provider already has a token (i.e. this is
        /// NOT the first run), LoadAllActiveUsers returns this list instead of the
        /// full fake-user list. Mirrors the real Graph behaviour where /users/delta
        /// only returns users whose tracked properties changed since the previous
        /// delta token was issued. Used by the licence-drift regression test to
        /// simulate a run where most of the tenant does not flow through the delta
        /// even though their licence assignments in Graph have changed.
        /// </summary>
        public List<GraphUser> DeltaUsersOverride { get; set; }

        public FakeUserMetadataLoader(
            List<GraphUser> fakeUsers = null,
            List<SubscribedSku> fakeSkus = null,
            Dictionary<Guid, List<Microsoft.Graph.Models.User>> fakeUsersBySku = null,
            Dictionary<string, List<LicenseDetails>> fakeLicenseDetails = null)
        {
            _fakeUsers = fakeUsers ?? new List<GraphUser>();
            _fakeSkus = fakeSkus;
            _fakeUsersBySku = fakeUsersBySku ?? new Dictionary<Guid, List<Microsoft.Graph.Models.User>>();
            _fakeLicenseDetails = fakeLicenseDetails ?? new Dictionary<string, List<LicenseDetails>>();
            _deltaProvider = new FakeDeltaValueProvider();
        }

        /// <summary>
        /// Replaces the fake Graph state for a subsequent import run while keeping
        /// the same loader (and therefore the same delta provider) so tests can
        /// simulate persistent-delta-token scenarios such as a customer running
        /// against Redis.
        /// </summary>
        public void SetFakeState(
            List<GraphUser> fakeUsers,
            List<SubscribedSku> fakeSkus,
            Dictionary<Guid, List<Microsoft.Graph.Models.User>> fakeUsersBySku)
        {
            _fakeUsers = fakeUsers ?? new List<GraphUser>();
            _fakeSkus = fakeSkus;
            _fakeUsersBySku = fakeUsersBySku ?? new Dictionary<Guid, List<Microsoft.Graph.Models.User>>();
        }

        /// <summary>
        /// Optional hook invoked by LoadUsersBySku before returning. Tests can
        /// set this to throw an exception, simulating a Graph error mid-import.
        /// </summary>
        public Func<Guid, Task> OnLoadUsersBySku { get; set; }

        public IDeltaValueProvider DeltaValueProvider => _deltaProvider;

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Simulate GraphUserLoader behavior: buffer the new delta token in
            // memory; only CommitDeltaTokenAsync persists it.
            _pendingDeltaToken = SimulatedNewDeltaToken;
            _hasPendingDeltaToken = true;

            // Simulate Graph delta behaviour: when a delta token is already
            // persisted and the test has supplied a delta-only subset, return
            // just that subset (mirrors the real /users/delta which only
            // returns users whose tracked properties have changed).
            var existingDelta = await _deltaProvider.GetDeltaToken();
            if (!string.IsNullOrEmpty(existingDelta) && DeltaUsersOverride != null)
            {
                return new List<GraphUser>(DeltaUsersOverride);
            }

            // Return a defensive copy so callers that Clear() the result
            // do not wipe the original list (InsertAndUpdateDatabaseFromExternalUsers does this).
            return new List<GraphUser>(_fakeUsers);
        }

        public Task<List<SubscribedSku>> LoadTenantSkus()
        {
            return Task.FromResult(_fakeSkus);
        }

        public async Task<List<Microsoft.Graph.Models.User>> LoadUsersBySku(Guid skuId)
        {
            if (OnLoadUsersBySku != null)
            {
                await OnLoadUsersBySku(skuId);
            }

            // Return a defensive copy, like LoadAllActiveUsers above: GraphUserLoader materialises a
            // fresh list per call and the licence refresh Clear()s the result to free memory, so
            // handing out the stored list would empty the fake's state after the first import.
            if (_fakeUsersBySku.ContainsKey(skuId))
            {
                return new List<Microsoft.Graph.Models.User>(_fakeUsersBySku[skuId]);
            }
            return new List<Microsoft.Graph.Models.User>();
        }

        public Task<List<LicenseDetails>> LoadUserLicenseDetails(string userId)
        {
            if (_fakeLicenseDetails.ContainsKey(userId))
            {
                return Task.FromResult(_fakeLicenseDetails[userId]);
            }
            return Task.FromResult<List<LicenseDetails>>(null);
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
