using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Runs one queued CSV import to completion: claims it, applies it, and records the outcome.
    /// </summary>
    /// <remarks>
    /// Deliberately free of any web dependency - it takes a port and a clock's worth of timing, so the
    /// whole lifecycle including the failure paths can be exercised without ASP.NET. The web app is
    /// responsible only for getting this onto the thread pool; see the note on
    /// <see cref="RunAsync"/> about why that matters.
    /// </remarks>
    public sealed class UserOrgImportRunner
    {
        /// <summary>
        /// How often the worker reports that it is still alive while the merge runs.
        /// </summary>
        /// <remarks>
        /// Frequent enough that a stalled or killed worker is obvious within a minute, infrequent
        /// enough to be irrelevant next to the merge itself.
        /// </remarks>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long a <see cref="UserOrgImportStatus.Running"/> job may go without a heartbeat before
        /// the portal reports it as interrupted.
        /// </summary>
        /// <remarks>
        /// Four missed heartbeats. An App Service recycle mid-import leaves the row saying "Running"
        /// forever otherwise, which is indistinguishable from a very slow import - and an admin staring
        /// at a spinner has no way to tell which they are looking at.
        /// </remarks>
        public static readonly TimeSpan StaleHeartbeatThreshold = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a job may sit <see cref="UserOrgImportStatus.Pending"/> before it is treated as
        /// abandoned.
        /// </summary>
        /// <remarks>
        /// Generous, because a busy web instance can legitimately take a little while to pick a job up,
        /// and declaring a live job abandoned would let a second import start alongside it. Five minutes
        /// is far longer than any healthy dispatch and far shorter than "blocked forever", which is what
        /// the alternative amounts to.
        /// </remarks>
        public static readonly TimeSpan StalePendingThreshold = TimeSpan.FromMinutes(5);

        /// <summary>
        /// What an administrator is told when an import fails.
        /// </summary>
        /// <remarks>
        /// Deliberately fixed and detail-free. It is rendered verbatim in the portal, and a raw SQL or
        /// Graph exception message can carry object names, index names, duplicate key values and
        /// identities. The real exception goes to Application Insights, which is where it belongs.
        /// </remarks>
        internal const string FailureMessage =
            "The import could not be completed. No partial changes were kept. Check the service logs for details, "
            + "then try again.";

        private readonly IUserOrgImportJobStore _jobs;

        public UserOrgImportRunner(IUserOrgImportJobStore jobs)
        {
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        }

        /// <summary>
        /// Claims and applies a job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The caller must start this with <c>Task.Run</c> from a web request, never by awaiting it
        /// inline. ASP.NET installs a request-bound <c>SynchronizationContext</c>, and any await in
        /// this call chain that does not say <c>ConfigureAwait(false)</c> would capture it and post its
        /// continuation back - to a context that never pumps again once the request has ended. The
        /// import would then stop mid-flight with no exception and no timeout, leaving the job stuck on
        /// "Running" forever. This is the same failure that took the Copilot Adoption page down in
        /// issue #441.
        /// </para>
        /// <para>
        /// The cancellation token must not be the request's. This run outlives the request that started
        /// it, and it writes to the database - cancelling it half way through a Replace would leave an
        /// org type emptied but not repopulated.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The finished job, or the job as-is when another instance had already claimed it.
        /// </returns>
        public async Task<UserOrgImportJob> RunAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var claimed = await _jobs.TryClaimJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                // Somebody else got there first, or the job was already finished or cancelled. Either
                // way this instance must not import the same file a second time.
                return await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            }

            using (var heartbeatStop = new CancellationTokenSource())
            {
                var heartbeat = HeartbeatUntilStopped(jobId, heartbeatStop.Token);

                try
                {
                    var applied = await _jobs.ApplyAsync(jobId, cancellationToken).ConfigureAwait(false);
                    heartbeatStop.Cancel();
                    await SwallowAsync(heartbeat).ConfigureAwait(false);

                    await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Succeeded, null, cancellationToken)
                        .ConfigureAwait(false);

                    if (applied != null)
                    {
                        applied.Status = UserOrgImportStatus.Succeeded;
                    }

                    return applied ?? await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    heartbeatStop.Cancel();
                    await SwallowAsync(heartbeat).ConfigureAwait(false);

                    // Recorded on the job rather than only thrown, because the request that queued this
                    // has long since returned and nothing else is awaiting the task. Without this the
                    // admin would see a job that stopped at "Running" and never learn why.
                    //
                    // A fixed message, not ex.Message. This text is rendered verbatim in the portal, and
                    // a SQL or Graph exception can carry object names, index names, duplicate key values
                    // and identities. The full exception still reaches Application Insights through the
                    // caller, which is where an engineer should be reading it.
                    try
                    {
                        await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Failed, FailureMessage, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Recording the failure is best-effort and must never replace the original fault.
                    }

                    throw;
                }
            }
        }

        private async Task HeartbeatUntilStopped(int jobId, CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, stop).ConfigureAwait(false);
                    if (stop.IsCancellationRequested)
                    {
                        return;
                    }

                    await _jobs.HeartbeatAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the import finished.
            }
            catch (Exception)
            {
                // A heartbeat is a progress signal, not the work. Losing it must never fail the import.
            }
        }

        private static async Task SwallowAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already handled inside the loop; this is only here so the task is observed.
            }
        }

        /// <summary>
        /// Whether a job looks abandoned: either still <see cref="UserOrgImportStatus.Running"/> with a
        /// heartbeat that has stopped moving, or still <see cref="UserOrgImportStatus.Pending"/> long
        /// after it was queued.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both states matter, and the pending one is the easier to overlook. The rows are staged and
        /// the job row committed <b>before</b> the background worker is dispatched, so an App Service
        /// recycle in that window leaves a job nobody will ever claim. Because a pending job also
        /// counts as active, it would then block every later upload for that organisation type -
        /// permanently, with the portal polling a job that can never move.
        /// </para>
        /// <para>
        /// Reported rather than acted on automatically: an admin re-uploading is a safer resolution
        /// than this code guessing how far a partially-applied Replace got.
        /// </para>
        /// </remarks>
        public static bool LooksInterrupted(UserOrgImportJob job, DateTime utcNow)
        {
            if (job == null)
            {
                return false;
            }

            if (job.Status == UserOrgImportStatus.Pending)
            {
                return utcNow - job.QueuedUtc > StalePendingThreshold;
            }

            if (job.Status != UserOrgImportStatus.Running)
            {
                return false;
            }

            var lastSeen = job.HeartbeatUtc ?? job.StartedUtc;
            if (!lastSeen.HasValue)
            {
                return false;
            }

            return utcNow - lastSeen.Value > StaleHeartbeatThreshold;
        }
    }
}
