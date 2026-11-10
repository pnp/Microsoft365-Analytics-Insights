using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for PageUpdateEventCustomProps.TaxonomyProps / PageComments / Likes
    /// after the refactor that extracted TryDeserializeList&lt;T&gt;. The behavioural contract
    /// (empty input -> empty list, malformed input -> empty list with stderr note, valid input
    /// -> populated list) must be unchanged across the three lazy-parse properties.
    /// </summary>
    [TestClass]
    public class PageUpdateEventListParseTests
    {
        [TestMethod]
        public void TaxonomyProps_EmptyOrNullString_ReturnsEmptyList()
        {
            Assert.AreEqual(0, new PageUpdateEventCustomProps { Url = "u", TaxonomyPropsString = null }.TaxonomyProps.Count);
            Assert.AreEqual(0, new PageUpdateEventCustomProps { Url = "u", TaxonomyPropsString = string.Empty }.TaxonomyProps.Count);
        }

        [TestMethod]
        public void TaxonomyProps_ValidJsonArray_IsParsed()
        {
            var props = new PageUpdateEventCustomProps
            {
                Url = "u",
                TaxonomyPropsString = "[{\"Name\":\"Tag1\",\"Value\":\"V1\"},{\"Name\":\"Tag2\",\"Value\":\"V2\"}]"
            };
            Assert.AreEqual(2, props.TaxonomyProps.Count);
        }

        [TestMethod]
        public void TaxonomyProps_MalformedJson_ReturnsEmptyListWithoutThrowing()
        {
            var props = new PageUpdateEventCustomProps { Url = "u", TaxonomyPropsString = "not-json" };
            // Must not throw; must return a non-null, empty list (so callers can foreach safely).
            Assert.IsNotNull(props.TaxonomyProps);
            Assert.AreEqual(0, props.TaxonomyProps.Count);
        }

        /// <summary>
        /// A page-update event as AITracker sends it from 1.6.5, read the way the importer reads it from App Insights.
        /// The SDK sends each property holding an object as its JSON, so the taxonomy fields arrive as a string:
        /// <c>TrackerTaxonomyJson</c> is the string AITracker's models.test.ts asserts the tracker produces. The raw
        /// value of the same field also arrives with the other properties.
        /// </summary>
        [TestMethod]
        public void TaxonomyProps_AsTheTrackerSendsThem_AreParsed()
        {
            const string TrackerTaxonomyJson = "[{\"id\":\"f5b7ced7-2039-47f9-a22d-32c66d2eec65\",\"label\":\"Contoso Finance\",\"propName\":\"Department\"}]";
            const string RawValue = "2;#Contoso Finance|f5b7ced7-2039-47f9-a22d-32c66d2eec65";
            var filed = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
            var customDimensions = JsonConvert.SerializeObject(new Dictionary<string, string>
            {
                ["url"] = "https://contoso.sharepoint.com/sites/finance/SitePages/Home.aspx",
                ["props"] = JsonConvert.SerializeObject(new Dictionary<string, string> { ["Title"] = "Home", ["Department"] = RawValue }),
                ["pageComments"] = "[]",
                ["pageLikes"] = "[]",
                ["taxonomyProps"] = TrackerTaxonomyJson,
            });
            var table = new AppInsightsTable
            {
                Columns = new[] { "timestamp", "customDimensions", "user_AuthenticatedId", "name" }.Select(n => new Column { Name = n }).ToList(),
                Rows = new List<List<object>> { new List<object> { filed, customDimensions, "user@contoso.com", AppInsightsImporterConstants.EVENT_NAME_PAGE_UPDATE } }
            };

            var update = (PageUpdateEventAppInsightsQueryResult)new CustomEventsResultCollection(table, filed.Date, NullLogger.Instance).Rows.Single();

            var term = update.CustomProperties.TaxonomyProps.Single();
            Assert.IsTrue(term.IsValid);
            Assert.AreEqual(new Guid("f5b7ced7-2039-47f9-a22d-32c66d2eec65"), term.Id);
            Assert.AreEqual("Contoso Finance", term.Label);
            Assert.AreEqual("Department", term.PropName);
            Assert.AreEqual(RawValue, update.CustomProperties.SimplePropsDic["Department"]);
        }

        [TestMethod]
        public void PageComments_EmptyOrNullString_ReturnsEmptyList()
        {
            Assert.AreEqual(0, new PageUpdateEventCustomProps { Url = "u", CommentsString = null }.PageComments.Count);
            Assert.AreEqual(0, new PageUpdateEventCustomProps { Url = "u", CommentsString = string.Empty }.PageComments.Count);
        }

        [TestMethod]
        public void PageComments_MalformedJson_ReturnsEmptyListWithoutThrowing()
        {
            var props = new PageUpdateEventCustomProps { Url = "u", CommentsString = "{not-an-array}" };
            Assert.IsNotNull(props.PageComments);
            Assert.AreEqual(0, props.PageComments.Count);
        }

        /// <summary>
        /// The problem is recorded with the field and its length, for PageUpdateManager to log by URL id (#853). Neither
        /// the comments nor the page's URL are printed.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void PageComments_MalformedJson_RecordsTheFieldButNotTheComments()
        {
            const string commentText = "Contoso private comment";
            const string url = "https://contoso.sharepoint.com/sites/private/SitePages/page.aspx";
            var original = System.Console.Out;
            var captured = new System.IO.StringWriter();
            System.Console.SetOut(captured);
            PageUpdateEventCustomProps props;
            try
            {
                // Cut off mid-way, as a truncated payload would be: valid entries before an invalid ending.
                props = new PageUpdateEventCustomProps { Url = url, CommentsString = "[{\"comment\":\"" + commentText + "\"}, {\"comment\":" };
                Assert.AreEqual(0, props.PageComments.Count);
            }
            finally
            {
                System.Console.SetOut(original);
            }

            var problem = props.ParseProblems.Single();
            StringAssert.Contains(problem, nameof(PageUpdateEventCustomProps.CommentsString));
            Assert.IsFalse(problem.Contains(commentText) || problem.Contains(url), "The comments or the URL were recorded: " + problem);
            var output = captured.ToString();
            Assert.IsFalse(output.Contains(commentText) || output.Contains(url), "Printed: " + output);
        }

        [TestMethod]
        public void Likes_EmptyOrNullString_ReturnsEmptyList()
        {
            Assert.AreEqual(0, new PageUpdateEventCustomProps { Url = "u", LikesString = null }.Likes.Count);
            Assert.AreEqual(0, new PageUpdateEventCustomProps { Url = "u", LikesString = string.Empty }.Likes.Count);
        }

        [TestMethod]
        public void Likes_MalformedJson_ReturnsEmptyListWithoutThrowing()
        {
            var props = new PageUpdateEventCustomProps { Url = "u", LikesString = "garbage" };
            Assert.IsNotNull(props.Likes);
            Assert.AreEqual(0, props.Likes.Count);
        }

        [TestMethod]
        public void LazyParsers_AreCached_SecondAccessReturnsSameInstance()
        {
            // Pin the lazy-init contract so a refactor that re-parses on every get is caught here.
            var props = new PageUpdateEventCustomProps
            {
                Url = "u",
                TaxonomyPropsString = "[]",
                CommentsString = "[]",
                LikesString = "[]"
            };
            Assert.AreSame(props.TaxonomyProps, props.TaxonomyProps);
            Assert.AreSame(props.PageComments, props.PageComments);
            Assert.AreSame(props.Likes, props.Likes);
        }
    }
}
