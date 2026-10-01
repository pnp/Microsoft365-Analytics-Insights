namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Which phases of one Graph user import cycle completed successfully.
    /// </summary>
    /// <remarks>
    /// A phase is "succeeded" only once it has finished without throwing. A phase that did not run
    /// at all - the licence refresh when the tenant does not grant <c>Organization.Read.All</c>, so
    /// licences are handled per user inside the update phase instead - also counts as succeeded,
    /// because there is no outstanding work it could have skipped.
    /// </remarks>
    public sealed class UserImportPhaseResults
    {
        /// <summary>New users were inserted and enriched with metadata.</summary>
        public bool InsertPhaseSucceeded { get; set; }

        /// <summary>Existing users had their metadata updated (bulk-SQL or per-entity path).</summary>
        public bool UpdatePhaseSucceeded { get; set; }

        /// <summary>
        /// The tenant-wide licence refresh finished, or no licence refresh was required this cycle.
        /// </summary>
        public bool LicenceRefreshSucceeded { get; set; }

        /// <summary>
        /// The user-organisation values were applied, or there was no organisation work to do.
        /// </summary>
        /// <remarks>
        /// A phase like the others, and for the same reason. Configuring an organisation attribute
        /// deliberately discards the delta token so the next cycle re-reads every user - that full
        /// enumeration is the only thing that populates the new attribute for people who do not
        /// otherwise change. If the organisation merge then fails and the token is committed anyway,
        /// that expensive snapshot is thrown away and every later cycle is a small delta, so those
        /// users never receive a value until somebody changes the configuration again. "It will be
        /// retried next cycle" is simply not true under delta semantics.
        ///
        /// Note this does not make the user import fail: the org step still swallows its own
        /// exceptions, so metadata, managers and licences are all saved. It only withholds the token,
        /// which costs one repeated enumeration and buys correctness.
        /// </remarks>
        public bool UserOrgsSucceeded { get; set; } = true;
    }

    /// <summary>
    /// The ordering guarantee that matters most in the Graph user import: the delta token is only
    /// persisted after the insert, update and licence phases have <b>all</b> succeeded.
    /// </summary>
    /// <remarks>
    /// Extracted for issues #372 / #381. Graph's <c>/users/delta</c> token is a promise that the
    /// caller has dealt with everything up to that point; committing it after a partial cycle makes
    /// Graph stop reporting those users, so the work is not retried and the missing data is
    /// invisible until somebody notices absent users.
    ///
    /// The end-to-end behaviour is already covered by the database-backed
    /// <c>UserMetadataUpdater_SuccessfulImport_DeltaTokenCommitted</c> and
    /// <c>UserMetadataUpdater_LicenseProcessingThrows_DeltaTokenNotAdvanced</c>. What had no test
    /// was the <i>rule</i>, because there was no rule to test: the guarantee was a side effect of an
    /// exception propagating out of the orchestration method. The update phase already sits inside a
    /// <c>try</c>/<c>catch</c> that logs and rethrows, and deleting that one <c>throw</c> would have
    /// been enough to start committing a delta token for a failed cycle.
    ///
    /// Making it an explicit, checked decision means the safe answer survives that edit: a phase
    /// whose failure is swallowed leaves its flag false, and the token is not committed.
    /// </remarks>
    public static class UserImportCommitPolicy
    {
        /// <summary>
        /// True only when every phase of the cycle succeeded.
        /// </summary>
        public static bool ShouldCommitDelta(UserImportPhaseResults results)
        {
            return results.InsertPhaseSucceeded
                && results.UpdatePhaseSucceeded
                && results.LicenceRefreshSucceeded
                && results.UserOrgsSucceeded;
        }
    }
}
