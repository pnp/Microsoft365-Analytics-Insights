using Microsoft.VisualStudio.TestTools.UnitTesting;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the search-term staging rules extracted from SearchesSaveExtension (issue #369).
    /// Runs with zero SQL Server, Graph, Redis or Service Bus dependency.
    /// </summary>
    [TestClass]
    public class AppInsightsSearchRulesTests
    {
        private static SearchEventAppInsightsQueryResult SearchFor(string text)
        {
            return new SearchEventAppInsightsQueryResult
            {
                CustomProperties = new SearchCustomProps { SearchText = text, SessionId = "session-1" }
            };
        }

        [TestMethod]
        public void Searches_SearchTextOver250Chars_IsTruncatedToExactly250()
        {
            var result = SearchTermRules.Truncate(new string('a', 400));

            Assert.AreEqual(SearchTermRules.MaxSearchTermLength, result.Length,
                "The staged value must fit the nvarchar(250) parameter exactly.");
            Assert.IsTrue(result.EndsWith("..."), "Truncation keeps the trailing ellipsis the original code wrote.");
            Assert.AreEqual(new string('a', 247) + "...", result);
        }

        [TestMethod]
        public void Searches_SearchTextExactly250Chars_IsNotTruncated()
        {
            var exact = new string('b', 250);

            Assert.AreEqual(exact, SearchTermRules.Truncate(exact), "A term that already fits must be left alone.");
        }

        [TestMethod]
        public void Searches_ShortSearchText_IsUnchanged()
        {
            Assert.AreEqual("quarterly report", SearchTermRules.Truncate("quarterly report"));
        }

        [TestMethod]
        public void Searches_SearchTextWithGreekCharacters_TruncatesByCharacterNotByte()
        {
            // Greek is two bytes per character in UTF-8 but one UTF-16 char, which is what both
            // string.Length and the nvarchar(250) parameter count. Truncating by byte would cut this
            // to roughly half the characters it should keep.
            var greek = string.Concat(System.Linq.Enumerable.Repeat("καλημέρα", 50)); // 400 chars

            var result = SearchTermRules.Truncate(greek);

            Assert.AreEqual(250, result.Length, "Must be limited to 250 characters, not 250 bytes.");
            Assert.AreEqual(greek.Substring(0, 247) + "...", result);
            Assert.IsTrue(result.StartsWith("καλημέρα"), "Greek characters must survive intact, not become '?'.");
        }

        [TestMethod]
        public void Searches_TruncationNeverSplitsASurrogatePair()
        {
            // An emoji is a surrogate pair. Cutting at a fixed offset can land between the two halves
            // and leave an unpaired surrogate - a string SQL Server stores but which is not valid UTF-16.
            // Pad so that the 247-char cut falls exactly in the middle of a pair.
            var emoji = "\U0001F600";
            var text = new string('x', 246) + emoji + new string('y', 100);

            var result = SearchTermRules.Truncate(text);

            Assert.IsTrue(result.Length <= SearchTermRules.MaxSearchTermLength);
            AssertNoLoneSurrogates(result);
            Assert.AreEqual(new string('x', 246) + "...", result,
                "The straddling pair must be dropped whole, not halved.");
        }

        [TestMethod]
        public void Searches_TruncationKeepsWholeSurrogatePairsThatFit()
        {
            // Guards against an over-eager fix that strips every surrogate rather than only the pair
            // straddling the cut: a complete pair well inside the kept portion must survive untouched.
            var emoji = "\U0001F600";
            var text = emoji + new string('x', 400);

            var result = SearchTermRules.Truncate(text);

            Assert.IsTrue(result.StartsWith(emoji), "A complete pair inside the kept portion must be preserved.");
            Assert.AreEqual(SearchTermRules.MaxSearchTermLength, result.Length);
            AssertNoLoneSurrogates(result);
        }

        [TestMethod]
        public void Searches_AllEmojiInput_TruncatesToWholePairsOnly()
        {
            // The pathological case for the boundary: every even index is a high surrogate. Guards
            // against both halving a pair and stripping supplementary characters wholesale.
            var emoji = "\U0001F600";
            var text = string.Concat(System.Linq.Enumerable.Repeat(emoji, 200)); // 400 UTF-16 units

            var result = SearchTermRules.Truncate(text);

            // Exact expectation, not just invariants: index 246 is a high surrogate, so the cut moves
            // back to 246, keeping 123 whole pairs. Asserting the precise value stops a degenerate
            // implementation (e.g. returning just "<emoji>...") from satisfying the looser checks.
            Assert.AreEqual(string.Concat(System.Linq.Enumerable.Repeat(emoji, 123)) + "...", result);
            Assert.AreEqual(249, result.Length);
            Assert.IsTrue(result.Length <= SearchTermRules.MaxSearchTermLength);
            AssertNoLoneSurrogates(result);
        }

        /// <summary>
        /// Walks the string in pairs. Note a per-char "is not a surrogate" assertion would be wrong: a
        /// valid emoji is made of two surrogate chars, so that check really asserts "contains no emoji"
        /// and passes only while the test data happens to truncate every pair away.
        /// </summary>
        private static void AssertNoLoneSurrogates(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    Assert.IsTrue(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]),
                        $"High surrogate at index {i} has no low partner.");
                    i++; // consume the matched pair
                }
                else
                {
                    Assert.IsFalse(char.IsLowSurrogate(value[i]), $"Lone low surrogate at index {i}.");
                }
            }
        }

        [TestMethod]
        public void Searches_NullOrEmptySearchText_IsSkipped()
        {
            // Regression guard: the old save path went straight to searchTerm.Length, so a null
            // SearchText threw a NullReferenceException that aborted the entire Searches section.
            //
            // The empty-string case cannot arrive from the API - the parser only adds events for which
            // IsValid holds, and that already requires a non-empty SearchText - but Rows is a public
            // settable list, so the guard still matters for collections built any other way.
            Assert.IsFalse(SearchTermRules.ShouldStage(SearchFor(null)));
            Assert.IsFalse(SearchTermRules.ShouldStage(SearchFor(string.Empty)));
            Assert.IsFalse(SearchTermRules.ShouldStage(null));
        }

        [TestMethod]
        public void Searches_WhitespaceOnlySearchText_IsStillStaged()
        {
            // Pins the boundary deliberately: this is IsNullOrEmpty, not IsNullOrWhiteSpace, so a
            // whitespace-only term stages exactly as it did before. Tightening it would be a real
            // behavioural change to what lands in search_terms, not a crash fix.
            Assert.IsTrue(SearchTermRules.ShouldStage(SearchFor("   ")));
        }

        [TestMethod]
        public void Searches_ValidSearchText_IsStaged()
        {
            Assert.IsTrue(SearchTermRules.ShouldStage(SearchFor("καλημέρα κόσμε")));
        }

        [TestMethod]
        public void Searches_NullSearchText_DoesNotThrowOnTruncate()
        {
            Assert.IsNull(SearchTermRules.Truncate(null));
            Assert.AreEqual(string.Empty, SearchTermRules.Truncate(string.Empty));
        }
    }
}
