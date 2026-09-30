using Common.Entities.CopilotAdoption;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The wire format a user filter travels in on a query string - the one contract the portal and every
    /// report endpoint share.
    /// </summary>
    [TestClass]
    public class UserFilterCodecTests
    {
        [TestMethod]
        public void Parse_BlankOrNull_IsNoFilter()
        {
            Assert.IsTrue(UserFilterCodec.Parse(null).IsEmpty);
            Assert.IsTrue(UserFilterCodec.Parse("   ").IsEmpty);
            Assert.IsTrue(UserFilterCodec.Parse("null").IsEmpty);
            Assert.IsTrue(UserFilterCodec.Parse("[]").IsEmpty);
        }

        [TestMethod]
        public void Parse_ReadsEveryPartOfAClause()
        {
            var filter = UserFilterCodec.Parse(
                "[{\"d\":\"department\",\"v\":[\"Sales\",\"Marketing\"]},"
                + "{\"j\":\"or\",\"d\":\"org:12\",\"op\":\"isNot\",\"v\":[\"CC-100\"],\"n\":true}]");

            Assert.AreEqual(2, filter.Clauses.Count);

            var first = filter.Clauses[0];
            Assert.AreEqual(UserFilterDimensions.Department, first.Dimension);
            Assert.AreEqual(UserFilterOperator.Is, first.Operator, "'is' is the default operator.");
            Assert.AreEqual(UserFilterJoin.And, first.Join, "'and' is the default join.");
            CollectionAssert.AreEqual(new[] { "Sales", "Marketing" }, first.Values.ToArray());
            Assert.IsFalse(first.IncludeNotSet);

            var second = filter.Clauses[1];
            Assert.AreEqual("org:12", second.Dimension);
            Assert.AreEqual(UserFilterOperator.IsNot, second.Operator);
            Assert.AreEqual(UserFilterJoin.Or, second.Join);
            Assert.IsTrue(second.IncludeNotSet);
        }

        [TestMethod]
        public void Parse_TrimsAndDeduplicatesValuesCaseInsensitively_KeepingTheFirstSpelling()
        {
            var filter = UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\" Sales \",\"sales\",\"\",\"  \",\"Marketing\"]}]");

            CollectionAssert.AreEqual(new[] { "Sales", "Marketing" }, filter.Clauses[0].Values.ToArray());
        }

        [TestMethod]
        public void Parse_KeepsNonLatinValuesIntact()
        {
            var filter = UserFilterCodec.Parse("[{\"d\":\"org:3\",\"v\":[\"Καλημέρα κόσμε\"]}]");

            Assert.AreEqual("Καλημέρα κόσμε", filter.Clauses[0].Values.Single());
        }

        [TestMethod]
        public void Parse_NormalisesEmailDomainsTheWayTheReportDerivesThem()
        {
            var filter = UserFilterCodec.Parse("[{\"d\":\"emailDomain\",\"v\":[\"@Contoso.COM\",\"someone@Fabrikam.com\"]}]");

            CollectionAssert.AreEqual(new[] { "contoso.com", "fabrikam.com" }, filter.Clauses[0].Values.ToArray());
        }

        [TestMethod]
        public void Parse_TurnsTheNoDomainBucketIntoTheNotSetFlag()
        {
            // The adoption report lists people with no derivable domain as "(no domain)" and the row is
            // clickable. That is the absence of a domain, and compared as a value it would match nobody.
            var filter = UserFilterCodec.Parse(
                "[{\"d\":\"emailDomain\",\"v\":[\"" + CopilotAdoptionEmailDomain.NoDomainLabel + "\",\"contoso.com\"]}]");

            var clause = filter.Clauses.Single();
            Assert.IsTrue(clause.IncludeNotSet);
            CollectionAssert.AreEqual(new[] { "contoso.com" }, clause.Values.ToArray());
        }

        [TestMethod]
        public void Parse_CanonicalisesFixedValueTokens()
        {
            var filter = UserFilterCodec.Parse("[{\"d\":\"userType\",\"v\":[\"GUEST\"]},{\"d\":\"accountStatus\",\"v\":[\"Disabled\"]}]");

            Assert.AreEqual(UserFilterTokens.Guest, filter.Clauses[0].Values.Single());
            Assert.AreEqual(UserFilterTokens.Disabled, filter.Clauses[1].Values.Single());
        }

        [TestMethod]
        public void Parse_AllowsAClauseThatOnlyMatchesNotSet()
        {
            var clause = UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"n\":true}]").Clauses.Single();

            Assert.AreEqual(0, clause.Values.Count);
            Assert.IsTrue(clause.IncludeNotSet);
        }

        [DataTestMethod]
        [DataRow("not json", DisplayName = "Not JSON")]
        [DataRow("{\"d\":\"department\"}", DisplayName = "Object rather than array")]
        [DataRow("[\"department\"]", DisplayName = "Clause is not an object")]
        [DataRow("[{\"v\":[\"Sales\"]}]", DisplayName = "No dimension")]
        [DataRow("[{\"d\":\"favouriteColour\",\"v\":[\"Blue\"]}]", DisplayName = "Unknown Entra attribute")]
        [DataRow("[{\"d\":\"org:abc\",\"v\":[\"x\"]}]", DisplayName = "Org key without an id")]
        [DataRow("[{\"d\":\"org:012\",\"v\":[\"x\"]}]", DisplayName = "Org key with a leading zero")]
        [DataRow("[{\"d\":\"org:0\",\"v\":[\"x\"]}]", DisplayName = "Org key with id zero")]
        [DataRow("[{\"d\":\"org:+12\",\"v\":[\"x\"]}]", DisplayName = "Org key with a sign")]
        [DataRow("[{\"d\":\"department\",\"op\":\"startsWith\",\"v\":[\"S\"]}]", DisplayName = "Unknown operator")]
        [DataRow("[{\"d\":\"department\",\"j\":\"xor\",\"v\":[\"S\"]}]", DisplayName = "Unknown join")]
        [DataRow("[{\"d\":\"department\",\"v\":[]}]", DisplayName = "No values and not-set not requested")]
        [DataRow("[{\"d\":\"department\",\"v\":[\"  \"]}]", DisplayName = "Only blank values")]
        [DataRow("[{\"d\":\"department\",\"v\":\"Sales\"}]", DisplayName = "Values not an array")]
        [DataRow("[{\"d\":\"department\",\"v\":[1]}]", DisplayName = "A value that is not a string")]
        [DataRow("[{\"d\":\"department\",\"v\":[\"Sales\"],\"n\":\"yes\"}]", DisplayName = "Not-set flag not a boolean")]
        [DataRow("[{\"d\":7,\"v\":[\"Sales\"]}]", DisplayName = "Dimension not a string")]
        [DataRow("[{\"d\":\"userType\",\"v\":[\"contractor\"]}]", DisplayName = "Unknown fixed token")]
        [DataRow("[{\"d\":\"accountStatus\",\"op\":\"contains\",\"v\":[\"en\"]}]", DisplayName = "Contains on a fixed-value dimension")]
        [DataRow("[{\"d\":\"managementChain\",\"op\":\"contains\",\"v\":[\"a\"]}]", DisplayName = "Contains on the management chain")]
        public void Parse_RejectsMalformedFilters(string encoded)
        {
            Assert.ThrowsException<UserFilterFormatException>(() => UserFilterCodec.Parse(encoded));
        }

        [TestMethod]
        public void Parse_RejectsMoreClausesThanTheLimit()
        {
            var clauses = Enumerable.Range(0, UserFilterCodec.MaxClauses + 1)
                .Select(i => "{\"d\":\"department\",\"v\":[\"D" + i + "\"]}");

            Assert.ThrowsException<UserFilterFormatException>(() => UserFilterCodec.Parse("[" + string.Join(",", clauses) + "]"));
        }

        [TestMethod]
        public void Parse_RejectsMoreValuesThanTheLimit()
        {
            var values = Enumerable.Range(0, UserFilterCodec.MaxValuesPerClause + 1).Select(i => "\"V" + i + "\"");

            Assert.ThrowsException<UserFilterFormatException>(
                () => UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[" + string.Join(",", values) + "]}]"));
        }

        [TestMethod]
        public void Parse_LimitsThePiecesOfTextOneConditionLooksFor()
        {
            // Each piece of text is searched for in every distinct value - one per person for the user
            // name - so the limit is far lower than for exact values, which are a lookup each.
            Assert.AreEqual(
                UserFilterCodec.MaxTextTermsPerClause,
                UserFilterCodec.Parse(TextClauses(UserFilterDimensions.UserName, "contains", UserFilterCodec.MaxTextTermsPerClause))
                    .Clauses.Single().Values.Count,
                "The limit itself is allowed.");

            foreach (var op in new[] { "contains", "notContains" })
            {
                var ex = Assert.ThrowsException<UserFilterFormatException>(
                    () => UserFilterCodec.Parse(TextClauses(UserFilterDimensions.Department, op, UserFilterCodec.MaxTextTermsPerClause + 1)));
                StringAssert.Contains(ex.Message, "pieces of text");
            }

            Assert.AreEqual(
                UserFilterCodec.MaxTextTermsPerClause + 1,
                UserFilterCodec.Parse(TextClauses(UserFilterDimensions.Department, "is", UserFilterCodec.MaxTextTermsPerClause + 1))
                    .Clauses.Single().Values.Count,
                "Exact values are not text searches, so they keep the far higher per-condition limit.");
        }

        [TestMethod]
        public void Parse_LimitsThePiecesOfTextTheWholeFilterLooksFor()
        {
            // Split across conditions, each within its own limit, the whole filter still searches for
            // more text than one request should pay for.
            var half = UserFilterCodec.MaxTextTerms / 2;
            var withinLimit = "[" + TextClause(UserFilterDimensions.UserName, "contains", half, "a")
                + "," + TextClause(UserFilterDimensions.Department, "notContains", UserFilterCodec.MaxTextTerms - half, "b") + "]";
            Assert.AreEqual(2, UserFilterCodec.Parse(withinLimit).Clauses.Count);

            var overLimit = "[" + TextClause(UserFilterDimensions.UserName, "contains", half, "a")
                + "," + TextClause(UserFilterDimensions.Department, "notContains", UserFilterCodec.MaxTextTerms - half + 1, "b") + "]";
            var ex = Assert.ThrowsException<UserFilterFormatException>(() => UserFilterCodec.Parse(overLimit));
            StringAssert.Contains(ex.Message, "in all");

            var exactValuesDoNotCount = "[" + TextClause(UserFilterDimensions.UserName, "contains", UserFilterCodec.MaxTextTerms, "a")
                + "," + TextClause(UserFilterDimensions.Department, "is", 20, "b") + "]";
            Assert.AreEqual(2, UserFilterCodec.Parse(exactValuesDoNotCount).Clauses.Count);
        }

        private static string TextClauses(string dimension, string op, int count)
        {
            return "[" + TextClause(dimension, op, count, "t") + "]";
        }

        private static string TextClause(string dimension, string op, int count, string prefix)
        {
            var values = Enumerable.Range(0, count).Select(i => "\"" + prefix + i + "\"");
            return "{\"d\":\"" + dimension + "\",\"op\":\"" + op + "\",\"v\":[" + string.Join(",", values) + "]}";
        }

        [TestMethod]
        public void Parse_RejectsAValueWiderThanAnOrganisationName()
        {
            var value = new string('x', UserFilterCodec.MaxValueLength + 1);

            Assert.ThrowsException<UserFilterFormatException>(
                () => UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"" + value + "\"]}]"));
        }

        [TestMethod]
        public void Parse_RejectsAnEncodedFilterLongerThanTheLimit()
        {
            var text = "[{\"d\":\"department\",\"v\":[\"" + new string('x', UserFilterCodec.MaxEncodedLength) + "\"]}]";

            Assert.ThrowsException<UserFilterFormatException>(() => UserFilterCodec.Parse(text));
        }

        [TestMethod]
        public void Parse_DoesNotReinterpretDateLikeValues()
        {
            // Json.NET turns ISO-looking strings into DateTime by default, which would silently change
            // a department literally called "2026-01-01" into a differently formatted string.
            var filter = UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"2026-01-01T00:00:00Z\"]}]");

            Assert.AreEqual("2026-01-01T00:00:00Z", filter.Clauses[0].Values.Single());
        }

        [TestMethod]
        public void Serialize_RoundTrips_AndOmitsDefaults()
        {
            const string encoded =
                "[{\"d\":\"department\",\"v\":[\"Sales\"]},{\"j\":\"or\",\"d\":\"org:4\",\"op\":\"notContains\",\"v\":[\"intern\"],\"n\":true}]";

            var serialized = UserFilterCodec.Serialize(UserFilterCodec.Parse(encoded));

            Assert.AreEqual(encoded, serialized);
            Assert.IsNull(UserFilterCodec.Serialize(UserFilterExpression.Empty));
        }

        [TestMethod]
        public void Groups_SplitAtOr_BecauseAndBindsTighter()
        {
            var filter = UserFilterCodec.Parse(
                "[{\"d\":\"department\",\"v\":[\"A\"]},{\"d\":\"country\",\"v\":[\"B\"]},"
                + "{\"j\":\"or\",\"d\":\"companyName\",\"v\":[\"C\"]},{\"d\":\"jobTitle\",\"v\":[\"D\"]}]");

            Assert.AreEqual(2, filter.Groups.Count);
            CollectionAssert.AreEqual(new[] { "department", "country" }, filter.Groups[0].Select(c => c.Dimension).ToArray());
            CollectionAssert.AreEqual(new[] { "companyName", "jobTitle" }, filter.Groups[1].Select(c => c.Dimension).ToArray());
        }

        [TestMethod]
        public void Groups_IgnoreAnOrOnTheFirstClause()
        {
            // There is nothing before the first clause to join to. An empty leading group would match
            // nobody - or, evaluated naively as "all of nothing", everybody.
            var filter = UserFilterCodec.Parse("[{\"j\":\"or\",\"d\":\"department\",\"v\":[\"A\"]}]");

            Assert.AreEqual(1, filter.Groups.Count);
            Assert.AreEqual(1, filter.Groups[0].Count);
        }
    }
}
