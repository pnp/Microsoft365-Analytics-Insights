namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// Withholding figures that describe too few people from a reader without See PII, shared by the Copilot Spend and Copilot
    /// Investment reports. The callers pass the portal's floor (<c>ReportScopeResolver.MinimumPeopleWithoutSeePii</c>, 5).
    /// </summary>
    internal static class SmallPopulations
    {
        /// <summary>A population a figure would single out: some people, but fewer than the minimum. Nobody is not small.</summary>
        public static bool IsSmall(int people, int minPeople) => people > 0 && people < minPeople;
    }
}
