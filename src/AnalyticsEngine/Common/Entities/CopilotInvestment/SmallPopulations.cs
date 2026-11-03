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
    }
}
