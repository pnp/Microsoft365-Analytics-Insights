extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserScope;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserScope;
using Common.Entities.UserScope.Purge;
using DataUtils.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// A User scope purge never starts from a group membership that Graph returned only part of. The group is read
    /// through the real reader, resolver and provider from canned Graph responses. The purge lock is taken in a scratch
    /// database of the test's own, never the shared unit-test database, holding only a synthetic users table.
    /// </summary>
    [TestClass]
    public class UserScopePurgeIncompleteMembershipTests
    {
        private const string PilotGroupId = "00000000-0000-0000-0000-0000000000a1";
        private const string Alice = "00000000-0000-0000-0000-000000000001";
        private const string Bob = "00000000-0000-0000-0000-000000000002";
        private const string Carol = "00000000-0000-0000-0000-000000000003";
        private const string MembersPath = "/v1.0/groups/" + PilotGroupId + "/members/microsoft.graph.user";

        private static readonly UserGroupsFilterModel Filter = new UserGroupsFilterModel(PilotGroupId);

        /// <summary>Records starts instead of running a purge, and lets go of the purge lock handed to it.</summary>
        private sealed class RecordingRunner : IUserScopePurgeRunner
        {
            public List<int> Started { get; } = new List<int>();

            public UserImportScopeResolution PreResolved { get; private set; }

            public bool IsRunning(int jobId) => Started.Contains(jobId);

            public void Start(int jobId, UserImportScopeResolution preResolved, UserScopePurgeSession lockedSession)
            {
                lockedSession?.Dispose();
                Started.Add(jobId);
                PreResolved = preResolved;
            }
        }

        private static object Member(string objectId, string name)
            => new { id = objectId, userPrincipalName = name + "@contoso.local", mail = name + "@contoso.local", accountEnabled = true };

        /// <summary>The pilot group over Graph: alice and bob on the first member page, then the test's second page.</summary>
        private static FakeGraphDirectoryHandler Graph(string secondMemberPage) => new FakeGraphDirectoryHandler()
            .On("/v1.0/groups/" + PilotGroupId + "?", "{\"id\":\"" + PilotGroupId + "\",\"displayName\":\"Copilot Pilot\"}")
            .On(MembersPath + "?$select", FakeGraphDirectoryHandler.Page("https://graph.microsoft.com" + MembersPath + "?$skiptoken=page-2",
                Member(Alice, "alice"), Member(Bob, "bob")))
            .On(MembersPath + "?$skiptoken=page-2", secondMemberPage);

        private static IUserImportScopeProvider Provider(AutoThrottleHttpClient http)
            => new UserImportScopeProvider(Filter,
                new GroupMembershipResolver(new GraphGroupDirectoryReader(http, NullLogger.Instance), NullLogger.Instance), NullLogger.Instance);

        /// <summary>
        /// The pilot group's three members and zed, who is in no group. The purge lock is an application lock, so this
        /// table - which a start counts against the scope - is all the database needs.
        /// </summary>
        private static ScratchDatabase UsersDatabase()
        {
            var database = ScratchDatabase.Create("UserScopeIncomplete");
            try
            {
                database.Execute(@"
CREATE TABLE dbo.users (id int IDENTITY(1,1) PRIMARY KEY, user_name varchar(250) NULL, mail nvarchar(max) NULL, azure_ad_id nvarchar(max) NULL);
INSERT INTO dbo.users (user_name, mail, azure_ad_id) VALUES
    ('alice@contoso.local', N'alice@contoso.local', N'" + Alice + @"'),
    ('bob@contoso.local', N'bob@contoso.local', N'" + Bob + @"'),
    ('carol@contoso.local', N'carol@contoso.local', N'" + Carol + @"'),
    ('zed@contoso.local', N'zed@contoso.local', N'00000000-0000-0000-0000-000000000009');");
                return database;
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Confirms two people outside the scope: exactly how many the first page alone leaves out (carol and zed). So the
        /// confirmed-count ceiling cannot catch a membership read as its first page only, and the filter has not changed.
        /// </summary>
        private static UserScopePurgeRequest ConfirmingTwo() => new UserScopePurgeRequest
        {
            Acknowledged = true,
            ConfirmedOutsideScopeCount = 2,
            ConfirmedFilterFingerprint = Filter.Fingerprint,
        };

        [DataTestMethod]
        [DataRow("")]
        [DataRow("{}")]
        [DataRow("{\"value\":null}")]
        public async Task StartPurge_AMemberPageWithoutItsMembers_IsRefused_AndNoPurgeIsCreated(string secondPage)
        {
            using (var database = UsersDatabase())
            using (var http = new AutoThrottleHttpClient(Graph(secondPage), NullLogger.Instance))
            {
                var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false);
                var runner = new RecordingRunner();
                var purgeDatabase = new UserScopePurgeDatabase(database.ConnectionString);
                var service = new UserScopeService(Provider(http), purgeDatabase, state, runner, NullLogger.Instance);

                var refused = await Assert.ThrowsExceptionAsync<UserScopeRequestException>(() => service.StartPurgeAsync(ConfirmingTwo(), "admin@contoso.local"));

                Assert.AreEqual(UserScopeErrorCodes.ScopeUnavailable, refused.Code, "Carol is on the page that came back empty: she is not outside the scope.");
                Assert.AreEqual(0, runner.Started.Count);
                Assert.IsNull(await state.GetLatestAsync(), "No purge was recorded.");
                Assert.IsFalse(await purgeDatabase.IsPurgeRunningAsync(), "A refused start lets go of the purge lock.");
            }
        }

        [TestMethod]
        public async Task StartPurge_EveryMemberPageRead_StartsAgainstTheWholeMembership()
        {
            using (var database = UsersDatabase())
            using (var http = new AutoThrottleHttpClient(Graph(FakeGraphDirectoryHandler.Page(null, Member(Carol, "carol"))), NullLogger.Instance))
            {
                var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false);
                var runner = new RecordingRunner();
                var service = new UserScopeService(Provider(http), new UserScopePurgeDatabase(database.ConnectionString), state, runner, NullLogger.Instance);

                var job = await service.StartPurgeAsync(ConfirmingTwo(), "admin@contoso.local");

                Assert.AreEqual(UserScopePurgeStates.Queued, job.State);
                CollectionAssert.AreEqual(new[] { job.Id }, runner.Started);
                Assert.AreEqual(3, runner.PreResolved.Members.Count, "Alice, bob and carol are in scope: the purge is about zed alone.");
            }
        }

        [TestMethod]
        public async Task PurgeEngine_RereadingGroupsThatComeBackIncomplete_RemovesNobody()
        {
            using (var database = UsersDatabase())
            using (var http = new AutoThrottleHttpClient(Graph(string.Empty), NullLogger.Instance))
            {
                // The worst case: the purge was confirmed against the same first-page-only membership that the run reads
                // again (after a restart), so the membership fingerprint matches and only the read itself can refuse.
                var firstPageOnly = new UserScopeMembers();
                firstPageOnly.Add(Alice, "alice@contoso.local", "alice@contoso.local");
                firstPageOnly.Add(Bob, "bob@contoso.local", "bob@contoso.local");
                var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false);
                var job = await state.CreateAsync("admin@contoso.local", Filter.Fingerprint, firstPageOnly.Count, 2, firstPageOnly.MembershipFingerprint);

                var outcome = await new UserScopePurgeEngine(new UserScopePurgeDatabase(database.ConnectionString), state, NullLogger.Instance)
                    .RunAsync(job.Id, Provider(http));

                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, outcome);
                var ended = await state.GetAsync(job.Id);
                Assert.AreEqual(UserScopePurgeErrorCodes.ScopeUnavailable, ended.ErrorCode);
                Assert.AreEqual(0, ended.CandidateCount, "Nobody was chosen, so nobody could be removed.");
                Assert.AreEqual(4, (int)database.Scalar("SELECT COUNT(*) FROM dbo.users;"));
            }
        }
    }
}
