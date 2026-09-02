using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.PageUpdates.Rules;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace Tests.UnitTests
{
    /// <summary>
    /// The page-update rules extracted from PageUpdateManager by issue #369 part 2: URL grouping, the
    /// metadata refresh-suppression window, the comment/like de-duplication and the sentiment-enrichment
    /// gate. All run with zero SQL Server, zero cognitive services and zero wall clock.
    /// </summary>
    [TestClass]
    public class AppInsightsPageUpdateRulesTests
    {
        private static readonly DateTime NowUtc = new DateTime(2026, 5, 4, 11, 0, 0, DateTimeKind.Utc);

        private static PageUpdateEventAppInsightsQueryResult UpdateFor(string url, string name = null)
        {
            var e = new PageUpdateEventAppInsightsQueryResult { Name = name };
            e.CustomProperties.Url = url;
            return e;
        }

        #region Grouping

        [TestMethod]
        public void PageUpdateGrouping_UrlsDifferingOnlyByCase_ShareOneBucket()
        {
            // SharePoint emits the same page with different casing constantly, and the SQL collation is
            // case-insensitive. Grouping them apart would create a second urls row for the same page.
            var grouped = PageUpdateGroupingRules.GroupByUrl(new List<PageUpdateEventAppInsightsQueryResult>
            {
                UpdateFor("https://contoso.sharepoint.com/sites/Marketing/SitePages/Home.aspx", "a"),
                UpdateFor("https://contoso.sharepoint.com/sites/marketing/sitepages/home.aspx", "b")
            });

            Assert.AreEqual(1, grouped.Count);
            Assert.AreEqual(2, grouped.Values.Single().Count);
        }

        [TestMethod]
        public void PageUpdateGrouping_QueryStringAndFragmentAreIgnored()
        {
            var grouped = PageUpdateGroupingRules.GroupByUrl(new List<PageUpdateEventAppInsightsQueryResult>
            {
                UpdateFor("https://contoso.sharepoint.com/sites/x/SitePages/Home.aspx"),
                UpdateFor("https://contoso.sharepoint.com/sites/x/SitePages/Home.aspx?web=1&xsdata=abc"),
                UpdateFor("https://contoso.sharepoint.com/sites/x/SitePages/Home.aspx#section")
            });

            Assert.AreEqual(1, grouped.Count);
            Assert.AreEqual(3, grouped.Values.Single().Count);
            Assert.AreEqual("https://contoso.sharepoint.com/sites/x/SitePages/Home.aspx", grouped.Keys.Single());
        }

        [TestMethod]
        public void PageUpdateGrouping_EventWithNoUsableUrl_IsDropped()
        {
            var withoutCustomProps = new PageUpdateEventAppInsightsQueryResult();
            withoutCustomProps.CustomProperties = null;

            var grouped = PageUpdateGroupingRules.GroupByUrl(new List<PageUpdateEventAppInsightsQueryResult>
            {
                UpdateFor(null),
                UpdateFor(string.Empty),
                withoutCustomProps,
                UpdateFor("https://contoso.sharepoint.com/sites/x/SitePages/Home.aspx")
            });

            Assert.AreEqual(1, grouped.Count, "Only the one event with a usable URL should survive.");
        }

        [TestMethod]
        public void PageUpdateGrouping_PreservesEventOrderWithinABucket()
        {
            // The compile step takes Name/Username from the FIRST update in the bucket, so order is load
            // bearing rather than incidental.
            const string url = "https://contoso.sharepoint.com/sites/x/SitePages/Home.aspx";
            var grouped = PageUpdateGroupingRules.GroupByUrl(new List<PageUpdateEventAppInsightsQueryResult>
            {
                UpdateFor(url, "first"), UpdateFor(url, "second"), UpdateFor(url, "third")
            });

            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, grouped[url].Select(e => e.Name).ToArray());
        }

        [TestMethod]
        public void PageUpdateGrouping_NonLatinUrl_IsGroupedNotDropped()
        {
            // Customer file names are routinely non-Latin; a URL that fails to parse would silently lose
            // every metadata update for that page.
            const string greekUrl = "https://contoso.sharepoint.com/sites/x/Shared Documents/Καλημέρα κόσμε.aspx";
            var grouped = PageUpdateGroupingRules.GroupByUrl(new List<PageUpdateEventAppInsightsQueryResult> { UpdateFor(greekUrl) });

            Assert.AreEqual(1, grouped.Count);
            StringAssert.Contains(Uri.UnescapeDataString(grouped.Keys.Single()), "Καλημέρα κόσμε");
        }

        #endregion

        #region Refresh suppression

        [TestMethod]
        public void PageUpdateRefresh_StaleCutoffIsTheConfiguredWindowBeforeNow()
        {
            // A sign flip here would make every URL look stale and rewrite the whole page-metadata set on
            // every import cycle.
            Assert.AreEqual(NowUtc.AddMinutes(-1440), PageUpdateRefreshPolicy.StaleBeforeUtc(1440, NowUtc));
            Assert.AreEqual(NowUtc.AddMinutes(-15), PageUpdateRefreshPolicy.StaleBeforeUtc(15, NowUtc));
            Assert.IsTrue(PageUpdateRefreshPolicy.StaleBeforeUtc(1440, NowUtc) < NowUtc);
        }

        [TestMethod]
        public void PageUpdateRefresh_IsDrivenByTheSuppliedInstantNotTheWallClock()
        {
            var fixedInstant = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(fixedInstant.AddMinutes(-1440), PageUpdateRefreshPolicy.StaleBeforeUtc(1440, fixedInstant));
        }

        #endregion

        #region Metadata property filter

        [TestMethod]
        public void UrlMetadataProps_SharePointSystemFieldsAreDropped()
        {
            Assert.IsFalse(UrlMetadataPropertyRules.IsImportableSimpleProp("vti_x005fparserversion"));
            Assert.IsTrue(UrlMetadataPropertyRules.IsImportableSimpleProp("vti_parserversion"),
                "Only the escaped-underscore system prefix is filtered, not everything starting with 'vti_'.");
        }

        [TestMethod]
        public void UrlMetadataProps_FieldNameLengthBoundaryIsExclusiveAtOneHundred()
        {
            Assert.IsTrue(UrlMetadataPropertyRules.IsImportableSimpleProp(new string('a', 99)));
            Assert.IsFalse(UrlMetadataPropertyRules.IsImportableSimpleProp(new string('a', 100)));
        }

        [TestMethod]
        public void UrlMetadataProps_OrdinaryAndNonLatinFieldNamesAreKept()
        {
            Assert.IsTrue(UrlMetadataPropertyRules.IsImportableSimpleProp("Title"));
            Assert.IsTrue(UrlMetadataPropertyRules.IsImportableSimpleProp("Κατηγορία"));
        }

        #endregion

        #region Comments and likes

        private static PageCommentEvent Comment(int? spId, string email) => new PageCommentEvent { SharePointId = spId, Email = email, Comment = "hi" };

        [TestMethod]
        public void PageUserEvents_EventWithoutEmailOrSharePointId_IsInvalid()
        {
            var decisions = PageUserEventRules.Classify(new List<PageCommentEvent>
            {
                Comment(1, null),
                Comment(2, string.Empty),
                Comment(null, "a@contoso.com")
            }, new List<PageComment>());

            Assert.IsTrue(decisions.All(d => d.Outcome == PageUserEventOutcome.Invalid));
            Assert.IsTrue(decisions.All(d => d.NormalisedEmail == null));
        }

        [TestMethod]
        public void PageUserEvents_EmailIsLowerCasedForTheUserLookup()
        {
            // The user cache is keyed on the address, so a mixed-case address must not create a second user.
            // The address is ASCII on purpose: it is the signed-in user's Entra UPN, which Entra restricts
            // to A-Z a-z 0-9 ' . - _ ! # ^ ~ (#402/#414). The culture-invariance of the lower-casing (the
            // tr-TR dotless-i trap) is guarded separately in OrgUrlStoreTests.
            var decisions = PageUserEventRules.Classify(new List<PageCommentEvent> { Comment(1, "KAlimera@Contoso.OnMicrosoft.com") },
                new List<PageComment>());

            Assert.AreEqual(PageUserEventOutcome.New, decisions.Single().Outcome);
            Assert.AreEqual("kalimera@contoso.onmicrosoft.com", decisions.Single().NormalisedEmail);
        }

        [TestMethod]
        public void PageUserEvents_AlreadyStoredSharePointId_IsNotRecreated()
        {
            var stored = new List<PageComment> { new PageComment { SpID = 7 } };
            var decisions = PageUserEventRules.Classify(new List<PageCommentEvent> { Comment(7, "a@contoso.com") }, stored);

            Assert.AreEqual(PageUserEventOutcome.AlreadyStored, decisions.Single().Outcome);
        }

        [TestMethod]
        public void PageUserEvents_DecisionsComeBackInInputOrder()
        {
            // The caller walks this list logging invalid events and creating new ones, so re-ordering would
            // change the operator-facing log sequence and what has already been created when a create throws.
            var stored = new List<PageComment> { new PageComment { SpID = 2 } };
            var decisions = PageUserEventRules.Classify(new List<PageCommentEvent>
            {
                Comment(1, "a@contoso.com"),   // new
                Comment(2, "b@contoso.com"),   // already stored
                Comment(3, null)               // invalid
            }, stored);

            CollectionAssert.AreEqual(
                new[] { PageUserEventOutcome.New, PageUserEventOutcome.AlreadyStored, PageUserEventOutcome.Invalid },
                decisions.Select(d => d.Outcome).ToArray());
        }

        [TestMethod]
        public void PageUserEvents_LikesAreClassifiedTheSameWayAsComments()
        {
            var stored = new List<PageLike> { new PageLike { SpID = 5 } };
            var decisions = PageUserEventRules.Classify(new List<UserBasedCustomAIEvent>
            {
                new UserBasedCustomAIEvent { SharePointId = 5, Email = "a@contoso.com" },
                new UserBasedCustomAIEvent { SharePointId = 6, Email = "a@contoso.com" }
            }, stored);

            Assert.AreEqual(PageUserEventOutcome.AlreadyStored, decisions[0].Outcome);
            Assert.AreEqual(PageUserEventOutcome.New, decisions[1].Outcome);
        }

        [TestMethod]
        public void Sentiment_IsOnlyRequestedWhenConfiguredAndThereAreNewComments()
        {
            // A cycle with no new comments must produce no cognitive-services traffic (and no bill).
            Assert.IsFalse(PageUserEventRules.ShouldRequestSentiment(hasCognitiveClient: true, newCommentCount: 0));
            Assert.IsFalse(PageUserEventRules.ShouldRequestSentiment(hasCognitiveClient: false, newCommentCount: 5));
            Assert.IsTrue(PageUserEventRules.ShouldRequestSentiment(hasCognitiveClient: true, newCommentCount: 5));
        }

        #endregion
    }
}
