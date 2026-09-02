extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the admin user-data lookup service and its rules - the logic that used to live inside
    /// <c>UserDataLookupAPIController</c> and could not be tested at all. Everything here runs against
    /// <see cref="InMemoryUserDataLookupQuery"/>, so there is no SQL Server, Graph or ASP.NET dependency.
    /// See issue #379.
    /// </summary>
    [TestClass]
    public class UserDataLookupServiceTests
    {
        private const string Upn = "jane.doe@contoso.com";

        /// <summary>Import settings with every workload on, so a test can turn individual ones off.</summary>
        private static ImportTaskSettings AllWorkloadsOn()
        {
            return new ImportTaskSettings
            {
                ActivityLog = true,
                Copilot = true,
                WebTraffic = true,
                SentEmails = true,
                GraphTeams = true,
                Calls = true,
                GraphUsageReports = true,
                GraphUsersMetadata = true,
            };
        }

        #region Validation

        [TestMethod]
        public async Task Summary_MissingUpn_IsRejectedWithoutTouchingStorage()
        {
            var store = new InMemoryUserDataLookupQuery();
            var service = new UserDataLookupService(store);

            foreach (var missing in new[] { null, "", "   " })
            {
                var result = await service.GetSummaryAsync(missing, AllWorkloadsOn);

                Assert.AreEqual(UserDataLookupStatus.BadRequest, result.Status, $"'{missing ?? "(null)"}' should be rejected");
                Assert.AreEqual("A 'upn' query parameter is required.", result.ErrorMessage);
                Assert.IsNull(result.Value);
            }

            // A malformed request must not cost a database round trip.
            Assert.AreEqual(0, store.ProfileLookups.Count);
            Assert.AreEqual(0, store.CountsByCategoryCallCount);
        }

        /// <summary>
        /// The controller reads <c>AppConfig</c> to answer "which workloads are importing?". That read
        /// happened only after the user was found, and must stay that way: a bad request or an unknown
        /// user should not depend on configuration being loadable.
        /// </summary>
        [TestMethod]
        public async Task Summary_ImportSettingsAreOnlyReadOnceTheUserIsFound()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);
            var reads = 0;
            Func<ImportTaskSettings> provider = () => { reads++; return AllWorkloadsOn(); };

            await service.GetSummaryAsync("", provider);
            Assert.AreEqual(0, reads, "a missing UPN is rejected before configuration is touched");

            await service.GetSummaryAsync("nobody@contoso.com", provider);
            Assert.AreEqual(0, reads, "an unknown user is rejected before configuration is touched");

            await service.GetSummaryAsync(Upn, provider);
            Assert.AreEqual(1, reads, "and read exactly once for a real lookup");
        }

        [TestMethod]
        public async Task Summary_UnknownUser_IsReportedAsNotFoundAndNoCountsAreRun()
        {
            var store = new InMemoryUserDataLookupQuery();
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync("nobody@contoso.com", AllWorkloadsOn);

            Assert.AreEqual(UserDataLookupStatus.UserNotFound, result.Status);
            Assert.AreEqual("No user found with UPN 'nobody@contoso.com'.", result.ErrorMessage);
            Assert.IsNull(result.Value);
            Assert.AreEqual(0, store.CountsByCategoryCallCount, "counting 30 categories for a user that doesn't exist is wasted work");
        }

        [TestMethod]
        public async Task Detail_UnknownCategory_IsRejectedWithoutThrowing()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetDetailAsync(Upn, "not-a-category", 10);

            Assert.AreEqual(UserDataLookupStatus.BadRequest, result.Status);
            Assert.AreEqual("Unknown category 'not-a-category'.", result.ErrorMessage);
            Assert.AreEqual(0, store.UserIdLookups.Count, "an unknown category is rejected before the user is looked up");
        }

        [TestMethod]
        public async Task Detail_CategoryWithoutDrillDown_IsRejected()
        {
            // call-feedback is deliberately count-only (SupportsDetail = false).
            var callFeedback = UserDataLookupRules.FindCategory(UserDataLookupRules.CatCallFeedback);
            Assert.IsFalse(callFeedback.SupportsDetail, "this test is meaningless if call-feedback starts supporting drill-down");

            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetDetailAsync(Upn, UserDataLookupRules.CatCallFeedback, 10);

            Assert.AreEqual(UserDataLookupStatus.BadRequest, result.Status);
            Assert.AreEqual("Category 'call-feedback' does not support drill-down.", result.ErrorMessage);
        }

        [TestMethod]
        public async Task Detail_UnknownUser_IsReportedAsNotFound()
        {
            var store = new InMemoryUserDataLookupQuery();
            var service = new UserDataLookupService(store);

            var result = await service.GetDetailAsync("nobody@contoso.com", UserDataLookupRules.CatAuditEvents, 10);

            Assert.AreEqual(UserDataLookupStatus.UserNotFound, result.Status);
            Assert.AreEqual("No user found with UPN 'nobody@contoso.com'.", result.ErrorMessage);
        }

        #endregion

        #region Summary

        [TestMethod]
        public async Task Summary_ReturnsOneRowPerKnownCategory_InCatalogueOrder()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync(Upn, AllWorkloadsOn);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status);
            CollectionAssert.AreEqual(
                UserDataLookupRules.Categories.Select(c => c.Key).ToList(),
                result.Value.Categories.Select(c => c.Key).ToList(),
                "the page shows the categories in catalogue order, so dropping or reordering one is a visible change");
        }

        /// <summary>
        /// The whole point of the batched query: a single user lookup used to fan out into one COUNT
        /// round trip per category. If someone reinstates the per-category loop this fails.
        /// </summary>
        [TestMethod]
        public async Task Summary_IssuesOneBatchedCountQuery_NotOnePerCategory()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            await service.GetSummaryAsync(Upn, AllWorkloadsOn);

            Assert.AreEqual(1, store.CountsByCategoryCallCount);
            Assert.AreEqual(0, store.CountForCategoryCallCount);
            Assert.IsTrue(UserDataLookupRules.Categories.Count > 1, "one batched call is only an improvement because there are many categories");
        }

        [TestMethod]
        public async Task Summary_EachCategoryShowsItsOwnCount()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            store.SetCount(7, UserDataLookupRules.CatAuditEvents, 12345);
            store.SetCount(7, UserDataLookupRules.CatCopilot, 42);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync(Upn, AllWorkloadsOn);
            var byKey = result.Value.Categories.ToDictionary(c => c.Key);

            Assert.AreEqual(12345, byKey[UserDataLookupRules.CatAuditEvents].Count);
            Assert.AreEqual(42, byKey[UserDataLookupRules.CatCopilot].Count);
            Assert.AreEqual(0, byKey[UserDataLookupRules.CatSentEmails].Count, "a category with no rows must read 0, not another category's count");
        }

        [TestMethod]
        public async Task Summary_CategoryMissingFromTheBatch_ReadsAsZeroRatherThanThrowing()
        {
            // A user deleted between the profile load and the count batch produces an all-zero answer;
            // an empty dictionary is the harsher version of the same case.
            var service = new UserDataLookupService(new EmptyCountsQuery(7, Upn));

            var result = await service.GetSummaryAsync(Upn, AllWorkloadsOn);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status);
            Assert.AreEqual(UserDataLookupRules.Categories.Count, result.Value.Categories.Count);
            Assert.IsTrue(result.Value.Categories.All(c => c.Count == 0));
        }

        [TestMethod]
        public async Task Summary_SqlQueryReproducesTheCountForThatCategory()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync(Upn, AllWorkloadsOn);
            var byKey = result.Value.Categories.ToDictionary(c => c.Key);

            // Direct FK, the sessions form and the audit-event join form each have their own SQL shape.
            StringAssert.Contains(byKey[UserDataLookupRules.CatSentEmails].SqlQuery, "FROM sent_emails");
            StringAssert.Contains(byKey[UserDataLookupRules.CatWebHits].SqlQuery, "SELECT id FROM sessions");
            StringAssert.Contains(byKey[UserDataLookupRules.CatCopilot].SqlQuery, "INNER JOIN audit_events e ON c.event_id = e.id");
            foreach (var category in result.Value.Categories)
            {
                StringAssert.Contains(category.SqlQuery, Upn, $"the SQL shown for '{category.Key}' must be for the user being looked up");
            }
        }

        [TestMethod]
        public async Task Summary_WorkloadsEnabledReflectsTheImportSettings()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            // Only web traffic is importing, so only the categories it feeds are "expected to have data".
            var settings = new ImportTaskSettings { WebTraffic = true };
            var result = await service.GetSummaryAsync(Upn, () => settings);
            var byKey = result.Value.Categories.ToDictionary(c => c.Key);

            Assert.IsTrue(byKey[UserDataLookupRules.CatWebHits].WorkloadsEnabled);
            Assert.IsTrue(byKey[UserDataLookupRules.CatPageLikes].WorkloadsEnabled);
            Assert.IsFalse(byKey[UserDataLookupRules.CatSentEmails].WorkloadsEnabled);
            Assert.IsFalse(byKey[UserDataLookupRules.CatCopilot].WorkloadsEnabled);

            // audit-events is fed by two workloads, so one of them being on is enough.
            Assert.IsFalse(byKey[UserDataLookupRules.CatAuditEvents].WorkloadsEnabled);
            var withAudit = await service.GetSummaryAsync(Upn, () => new ImportTaskSettings { ActivityLog = true });
            Assert.IsTrue(withAudit.Value.Categories.Single(c => c.Key == UserDataLookupRules.CatAuditEvents).WorkloadsEnabled);
        }

        [TestMethod]
        public async Task Summary_ListsEveryWorkloadWithItsEnabledFlag()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync(Upn, () => new ImportTaskSettings { Calls = true });

            CollectionAssert.AreEqual(
                UserDataLookupRules.Workloads.Select(w => w.Name).ToList(),
                result.Value.Workloads.Select(w => w.Name).ToList());
            Assert.IsTrue(result.Value.Workloads.Single(w => w.Name == "Teams calls").Enabled);
            Assert.IsFalse(result.Value.Workloads.Single(w => w.Name == "Sent emails").Enabled);
        }

        [TestMethod]
        public async Task Summary_NoImportSettings_ReportsEveryWorkloadDisabledRatherThanThrowing()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync(Upn, () => null);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status);
            Assert.IsTrue(result.Value.Workloads.All(w => !w.Enabled));
            Assert.IsTrue(result.Value.Categories.All(c => !c.WorkloadsEnabled));
        }

        [TestMethod]
        public async Task Summary_UpnIsTrimmedBeforeLookupAndInTheGeneratedSql()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync("   " + Upn + "  ", AllWorkloadsOn);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status, "surrounding whitespace must not stop the user being found");
            CollectionAssert.AreEqual(new[] { Upn }, store.ProfileLookups.ToArray());
            StringAssert.Contains(result.Value.Categories.First().SqlQuery, "user_name = '" + Upn + "'");
        }

        /// <summary>
        /// UPNs are customer text and can be non-Latin. Nothing in the lookup's own logic may fold or
        /// mangle them - see the character-set rule in the repo's C# instructions. (The database column
        /// currently does mangle them; that is a separate schema bug, issue #402.)
        /// </summary>
        [TestMethod]
        public async Task Summary_UpnWithNonAsciiCharacters_IsMatchedAndEmittedVerbatim()
        {
            const string greekUpn = "καλημέρα.κόσμε@contoso.com";
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(11, greekUpn);
            var service = new UserDataLookupService(store);

            var result = await service.GetSummaryAsync(greekUpn, AllWorkloadsOn);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status);
            Assert.AreEqual(greekUpn, result.Value.Profile.UserPrincipalName);
            StringAssert.Contains(result.Value.Categories.First().SqlQuery, "user_name = '" + greekUpn + "'");
        }

        #endregion

        #region Detail

        [TestMethod]
        public async Task Detail_TakeLimitsTheReturnedRows()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            store.SetRows(7, UserDataLookupRules.CatAuditEvents,
                Row("newest"), Row("second"), Row("third"), Row("fourth"), Row("fifth"));
            var service = new UserDataLookupService(store);

            var result = await service.GetDetailAsync(Upn, UserDataLookupRules.CatAuditEvents, 2);

            Assert.AreEqual(2, store.LastTakeRequested);
            Assert.AreEqual(2, result.Value.Rows.Count);
            Assert.AreEqual(2, result.Value.ReturnedCount);
            Assert.AreEqual("newest", result.Value.Rows[0].Title, "rows keep the store's newest-first order");
        }

        [TestMethod]
        public async Task Detail_TakeOfZeroOrNegative_FallsBackToTheDefault()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            foreach (var unusable in new[] { 0, -1, int.MinValue })
            {
                await service.GetDetailAsync(Upn, UserDataLookupRules.CatAuditEvents, unusable);
                Assert.AreEqual(UserDataLookupRules.DefaultTake, store.LastTakeRequested, $"take={unusable} must clamp to the default");
            }
        }

        [TestMethod]
        public async Task Detail_TakeAboveTheMaximum_IsCapped()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            await service.GetDetailAsync(Upn, UserDataLookupRules.CatAuditEvents, 100000);

            Assert.AreEqual(UserDataLookupRules.MaxTake, store.LastTakeRequested,
                "an unbounded take would let an admin pull an arbitrary slice of a 200k-user tenant's audit table in one request");
        }

        [TestMethod]
        public async Task Detail_TotalCountIsTheFullCount_NotTheNumberOfRowsReturned()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            store.SetCount(7, UserDataLookupRules.CatAuditEvents, 500);
            store.SetRows(7, UserDataLookupRules.CatAuditEvents, Row("a"), Row("b"), Row("c"));
            var service = new UserDataLookupService(store);

            var result = await service.GetDetailAsync(Upn, UserDataLookupRules.CatAuditEvents, 2);

            Assert.AreEqual(500, result.Value.TotalCount);
            Assert.AreEqual(2, result.Value.ReturnedCount);
        }

        [TestMethod]
        public async Task Detail_CountsOnlyTheCategoryAskedFor()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            await service.GetDetailAsync(Upn, UserDataLookupRules.CatAuditEvents, 10);

            Assert.AreEqual(1, store.CountForCategoryCallCount);
            Assert.AreEqual(0, store.CountsByCategoryCallCount, "a drill-down must not count all 30 categories");
        }

        [TestMethod]
        public async Task Detail_EchoesTheCategoryKeyAndLabel()
        {
            var store = new InMemoryUserDataLookupQuery();
            store.AddUser(7, Upn);
            var service = new UserDataLookupService(store);

            var result = await service.GetDetailAsync(Upn, "  " + UserDataLookupRules.CatCopilot + " ", 10);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status, "a category name with stray whitespace must still resolve");
            Assert.AreEqual(UserDataLookupRules.CatCopilot, result.Value.Category);
            Assert.AreEqual("Copilot interactions", result.Value.Label);
        }

        #endregion

        #region Rules

        [TestMethod]
        public void ClampTake_KeepsUsableValuesAndBoundsTheRest()
        {
            Assert.AreEqual(1, UserDataLookupRules.ClampTake(1));
            Assert.AreEqual(75, UserDataLookupRules.ClampTake(75));
            Assert.AreEqual(UserDataLookupRules.MaxTake, UserDataLookupRules.ClampTake(UserDataLookupRules.MaxTake));
            Assert.AreEqual(UserDataLookupRules.MaxTake, UserDataLookupRules.ClampTake(UserDataLookupRules.MaxTake + 1));
            Assert.AreEqual(UserDataLookupRules.DefaultTake, UserDataLookupRules.ClampTake(0));
            Assert.AreEqual(UserDataLookupRules.DefaultTake, UserDataLookupRules.ClampTake(-1));
        }

        [TestMethod]
        public void Truncate_OnlyShortensOverlongText_AndMarksThatItDidSo()
        {
            Assert.IsNull(UserDataLookupRules.Truncate(null, 10));
            Assert.AreEqual("", UserDataLookupRules.Truncate("", 10));
            Assert.AreEqual("exactlyten", UserDataLookupRules.Truncate("exactlyten", 10), "a value at the limit is not touched");
            Assert.AreEqual("exactlyten…", UserDataLookupRules.Truncate("exactlyten!", 10), "an over-long value is cut and flagged with an ellipsis");

            // Non-Latin text must be cut by the same rule, not mangled.
            Assert.AreEqual("καλη…", UserDataLookupRules.Truncate("καλημέρα", 4));
        }

        [TestMethod]
        public void FindCategory_IsExactAndReturnsNullForAnythingElse()
        {
            Assert.IsNotNull(UserDataLookupRules.FindCategory(UserDataLookupRules.CatAuditEvents));
            Assert.IsNull(UserDataLookupRules.FindCategory("audit-event"), "a near-miss key must not resolve to a different category");
            Assert.IsNull(UserDataLookupRules.FindCategory(""));
            Assert.IsNull(UserDataLookupRules.FindCategory(null));
        }

        [TestMethod]
        public void Categories_HaveUniqueKeysAndEnoughMetadataToBuildTheirCountSql()
        {
            var keys = UserDataLookupRules.Categories.Select(c => c.Key).ToList();
            CollectionAssert.AllItemsAreUnique(keys, "two categories sharing a key would silently overwrite each other in the count dictionary");

            foreach (var meta in UserDataLookupRules.Categories)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(meta.Table), $"'{meta.Key}' has no table to count");
                Assert.IsFalse(string.IsNullOrWhiteSpace(meta.Label), $"'{meta.Key}' has no label");
                Assert.IsTrue(meta.WorkloadFlags.Length > 0, $"'{meta.Key}' has no workload, so the page can't explain a zero count");

                // Exactly one of the three link shapes: a direct FK column, via sessions, or via audit_events.
                var shapes = (meta.IndirectViaSession ? 1 : 0) + (meta.ViaAuditEvent ? 1 : 0) + (meta.UserColumn != null ? 1 : 0);
                Assert.AreEqual(1, shapes, $"'{meta.Key}' must link to a user in exactly one way");
            }
        }

        [TestMethod]
        public void WorkloadName_MapsKnownFlagsAndFallsBackToTheFlagItself()
        {
            Assert.AreEqual("Teams calls", UserDataLookupRules.WorkloadName(UserDataLookupRules.Wf.Calls));
            Assert.AreEqual("Audit log", UserDataLookupRules.WorkloadName(UserDataLookupRules.Wf.AuditLog));
            Assert.AreEqual("SomethingNew", UserDataLookupRules.WorkloadName("SomethingNew"));
        }

        [TestMethod]
        public void WorkloadEnabled_ReadsEachFlagFromItsOwnImportSetting()
        {
            // Every flag must read its own property: a copy/paste slip here would show an admin the
            // wrong reason for an empty category.
            var settings = new ImportTaskSettings();
            var flags = new[]
            {
                UserDataLookupRules.Wf.Calls, UserDataLookupRules.Wf.UsersMetadata, UserDataLookupRules.Wf.UsageReports,
                UserDataLookupRules.Wf.Teams, UserDataLookupRules.Wf.AuditLog, UserDataLookupRules.Wf.WebTraffic,
                UserDataLookupRules.Wf.SentEmails, UserDataLookupRules.Wf.Copilot,
            };

            foreach (var flag in flags)
            {
                Assert.IsFalse(UserDataLookupRules.WorkloadEnabled(settings, flag), $"{flag} should start off");
            }

            settings.Calls = true;
            settings.GraphUsersMetadata = true;
            settings.GraphUsageReports = true;
            settings.GraphTeams = true;
            settings.ActivityLog = true;
            settings.WebTraffic = true;
            settings.SentEmails = true;
            settings.Copilot = true;

            foreach (var flag in flags)
            {
                Assert.IsTrue(UserDataLookupRules.WorkloadEnabled(settings, flag), $"{flag} should now be on");
            }

            Assert.IsFalse(UserDataLookupRules.WorkloadEnabled(settings, "NotAWorkload"));
            Assert.IsFalse(UserDataLookupRules.WorkloadEnabled(null, UserDataLookupRules.Wf.Calls));
        }

        #endregion

        private static UserDataDetailRowModel Row(string title)
        {
            return new UserDataDetailRowModel { Title = title, Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        }

        /// <summary>
        /// A store whose batched count answers no categories at all - what a user id that no longer
        /// exists produces.
        /// </summary>
        private sealed class EmptyCountsQuery : IUserDataLookupQuery
        {
            private readonly int _userId;
            private readonly string _upn;

            public EmptyCountsQuery(int userId, string upn)
            {
                _userId = userId;
                _upn = upn;
            }

            public Task<UserProfileModel> GetProfileAsync(string upn)
                => Task.FromResult(string.Equals(upn, _upn, StringComparison.OrdinalIgnoreCase)
                    ? new UserProfileModel { UserId = _userId, UserPrincipalName = _upn }
                    : null);

            public Task<int?> GetUserIdAsync(string upn) => Task.FromResult((int?)_userId);

            public Task<System.Collections.Generic.IReadOnlyDictionary<string, int>> GetCountsByCategoryAsync(int userId)
                => Task.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, int>>(new System.Collections.Generic.Dictionary<string, int>());

            public Task<int> GetCountForCategoryAsync(int userId, string categoryKey) => Task.FromResult(0);

            public Task<System.Collections.Generic.IReadOnlyList<UserDataDetailRowModel>> GetRowsForCategoryAsync(int userId, string categoryKey, int take)
                => Task.FromResult<System.Collections.Generic.IReadOnlyList<UserDataDetailRowModel>>(new System.Collections.Generic.List<UserDataDetailRowModel>());
        }
    }
}
