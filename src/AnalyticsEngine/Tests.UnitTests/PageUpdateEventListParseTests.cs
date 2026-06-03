using Microsoft.VisualStudio.TestTools.UnitTesting;
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
