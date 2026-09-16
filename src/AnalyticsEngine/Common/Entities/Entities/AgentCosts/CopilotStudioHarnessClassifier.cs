using System;

namespace Common.Entities.Entities.AgentCosts
{
    /// <summary>
    /// Maps a Copilot Studio billing feature name onto a harness.
    ///
    /// Pure and dependency-free so the mapping can be unit tested without an HTTP call or a database, and so
    /// the one genuinely uncertain part of the Copilot Studio credit import is isolated in a single place.
    /// </summary>
    /// <remarks>
    /// <para>The licensing API does <b>not</b> report a harness. The Power Platform admin centre shows spend
    /// broken down by "Copilot Studio - Standard/Copilot Chat harnesses" and "Copilot Studio - GitHub Copilot
    /// harness", but those are portal display names: every harness bills through the same <c>MCSMessages</c>
    /// entitlement, and the only per-row signal is the feature name.</para>
    ///
    /// <para>The "Process Agent" =&gt; GitHub Copilot harness mapping is community-observed rather than
    /// documented by Microsoft, which is why it is an exact match on one known string. Anything unrecognised
    /// becomes <see cref="CopilotStudioHarness.Unknown"/> so a future Microsoft change shows up as visibly
    /// unclassified spend rather than as spend quietly attributed to the wrong harness.</para>
    ///
    /// <para><b>Microsoft 365 Copilot Cowork is deliberately absent from this mapping, and must stay
    /// absent.</b> Cowork meters against the shared <i>Copilot Credits</i> pool - the same currency as
    /// Copilot Studio and AI Builder - and Microsoft publishes no Cowork-specific meter, feature name or
    /// workload discriminator. The licensing API reports an aggregate "AI" capacity category only. So
    /// Cowork consumption genuinely <i>is</i> inside the tenant capacity figures, but it cannot be
    /// separated out of them, and no per-row signal identifies it.</para>
    ///
    /// <para>Inventing a Cowork feature name here to make a "Cowork spend" column possible would be
    /// precisely the silent mis-attribution the paragraph above exists to prevent - and it would land on a
    /// page used to justify spend. Cowork <i>usage</i> is reported from the audit log
    /// (<c>CopilotAdoptionSql.CoworkPredicate</c>) instead, which is real evidence; Cowork <i>cost</i> is
    /// reported only at the tenant-pool level, where it is honest.</para>
    /// </remarks>
    public static class CopilotStudioHarnessClassifier
    {
        /// <summary>
        /// The feature name observed on agents running the GitHub Copilot harness. Public so a test can
        /// reference it without restating the literal.
        /// </summary>
        public const string GitHubCopilotHarnessFeatureName = "Process Agent";

        /// <summary>
        /// Feature names known to be produced by the Standard and Copilot Chat harnesses. Both harnesses emit
        /// the same feature names, so this can only ever narrow a row to "one of those two" - which is why
        /// the result is <see cref="CopilotStudioHarness.StandardOrCopilotChat"/> rather than a single named
        /// harness.
        /// </summary>
        /// <remarks>
        /// Taken from Microsoft's published Copilot Studio billing rates. Matching is case-insensitive and
        /// ignores surrounding whitespace, but is otherwise exact: a partial match would let a future feature
        /// name be absorbed into an existing bucket without anyone noticing.
        /// </remarks>
        private static readonly string[] StandardOrCopilotChatFeatureNames =
        {
            "Classic answer",
            "Generative answer",
            "Agent action",
            "Tenant graph grounding",
            "Agent flow actions",
            "Text and generative AI tools (basic)",
            "Text and generative AI tools (standard)",
            "Text and generative AI tools (premium)",
        };

        /// <summary>
        /// Classifies a billing feature name into one of the <see cref="CopilotStudioHarness"/> constants.
        /// Never throws and never returns null.
        /// </summary>
        public static string Classify(string featureName)
        {
            if (string.IsNullOrWhiteSpace(featureName))
            {
                return CopilotStudioHarness.NotAssessed;
            }

            var trimmed = featureName.Trim();

            if (trimmed.Equals(GitHubCopilotHarnessFeatureName, StringComparison.OrdinalIgnoreCase))
            {
                return CopilotStudioHarness.GitHubCopilot;
            }

            foreach (var known in StandardOrCopilotChatFeatureNames)
            {
                if (trimmed.Equals(known, StringComparison.OrdinalIgnoreCase))
                {
                    return CopilotStudioHarness.StandardOrCopilotChat;
                }
            }

            return CopilotStudioHarness.Unknown;
        }
    }
}
