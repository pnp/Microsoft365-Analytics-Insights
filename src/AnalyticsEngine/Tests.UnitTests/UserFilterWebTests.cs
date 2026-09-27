extern alias AnalyticsWeb;

using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http.Results;
using CachedUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.CachedUserDirectorySource;
using CopilotAdoptionAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotAdoptionAPIController;
using IUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IUserDirectorySource;
using UserFilterAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.UserFilterAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The web side of the user filter: the shared directory snapshot cache, the picker API and the
    /// adoption API's handling of the <c>userFilter</c> parameter.
    /// </summary>
    [TestClass]
    public class UserFilterWebTests
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Usable = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        #region Directory cache

        [TestMethod]
        public async Task Cache_LoadsOnce_AndServesTheSnapshotWhileFresh()
        {
            var clock = new Clock();
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, clock.Now);

            var first = source.GetAsync(CancellationToken.None);
            loader.Complete(Snapshot("first"));
            var snapshot = await first.TimeoutAfter(Wait);

            clock.Advance(TimeSpan.FromMinutes(4));
            var again = await source.GetAsync(CancellationToken.None).TimeoutAfter(Wait);

            Assert.AreSame(snapshot, again);
            Assert.AreEqual(1, loader.Calls);
        }

        [TestMethod]
        public async Task Cache_ConcurrentFirstRequests_ShareOneLoad()
        {
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, new Clock().Now);

            var a = source.GetAsync(CancellationToken.None);
            var b = source.GetAsync(CancellationToken.None);
            loader.Complete(Snapshot("only"));

            Assert.AreSame(await a.TimeoutAfter(Wait), await b.TimeoutAfter(Wait));
            Assert.AreEqual(1, loader.Calls);
        }

        [TestMethod]
        public async Task Cache_ServesAStaleSnapshotImmediately_AndRefreshesBehindIt()
        {
            var clock = new Clock();
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, clock.Now);

            var first = source.GetAsync(CancellationToken.None);
            loader.Complete(Snapshot("old"));
            var old = await first.TimeoutAfter(Wait);

            clock.Advance(TimeSpan.FromMinutes(6));
            var stale = await source.GetAsync(CancellationToken.None).TimeoutAfter(Wait);

            Assert.AreSame(old, stale, "A snapshot minutes old is not worth making a reader wait for.");
            loader.WaitForCalls(2, "...but a refresh starts behind it.");

            var fresh = Snapshot("new");
            loader.Complete(fresh);
            await Eventually(async () => ReferenceEquals(await source.GetAsync(CancellationToken.None), fresh));
        }

        [TestMethod]
        public async Task Cache_WaitsForANewSnapshot_OnceTheOldOneIsTooOld()
        {
            var clock = new Clock();
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, clock.Now);

            var first = source.GetAsync(CancellationToken.None);
            loader.Complete(Snapshot("old"));
            await first.TimeoutAfter(Wait);

            clock.Advance(TimeSpan.FromMinutes(31));
            var waiting = source.GetAsync(CancellationToken.None);
            Assert.IsFalse(waiting.IsCompleted, "Past the usable window the request waits for the new read.");

            var fresh = Snapshot("new");
            loader.Complete(fresh);
            Assert.AreSame(fresh, await waiting.TimeoutAfter(Wait));
        }

        [TestMethod]
        public async Task Cache_Invalidate_DiscardsTheSnapshot_AndAnyLoadAlreadyRunning()
        {
            var clock = new Clock();
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, clock.Now);

            var first = source.GetAsync(CancellationToken.None);
            loader.Complete(Snapshot("before"));
            await first.TimeoutAfter(Wait);

            // A load in flight read the directory BEFORE the change that prompted the invalidation.
            clock.Advance(TimeSpan.FromMinutes(6));
            await source.GetAsync(CancellationToken.None).TimeoutAfter(Wait);
            loader.WaitForCalls(2);

            source.Invalidate();
            var afterInvalidate = source.GetAsync(CancellationToken.None);
            loader.WaitForCalls(3, "Invalidation forces a new read rather than joining the old one.");

            loader.Complete(Snapshot("in flight before the change"), callIndex: 1);
            var current = Snapshot("after the change");
            loader.Complete(current, callIndex: 2);

            Assert.AreSame(current, await afterInvalidate.TimeoutAfter(Wait));
            await Task.Delay(50);
            Assert.AreSame(current, await source.GetAsync(CancellationToken.None).TimeoutAfter(Wait),
                "The load that started before the invalidation must never become current.");
        }

        [TestMethod]
        public async Task Cache_ARequestWaitingWhenTheCacheIsInvalidated_GetsARead_ThatStartedAfterIt()
        {
            // An admin disables an org type while a filtered report is waiting for the directory. The
            // read that request joined started before the change; answering with it would contradict the
            // admin's own edit.
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, new Clock().Now);

            var waiting = source.GetAsync(CancellationToken.None);
            loader.WaitForCalls(1);

            source.Invalidate();
            loader.Complete(Snapshot("read before the change"), callIndex: 0);

            // The waiter notices, and joins a new read rather than returning the old one.
            loader.WaitForCalls(2);
            var after = Snapshot("read after the change");
            loader.Complete(after, callIndex: 1);

            Assert.AreSame(after, await waiting.TimeoutAfter(Wait));
        }

        [TestMethod]
        public async Task Cache_AFailedLoad_SurfacesToTheCaller_AndTheNextRequestRetries()
        {
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, new Clock().Now);

            var failing = source.GetAsync(CancellationToken.None);
            loader.Fail(new InvalidOperationException("SQL is down"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => failing.TimeoutAfter(Wait));

            // The failed load is forgotten a moment after it faults; until then a request may still
            // be handed the faulted task, which is fine - it is simply told the read failed.
            await Eventually(() =>
            {
                var retry = source.GetAsync(CancellationToken.None);
                retry.ContinueWith(t => t.Exception, TaskScheduler.Default);
                return Task.FromResult(loader.Calls >= 2);
            });

            Assert.AreEqual(2, loader.Calls, "One retry, shared by every request that arrived during it.");
        }

        [TestMethod]
        public async Task Cache_ACancelledWait_DoesNotCancelTheSharedLoad()
        {
            var loader = new ControlledLoader();
            var source = new CachedUserDirectorySource(() => loader, Fresh, Usable, new Clock().Now);

            using (var cts = new CancellationTokenSource())
            {
                var abandoned = source.GetAsync(cts.Token);
                cts.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => abandoned.TimeoutAfter(Wait));
            }

            var patient = source.GetAsync(CancellationToken.None);
            var snapshot = Snapshot("shared");
            loader.Complete(snapshot);

            Assert.AreSame(snapshot, await patient.TimeoutAfter(Wait));
            Assert.AreEqual(1, loader.Calls, "The reader who gave up must not have cost anyone a second read.");
        }

        #endregion

        #region Picker API

        [TestMethod]
        public async Task Api_ListsTheDimensions()
        {
            var controller = new UserFilterAPIController(new StaticSource(Snapshot("api")));

            var result = await controller.Dimensions(CancellationToken.None) as OkNegotiatedContentResult<UserFilterDimensionList>;

            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Content.People);
            Assert.IsTrue(result.Content.Dimensions.Any(d => d.Key == "org:5" && d.Name == "Programme"));
        }

        [TestMethod]
        public async Task Api_ListsADimensionsValues()
        {
            var controller = new UserFilterAPIController(new StaticSource(Snapshot("api")));

            var result = await controller.Values("department", "sal", 10, CancellationToken.None) as OkNegotiatedContentResult<UserFilterValuePage>;

            Assert.IsNotNull(result);
            CollectionAssert.AreEqual(new[] { "Sales" }, result.Content.Values.Select(v => v.Value).ToArray());
        }

        [TestMethod]
        public async Task Api_RefusesAnAttributeItCannotFilterOn_AndSays404ForOneThatNoLongerExists()
        {
            var controller = new UserFilterAPIController(new StaticSource(Snapshot("api")));

            var malformed = await controller.Values("favouriteColour", null, 10, CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.BadRequest, StatusOf(malformed));

            var gone = await controller.Values("org:999", null, 10, CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.NotFound, StatusOf(gone));
        }

        [TestMethod]
        public async Task Api_ReportsADirectoryFailureWithoutItsDetail()
        {
            var controller = new UserFilterAPIController(new StaticSource(null, new InvalidOperationException("Invalid object name 'dbo.users'.")));

            var result = await controller.Dimensions(CancellationToken.None);

            Assert.AreEqual(HttpStatusCode.InternalServerError, StatusOf(result));
            var body = ((NegotiatedContentResult<AnalyticsWeb::Web.AnalyticsWeb.Models.ApiErrorModel>)result).Content;
            Assert.IsFalse(body.Message.Contains("dbo.users"), "A SQL error must never reach the browser.");
        }

        #endregion

        #region Adoption API parameter

        [TestMethod]
        public void AdoptionApi_ReadsAValidFilter_AndRefusesAnInvalidOneWithAReason()
        {
            Assert.IsTrue(CopilotAdoptionAPIController.TryParseUserFilter(
                "[{\"d\":\"department\",\"v\":[\"Sales\"]}]", out var filter, out var error));
            Assert.AreEqual(1, filter.Clauses.Count);
            Assert.IsNull(error);

            Assert.IsTrue(CopilotAdoptionAPIController.TryParseUserFilter(null, out var none, out _));
            Assert.IsTrue(none.IsEmpty);

            Assert.IsFalse(CopilotAdoptionAPIController.TryParseUserFilter("[{\"d\":\"nope\"}]", out var rejected, out var reason));
            Assert.IsTrue(rejected.IsEmpty);
            StringAssert.StartsWith(reason, "The filter could not be applied:");
        }

        #endregion

        #region Fixtures

        private static HttpStatusCode StatusOf(System.Web.Http.IHttpActionResult result)
        {
            switch (result)
            {
                case NegotiatedContentResult<AnalyticsWeb::Web.AnalyticsWeb.Models.ApiErrorModel> error: return error.StatusCode;
                case StatusCodeResult status: return status.StatusCode;
                case OkNegotiatedContentResult<UserFilterValuePage> _: return HttpStatusCode.OK;
                case OkNegotiatedContentResult<UserFilterDimensionList> _: return HttpStatusCode.OK;
                default: throw new AssertFailedException("Unexpected result " + result?.GetType().Name);
            }
        }

        private static UserDirectorySnapshot Snapshot(string tag)
        {
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = tag.Replace(' ', '-') + "@contoso.com", Department = "Sales" });
            builder.AddUser(new UserDirectoryEntry { UserId = 2, UserPrincipalName = "other@contoso.com", Department = "Engineering" });
            builder.AddOrgType(5, "Programme");
            builder.AddOrgAssignment(1, 5, "Pilot");
            return builder.Build(DateTime.UtcNow);
        }

        private static async Task Eventually(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return;
                await Task.Delay(20);
            }

            Assert.Fail("The condition never became true.");
        }

        private sealed class Clock
        {
            private DateTime _now = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

            public DateTime Now() => _now;

            public void Advance(TimeSpan by) => _now += by;
        }

        /// <summary>A loader whose every read completes only when the test says so.</summary>
        private sealed class ControlledLoader : IUserDirectoryLoader
        {
            private readonly object _sync = new object();
            private readonly System.Collections.Generic.List<TaskCompletionSource<UserDirectorySnapshot>> _reads =
                new System.Collections.Generic.List<TaskCompletionSource<UserDirectorySnapshot>>();

            public int Calls
            {
                get { lock (_sync) return _reads.Count; }
            }

            /// <summary>
            /// Waits for the source to have started this many reads. It starts them on the thread pool,
            /// so the count lags the call that caused it by a moment.
            /// </summary>
            public void WaitForCalls(int expected, string because = null)
            {
                var deadline = DateTime.UtcNow + Wait;
                while (DateTime.UtcNow < deadline && Calls < expected) Thread.Sleep(10);

                Assert.AreEqual(expected, Calls, because);
            }

            public Task<UserDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                var read = new TaskCompletionSource<UserDirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync) _reads.Add(read);
                return read.Task;
            }

            /// <summary>Completes a read - the latest by default. Waits for it to have started.</summary>
            public void Complete(UserDirectorySnapshot snapshot, int? callIndex = null)
            {
                Read(callIndex).SetResult(snapshot);
            }

            public void Fail(Exception ex, int? callIndex = null)
            {
                Read(callIndex).SetException(ex);
            }

            private TaskCompletionSource<UserDirectorySnapshot> Read(int? callIndex)
            {
                var deadline = DateTime.UtcNow + Wait;
                while (DateTime.UtcNow < deadline)
                {
                    lock (_sync)
                    {
                        var index = callIndex ?? _reads.Count - 1;
                        if (index >= 0 && index < _reads.Count) return _reads[index];
                    }

                    Thread.Sleep(10);
                }

                throw new AssertFailedException("The expected directory read never started.");
            }
        }

        private sealed class StaticSource : IUserDirectorySource
        {
            private readonly UserDirectorySnapshot _snapshot;
            private readonly Exception _failure;

            public StaticSource(UserDirectorySnapshot snapshot, Exception failure = null)
            {
                _snapshot = snapshot;
                _failure = failure;
            }

            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken)
            {
                if (_failure != null) throw _failure;
                return Task.FromResult(_snapshot);
            }

            public void Prefetch()
            {
            }

            public void Invalidate()
            {
            }
        }

        #endregion
    }

    internal static class UserFilterTaskExtensions
    {
        /// <summary>Fails the test rather than hanging it when a task never completes.</summary>
        public static async Task<T> TimeoutAfter<T>(this Task<T> task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            {
                throw new AssertFailedException("Timed out waiting for the task.");
            }

            return await task;
        }
    }
}
