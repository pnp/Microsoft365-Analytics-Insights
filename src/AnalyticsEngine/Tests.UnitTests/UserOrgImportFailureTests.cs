using Common.Entities;
using Common.Entities.Config;
using Common.Entities.UserOrgs;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// What happens when Microsoft Graph refuses the configured organisation attributes.
    /// </summary>
    /// <remarks>
    /// The single most important property of this feature is that it can never take the user import
    /// down with it. Graph answers an unknown <c>$select</c> property with a 400 that fails the
    /// <b>whole</b> request, so an attribute that stops existing after it was configured would
    /// otherwise stop licences, managers and department metadata importing as well.
    /// </remarks>
    [TestClass]
    public class UserOrgImportFailureTests
    {
        private const string DeltaPayload =
            "{\"value\":[{\"id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"userPrincipalName\":\"a@contoso.com\",\"accountEnabled\":true," +
            "\"onPremisesExtensionAttributes\":{\"extensionAttribute1\":\"Retail\"}}]," +
            "\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=NEWTOKEN\"}";

        private static GraphUserOrgSelection OneOrgAttribute()
        {
            return GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute1" });
        }

        private static GraphUserLoader BuildLoader(RecordingHandler handler, out FakeDeltaValueProvider provider)
        {
            provider = new FakeDeltaValueProvider();
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var client = new ManualGraphCallClient(handler, logger);
            return new GraphUserLoader(client, provider, logger, null);
        }

        [TestMethod]
        public async Task HappyPath_AsksForTheOrgPropertyAndQualifiesTheDeltaKey()
        {
            var handler = new RecordingHandler(_ => Ok(DeltaPayload));
            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);
            var selection = OneOrgAttribute();

            loader.SetOrgSelection(selection);
            var users = await loader.LoadAllActiveUsers();

            Assert.AreEqual(1, users.Count);
            Assert.IsFalse(loader.OrgSelectionWasRejected);
            StringAssert.Contains(handler.Urls.Single(), "onPremisesExtensionAttributes");
            Assert.AreEqual(
                selection.DeltaKeyQualifier,
                provider.KeyQualifier,
                "A token minted with the org property must be stored under the qualified key.");
            Assert.AreEqual(
                "Retail",
                UserOrgRules.ExtractRawValue(users[0].AdditionalProperties, Spec("extensionAttribute1")),
                "The org attribute must survive deserialisation into the extension-data bucket.");
        }

        [TestMethod]
        public async Task ABadAttribute_DoesNotStopTheUserImport()
        {
            // The regression this whole class exists for. LoadAllPagesPlusDeltaWithThrottleRetries
            // swallows a non-transient HTTP failure by default and returns the rows gathered so far, so
            // without the strict flag the 400 came back as an EMPTY USER LIST - the fallback never ran
            // and the import quietly did nothing, every cycle, with only a warning.
            var handler = new RecordingHandler(url =>
                url.Contains("onPremisesExtensionAttributes")
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"code\":\"Request_UnsupportedQuery\",\"message\":\"Could not find a property named 'onPremisesExtensionAttributes' on type 'microsoft.graph.user'.\"}}"),
                    }
                    : Ok(DeltaPayload));

            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);

            loader.SetOrgSelection(OneOrgAttribute());
            var users = await loader.LoadAllActiveUsers();

            Assert.AreEqual(1, users.Count, "The rest of the user import must still complete.");
            Assert.IsTrue(loader.OrgSelectionWasRejected, "The rejection must be reported so org values are not written.");
            Assert.AreEqual(2, handler.Urls.Count, "It should retry exactly once, without the org property.");
            Assert.IsFalse(handler.Urls[1].Contains("onPremisesExtensionAttributes"));
        }

        [TestMethod]
        public async Task AfterFallingBack_TheTokenIsStoredUnderTheUnqualifiedKey()
        {
            // Otherwise this cycle would save a token minted WITHOUT the org property under the key that
            // means "minted WITH it", and the next cycle would trust it and never see the attribute.
            var handler = new RecordingHandler(url =>
                url.Contains("onPremisesExtensionAttributes")
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
                    : Ok(DeltaPayload));

            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);

            loader.SetOrgSelection(OneOrgAttribute());
            await loader.LoadAllActiveUsers();
            await loader.CommitDeltaTokenAsync();

            Assert.AreEqual(string.Empty, provider.KeyQualifier);
            Assert.AreEqual("NEWTOKEN", await provider.GetDeltaToken());
        }

        [TestMethod]
        public async Task ATransientFailureAlsoFallsBackRatherThanFailingTheCycle()
        {
            // Making the org attempt strict must not newly propagate a 500 as a failed cycle for
            // tenants that use this feature. The fallback load is lenient - exactly what this method did
            // before org attributes existed - so the outcome matches the old behaviour.
            var handler = new RecordingHandler(url =>
                url.Contains("onPremisesExtensionAttributes")
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
                    : Ok(DeltaPayload));

            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);

            loader.SetOrgSelection(OneOrgAttribute());
            var users = await loader.LoadAllActiveUsers();

            Assert.AreEqual(1, users.Count);
            Assert.IsTrue(loader.OrgSelectionWasRejected);
        }

        [TestMethod]
        public async Task WithNoOrgAttributes_AFailureBehavesExactlyAsItDidBefore()
        {
            // The upgrade guarantee: a deployment that never configures an org type must see no change
            // at all, including in how an HTTP failure is handled. Lenient means an empty list, not an
            // exception.
            var handler = new RecordingHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });

            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);

            loader.SetOrgSelection(GraphUserOrgSelection.None);
            var users = await loader.LoadAllActiveUsers();

            Assert.AreEqual(0, users.Count);
            Assert.IsFalse(loader.OrgSelectionWasRejected);
            Assert.AreEqual(1, handler.Urls.Count, "There is nothing to fall back to, so it must not retry.");
            Assert.AreEqual(string.Empty, provider.KeyQualifier);
        }

        [TestMethod]
        public async Task AStaleDeltaTokenIsDiscardedRatherThanBlamedOnTheAttributes()
        {
            // Graph answers 400 for a delta token it will not accept as well as for a property it does
            // not recognise. Assuming the attributes were at fault used to re-point the cache key and
            // commit a fresh token under the UNQUALIFIED key, leaving the dead token on the qualified
            // key forever - so every later cycle hit it again, fell back again, and organisation values
            // never updated again while the user import looked perfectly healthy.
            var handler = new RecordingHandler(url =>
                url.Contains("deltatoken=STALE")
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":{\"code\":\"resyncRequired\",\"message\":\"Data has changed.\"}}"),
                    }
                    : Ok(DeltaPayload));

            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);
            var selection = OneOrgAttribute();
            loader.SetOrgSelection(selection);
            await provider.SetDeltaToken("STALE");

            var users = await loader.LoadAllActiveUsers();
            await loader.CommitDeltaTokenAsync();

            Assert.AreEqual(1, users.Count);
            Assert.IsFalse(
                loader.OrgSelectionWasRejected,
                "A dead token says nothing about the attributes, so org values must still be written.");
            Assert.AreEqual(
                selection.DeltaKeyQualifier,
                provider.KeyQualifier,
                "The key must stay qualified - the selection was never the problem.");
            Assert.IsTrue(
                handler.Urls[1].Contains("onPremisesExtensionAttributes"),
                "The retry must keep the org property and simply drop the token.");
            Assert.IsFalse(handler.Urls[1].Contains("deltatoken"));
            Assert.AreEqual("NEWTOKEN", await provider.GetDeltaToken());
        }

        [TestMethod]
        public async Task AnAttributeThatIsStillRejectedWithoutATokenFallsBack()
        {
            // Once a stale token has been ruled out, the selection really is the remaining suspect.
            var handler = new RecordingHandler(url =>
                url.Contains("onPremisesExtensionAttributes")
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
                    : Ok(DeltaPayload));

            FakeDeltaValueProvider provider;
            var loader = BuildLoader(handler, out provider);
            loader.SetOrgSelection(OneOrgAttribute());
            await provider.SetDeltaToken("SOMETOKEN");

            var users = await loader.LoadAllActiveUsers();

            Assert.AreEqual(1, users.Count, "The rest of the user import must still complete.");
            Assert.IsTrue(loader.OrgSelectionWasRejected);
            Assert.AreEqual(3, handler.Urls.Count, "With token, without token, then without the attributes.");
            Assert.IsFalse(handler.Urls[2].Contains("onPremisesExtensionAttributes"));
            Assert.AreEqual(string.Empty, provider.KeyQualifier);
        }

        [TestMethod]
        public void AnOrgFailureWithholdsTheDeltaTokenWithoutFailingTheImport()
        {
            // Configuring an attribute deliberately discards the token so the next cycle re-reads every
            // user - that enumeration is the only thing that populates the attribute for people who do
            // not otherwise change. Committing the token after the merge failed would throw that
            // snapshot away, and every later cycle is a small delta, so those users would never get a
            // value until somebody changed the configuration again.
            var results = new UserImportPhaseResults
            {
                InsertPhaseSucceeded = true,
                UpdatePhaseSucceeded = true,
                LicenceRefreshSucceeded = true,
                UserOrgsSucceeded = false,
            };

            Assert.IsFalse(UserImportCommitPolicy.ShouldCommitDelta(results));

            results.UserOrgsSucceeded = true;
            Assert.IsTrue(UserImportCommitPolicy.ShouldCommitDelta(results));
        }

        [TestMethod]
        public void AnImportWithNoOrgWorkStillCommitsTheToken()
        {
            // The flag defaults to true so a cycle that did no organisation work - which is every cycle
            // on a deployment that has not adopted the feature - behaves exactly as it did before.
            Assert.IsTrue(UserImportCommitPolicy.ShouldCommitDelta(new UserImportPhaseResults
            {
                InsertPhaseSucceeded = true,
                UpdatePhaseSucceeded = true,
                LicenceRefreshSucceeded = true,
            }));
        }

        [TestMethod]
        public async Task ARejectedSelectionMeansOrgValuesAreNotWritten()
        {
            // The response from a fallback carries no org properties at all, so applying it would read
            // as "every user's value was cleared" - wiping the assignments the admin is trying to fix.
            var orgTypes = new FakeUserOrgTypeStore("extensionAttribute1");
            var assignments = new FakeUserOrgAssignmentStore();

            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "a@contoso.com", AccountEnabled = true, Id = Guid.NewGuid().ToString() },
            })
            {
                OrgSelectionWasRejected = true,
            };

            await RunImport(loader, orgTypes, assignments);

            Assert.AreEqual(0, assignments.MergeCalls, "No org write may happen when Graph rejected the selection.");
        }

        [TestMethod]
        public async Task AnOrgStoreFailureDoesNotFailTheUserImport()
        {
            // User organisations are optional. A database that has not been migrated yet simply has no
            // user_org tables, and the web-jobs deliberately do not run migrations.
            var orgTypes = new FakeUserOrgTypeStore("extensionAttribute1") { Throw = true };
            var assignments = new FakeUserOrgAssignmentStore();

            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "a@contoso.com", AccountEnabled = true, Id = Guid.NewGuid().ToString() },
            });

            // Must not throw.
            await RunImport(loader, orgTypes, assignments);

            Assert.AreEqual(0, assignments.MergeCalls);
        }

        private static async Task RunImport(
            FakeUserMetadataLoader loader,
            IUserOrgTypeStore orgTypes,
            IUserOrgAssignmentStore assignments)
        {
            var updater = new UserMetadataUpdater(
                AnalyticsLogger.ConsoleOnlyTracer(),
                BuildConfig(),
                loader,
                DefaultAnalyticsDbContextFactory.Instance,
                null,
                orgTypes,
                assignments);

            await updater.InsertAndUpdateDatabaseFromExternalUsers();
        }

        private static AppConfig BuildConfig()
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Parse("00000000-0000-0000-0000-000000000001");
            config.ConnectionStrings = new AppConnectionStrings();
            return config;
        }

        private static EntraOrgAttributeSpec Spec(string name)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsTrue(EntraOrgAttributeSpec.TryParse(name, out spec, out error), error);
            return spec;
        }

        private static HttpResponseMessage Ok(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>Records every URL requested and answers from a caller-supplied rule.</summary>
        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<string, HttpResponseMessage> _respond;

            public RecordingHandler(Func<string, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            public List<string> Urls { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = Uri.UnescapeDataString(request.RequestUri.ToString());
                Urls.Add(url);
                return Task.FromResult(_respond(url));
            }
        }

        private sealed class FakeUserOrgTypeStore : IUserOrgTypeStore
        {
            private readonly List<UserOrgType> _types;

            public FakeUserOrgTypeStore(params string[] attributeNames)
            {
                _types = attributeNames.Select((a, i) => new UserOrgType
                {
                    Id = i + 1,
                    Name = "Org " + (i + 1),
                    SourceKind = UserOrgSourceKind.EntraAttribute,
                    EntraAttributeName = a,
                    IsEnabled = true,
                }).ToList();
            }

            /// <summary>Simulates the org tables not existing yet.</summary>
            public bool Throw { get; set; }

            public Task<IReadOnlyList<UserOrgType>> GetEnabledEntraTypesAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                if (Throw) throw new InvalidOperationException("Invalid object name 'dbo.user_org_types'.");
                return Task.FromResult<IReadOnlyList<UserOrgType>>(_types);
            }

            public Task<IReadOnlyList<UserOrgType>> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgType>>(_types);

            public Task<UserOrgType> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(_types.FirstOrDefault(t => t.Id == id));

            public Task<IReadOnlyList<UserOrgTypeSummary>> GetSummariesAsync(CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgTypeSummary>>(
                    _types.Select(t => new UserOrgTypeSummary { Type = t }).ToList());

            public Task<int> CreateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(0);

            public Task UpdateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken))
                => Task.CompletedTask;

            public Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
                => Task.CompletedTask;
        }

        private sealed class FakeUserOrgAssignmentStore : IUserOrgAssignmentStore
        {
            public int MergeCalls { get; private set; }

            public List<UserOrgAssignmentUpdate> LastUpdates { get; private set; } = new List<UserOrgAssignmentUpdate>();

            public Task<UserOrgMergeResult> MergeAsync(IReadOnlyList<UserOrgAssignmentUpdate> updates, CancellationToken cancellationToken = default(CancellationToken))
            {
                MergeCalls++;
                LastUpdates = updates.ToList();
                return Task.FromResult(new UserOrgMergeResult { Applied = updates.Count });
            }

            public Task<IReadOnlyList<UserOrgValueForUser>> GetForUserAsync(int userId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgValueForUser>>(new UserOrgValueForUser[0]);

            public Task<int> ClearAllForTypeAsync(int orgTypeId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(0);
        }
    }
}
