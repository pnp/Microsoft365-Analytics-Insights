using System;
using System.Collections.Generic;
using System.Threading;

namespace Common.Entities.LicenceActivity
{
    /// <summary>
    /// Complementary withholding across licences, for a reader without See PII (#844): withholding a licence that fewer
    /// than the minimum hold must hold against the figures published beside it.
    /// </summary>
    /// <remarks>
    /// <para>The licence table puts "Everyone holding a licence" beside every licence. One person can hold several licences,
    /// so everyone is the union of the licences' holders, not their sum. Take a figure - everyone, or a licence - and
    /// subtract licences whose holders all hold it, no two of which share a holder: the difference is exactly the activity
    /// of the people that selection leaves out. When they are 1 to minimum - 1 people, typically the holders of a licence
    /// withheld for having so few, the difference gives them back. With licences A (12 holders), B (3) and C (6) held by
    /// different people, everyone less A less C is B.</para>
    /// <para>So while such a selection exists, the smallest licence in it - the figure subtracted from included - is
    /// withheld too, and the search runs again. Withholding a licence only ever takes selections away, so this ends, and
    /// nothing withheld for one figure exposes another. "Everyone holding a licence" itself always stays.</para>
    /// <para>Counts can't decide this, so the count-based rule Copilot Adoption, spend and investment use
    /// (<c>SmallPopulations.WithholdOverlappingComplement</c>, the smallest other group of at least the minimum) doesn't fit.
    /// If C's holders all hold A, withholding C hides nobody - everyone less A is still B - and A has to go instead; and a
    /// licence nearly everyone holds gives the few without it back however small the other licences are. The read model
    /// holds every membership, so this decides on the people themselves.</para>
    /// <para>What this does not cover is listed in <c>PortalPrivacyFloorTests</c>: combinations that add a figure back (two
    /// licences that between them hold everyone, added together less everyone, are the people who hold both), and rows of
    /// different breakdowns - a department, a country, a licence - set against each other.</para>
    /// </remarks>
    internal static class LicenceActivityComplements
    {
        /// <summary>
        /// The most branches one search explores. Real licence data settles in far fewer; past this, the licences inside
        /// the figure are withheld smallest first until enough of its people hold none of them (see <see cref="Withhold"/>).
        /// </summary>
        internal const int DefaultSearchBudget = 10000;

        /// <summary>
        /// Withholds, besides the licences already <paramref name="withheld"/>, every licence that one of the figures shown
        /// would otherwise give a handful of people back through.
        /// </summary>
        /// <param name="profiles">
        /// Each different set of licences the people in view hold, a bit per licence slot (slot <c>s</c> is bit
        /// <c>s % 64</c> of word <c>s / 64</c>); the empty set for people holding none that are shown.
        /// </param>
        /// <param name="people">For each profile, how many people in view hold exactly that set.</param>
        /// <param name="holders">For each licence slot, how many people in view hold it.</param>
        /// <param name="withheld">For each slot, whether its activity is withheld; set here for every licence this withholds.</param>
        /// <param name="minimum">The fewest people a figure may describe.</param>
        /// <param name="searchBudget">For tests: the most branches one search explores.</param>
        /// <returns>For each slot, whether this withheld it.</returns>
        internal static bool[] Withhold(
            IReadOnlyList<ulong[]> profiles, IReadOnlyList<int> people, IReadOnlyList<int> holders, bool[] withheld,
            int minimum, CancellationToken cancellationToken, int searchBudget = DefaultSearchBudget)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            if (people == null) throw new ArgumentNullException(nameof(people));
            if (holders == null) throw new ArgumentNullException(nameof(holders));
            if (withheld == null) throw new ArgumentNullException(nameof(withheld));
            if (profiles.Count != people.Count || withheld.Length != holders.Count)
                throw new ArgumentException("Every profile needs its people, and every licence its holders.");

            var words = (holders.Count + 63) >> 6;
            var complement = new bool[holders.Count];
            // Everyone holding a licence (-1) first, then each licence shown.
            for (var outer = -1; outer < holders.Count; outer++)
            {
                while (outer < 0 || (!withheld[outer] && holders[outer] > 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidates = Inside(profiles, people, holders, withheld, outer, words);
                    if (IsEmpty(candidates)) break;
                    var search = new Search(profiles, people, outer, candidates, minimum, searchBudget, words);
                    var selection = search.Find(cancellationToken);
                    if (selection != null)
                    {
                        if (outer >= 0) Set(selection, outer);
                        WithholdSmallest(selection, holders, withheld, complement);
                        continue;
                    }
                    if (search.Exhausted)
                    {
                        // Unproven either way: withhold the licences inside this figure, smallest first, until at least the
                        // minimum of its people hold none of those left. Every selection then leaves them all out.
                        while (!IsEmpty(candidates) && search.Uncoverable(candidates) < minimum)
                        {
                            Clear(candidates, WithholdSmallest(candidates, holders, withheld, complement));
                        }
                    }
                    break;
                }
            }
            return complement;
        }

        /// <summary>
        /// The licences shown that a reader can subtract from the figure: for everyone (<paramref name="outer"/> -1), every
        /// licence shown; for a licence, every other licence shown all of whose holders hold it as well.
        /// </summary>
        private static ulong[] Inside(
            IReadOnlyList<ulong[]> profiles, IReadOnlyList<int> people, IReadOnlyList<int> holders, bool[] withheld,
            int outer, int words)
        {
            var result = new ulong[words];
            for (var slot = 0; slot < holders.Count; slot++)
                if (!withheld[slot] && holders[slot] > 0 && slot != outer) Set(result, slot);
            if (outer < 0) return result;
            // A licence that anyone holds without the outer one is not inside it.
            for (var index = 0; index < profiles.Count; index++)
            {
                if (people[index] <= 0 || Has(profiles[index], outer)) continue;
                for (var word = 0; word < words; word++) result[word] &= ~profiles[index][word];
            }
            return result;
        }

        private static int WithholdSmallest(ulong[] slots, IReadOnlyList<int> holders, bool[] withheld, bool[] complement)
        {
            var smallest = -1;
            for (var slot = 0; slot < holders.Count; slot++)
                if (Has(slots, slot) && (smallest < 0 || holders[slot] < holders[smallest])) smallest = slot;
            withheld[smallest] = true;
            complement[smallest] = true;
            return smallest;
        }

        private static bool Has(ulong[] bits, int slot) => (bits[slot >> 6] & (1UL << (slot & 63))) != 0;
        private static void Set(ulong[] bits, int slot) => bits[slot >> 6] |= 1UL << (slot & 63);
        private static void Clear(ulong[] bits, int slot) => bits[slot >> 6] &= ~(1UL << (slot & 63));

        private static bool IsEmpty(ulong[] bits)
        {
            foreach (var word in bits)
                if (word != 0) return false;
            return true;
        }

        /// <summary>
        /// One figure's search for a selection of licences inside it, no two sharing a holder, that leaves out 1 to
        /// minimum - 1 of its people: an exact cover of its people allowing a few out, branching on the largest group
        /// that must be covered with the fewest licences that could cover it.
        /// </summary>
        private sealed class Search
        {
            private readonly ulong[][] _groups;
            private readonly int[] _people;
            private readonly ulong[][] _conflicts;
            private readonly int _minimum;
            private readonly int _words;
            private int _budget;
            private CancellationToken _cancellationToken;

            /// <param name="outer">The figure: -1 for everyone, otherwise its licence slot.</param>
            /// <param name="candidates">The licences inside it that a selection may use.</param>
            internal Search(
                IReadOnlyList<ulong[]> profiles, IReadOnlyList<int> people, int outer, ulong[] candidates,
                int minimum, int budget, int words)
            {
                _minimum = minimum;
                _budget = budget;
                _words = words;

                // The figure's people, grouped by which of the candidates they hold.
                var groups = new Dictionary<ulong[], int>(ProfileComparer.Instance);
                foreach (var index in Indexes(profiles.Count))
                {
                    if (people[index] <= 0 || (outer >= 0 && !Has(profiles[index], outer))) continue;
                    var group = new ulong[words];
                    for (var word = 0; word < words; word++) group[word] = profiles[index][word] & candidates[word];
                    groups.TryGetValue(group, out var count);
                    groups[group] = count + people[index];
                }
                _groups = new ulong[groups.Count][];
                _people = new int[groups.Count];
                var next = 0;
                foreach (var pair in groups)
                {
                    _groups[next] = pair.Key;
                    _people[next++] = pair.Value;
                }

                // Two licences someone holds together can't both be subtracted: that person would be taken away twice.
                _conflicts = new ulong[words * 64][];
                foreach (var group in _groups)
                    for (var slot = 0; slot < words * 64; slot++)
                    {
                        if (!Has(group, slot)) continue;
                        var conflicts = _conflicts[slot] ?? (_conflicts[slot] = new ulong[words]);
                        for (var word = 0; word < words; word++) conflicts[word] |= group[word];
                    }
            }

            /// <summary>True when the search gave up before deciding.</summary>
            internal bool Exhausted { get; private set; }

            /// <summary>The figure's people who hold none of <paramref name="candidates"/>: left out by every selection of them.</summary>
            internal int Uncoverable(ulong[] candidates)
            {
                var people = 0;
                for (var index = 0; index < _groups.Length; index++)
                    if (!Intersects(_groups[index], candidates)) people += _people[index];
                return people;
            }

            /// <summary>A selection that leaves out 1 to minimum - 1 of the figure's people, or null when none does (or <see cref="Exhausted"/>).</summary>
            internal ulong[] Find(CancellationToken cancellationToken)
            {
                _cancellationToken = cancellationToken;
                return Find(new ulong[_words], new ulong[_words]);
            }

            private ulong[] Find(ulong[] chosen, ulong[] excluded)
            {
                if (_budget-- <= 0)
                {
                    Exhausted = true;
                    return null;
                }
                if ((_budget & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();

                // Groups no licence still allowed can cover are left out whatever is chosen next.
                var allowed = new ulong[_words];
                for (var word = 0; word < _words; word++) allowed[word] = ~excluded[word];
                var leftOut = 0;
                var open = new List<int>();
                for (var index = 0; index < _groups.Length; index++)
                {
                    if (Intersects(_groups[index], chosen)) continue;
                    if (Intersects(_groups[index], allowed))
                    {
                        open.Add(index);
                        continue;
                    }
                    leftOut += _people[index];
                    if (leftOut >= _minimum) return null;
                }
                // Choosing nothing subtracts nothing: the figure itself, which is shown or withheld on its own.
                if (open.Count == 0) return leftOut > 0 && !IsEmpty(chosen) ? chosen : null;

                // A group larger than the people that may still be left out has to be covered: branch on the one with the
                // fewest licences that could. Otherwise branch on the largest group: cover it, or leave it out.
                var room = _minimum - 1 - leftOut;
                var pick = -1;
                var pickWays = 0;
                var mustCover = false;
                foreach (var index in open)
                {
                    var must = _people[index] > room;
                    var ways = CountAllowed(_groups[index], excluded);
                    if (must
                        ? !mustCover || ways < pickWays || (ways == pickWays && _people[index] > _people[pick])
                        : !mustCover && (pick < 0 || _people[index] > _people[pick]))
                    {
                        pick = index;
                        pickWays = ways;
                        mustCover = must;
                    }
                }

                var options = _groups[pick];
                for (var slot = 0; slot < _words * 64; slot++)
                {
                    if (!Has(options, slot) || Has(excluded, slot)) continue;
                    var nextChosen = (ulong[])chosen.Clone();
                    Set(nextChosen, slot);
                    var nextExcluded = (ulong[])excluded.Clone();
                    for (var word = 0; word < _words; word++) nextExcluded[word] |= _conflicts[slot][word];
                    var found = Find(nextChosen, nextExcluded);
                    if (found != null || Exhausted) return found;
                }
                if (mustCover) return null;

                var leaveOut = (ulong[])excluded.Clone();
                for (var word = 0; word < _words; word++) leaveOut[word] |= options[word];
                return Find(chosen, leaveOut);
            }

            private int CountAllowed(ulong[] group, ulong[] excluded)
            {
                var count = 0;
                for (var word = 0; word < _words; word++)
                {
                    var bits = group[word] & ~excluded[word];
                    while (bits != 0)
                    {
                        bits &= bits - 1;
                        count++;
                    }
                }
                return count;
            }

            private static bool Intersects(ulong[] left, ulong[] right)
            {
                for (var word = 0; word < left.Length; word++)
                    if ((left[word] & right[word]) != 0) return true;
                return false;
            }

            private static IEnumerable<int> Indexes(int count)
            {
                for (var index = 0; index < count; index++) yield return index;
            }
        }

        /// <summary>Compares licence sets by the licences in them.</summary>
        internal sealed class ProfileComparer : IEqualityComparer<ulong[]>
        {
            internal static readonly ProfileComparer Instance = new ProfileComparer();

            public bool Equals(ulong[] left, ulong[] right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (left == null || right == null || left.Length != right.Length) return false;
                for (var word = 0; word < left.Length; word++)
                    if (left[word] != right[word]) return false;
                return true;
            }

            public int GetHashCode(ulong[] bits)
            {
                unchecked
                {
                    var hash = 17;
                    foreach (var word in bits) hash = hash * 31 + word.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
