using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Source-agnostic loader for one user's Copilot interaction history, so the importer can be tested
    /// against canned payloads with no HTTP and no tenant.
    /// </summary>
    public interface IAiInteractionSourceLoader
    {
        /// <summary>
        /// Whether the runtime identity actually holds <c>AiEnterpriseInteraction.Read.All</c>.
        /// </summary>
        /// <remarks>
        /// Worth its own pre-flight check rather than letting per-user calls fail: this permission is not
        /// granted by the installer and needs separate admin consent, so "not consented yet" is the single
        /// most likely reason for this import to do nothing. Without the check the symptom would be a 403 per
        /// user - up to one per user in the pilot group, every cycle - instead of one clear warning.
        /// </remarks>
        Task<bool> HasInteractionReadAccessAsync();

        /// <summary>
        /// Load interactions for one user created strictly between <paramref name="fromUtc"/> and
        /// <paramref name="toUtc"/>.
        /// </summary>
        /// <remarks>
        /// Both bounds are required because Graph only supports <c>$filter</c> on <c>createdDateTime</c> as a
        /// range - a single-sided filter is rejected.
        /// </remarks>
        Task<AiInteractionLoadResult> LoadInteractionsForUserAsync(Common.Entities.User user, DateTime fromUtc, DateTime toUtc);
    }

    /// <summary>
    /// What could be established about the runtime identity's <c>AiEnterpriseInteraction.Read.All</c> grant.
    /// </summary>
    /// <remarks>
    /// The importer only needs a yes/no ("should I run?"), which
    /// <see cref="IAiInteractionSourceLoader.HasInteractionReadAccessAsync"/> gives it. The installer's Test
    /// Configuration needs the distinction: telling an admin a permission is definitely missing when the
    /// check merely could not run sends them to re-consent something they may already hold (issue #329).
    /// </remarks>
    public enum InteractionReadAccess
    {
        /// <summary>The token carries <c>AiEnterpriseInteraction.Read.All</c>. The import will run.</summary>
        Granted,

        /// <summary>The token was read successfully and does not carry the permission.</summary>
        NotGranted,

        /// <summary>
        /// The token could not be acquired or its permissions could not be read, so neither answer is proven.
        /// </summary>
        Unknown,

        /// <summary>
        /// There was no app identity to inspect at all. The importer treats this as "carry on and let the
        /// per-user calls report the truth"; a verifier must not treat it as a pass.
        /// </summary>
        NoIdentityToInspect,
    }

    /// <summary>Outcome of loading one user's interactions.</summary>
    public class AiInteractionLoadResult
    {
        public static AiInteractionLoadResult Empty() => new AiInteractionLoadResult();

        /// <summary>
        /// Interactions returned. Transient - holds real prompt/response text, so must be projected via
        /// <see cref="InteractionStatsExtractor"/> and dropped, never stored or logged.
        /// </summary>
        public IReadOnlyList<AiInteraction> Interactions { get; set; } = Array.Empty<AiInteraction>();

        /// <summary>
        /// True when Graph said this user has nothing to give us - no Copilot licence, no mailbox, or the
        /// user no longer exists. Terminal for this user, so the caller applies a back-off rather than
        /// counting it as a failure.
        /// </summary>
        public bool UserNotAvailable { get; set; }

        /// <summary>
        /// True when paging stopped at the safety cap rather than at the end of the data. The caller must
        /// then advance the watermark only as far as the interactions it actually received, so the rest is
        /// picked up next cycle instead of being skipped.
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// Set when the call failed for a reason that may resolve on a later run.
        /// </summary>
        /// <remarks>
        /// Deliberately a sanitised summary (status code, Graph error code, page number) rather than the
        /// underlying exception message or response body. This value is persisted to
        /// <c>copilot_interaction_user_watermarks.last_error</c> and logged, and the raw payload for this
        /// endpoint contains the user's prompts and Copilot's answers.
        /// </remarks>
        public string Error { get; set; }

        public bool Failed => !string.IsNullOrEmpty(Error);

        /// <summary>True only when the full window was read successfully and completely.</summary>
        public bool IsCompleteSuccess => !Failed && !UserNotAvailable && !Truncated;
    }
}
