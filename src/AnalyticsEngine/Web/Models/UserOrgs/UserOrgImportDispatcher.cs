using Common.Entities.UserOrgs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// Gets a queued CSV import onto the thread pool and keeps it there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Task.Run</c> is load-bearing, not a style choice. This work outlives the request that queued
    /// it: the upload returns as soon as the rows are staged, and the page then polls for progress. But
    /// the dispatch happens ON a request thread, where ASP.NET has installed a request-bound
    /// <c>SynchronizationContext</c>. Any await inside the import that does not say
    /// <c>ConfigureAwait(false)</c> would capture it and post its continuation back - and once the
    /// request has ended that context never pumps again, so the continuation simply never runs. The
    /// import would stop mid-flight with no exception, no timeout and nothing logged, leaving the job
    /// stuck on "Running" forever.
    /// </para>
    /// <para>
    /// That is exactly what happened to the Copilot Adoption page in issue #441. Starting on the thread
    /// pool gives the run no ambient synchronisation context at all, which fixes it for every await in
    /// the call chain - including ones not yet written - rather than relying on a long list of
    /// <c>ConfigureAwait(false)</c> calls staying correct forever.
    /// </para>
    /// <para>
    /// <see cref="CancellationToken.None"/> is equally deliberate. Passing the request's token would
    /// let the upload response completing cancel the import that response just started, half way
    /// through a Replace - leaving an org type emptied but not repopulated.
    /// </para>
    /// </remarks>
    public static class UserOrgImportDispatcher
    {
        /// <summary>Starts a job in the background and returns immediately.</summary>
        /// <param name="jobId">The queued job.</param>
        /// <param name="jobStore">The store the runner will use. Created by the caller, not captured from a request scope.</param>
        /// <param name="reportFailure">Best-effort telemetry for a failure nobody is awaiting.</param>
        public static void Start(int jobId, IUserOrgImportJobStore jobStore, Action<Exception, string> reportFailure = null)
        {
            if (jobStore == null)
            {
                throw new ArgumentNullException(nameof(jobStore));
            }

            var report = reportFailure ?? WebExceptionTelemetry.Report;

            Task.Run(async () =>
            {
                try
                {
                    await new UserOrgImportRunner(jobStore).RunAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The request that started this has long since returned, so nothing observes the
                    // task. The runner has already recorded the failure on the job row for the admin;
                    // this is the copy an engineer sees. Both are needed - one is the user-facing
                    // answer, the other is the diagnosable one.
                    try
                    {
                        report(ex, $"User organisation CSV import (job {jobId})");
                    }
                    catch (Exception)
                    {
                        // Failure telemetry is best-effort and must never replace the original fault.
                    }
                }
            });
        }
    }
}
