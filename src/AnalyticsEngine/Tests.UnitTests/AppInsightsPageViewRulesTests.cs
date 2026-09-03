using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the page-view staging rules extracted from PageViewsSaveExtension (issue #369) -
    /// de-duplication by page-request id and the org URL in-scope filter, plus the counts that were
    /// previously local variables only ever written to a log line.
    /// Runs with zero SQL Server, Graph, Redis or Service Bus dependency.
    /// </summary>
    [TestClass]
    public class AppInsightsPageViewRulesTests
    {
        private const string InScopeSite = "https://contoso.sharepoint.com/sites/example";

        private static PageViewAppInsightsQueryResult PageView(Guid? pageRequestId, string url, string siteUrl = InScopeSite)
        {
            return new PageViewAppInsightsQueryResult
            {
                Url = url,
                CustomProperties = new PageViewCustomProps
                {
                    PageRequestId = pageRequestId,
                    SiteUrl = siteUrl,
                    SessionId = "session-1",
                    EventTimestamp = new DateTime(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc)
                }
            };
        }

        private static PageViewCollection CollectionOf(params PageViewAppInsightsQueryResult[] rows)
        {
            var collection = new PageViewCollection();
            collection.Rows.AddRange(rows);
            return collection;
        }

        private static List<FilterUrlConfig> FilterFor(params string[] urls)
        {
            var list = new List<FilterUrlConfig>();
            foreach (var u in urls)
            {
                list.Add(new FilterUrlConfig { Url = u });
            }
            return list;
        }

        [TestMethod]
        public void PageViews_UrlMatchingFilterList_IsStaged()
        {
            var plan = PageViewStagingRules.Plan(
                CollectionOf(PageView(Guid.NewGuid(), InScopeSite + "/pages/home.aspx")),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(1, plan.RowsToStage.Count);
            Assert.AreEqual(0, plan.OutOfScopeUrls);
            Assert.AreEqual(0, plan.DuplicatePageRequestIds);
            Assert.AreEqual(1, plan.RawPageViews);
        }

        [TestMethod]
        public void PageViews_UrlOutsideFilterList_IsExcludedAndCounted()
        {
            var plan = PageViewStagingRules.Plan(
                CollectionOf(
                    PageView(Guid.NewGuid(), "https://fabrikam.sharepoint.com/sites/other/home.aspx", "https://fabrikam.sharepoint.com/sites/other"),
                    PageView(Guid.NewGuid(), InScopeSite + "/pages/home.aspx")),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(1, plan.RowsToStage.Count, "Only the in-scope page-view is staged.");
            Assert.AreEqual(1, plan.OutOfScopeUrls, "The rejection must be counted, not just logged.");
        }

        [TestMethod]
        public void PageViews_DuplicatePageRequestIds_AreSkippedAndCounted()
        {
            var sharedId = Guid.NewGuid();
            var plan = PageViewStagingRules.Plan(
                CollectionOf(
                    PageView(sharedId, InScopeSite + "/pages/home.aspx"),
                    PageView(sharedId, InScopeSite + "/pages/home.aspx"),
                    PageView(sharedId, InScopeSite + "/pages/home.aspx")),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(1, plan.RowsToStage.Count, "Only the first occurrence is staged.");
            Assert.AreEqual(2, plan.DuplicatePageRequestIds);
        }

        [TestMethod]
        public void PageViews_EmptyPageRequestId_IsNotStaged()
        {
            // Guid.Empty is treated as 'not new' and lands in the duplicate count. That is the existing
            // behaviour and the number operators see today, so the extraction preserves it deliberately.
            var plan = PageViewStagingRules.Plan(
                CollectionOf(PageView(Guid.Empty, InScopeSite + "/pages/home.aspx")),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(0, plan.RowsToStage.Count);
            Assert.AreEqual(1, plan.DuplicatePageRequestIds);
        }

        [TestMethod]
        public void PageViews_NullPageRequestId_IsIgnoredEntirely()
        {
            // The original Where clause dropped these before any counting, so they appear in neither
            // the staged rows nor either reject count.
            var plan = PageViewStagingRules.Plan(
                CollectionOf(PageView(null, InScopeSite + "/pages/home.aspx")),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(0, plan.RowsToStage.Count);
            Assert.AreEqual(0, plan.DuplicatePageRequestIds);
            Assert.AreEqual(0, plan.OutOfScopeUrls);
            Assert.AreEqual(1, plan.RawPageViews, "It still counts as a raw page-view that was considered.");
        }

        [TestMethod]
        public void PageViews_EmptyCollection_PerformsNoWrite()
        {
            var plan = PageViewStagingRules.Plan(CollectionOf(), FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(0, plan.RowsToStage.Count);
            Assert.AreEqual(0, plan.RawPageViews);
        }

        [TestMethod]
        public void PageViews_NoFilterConfigured_StagesEverything()
        {
            // An empty rule list means "no filtering" (FilterUrlConfigExtensions.UrlInScope returns true).
            var plan = PageViewStagingRules.Plan(
                CollectionOf(PageView(Guid.NewGuid(), "https://fabrikam.sharepoint.com/sites/other/home.aspx")),
                new List<FilterUrlConfig>());

            Assert.AreEqual(1, plan.RowsToStage.Count);
            Assert.AreEqual(0, plan.OutOfScopeUrls);
        }

        [TestMethod]
        public void PageViews_GreekUrl_SurvivesProjectionToStaging()
        {
            var greekUrl = InScopeSite + "/Shared Documents/καλημέρα κόσμε.aspx";

            var plan = PageViewStagingRules.Plan(
                CollectionOf(PageView(Guid.NewGuid(), greekUrl)),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(1, plan.RowsToStage.Count);

            // HitTempEntity normalises through StringUtils.GetUrlBaseAddressIfValidUrl, which
            // percent-encodes. That is lossless, so the test asserts the round trip rather than the
            // raw characters - the failure this guards against is degradation to '?', not escaping.
            // (Note ClickTempEntity takes a different path and keeps the characters unescaped.)
            var staged = plan.RowsToStage[0].Url;
            StringAssert.Contains(Uri.UnescapeDataString(staged), "καλημέρα κόσμε",
                "A Greek URL must survive the round trip intact, not degrade to '?'.");
            Assert.IsFalse(staged.Contains("?"), "No character may be replaced by '?'.");
        }

        [TestMethod]
        public void PageViews_DuplicateCheckHappensBeforeTheUrlFilter()
        {
            // Pins the ORDER of the two rules, which no other test does. The same id appears first on
            // an out-of-scope URL and then on an in-scope one.
            //   dedup first  (current): row 1 consumes the id then fails the filter -> 0 staged,
            //                           1 out-of-scope, 1 duplicate.
            //   filter first (broken):  row 1 is dropped without consuming the id, so row 2 looks new
            //                           -> 1 staged, 1 out-of-scope, 0 duplicates.
            var sharedId = Guid.NewGuid();
            var plan = PageViewStagingRules.Plan(
                CollectionOf(
                    PageView(sharedId, "https://fabrikam.sharepoint.com/sites/other/home.aspx", "https://fabrikam.sharepoint.com/sites/other"),
                    PageView(sharedId, InScopeSite + "/pages/home.aspx")),
                FilterFor("https://contoso.sharepoint.com"));

            Assert.AreEqual(0, plan.RowsToStage.Count, "An id consumed by an out-of-scope row must not be re-staged.");
            Assert.AreEqual(1, plan.OutOfScopeUrls);
            Assert.AreEqual(1, plan.DuplicatePageRequestIds);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void PageViews_NullFilterList_Throws()
        {
            // Deliberate hardening rather than exact preservation: the original only threw once a row
            // reached UrlInScope, so an empty batch used to survive a null list. Unobservable in
            // production - the only caller dereferences filterUrls.Count immediately after loading it.
            PageViewStagingRules.Plan(CollectionOf(), null);
        }
    }
}
