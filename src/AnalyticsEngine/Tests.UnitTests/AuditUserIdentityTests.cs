using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for resolving the Office 365 Management Activity API's <c>UserId</c> to a user principal
    /// name before it becomes a <c>dbo.users</c> row.
    /// </summary>
    /// <remarks>
    /// The importer stages <c>UserId</c> straight into <c>users.user_name</c> and the merge creates a
    /// row for any value it has not seen. The common schema does not guarantee a UPN there - Microsoft
    /// documents Entra object ids, Windows SIDs and service principals such as <c>app@sharepoint</c> in
    /// the same field - so without this, one person seen under an object id silently becomes a second
    /// "user" and every per-user figure splits between the two.
    /// </remarks>
    [TestClass]
    public class AuditUserIdentityTests
    {
        #region Classification

        [TestMethod]
        public void AUpnIsRecognisedAndNeedsNoResolution()
        {
            Assert.AreEqual(AuditUserIdKind.Upn, AuditUserIdentity.Classify("ada@contoso.com"));
            Assert.IsFalse(AuditUserIdentity.NeedsResolving("ada@contoso.com"));
        }

        [TestMethod]
        public void AnEntraObjectIdIsRecognisedAndNeedsResolution()
        {
            const string objectId = "00000000-0000-0000-0000-000000000001";

            Assert.AreEqual(AuditUserIdKind.EntraObjectId, AuditUserIdentity.Classify(objectId));
            Assert.IsTrue(AuditUserIdentity.NeedsResolving(objectId));

            // Braced and upper-case forms are still GUIDs.
            Assert.AreEqual(AuditUserIdKind.EntraObjectId, AuditUserIdentity.Classify("{00000000-0000-0000-0000-000000000001}"));
            Assert.AreEqual(AuditUserIdKind.EntraObjectId, AuditUserIdentity.Classify("00000000-0000-0000-0000-00000000000A".ToUpperInvariant()));
        }

        /// <summary>
        /// Service principals must never be resolved against Entra or counted as people. `app@sharepoint`
        /// contains an `@`, so it would otherwise be classified as a UPN.
        /// </summary>
        [TestMethod]
        public void ServicePrincipalsAreNotPeopleAndAreNotResolved()
        {
            foreach (var value in new[] { "app@sharepoint", "APP@SHAREPOINT", "DlpAgent", "dlpagent", "S-1-5-18", "S-1-12-1-4141" })
            {
                Assert.AreEqual(AuditUserIdKind.SystemAccount, AuditUserIdentity.Classify(value), value);
                Assert.IsTrue(AuditUserIdentity.IsSystemAccount(value), value);
                Assert.IsFalse(AuditUserIdentity.NeedsResolving(value), value);
            }
        }

        [TestMethod]
        public void MissingAndUnrecognisedValuesAreLeftAlone()
        {
            Assert.AreEqual(AuditUserIdKind.Missing, AuditUserIdentity.Classify(null));
            Assert.AreEqual(AuditUserIdKind.Missing, AuditUserIdentity.Classify("   "));
            Assert.AreEqual(AuditUserIdKind.Other, AuditUserIdentity.Classify("SHAREPOINT\\system"));

            // An unrecognised value must never be resolved - that would be guesswork.
            Assert.IsFalse(AuditUserIdentity.NeedsResolving("SHAREPOINT\\system"));
            Assert.IsFalse(AuditUserIdentity.NeedsResolving(null));
        }

        #endregion

        #region Resolution policy

        private sealed class FakeEntraIdStore : IUserEntraIdStore
        {
            public Dictionary<string, string> Known { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Queried { get; } = new List<string>();
            public Dictionary<string, string> Stored { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Exception StoreThrows { get; set; }

            public Task<IReadOnlyDictionary<string, string>> GetUpnsByEntraObjectIdAsync(IReadOnlyCollection<string> ids)
            {
                Queried.AddRange(ids);
                var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids)
                {
                    string upn;
                    if (Known.TryGetValue(id, out upn)) hits[id] = upn;
                }
                return Task.FromResult<IReadOnlyDictionary<string, string>>(hits);
            }

            public Task StoreEntraObjectIdAsync(IReadOnlyDictionary<string, string> map)
            {
                if (StoreThrows != null) throw StoreThrows;
                foreach (var pair in map) Stored[pair.Key] = pair.Value;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeEntraLookup : IEntraUserLookup
        {
            public Dictionary<string, string> Directory { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Queried { get; } = new List<string>();
            public Exception Throws { get; set; }

            public Task<IReadOnlyDictionary<string, string>> GetUpnsByObjectIdAsync(IReadOnlyCollection<string> ids)
            {
                if (Throws != null) throw Throws;
                Queried.AddRange(ids);
                var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids)
                {
                    string upn;
                    if (Directory.TryGetValue(id, out upn)) hits[id] = upn;
                }
                return Task.FromResult<IReadOnlyDictionary<string, string>>(hits);
            }
        }

        private const string ObjectIdA = "00000000-0000-0000-0000-0000000000a1";
        private const string ObjectIdB = "00000000-0000-0000-0000-0000000000b2";

        /// <summary>
        /// The database is authoritative and must be asked first: the Graph user import already stores
        /// every user's object id, so a tenant running it should never pay a directory round-trip here.
        /// </summary>
        [TestMethod]
        public async Task TheDatabaseAnswersFirstAndEntraIsNotCalledAtAll()
        {
            var store = new FakeEntraIdStore();
            store.Known[ObjectIdA] = "ada@contoso.com";
            var entra = new FakeEntraLookup();

            var resolver = new AuditUserIdentityResolver(store, entra);
            var resolved = await resolver.ResolveToUpnAsync(new[] { ObjectIdA });

            Assert.AreEqual("ada@contoso.com", resolved[ObjectIdA]);
            Assert.AreEqual(0, entra.Queried.Count, "Entra must not be called for an id the database already knows.");
            Assert.AreEqual(0, resolver.EntraLookupCount);
        }

        [TestMethod]
        public async Task EntraIsAskedOnlyForWhatTheDatabaseCouldNotAnswer()
        {
            var store = new FakeEntraIdStore();
            store.Known[ObjectIdA] = "ada@contoso.com";
            var entra = new FakeEntraLookup();
            entra.Directory[ObjectIdB] = "grace@contoso.com";

            var resolver = new AuditUserIdentityResolver(store, entra);
            var resolved = await resolver.ResolveToUpnAsync(new[] { ObjectIdA, ObjectIdB });

            Assert.AreEqual("ada@contoso.com", resolved[ObjectIdA]);
            Assert.AreEqual("grace@contoso.com", resolved[ObjectIdB]);
            CollectionAssert.AreEqual(new[] { ObjectIdB }, entra.Queried.ToArray(),
                "Only the id the database could not answer should reach Entra.");
        }

        /// <summary>
        /// What Entra teaches us is written back, so the next cycle is answered from SQL. Without this the
        /// same id costs a directory read on every import cycle, forever.
        /// </summary>
        [TestMethod]
        public async Task AnIdLearnedFromEntraIsRecordedAgainstTheUser()
        {
            var store = new FakeEntraIdStore();
            var entra = new FakeEntraLookup();
            entra.Directory[ObjectIdA] = "ada@contoso.com";

            var resolver = new AuditUserIdentityResolver(store, entra);
            await resolver.ResolveToUpnAsync(new[] { ObjectIdA });

            Assert.AreEqual("ada@contoso.com", store.Stored[ObjectIdA]);
        }

        /// <summary>
        /// A deleted user can never resolve. Without a negative cache it would be looked up again for
        /// every event that mentions it - the per-event Graph call this codebase forbids at scale.
        /// </summary>
        [TestMethod]
        public async Task AnUnresolvableIdIsAskedForExactlyOnce()
        {
            var store = new FakeEntraIdStore();
            var entra = new FakeEntraLookup();

            var resolver = new AuditUserIdentityResolver(store, entra);
            await resolver.ResolveToUpnAsync(new[] { ObjectIdA });
            await resolver.ResolveToUpnAsync(new[] { ObjectIdA });
            await resolver.ResolveToUpnAsync(new[] { ObjectIdA });

            Assert.AreEqual(1, entra.Queried.Count, "An id proven unresolvable must not be asked for again.");
            Assert.AreEqual(1, store.Queried.Count, "Nor should the database be re-queried for it.");
            Assert.AreEqual(1, resolver.UnresolvedCount);
        }

        [TestMethod]
        public async Task AResolvedIdIsCachedAndNeitherSourceIsAskedAgain()
        {
            var store = new FakeEntraIdStore();
            store.Known[ObjectIdA] = "ada@contoso.com";
            var entra = new FakeEntraLookup();

            var resolver = new AuditUserIdentityResolver(store, entra);
            await resolver.ResolveToUpnAsync(new[] { ObjectIdA });
            var second = await resolver.ResolveToUpnAsync(new[] { ObjectIdA });

            Assert.AreEqual("ada@contoso.com", second[ObjectIdA]);
            Assert.AreEqual(1, store.Queried.Count);
        }

        /// <summary>
        /// A directory outage must not fail the import. The events still save with their raw identifier,
        /// exactly as they did before this resolution existed.
        /// </summary>
        [TestMethod]
        public async Task ADirectoryFailureLeavesTheIdUnresolvedRatherThanFailingTheImport()
        {
            var store = new FakeEntraIdStore();
            var entra = new FakeEntraLookup { Throws = new InvalidOperationException("Graph is down") };

            var resolver = new AuditUserIdentityResolver(store, entra);
            var resolved = await resolver.ResolveToUpnAsync(new[] { ObjectIdA });

            Assert.AreEqual(0, resolved.Count);
        }

        /// <summary>Write-back is an optimisation for later cycles; failing it must not lose this cycle's answer.</summary>
        [TestMethod]
        public async Task AFailedWriteBackStillReturnsTheResolvedUpn()
        {
            var store = new FakeEntraIdStore { StoreThrows = new InvalidOperationException("SQL is busy") };
            var entra = new FakeEntraLookup();
            entra.Directory[ObjectIdA] = "ada@contoso.com";

            var resolver = new AuditUserIdentityResolver(store, entra);
            var resolved = await resolver.ResolveToUpnAsync(new[] { ObjectIdA });

            Assert.AreEqual("ada@contoso.com", resolved[ObjectIdA]);
        }

        [TestMethod]
        public async Task DuplicateIdsInOneBatchAreLookedUpOnce()
        {
            var store = new FakeEntraIdStore();
            store.Known[ObjectIdA] = "ada@contoso.com";
            var entra = new FakeEntraLookup();

            var resolver = new AuditUserIdentityResolver(store, entra);
            await resolver.ResolveToUpnAsync(new[] { ObjectIdA, ObjectIdA, ObjectIdA.ToUpperInvariant() });

            Assert.AreEqual(1, store.Queried.Count, "The same id, however it is cased, is one lookup.");
        }

        [TestMethod]
        public async Task NothingToResolveCallsNeitherSource()
        {
            var store = new FakeEntraIdStore();
            var entra = new FakeEntraLookup();

            var resolver = new AuditUserIdentityResolver(store, entra);
            var resolved = await resolver.ResolveToUpnAsync(new string[0]);

            Assert.AreEqual(0, resolved.Count);
            Assert.AreEqual(0, store.Queried.Count);
            Assert.AreEqual(0, entra.Queried.Count);
        }

        #endregion
    }
}
