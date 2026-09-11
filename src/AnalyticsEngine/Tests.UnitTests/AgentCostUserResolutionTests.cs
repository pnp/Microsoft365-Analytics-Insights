using Common.Entities;
using Common.Entities.AgentCosts;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.AgentCosts;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for attributing billed Copilot Studio credits to a person.
    ///
    /// <para>The licensing API reports a user as an Entra object id. <c>dbo.users.azure_ad_id</c> already
    /// holds exactly that value - the user import maps it from Graph's <c>user.id</c> - so the two join
    /// directly. Anyone not in <c>dbo.users</c> is fetched from the directory by object id.</para>
    ///
    /// <para>The rule every test here defends: <b>attribution must never cost the customer their billing
    /// data</b>. A user who cannot be resolved, for any reason, still has their spend recorded against the
    /// raw identifier; only the link is missing, and it is retried later.</para>
    /// </summary>
    [TestClass]
    public class AgentCostUserResolutionTests
    {
        private static readonly string Marker = "agentcost-user-test-" + Guid.NewGuid().ToString("N");

        #region Fakes

        private class FakeLinkStore : IAgentCostUserLinkStore
        {
            public Dictionary<string, int> Known { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<EntraUserRef> Created { get; } = new List<EntraUserRef>();
            public List<string> Outstanding { get; } = new List<string>();
            public IReadOnlyDictionary<string, int> Applied { get; private set; }
            public int NextId = 1000;

            public Task<IReadOnlyDictionary<string, int>> GetUserIdsByObjectIdAsync(IReadOnlyCollection<string> objectIds)
            {
                var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in objectIds)
                {
                    if (Known.TryGetValue(id, out var userId)) found[id] = userId;
                }
                return Task.FromResult((IReadOnlyDictionary<string, int>)found);
            }

            public Task<int> EnsureUserAsync(EntraUserRef user)
            {
                Created.Add(user);
                return Task.FromResult(NextId++);
            }

            public Task<IReadOnlyList<string>> GetUnresolvedObjectIdsAsync(int max)
                => Task.FromResult((IReadOnlyList<string>)Outstanding.Take(max).ToList());

            public Task<int> ApplyUserLinksAsync(IReadOnlyDictionary<string, int> userIdsByObjectId)
            {
                Applied = userIdsByObjectId;
                return Task.FromResult(userIdsByObjectId.Count);
            }
        }

        private class FakeDirectory : IEntraUserLookup
        {
            public Dictionary<string, EntraUserRef> Users { get; } = new Dictionary<string, EntraUserRef>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Throw { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int Calls { get; private set; }

            public Task<EntraUserRef> GetUserByObjectIdAsync(string objectId)
            {
                Calls++;
                if (Throw.Contains(objectId)) throw new TimeoutException("throttled");
                Users.TryGetValue(objectId, out var user);
                return Task.FromResult(user);
            }
        }

        private static AgentCostUserResolver Resolver(FakeLinkStore store, FakeDirectory directory, int budget = 200)
            => new AgentCostUserResolver(store, directory, NullLogger.Instance, budget);

        #endregion

        #region Resolution logic

        [TestMethod]
        public async Task AUserAlreadyInTheDatabaseCostsNoDirectoryCall()
        {
            var store = new FakeLinkStore();
            store.Known["11111111-1111-1111-1111-111111111111"] = 42;
            var directory = new FakeDirectory();

            var result = await Resolver(store, directory).ResolveAsync(new[] { "11111111-1111-1111-1111-111111111111" });

            Assert.AreEqual(42, result.UserIdsByObjectId["11111111-1111-1111-1111-111111111111"]);
            Assert.AreEqual(1, result.ResolvedFromDatabase);
            Assert.AreEqual(0, directory.Calls,
                "azure_ad_id already holds the object id, so the common case must not touch Graph at all.");
        }

        [TestMethod]
        public async Task AUserMissingFromTheDatabaseIsFetchedFromTheDirectoryAndCreated()
        {
            var store = new FakeLinkStore();
            var directory = new FakeDirectory();
            directory.Users["22222222-2222-2222-2222-222222222222"] = new EntraUserRef
            {
                UserPrincipalName = "newstarter@contoso.onmicrosoft.com",
                Mail = "newstarter@contoso.com",
            };

            var result = await Resolver(store, directory).ResolveAsync(new[] { "22222222-2222-2222-2222-222222222222" });

            Assert.AreEqual(1, result.ResolvedFromDirectory);
            Assert.AreEqual(1, store.Created.Count);
            Assert.AreEqual("newstarter@contoso.onmicrosoft.com", store.Created[0].UserPrincipalName);
            Assert.AreEqual("22222222-2222-2222-2222-222222222222", store.Created[0].ObjectId,
                "The object id must be carried onto the created user, or the next cycle would look them up again.");
        }

        [TestMethod]
        public async Task ADeletedUserIsSettledNotRetriedForEver()
        {
            // Graph answering "no such object" is a permanent state for a billing row from a past period.
            var store = new FakeLinkStore();
            var directory = new FakeDirectory(); // Knows nobody, so returns null.

            var result = await Resolver(store, directory).ResolveAsync(new[] { "33333333-3333-3333-3333-333333333333" });

            Assert.AreEqual(1, result.NotInDirectory);
            Assert.AreEqual(0, result.LookupFailures, "A definitive 'no such user' is not a failure to look up.");
            Assert.AreEqual(0, store.Created.Count, "Nothing should be invented for a user the directory does not have.");
        }

        [TestMethod]
        public async Task ADirectoryFailureIsCountedSeparatelyFromADeletedUser()
        {
            // The distinction matters: one is retried next cycle, the other never resolves. Conflating
            // them either abandons a resolvable user or re-asks about a deleted one for ever.
            var store = new FakeLinkStore();
            var directory = new FakeDirectory();
            directory.Throw.Add("44444444-4444-4444-4444-444444444444");

            var result = await Resolver(store, directory).ResolveAsync(new[] { "44444444-4444-4444-4444-444444444444" });

            Assert.AreEqual(1, result.LookupFailures);
            Assert.AreEqual(0, result.NotInDirectory);
        }

        [TestMethod]
        public async Task ADirectoryFailureNeverPreventsTheOtherUsersResolving()
        {
            var store = new FakeLinkStore();
            store.Known["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"] = 7;
            var directory = new FakeDirectory();
            directory.Throw.Add("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            directory.Users["cccccccc-cccc-cccc-cccc-cccccccccccc"] = new EntraUserRef { UserPrincipalName = "c@contoso.com" };

            var result = await Resolver(store, directory).ResolveAsync(new[]
            {
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "cccccccc-cccc-cccc-cccc-cccccccccccc",
            });

            Assert.AreEqual(2, result.TotalResolved, "One user's lookup failing must not abandon the rest.");
            Assert.AreEqual(1, result.LookupFailures);
        }

        [TestMethod]
        public async Task TheDirectoryLookupBudgetIsRespectedAndTheRestAreDeferred()
        {
            // Without a cap, a tenant that has never run the user import would issue one Graph call per
            // user on the first agent-cost import.
            var store = new FakeLinkStore();
            var directory = new FakeDirectory();
            var ids = Enumerable.Range(0, 10).Select(i => $"00000000-0000-0000-0000-{i:D12}").ToList();
            foreach (var id in ids) directory.Users[id] = new EntraUserRef { UserPrincipalName = id + "@contoso.com" };

            var result = await Resolver(store, directory, budget: 3).ResolveAsync(ids);

            Assert.AreEqual(3, directory.Calls, "The budget must cap directory calls.");
            Assert.AreEqual(3, result.ResolvedFromDirectory);
            Assert.AreEqual(7, result.Deferred, "Everything over budget must be deferred, not dropped.");
        }

        [TestMethod]
        public async Task WithNoDirectoryWiredInEveryUnknownUserIsDeferredRatherThanFailing()
        {
            var store = new FakeLinkStore();
            var resolver = new AgentCostUserResolver(store, null, NullLogger.Instance);

            var result = await resolver.ResolveAsync(new[] { "55555555-5555-5555-5555-555555555555" });

            Assert.AreEqual(1, result.Deferred);
            Assert.AreEqual(0, result.TotalResolved);
        }

        [TestMethod]
        public async Task DuplicateAndBlankIdentifiersAreCollapsedBeforeAnyLookup()
        {
            var store = new FakeLinkStore();
            var directory = new FakeDirectory();
            directory.Users["66666666-6666-6666-6666-666666666666"] = new EntraUserRef { UserPrincipalName = "six@contoso.com" };

            var result = await Resolver(store, directory).ResolveAsync(new[]
            {
                "66666666-6666-6666-6666-666666666666",
                "66666666-6666-6666-6666-666666666666",
                "  ", null, "",
            });

            Assert.AreEqual(1, directory.Calls, "The same user appears on many rows; they must be asked about once.");
            Assert.AreEqual(1, result.TotalResolved);
        }

        [TestMethod]
        public async Task OutstandingRowsAreReResolvedAndTheLinksApplied()
        {
            // Rows outside the trailing window are never re-read by the import, so without this pass a
            // user who appeared five minutes too late would stay unattributed for ever.
            var store = new FakeLinkStore();
            store.Outstanding.Add("77777777-7777-7777-7777-777777777777");
            store.Known["77777777-7777-7777-7777-777777777777"] = 99;

            var result = await Resolver(store, new FakeDirectory()).ResolveOutstandingAsync();

            Assert.AreEqual(1, result.ResolvedFromDatabase);
            Assert.IsNotNull(store.Applied, "Resolved links must actually be written back to the rows.");
            Assert.AreEqual(99, store.Applied["77777777-7777-7777-7777-777777777777"]);
        }

        #endregion

        #region SQL: the foreign key itself

        [TestCleanup]
        public void Cleanup()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = db.CopilotStudioCreditUserDaily.Where(r => r.EnvironmentId == Marker).ToList();
                db.CopilotStudioCreditUserDaily.RemoveRange(rows);
                db.SaveChanges();

                var users = db.users.Where(u => u.UserPrincipalName.StartsWith(Marker)).ToList();
                db.users.RemoveRange(users);
                db.SaveChanges();
            }
        }

        [TestMethod]
        public async Task AResolvedRowLoadsItsUserThroughTheForeignKey()
        {
            int userId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User
                {
                    UserPrincipalName = Marker + "-linked@contoso.onmicrosoft.com",
                    AzureAdId = "88888888-8888-8888-8888-888888888888",
                };
                db.users.Add(user);
                await db.SaveChangesAsync();
                userId = user.ID;

                db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                {
                    UsageDate = new DateTime(2001, 2, 1),
                    EntraObjectId = "88888888-8888-8888-8888-888888888888",
                    UserId = userId,
                    EnvironmentId = Marker,
                    BilledCredits = 5m,
                    DimensionHash = Guid.NewGuid().ToString("N"),
                    ImportedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var row = await db.CopilotStudioCreditUserDaily
                    .Include(r => r.User)
                    .FirstAsync(r => r.EnvironmentId == Marker);

                Assert.IsNotNull(row.User, "The relationship must load - this is the whole point of the key.");
                Assert.AreEqual(Marker + "-linked@contoso.onmicrosoft.com", row.User.UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task AnUnresolvedRowIsStoredWithItsSpendAndANullLink()
        {
            // The single most important behaviour here: a user we cannot identify must not cost us the money.
            using (var db = new AnalyticsEntitiesContext())
            {
                db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                {
                    UsageDate = new DateTime(2001, 2, 2),
                    EntraObjectId = "99999999-9999-9999-9999-999999999999",
                    UserId = null,
                    EnvironmentId = Marker,
                    BilledCredits = 12.5m,
                    DimensionHash = Guid.NewGuid().ToString("N"),
                    ImportedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var row = await db.CopilotStudioCreditUserDaily.FirstAsync(r => r.EnvironmentId == Marker);
                Assert.IsNull(row.UserId);
                Assert.AreEqual(12.5m, row.BilledCredits, "The spend must survive the user being unidentifiable.");
                Assert.AreEqual("99999999-9999-9999-9999-999999999999", row.EntraObjectId,
                    "The raw identifier must be kept so the row can be attributed later.");
            }
        }

        [TestMethod]
        public async Task TheStoreLinksAnExistingUserByTheirObjectIdWithoutCreatingAnything()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new User
                {
                    UserPrincipalName = Marker + "-existing@contoso.onmicrosoft.com",
                    AzureAdId = "abababab-abab-abab-abab-abababababab",
                });
                await db.SaveChangesAsync();
            }

            var store = new SqlAgentCostUserLinkStore(DefaultAnalyticsDbContextFactory.Instance, NullLogger.Instance);
            var found = await store.GetUserIdsByObjectIdAsync(new[] { "abababab-abab-abab-abab-abababababab" });

            Assert.AreEqual(1, found.Count, "azure_ad_id is the join key and it is already populated by the user import.");
        }

        [TestMethod]
        public async Task EnsureUserMatchesOnUpnAndBackfillsAMissingObjectId()
        {
            // Users arrive in dbo.users from several places - the audit staging merge inserts them by user
            // name with no object id at all. Inserting on object id alone would duplicate that person.
            var upn = Marker + "-byupn@contoso.onmicrosoft.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new User { UserPrincipalName = upn, AzureAdId = string.Empty });
                await db.SaveChangesAsync();
            }

            var store = new SqlAgentCostUserLinkStore(DefaultAnalyticsDbContextFactory.Instance, NullLogger.Instance);
            var id = await store.EnsureUserAsync(new EntraUserRef
            {
                ObjectId = "cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd",
                UserPrincipalName = upn,
            });

            using (var db = new AnalyticsEntitiesContext())
            {
                var matching = db.users.Where(u => u.UserPrincipalName == upn).ToList();
                Assert.AreEqual(1, matching.Count, "Matching on the UPN first is what stops a duplicate user being created.");
                Assert.AreEqual(id, matching[0].ID);
                Assert.AreEqual("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd", matching[0].AzureAdId,
                    "Backfilling the object id means this user never needs another directory call.");
            }
        }

        [TestMethod]
        public async Task AnUnresolvedRowIsFoundAndLinkedByTheReAttributionPass()
        {
            var objectId = "efefefef-efef-efef-efef-efefefefefef";
            var upn = Marker + "-latecomer@contoso.onmicrosoft.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                {
                    UsageDate = new DateTime(2001, 2, 3),
                    EntraObjectId = objectId,
                    UserId = null,
                    EnvironmentId = Marker,
                    BilledCredits = 3m,
                    DimensionHash = Guid.NewGuid().ToString("N"),
                    ImportedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();

                // The user turns up in the directory only afterwards.
                db.users.Add(new User { UserPrincipalName = upn, AzureAdId = objectId });
                await db.SaveChangesAsync();
            }

            var store = new SqlAgentCostUserLinkStore(DefaultAnalyticsDbContextFactory.Instance, NullLogger.Instance);
            var outstanding = await store.GetUnresolvedObjectIdsAsync(100);
            Assert.IsTrue(outstanding.Contains(objectId), "An unlinked row must be offered up for re-attribution.");

            var resolved = await store.GetUserIdsByObjectIdAsync(new[] { objectId });
            var updated = await store.ApplyUserLinksAsync(resolved);
            Assert.IsTrue(updated >= 1);

            using (var db = new AnalyticsEntitiesContext())
            {
                var row = await db.CopilotStudioCreditUserDaily.FirstAsync(r => r.EnvironmentId == Marker);
                Assert.IsNotNull(row.UserId, "The row must now be attributed - this is what makes the null link self-healing.");
            }
        }

        /// <summary>
        /// Exercises the actual report query the page calls. The other tests here prove the key exists and
        /// loads via <c>Include</c>; this proves the aggregation EF has to translate actually runs, which is
        /// a different risk - a group projection that reaches through a navigation property is exactly the
        /// shape EF6 refuses to translate, and it would fail at page load rather than at build.
        /// </summary>
        [TestMethod]
        public async Task TheTopUsersReportReturnsTheUpnAndStillReportsUnresolvedSpend()
        {
            var objectId = "12121212-1212-1212-1212-121212121212";
            var upn = Marker + "-report@contoso.onmicrosoft.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { UserPrincipalName = upn, AzureAdId = objectId };
                db.users.Add(user);
                await db.SaveChangesAsync();

                // Same person, two days - must aggregate into one row with two active days.
                foreach (var day in new[] { new DateTime(2001, 3, 1), new DateTime(2001, 3, 2) })
                {
                    db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                    {
                        UsageDate = day,
                        EntraObjectId = objectId,
                        UserId = user.ID,
                        EnvironmentId = Marker,
                        BilledCredits = 4m,
                        DimensionHash = Guid.NewGuid().ToString("N"),
                        ImportedUtc = DateTime.UtcNow,
                    });
                }

                // Somebody who could not be resolved. Their spend must still appear.
                db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                {
                    UsageDate = new DateTime(2001, 3, 1),
                    EntraObjectId = "34343434-3434-3434-3434-343434343434",
                    UserId = null,
                    EnvironmentId = Marker,
                    BilledCredits = 9m,
                    DimensionHash = Guid.NewGuid().ToString("N"),
                    ImportedUtc = DateTime.UtcNow,
                });

                await db.SaveChangesAsync();
            }

            var store = new SqlAgentCostReportStore(DefaultAnalyticsDbContextFactory.Instance);
            var rows = await store.GetTopUsersAsync(new AgentCostQuery
            {
                FromUtc = new DateTime(2001, 2, 1),
                ToUtc = new DateTime(2001, 4, 1),
                EnvironmentId = Marker,
            }, 20);

            var resolved = rows.SingleOrDefault(r => r.EntraObjectId == objectId);
            Assert.IsNotNull(resolved, "The resolved user must appear in the report.");
            Assert.AreEqual(upn, resolved.UserPrincipalName,
                "The whole point of the foreign key is that the report can name the person.");
            Assert.AreEqual(8m, resolved.BilledCredits, "Both days must aggregate into one row.");
            Assert.AreEqual(2, resolved.ActiveDays);

            var unresolved = rows.SingleOrDefault(r => r.EntraObjectId == "34343434-3434-3434-3434-343434343434");
            Assert.IsNotNull(unresolved, "An unresolved user must still be reported - the spend is real.");
            Assert.IsNull(unresolved.UserPrincipalName, "Unresolved must be distinguishable, not a blank name.");
            Assert.AreEqual(9m, unresolved.BilledCredits);
        }

        /// <summary>
        /// A person part-way through re-attribution - some rows linked, some not - must appear ONCE.
        /// </summary>
        /// <remarks>
        /// This is the state between an import storing a row it could not attribute and the re-attribution
        /// pass linking it. Grouping on the link rather than the identity would show the same person twice,
        /// once named and once as "Unresolved", and the two halves of their spend would look like two
        /// different people on a page whose whole job is to say who spent what.
        /// </remarks>
        [TestMethod]
        public async Task APersonPartWayThroughReAttributionIsReportedOnceNotTwice()
        {
            var objectId = "56565656-5656-5656-5656-565656565656";
            var upn = Marker + "-halflinked@contoso.onmicrosoft.com";

            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { UserPrincipalName = upn, AzureAdId = objectId };
                db.users.Add(user);
                await db.SaveChangesAsync();

                db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                {
                    UsageDate = new DateTime(2001, 3, 5),
                    EntraObjectId = objectId,
                    UserId = user.ID,          // linked
                    EnvironmentId = Marker,
                    BilledCredits = 2m,
                    DimensionHash = Guid.NewGuid().ToString("N"),
                    ImportedUtc = DateTime.UtcNow,
                });
                db.CopilotStudioCreditUserDaily.Add(new CopilotStudioCreditUserDaily
                {
                    UsageDate = new DateTime(2001, 3, 6),
                    EntraObjectId = objectId,
                    UserId = null,             // same person, not yet linked
                    EnvironmentId = Marker,
                    BilledCredits = 3m,
                    DimensionHash = Guid.NewGuid().ToString("N"),
                    ImportedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var store = new SqlAgentCostReportStore(DefaultAnalyticsDbContextFactory.Instance);
            var rows = await store.GetTopUsersAsync(new AgentCostQuery
            {
                FromUtc = new DateTime(2001, 2, 1),
                ToUtc = new DateTime(2001, 4, 1),
                EnvironmentId = Marker,
            }, 20);

            var forPerson = rows.Where(r => r.EntraObjectId == objectId).ToList();
            Assert.AreEqual(1, forPerson.Count,
                "One person must be one row, even while some of their rows are still unattributed.");
            Assert.AreEqual(5m, forPerson[0].BilledCredits, "Their whole spend must be counted, linked or not.");
            Assert.AreEqual(upn, forPerson[0].UserPrincipalName,
                "Knowing who they are on ANY row is enough to name them on all of them.");
        }

        #endregion
    }
}
