using Common.Entities.UserScope;
using System;
using System.Linq;

namespace Common.Entities.UserDiagnostics
{
    /// <summary>
    /// Whether one person is in the import scope, for the portal's live directory reads and the importer's one-person
    /// refresh alike. Fail-closed: only a complete resolution can say "in scope" or "excluded", and a partly-read member
    /// list proves neither - even though the scheduled import fails open on the same answer.
    /// </summary>
    public static class UserScopeDecider
    {
        public static UserScopeDecisionModel Decide(UserImportScopeResolution resolution, params string[] identifiers)
        {
            if (resolution == null || resolution.Status == UserImportScopeStatus.Unavailable)
            {
                return new UserScopeDecisionModel
                {
                    Decision = UserScopeDecisions.Unknown,
                    LiveComparisonAllowed = false,
                    ResolvedUtc = resolution == null ? (DateTime?)null : DateTime.SpecifyKind(resolution.ResolvedUtc, DateTimeKind.Utc),
                    FailureKind = FailureKindKey(resolution?.FailureKind ?? UserImportScopeFailureKind.Unexpected),
                };
            }

            if (resolution.Status == UserImportScopeStatus.Unfiltered)
            {
                return new UserScopeDecisionModel { Decision = UserScopeDecisions.Unfiltered, LiveComparisonAllowed = true };
            }

            var scope = UserImportScope.ForMembers(resolution.Members, "diagnostics");
            var inScope = scope.IsAnyInScope((identifiers ?? new string[0]).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray());
            return new UserScopeDecisionModel
            {
                Decision = inScope ? UserScopeDecisions.InScope : UserScopeDecisions.Excluded,
                LiveComparisonAllowed = inScope,
                ResolvedUtc = DateTime.SpecifyKind(resolution.ResolvedUtc, DateTimeKind.Utc),
                MatchedGroupCount = resolution.MatchedGroupCount,
                MemberCount = resolution.Members?.Count,
            };
        }

        /// <summary>The same keys the portal's User scope page uses for a failed resolution.</summary>
        public static string FailureKindKey(UserImportScopeFailureKind kind)
        {
            switch (kind)
            {
                case UserImportScopeFailureKind.DirectoryRead: return "directoryRead";
                case UserImportScopeFailureKind.BudgetExhausted: return "budgetExhausted";
                case UserImportScopeFailureKind.ClientUnavailable: return "clientUnavailable";
                default: return "unexpected";
            }
        }
    }
}
