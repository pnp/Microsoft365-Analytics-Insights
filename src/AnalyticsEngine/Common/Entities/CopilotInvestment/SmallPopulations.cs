using System.Collections.Generic;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// Withholding figures that describe too few people from a reader without See PII, shared by the Copilot Spend and Copilot
    /// Investment reports. The callers pass the portal's floor (<c>ReportScopeResolver.MinimumPeopleWithoutSeePii</c>, 5).
    /// </summary>
    internal static class SmallPopulations
    {
        /// <summary>The portal's floor for a reader without See PII, as <c>ReportScopeResolver.MinimumPeopleWithoutSeePii</c>.</summary>
        public const int MinPeopleWithoutSeePii = 5;

        /// <summary>A population a figure would single out: some people, but fewer than the minimum. Nobody is not small.</summary>
        public static bool IsSmall(int people, int minPeople) => people > 0 && people < minPeople;

        /// <summary>
        /// Complementary withholding over rows that split the people between them - each person in exactly one row - and whose
        /// figures add up to a total the reader is given elsewhere. Once a row is withheld, that total less the rows shown is
        /// the withheld rows together, so when they hold fewer than <paramref name="minPeople"/> people of any population
        /// behind their figures, the difference would single those people out. More rows are withheld, each time the shown row
        /// with the fewest people of the population that falls short (the last of them on a tie, the lists being largest first),
        /// until every population behind the withheld rows is nobody or at least the minimum, or no row with any of that
        /// population is left: the withheld rows then hold all of it, and the total is the population's own figure, which is
        /// withheld on its own when it is that small.
        /// </summary>
        /// <param name="populations">For each row, the size of each population behind its figures, always in the same order; a population a row does not list is nobody.</param>
        /// <param name="withheld">For each row, whether it is withheld; rows withheld to protect another are set here.</param>
        public static void WithholdComplements(IReadOnlyList<int[]> populations, bool[] withheld, int minPeople)
        {
            int Size(int row, int kind) => populations[row] != null && kind < populations[row].Length ? populations[row][kind] : 0;
            var kinds = 0;
            foreach (var row in populations) kinds = System.Math.Max(kinds, row?.Length ?? 0);
            bool added;
            do
            {
                added = false;
                for (var kind = 0; kind < kinds; kind++)
                {
                    var behind = 0;
                    for (var i = 0; i < populations.Count; i++)
                        if (withheld[i]) behind += Size(i, kind);
                    if (!IsSmall(behind, minPeople)) continue;

                    var next = -1;
                    for (var i = 0; i < populations.Count; i++)
                        if (!withheld[i] && Size(i, kind) > 0 && (next < 0 || Size(i, kind) <= Size(next, kind))) next = i;
                    if (next < 0) continue;
                    withheld[next] = true;
                    added = true;
                }
            } while (added);
        }

        /// <summary>
        /// Complementary withholding over rows whose people can overlap - days, say, where one person can be behind several -
        /// so adding the withheld rows' counts up proves nothing about how many different people they hold together. Once a
        /// row with someone behind it is withheld for having too few people, the shown row with the fewest people that on its
        /// own has at least <paramref name="minPeople"/> is withheld too (the last of them on a tie): whatever a total less the
        /// rows shown gives, it then holds at least that many different people. When no such row exists, every row with anyone
        /// behind it is small and withheld, and that difference is the population's whole total, which is withheld or not on its own.
        /// </summary>
        /// <param name="people">For each row, the different people behind its figures.</param>
        /// <param name="withheld">For each row, whether it is withheld; a row withheld to protect another is set here.</param>
        public static void WithholdOverlappingComplement(IReadOnlyList<int> people, bool[] withheld, int minPeople)
        {
            var exposed = false;
            for (var i = 0; i < people.Count; i++)
            {
                if (!withheld[i] || people[i] <= 0) continue;
                if (people[i] >= minPeople) return;
                exposed = true;
            }
            if (!exposed) return;

            var next = -1;
            for (var i = 0; i < people.Count; i++)
                if (!withheld[i] && people[i] >= minPeople && (next < 0 || people[i] <= people[next])) next = i;
            if (next >= 0) withheld[next] = true;
        }

        /// <summary>
        /// For rows that add up to a total the reader is not given: when every row with someone behind it is shown, they add
        /// up to that total all the same. So unless a row with someone behind it is withheld already, the shown row with the
        /// fewest people (more than none) is withheld too (the last of them on a tie).
        /// </summary>
        /// <param name="people">For each row, the people behind its figures that the withheld total adds up.</param>
        /// <param name="withheld">For each row, whether it is withheld; a row withheld to protect the total is set here.</param>
        public static void WithholdOneOf(IReadOnlyList<int> people, bool[] withheld)
        {
            var next = -1;
            for (var i = 0; i < people.Count; i++)
            {
                if (people[i] <= 0) continue;
                if (withheld[i]) return;
                if (next < 0 || people[i] <= people[next]) next = i;
            }
            if (next >= 0) withheld[next] = true;
        }

        /// <summary>
        /// Withholding over the parts of one total: populations that split the people behind it between them, each person in
        /// exactly one, where the reader is given several figures that each add up a different selection of the parts - the
        /// total, a part on its own, two parts together. Every part with too few people behind it is withheld, then more parts,
        /// the fewest people first, until the withheld parts together hold nobody or at least the minimum (as
        /// <see cref="WithholdComplements"/>). A figure is then shown only when it adds up none of the withheld parts, or every
        /// one of them while they hold enough people together: whatever difference of the figures shown a reader works out,
        /// the withheld parts come back only all together, never fewer of them.
        /// </summary>
        internal sealed class Parts
        {
            private readonly int[] _people;
            private readonly bool[] _withheld;
            private readonly int _minPeople;

            /// <param name="people">The people behind each part's figures: who its amount describes.</param>
            /// <param name="minPeople">The fewest people a figure may describe.</param>
            /// <param name="keep">
            /// A part never withheld only to protect another, or -1 for none. When it is the only part left that could be, no
            /// more are: the withheld parts then hold too few people together, and every figure adding up any of them - the
            /// total included - is withheld instead.
            /// </param>
            public Parts(IReadOnlyList<int> people, int minPeople, int keep = -1)
            {
                _people = new int[people.Count];
                for (var i = 0; i < people.Count; i++) _people[i] = System.Math.Max(0, people[i]);
                _minPeople = minPeople;
                _withheld = new bool[_people.Length];
                for (var i = 0; i < _people.Length; i++) _withheld[i] = IsSmall(_people[i], minPeople);
                while (IsSmall(Behind, minPeople))
                {
                    var next = -1;
                    for (var i = 0; i < _people.Length; i++)
                        if (!_withheld[i] && i != keep && _people[i] > 0 && (next < 0 || _people[i] <= _people[next])) next = i;
                    if (next < 0) break;
                    _withheld[next] = true;
                }
            }

            private int Behind
            {
                get
                {
                    var behind = 0;
                    for (var i = 0; i < _people.Length; i++)
                        if (_withheld[i]) behind += _people[i];
                    return behind;
                }
            }

            /// <summary>Whether a figure adding up exactly these parts may be given to the reader.</summary>
            public bool Shows(params int[] parts)
            {
                var people = 0;
                var withheldIn = 0;
                foreach (var part in parts)
                {
                    people += _people[part];
                    if (_withheld[part]) withheldIn++;
                }
                if (IsSmall(people, _minPeople)) return false;
                if (withheldIn == 0) return true;
                var withheld = 0;
                foreach (var w in _withheld)
                    if (w) withheld++;
                return withheldIn == withheld && !IsSmall(Behind, _minPeople);
            }

            /// <summary>Whether the total of every part may be given to the reader.</summary>
            public bool ShowsTotal
            {
                get
                {
                    var all = new int[_people.Length];
                    for (var i = 0; i < all.Length; i++) all[i] = i;
                    return Shows(all);
                }
            }
        }
    }
}
