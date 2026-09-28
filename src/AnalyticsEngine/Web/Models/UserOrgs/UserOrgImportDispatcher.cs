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
        /// <param name="changeLogShipper">
        /// Writes the import's change list to the change log once it has applied, or <c>null</c> to leave
        /// that to the resume sweep.
        /// </param>
        public static void Start(
            int jobId,
            IUserOrgImportJobStore jobStore,
            Action<Exception, string> reportFailure = null,
            IUserOrgImportTelemetry telemetry = null,
            Action onSucceeded = null,
            Func<UserOrgChangeLogShipper> changeLogShipper = null)
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
                UserOrgImportJob job;
                try
                {
                    job = await new UserOrgImportRunner(jobStore, events)
                        .RunAsync(jobId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The request that started this has long since returned, so nothing observes the
                    // task. The runner has already recorded the failure on the job row for the admin;
                    // this is the copy an engineer sees. Both are needed - one is the user-facing
                    // answer, the other is the diagnosable one.
                    Report(report, ex, $"User organisation CSV import (job {jobId})");
                    return;
                }

                if (job == null || job.Status != UserOrgImportStatus.Succeeded)
                {
                    return;
                }

                // The directory first: the organisations have changed whether or not the log is written.
                try
                {
                    succeeded();
                }
                catch (Exception ex)
                {
                    Report(report, ex, $"User organisation CSV import (job {jobId}): refreshing the user directory");
                }

                if (changeLogShipper != null)
                {
                    await ShipQuietlyAsync(changeLogShipper, jobId, report).ConfigureAwait(false);
                }
            };

            events.Record(new UserOrgImportTelemetryEvent { Stage = UserOrgImportStages.Dispatched, JobId = jobId });
            Queue(run);
        }

        /// <summary>Writes one applied import's change log in the background, for the resume sweep.</summary>
        public static void StartChangeLog(int jobId, Func<UserOrgChangeLogShipper> changeLogShipper, Action<Exception, string> reportFailure = null)
        {
            if (changeLogShipper == null)
            {
                throw new ArgumentNullException(nameof(changeLogShipper));
            }

            var report = reportFailure ?? WebExceptionTelemetry.Report;
            Queue(() => ShipQuietlyAsync(changeLogShipper, jobId, report));
        }

        private static async Task ShipQuietlyAsync(Func<UserOrgChangeLogShipper> changeLogShipper, int jobId, Action<Exception, string> report)
        {
            try
            {
                await changeLogShipper().ShipAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Not a failed import: the changes are applied, and the list waits in the outbox for the
                // resume sweep to write it again. Reported so a store that keeps refusing is visible.
                Report(report, ex, $"User organisation change log (job {jobId})");
            }
        }

        private static void Queue(Func<Task> run)
        {
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

        private static void Report(Action<Exception, string> report, Exception ex, string context)
        {
            try
            {
                report(ex, context);
            }
            catch (Exception)
            {
                // Failure telemetry is best-effort and must never replace the original fault.
            }
        }
    }
}
