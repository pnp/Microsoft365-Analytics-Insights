using Common.Entities.UserOrgs;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;
using Web.AnalyticsWeb.Models.UserFilters;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// Gets a queued CSV import onto the thread pool and keeps it there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Running off the request is load-bearing, not a style choice. This work outlives the request that
    /// queued it: the upload returns as soon as the import is queued, and the page then polls for
    /// progress. But the dispatch happens ON a request thread, where ASP.NET has installed a
    /// request-bound <c>SynchronizationContext</c>. Any await inside the import that does not say
    /// <c>ConfigureAwait(false)</c> would capture it and post its continuation back - and once the
    /// request has ended that context never pumps again, so the continuation simply never runs. The
    /// import would stop mid-flight with no exception, no timeout and nothing logged, leaving the job
    /// stuck on "Running" forever.
    /// </para>
    /// <para>
    /// That is exactly what happened to the Copilot Adoption page in issue #441. Both routes below start
    /// the run on a thread-pool thread with no ambient synchronisation context, which fixes it for every
    /// await in the call chain - including ones not yet written.
    /// </para>
    /// <para>
    /// Inside the web app the run is registered with <see cref="HostingEnvironment.QueueBackgroundWorkItem(Func{CancellationToken, Task})"/>,
    /// so a recycle waits for a short import to finish rather than cutting it off. A longer one that
    /// the recycle does interrupt has changed nothing - the apply commits its changes and its success
    /// together - and is resumed after the restart.
    /// </para>
    /// <para>
    /// <see cref="CancellationToken.None"/> is equally deliberate, even under
    /// <c>QueueBackgroundWorkItem</c>, which offers a shutdown token. A cancelled apply is recorded as a
    /// failed import, which would stop it being resumed; left alone, it either finishes or dies with the
    /// process, and is resumed.
    /// </para>
    /// </remarks>
    public static class UserOrgImportDispatcher
    {
        /// <summary>Starts a job in the background and returns immediately.</summary>
        /// <param name="jobId">The queued job.</param>
        /// <param name="jobStore">The store the runner will use. Created by the caller, not captured from a request scope.</param>
        /// <param name="reportFailure">Best-effort telemetry for a failure nobody is awaiting.</param>
        /// <param name="telemetry">Where the run's lifecycle events go.</param>
        /// <param name="onSucceeded">
        /// Called after a successful import. Defaults to clearing this process's cached user directory,
        /// so the reports' user filter sees the new organisations straight away - even when nobody is
        /// polling the job.
        /// </param>
        public static void Start(
            int jobId,
            IUserOrgImportJobStore jobStore,
            Action<Exception, string> reportFailure = null,
            IUserOrgImportTelemetry telemetry = null,
            Action onSucceeded = null)
        {
            if (jobStore == null)
            {
                throw new ArgumentNullException(nameof(jobStore));
            }

            var report = reportFailure ?? WebExceptionTelemetry.Report;
            var events = telemetry ?? UserOrgImportAppInsights.Default;
            var succeeded = onSucceeded ?? (() => CachedUserDirectorySource.Default.Invalidate());

            Func<Task> run = async () =>
            {
                try
                {
                    var job = await new UserOrgImportRunner(jobStore, events)
                        .RunAsync(jobId, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (job != null && job.Status == UserOrgImportStatus.Succeeded)
                    {
                        succeeded();
                    }
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
            };

            events.Record(new UserOrgImportTelemetryEvent { Stage = UserOrgImportStages.Dispatched, JobId = jobId });

            if (HostingEnvironment.IsHosted)
            {
                // Silently not scheduled once shutdown has begun. The job then waits Pending, and the
                // resume sweep after the restart picks it up.
                HostingEnvironment.QueueBackgroundWorkItem(_ => run());
            }
            else
            {
                Task.Run(run);
            }
        }
    }
}
