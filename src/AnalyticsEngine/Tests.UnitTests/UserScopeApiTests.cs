extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserScope;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserScope;
using Common.Entities.UserScope.Purge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// <c>api/UserScope</c>: the contract the portal's Administration &gt; User scope page is written against, the
    /// same-origin check on everything that changes state, and the refusals that stop a purge running on a scope that
    /// could be wrong.
    /// </summary>
    [TestClass]
    public class UserScopeApiTests
    {
        /// <summary>Records starts instead of running a purge; lets go of any purge lock handed to it at once.</summary>
        private sealed class RecordingRunner : IUserScopePurgeRunner
        {
            public List<int> Started { get; } = new List<int>();

            public bool IsRunning(int jobId) => Started.Contains(jobId);

            public void Start(int jobId, UserImportScopeResolution preResolved, UserScopePurgeSession lockedSession)
            {
                lockedSession?.Dispose();
                Started.Add(jobId);
            }
        }

        private static string ConnectionString => new AppConfig().ConnectionStrings.DatabaseConnectionString;

        private static UserScopePurgeDatabase Database => new UserScopePurgeDatabase(ConnectionString);

        [TestInitialize]
        public async Task NoPurgeRunning()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
            }
            Assert.IsFalse(await Database.IsPurgeRunningAsync(), "No session should still hold the purge lock from an earlier test.");
        }

        [TestMethod]
        public void EveryCallThatChangesState_IsAPostWithTheSameOriginCheck()
        {
            var actions = typeof(UserScopeAPIController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var posts = actions.Where(m => m.GetCustomAttribute<HttpPostAttribute>() != null).ToList();

            CollectionAssert.AreEquivalent(new[] { "Refresh", "StartPurge", "CancelPurge" }, posts.Select(m => m.Name).ToList());
            foreach (var post in posts)
            {
                Assert.IsNotNull(post.GetCustomAttribute<RequireSameOriginXhrAttribute>(), $"{post.Name} changes state, so it needs the same-origin check.");
            }
            Assert.IsNotNull(typeof(UserScopeAPIController).GetCustomAttribute<AuthorizeAttribute>(), "Signed-in users only.");
            var permission = typeof(UserScopeAPIController).GetCustomAttribute<RequirePortalPermissionAttribute>();
            Assert.IsNotNull(permission, "Every user-scope action is administrative, including the destructive purge.");
            Assert.AreEqual(PortalPermission.Administration, permission.Permission);
        }

        [TestMethod]
        public async Task EveryAction_RefusesAReaderWithoutAdministration_AndLetsAnAdministratorReachIt()
        {
            var endpoints = new[]
            {
                (HttpMethod.Get, "api/UserScope", (string)null, HttpStatusCode.OK),
                (HttpMethod.Post, "api/UserScope/refresh", "{}", HttpStatusCode.OK),
                (HttpMethod.Post, "api/UserScope/purge", "{\"acknowledged\":false}", HttpStatusCode.BadRequest),
                (HttpMethod.Get, "api/UserScope/purge/2147483647", (string)null, HttpStatusCode.NotFound),
                (HttpMethod.Post, "api/UserScope/purge/2147483647/cancel", "{}", HttpStatusCode.NotFound),
            };

            var refusedActionRan = false;
            using (var host = UserScopeHost(
                PortalTestHost.SignedIn(),
                () =>
                {
                    refusedActionRan = true;
                    throw new AssertFailedException("A refused request reached the destructive service.");
                }))
            {
                foreach (var endpoint in endpoints)
                using (var request = UserScopeRequest(endpoint.Item1, endpoint.Item2, endpoint.Item3))
                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{endpoint.Item1} {endpoint.Item2}");
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual(PortalPermissionNames.Administration, (string)body["permission"]);
                }
            }
            Assert.IsFalse(refusedActionRan, "The authorization filter must run before the controller creates its service.");

            var service = new UserScopeService(
                new FixedUserImportScopeProvider(UserImportScope.Unfiltered),
                Database,
                new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false),
                new RecordingRunner(),
                NullLogger.Instance);
            using (var host = UserScopeHost(
                PortalTestHost.SignedIn(PortalRoles.Administration),
                () => service))
            {
                foreach (var endpoint in endpoints)
                using (var request = UserScopeRequest(endpoint.Item1, endpoint.Item2, endpoint.Item3))
                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.AreEqual(endpoint.Item4, response.StatusCode,
                        $"Administration did not reach {endpoint.Item1} {endpoint.Item2}: {await response.Content.ReadAsStringAsync()}");
                }
            }
        }

        private static PortalTestHost UserScopeHost(System.Security.Principal.IPrincipal principal, Func<UserScopeService> service) =>
            new PortalTestHost(
                new[] { typeof(UserScopeAPIController) },
                principal,
                PortalAccessPolicy.Enforcing,
                _ => new UserScopeAPIController(service));

        private static HttpRequestMessage UserScopeRequest(HttpMethod method, string url, string body)
        {
            var request = new HttpRequestMessage(method, url);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            }
            return request;
        }

        [TestMethod]
        public void Status_SerialisesWithTheNamesThePortalReads()
        {
            var status = new UserScopeStatusModel
            {
                Resolution = new UserScopeResolutionModel { Groups = { new UserScopeGroupModel { DisplayName = "Ομάδα Πωλήσεων Αθήνα" } } },
                Database = new UserScopeDatabaseCountsModel(),
                LatestJob = new UserScopePurgeJobModel { RowsAffected = { new UserScopePurgeTableCountModel { Table = "audit_events", Rows = 3 } } },
            };

            var json = JObject.Parse(JsonConvert.SerializeObject(status));

            CollectionAssert.AreEquivalent(new[] { "filtered", "filterPatterns", "resolution", "database", "purgeUnavailableReason", "latestJob", "purgeStateDurable" },
                Names(json));
            CollectionAssert.AreEquivalent(new[] { "status", "failureKind", "httpStatus", "resolvedUtc", "memberCount", "matchedNoGroup", "groups", "unmatchedPatterns" },
                Names((JObject)json["resolution"]));
            CollectionAssert.AreEquivalent(new[] { "id", "displayName", "userMemberCount", "matchedPatterns" }, Names((JObject)json["resolution"]["groups"][0]));
            Assert.AreEqual("Ομάδα Πωλήσεων Αθήνα", (string)json["resolution"]["groups"][0]["displayName"], "Group names are Unicode and passed through as they are.");
            CollectionAssert.AreEquivalent(new[] { "totalUsers", "inScopeUsers", "outOfScopeUsers" }, Names((JObject)json["database"]));
            CollectionAssert.AreEquivalent(new[]
            {
                "id", "state", "phase", "stepIndex", "stepCount", "candidateCount", "usersDeleted", "usersSkipped", "rowsAffected",
                "cancelRequested", "requestedBy", "createdUtc", "startedUtc", "updatedUtc", "completedUtc", "errorCode",
            }, Names((JObject)json["latestJob"]));
            CollectionAssert.AreEquivalent(new[] { "table", "rows" }, Names((JObject)json["latestJob"]["rowsAffected"][0]));
        }

        private static List<string> Names(JObject json) => json.Properties().Select(p => p.Name).ToList();

        [TestMethod]
        public void Codes_AreTheOnesThePortalTurnsIntoSentences()
        {
            CollectionAssert.AreEquivalent(
                new[] { "acknowledgementRequired", "scopeNotFiltered", "scopeUnavailable", "scopeEmpty", "nothingToPurge", "purgeAlreadyRunning", "jobNotFound", "jobNotActive", "databaseUnavailable", "storageUnavailable" },
                ConstantValues(typeof(UserScopeErrorCodes)));
            CollectionAssert.AreEquivalent(new[] { "notFiltered", "scopeUnavailable", "scopeEmpty", "nothingToPurge", "jobActive", "storageUnavailable" },
                ConstantValues(typeof(UserScopePurgeUnavailableReasons)));
            CollectionAssert.AreEquivalent(new[] { "scopeUnavailable", "scopeEmpty", "databaseError", "filterChanged", "filterChangedWhileRunning", "stateUnavailable", "unexpected" },
                ConstantValues(typeof(UserScopePurgeErrorCodes)));
            CollectionAssert.AreEquivalent(new[] { "queued", "running", "completed", "failed", "cancelled" }, ConstantValues(typeof(UserScopePurgeStates)));
            CollectionAssert.AreEquivalent(new[]
            {
                "snapshot", "auditEvents", "webActivity", "calls", "pageComments", "sentEmails", "teams", "usageReports",
                "copilotInteractions", "licencesAndCredits", "sharedWith", "managers", "users", "done",
            }, ConstantValues(typeof(UserScopePurgePhases)));
            CollectionAssert.IsSubsetOf(UserScopePurgePlan.Steps.Select(s => s.Phase).Distinct().ToList(), ConstantValues(typeof(UserScopePurgePhases)),
                "Every phase a step reports is one the portal has a label for.");
        }

        private static List<string> ConstantValues(Type type)
            => type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).ToList();

        [TestMethod]
        public void Resolution_ReportsWhyItIsUnavailable_AsFacts()
        {
            var model = UserScopeService.ToModel(UserImportScopeResolution.Unavailable(null, null, "HTTP 403", DateTime.UtcNow,
                UserImportScopeFailureKind.DirectoryRead, 403));

            Assert.AreEqual("unavailable", model.Status);
            Assert.AreEqual("directoryRead", model.FailureKind);
            Assert.AreEqual(403, model.HttpStatus);
            Assert.AreEqual("unfiltered", UserScopeService.ToModel(UserImportScopeResolution.Unfiltered(DateTime.UtcNow)).Status);
        }

        [TestMethod]
        public async Task StartPurge_IsRefusedUnlessAcknowledged_Filtered_AndFreshlyResolvedToSomebody()
        {
            var filter = new UserGroupsFilterModel("Copilot pilot");
            var database = Database;
            var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false);

            async Task<UserScopeRequestException> Refused(IUserImportScopeProvider provider, bool acknowledged)
            {
                var runner = new RecordingRunner();
                var service = new UserScopeService(provider, database, state, runner, NullLogger.Instance);
                var refused = await Assert.ThrowsExceptionAsync<UserScopeRequestException>(() => service.StartPurgeAsync(acknowledged, "admin@contoso.local"));
                Assert.AreEqual(0, runner.Started.Count);
                Assert.IsFalse(await database.IsPurgeRunningAsync(), "A refused start lets go of the purge lock.");
                return refused;
            }

            var resolved = new FixedUserImportScopeProvider(TestUserScopes.Of("pilot@contoso.local"),
                UserImportScopeResolution.Resolved(Members("pilot@contoso.local"), null, null, DateTime.UtcNow), filter);

            var noTick = await Refused(resolved, acknowledged: false);
            Assert.AreEqual(UserScopeErrorCodes.AcknowledgementRequired, noTick.Code);
            Assert.AreEqual(HttpStatusCode.BadRequest, noTick.Status);

            Assert.AreEqual(UserScopeErrorCodes.ScopeNotFiltered,
                (await Refused(new FixedUserImportScopeProvider(UserImportScope.Unfiltered), acknowledged: true)).Code);

            var unavailable = await Refused(new FixedUserImportScopeProvider(UserImportScope.Everyone("down"),
                UserImportScopeResolution.Unavailable(Members("pilot@contoso.local"), null, "partial", DateTime.UtcNow), filter), acknowledged: true);
            Assert.AreEqual(UserScopeErrorCodes.ScopeUnavailable, unavailable.Code, "A partial scope would make people who are in it look outside it.");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.Status);

            Assert.AreEqual(UserScopeErrorCodes.ScopeEmpty, (await Refused(new FixedUserImportScopeProvider(TestUserScopes.OfMembers(),
                UserImportScopeResolution.Resolved(new UserScopeMembers(), null, null, DateTime.UtcNow), filter), acknowledged: true)).Code,
                "A filter that matches nobody must never purge everybody.");

            // Accepted: recorded, handed to the runner with the fresh resolution, and a second one can't start.
            var acceptedRunner = new RecordingRunner();
            var accepting = new UserScopeService(resolved, database, state, acceptedRunner, NullLogger.Instance);
            var job = await accepting.StartPurgeAsync(true, "admin@contoso.local");
            Assert.AreEqual(UserScopePurgeStates.Queued, job.State);
            CollectionAssert.AreEqual(new[] { job.Id }, acceptedRunner.Started);
            Assert.AreEqual(UserScopePurgeUnavailableReasons.JobActive, (await accepting.GetStatusAsync()).PurgeUnavailableReason);
            Assert.AreEqual(UserScopeErrorCodes.PurgeAlreadyRunning,
                (await Assert.ThrowsExceptionAsync<UserScopeRequestException>(() => accepting.StartPurgeAsync(true, "admin@contoso.local"))).Code);

            // Stop it before it ever runs: nothing was chosen or removed. The provider handed to the run can't resolve, so
            // even if the cancel were missed it could never choose anybody in this shared database.
            var cancelled = await accepting.CancelPurgeAsync(job.Id);
            Assert.IsTrue(cancelled.CancelRequested);
            var unreadable = new FixedUserImportScopeProvider(UserImportScope.Everyone("down"),
                UserImportScopeResolution.Unavailable(null, null, "down", DateTime.UtcNow), filter);
            Assert.AreEqual(UserScopePurgeRunOutcome.Cancelled, await new UserScopePurgeEngine(database, state, NullLogger.Instance).RunAsync(job.Id, unreadable));
            Assert.AreEqual(UserScopePurgeStates.Cancelled, (await accepting.GetPurgeAsync(job.Id)).State);
            Assert.AreEqual(0, (await accepting.GetPurgeAsync(job.Id)).CandidateCount);
            Assert.AreEqual(UserScopeErrorCodes.JobNotActive,
                (await Assert.ThrowsExceptionAsync<UserScopeRequestException>(() => accepting.CancelPurgeAsync(job.Id))).Code);
            Assert.AreEqual(UserScopeErrorCodes.JobNotFound,
                (await Assert.ThrowsExceptionAsync<UserScopeRequestException>(() => accepting.GetPurgeAsync(int.MaxValue))).Code);
        }

        private static UserScopeMembers Members(params string[] upns)
        {
            var members = new UserScopeMembers();
            foreach (var upn in upns) members.Add(Guid.NewGuid().ToString(), upn, upn);
            return members;
        }

        /// <summary>
        /// With purge records in memory, a web app instance knows nothing of a purge that another instance runs. The purge
        /// lock still stops it starting a second one, and the page still says a purge is running.
        /// </summary>
        [TestMethod]
        public async Task StartPurge_IsRefused_AndStatusSaysAPurgeIsRunning_WhileAnotherInstanceHoldsThePurgeLock()
        {
            var filter = new UserGroupsFilterModel("Copilot pilot");
            var resolved = new FixedUserImportScopeProvider(TestUserScopes.Of("pilot@contoso.local"),
                UserImportScopeResolution.Resolved(Members("pilot@contoso.local"), null, null, DateTime.UtcNow), filter);
            var runner = new RecordingRunner();
            var service = new UserScopeService(resolved, Database, new UserScopePurgeStateStore(new InMemoryKeyValueStore(), isDurable: false), runner,
                NullLogger.Instance);

            using (var otherInstance = await Database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                Assert.IsNotNull(otherInstance);

                var status = await service.GetStatusAsync();
                Assert.AreEqual(UserScopePurgeUnavailableReasons.JobActive, status.PurgeUnavailableReason);
                Assert.IsNull(status.LatestJob, "This instance has no record of the other's purge...");
                Assert.IsFalse(status.PurgeStateDurable, "...because it keeps purge records in memory.");

                var refused = await Assert.ThrowsExceptionAsync<UserScopeRequestException>(() => service.StartPurgeAsync(true, "admin@contoso.local"));
                Assert.AreEqual(UserScopeErrorCodes.PurgeAlreadyRunning, refused.Code);
                Assert.AreEqual(HttpStatusCode.Conflict, refused.Status);
                Assert.AreEqual(0, runner.Started.Count);
            }

            Assert.AreNotEqual(UserScopePurgeUnavailableReasons.JobActive, (await service.GetStatusAsync()).PurgeUnavailableReason,
                "Once the other purge has finished, one can start here.");
        }

        /// <summary>
        /// Where purge records are kept - Azure Table storage - can be out of reach while Graph and the database are fine.
        /// The page still shows the scope and the counts, and says why purging waits; every purge call answers
        /// <c>storageUnavailable</c>; and a start that can't record its purge lets go of the purge lock.
        /// </summary>
        [TestMethod]
        public async Task StorageOutOfReach_StatusStillShowsTheScope_AndEveryPurgeCallSaysWhy()
        {
            var filter = new UserGroupsFilterModel("Copilot pilot");
            var resolved = new FixedUserImportScopeProvider(TestUserScopes.Of("pilot@contoso.local"),
                UserImportScopeResolution.Resolved(Members("pilot@contoso.local"), null, null, DateTime.UtcNow), filter);
            var store = new OutageKeyValueStore { Down = true };
            var runner = new RecordingRunner();
            var service = new UserScopeService(resolved, Database, new UserScopePurgeStateStore(store, isDurable: true), runner, NullLogger.Instance);

            var status = await service.GetStatusAsync();
            Assert.AreEqual(UserScopePurgeUnavailableReasons.StorageUnavailable, status.PurgeUnavailableReason);
            Assert.AreEqual("resolved", status.Resolution.Status, "The scope doesn't need the store...");
            Assert.IsNotNull(status.Database, "...and nor do the counts.");
            Assert.IsNull(status.LatestJob);

            async Task AssertStorageUnavailable(Func<Task> call)
            {
                var refused = await Assert.ThrowsExceptionAsync<UserScopeRequestException>(call);
                Assert.AreEqual(UserScopeErrorCodes.StorageUnavailable, refused.Code);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.Status);
            }

            await AssertStorageUnavailable(() => service.StartPurgeAsync(true, "admin@contoso.local"));
            await AssertStorageUnavailable(() => service.GetPurgeAsync(1));
            await AssertStorageUnavailable(() => service.CancelPurgeAsync(1));

            store.Down = false;
            store.FailNextWrites = 1;
            await AssertStorageUnavailable(() => service.StartPurgeAsync(true, "admin@contoso.local"));
            Assert.AreEqual(0, runner.Started.Count);
            Assert.IsFalse(await Database.IsPurgeRunningAsync(), "A start that couldn't record its purge lets go of the purge lock.");
        }
    }
}
