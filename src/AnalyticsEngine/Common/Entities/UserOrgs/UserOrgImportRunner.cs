using System;
using System.Diagnostics;
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
        /// its worker is presumed gone.
        /// </summary>
        /// <remarks>
        /// Four missed heartbeats. An App Service recycle mid-import leaves the row saying "Running"
        /// forever otherwise, which is indistinguishable from a very slow import. Past this, the job is
        /// resumed by another worker, or taken over by a new upload.
        /// </remarks>
        public static readonly TimeSpan StaleHeartbeatThreshold = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a job may sit <see cref="UserOrgImportStatus.Pending"/> before a new upload for the
        /// same org type may take over from it.
        /// </summary>
        /// <remarks>
        /// Generous, because declaring a live job abandoned would let a second import start alongside
        /// it. A job whose dispatch was merely lost is re-dispatched long before this - after
        /// <see cref="UserOrgImportJobLimits.LostDispatchGrace"/> - so reaching it means resuming has
        /// failed too.
        /// </remarks>
        public static readonly TimeSpan StalePendingThreshold = TimeSpan.FromMinutes(5);

        /// <summary>
        /// What an administrator is told when an import fails.
        /// </summary>
        /// <remarks>
        /// Deliberately fixed and detail-free. It is rendered verbatim in the portal, and a raw SQL or
        /// Graph exception message can carry object names, index names, duplicate key values and
        /// identities. The real exception goes to Application Insights, which is where it belongs.
        /// "No partial changes were kept" is a promise the apply keeps: it records its own success in
        /// the transaction that makes the changes, so a job that did not succeed changed nothing.
        /// </remarks>
        internal const string FailureMessage =
            "The import could not be completed. No partial changes were kept. Check the service logs for details, "
            + "then try again.";

        /// <summary>
        /// What an administrator is told about a job that was overtaken by a later one.
        /// </summary>
        /// <remarks>
        /// A job stops being eligible once it has gone quiet for longer than
        /// <see cref="StaleHeartbeatThreshold"/> (or sat unclaimed past <see cref="StalePendingThreshold"/>),
        /// at which point a fresh upload for the same org type is allowed to take over. Saying so
        /// explicitly is the difference between an admin understanding that their second upload won and
        /// an admin looking at a job that simply stopped.
        /// </remarks>
        internal const string SupersededMessage =
            "This import was overtaken by a later one for the same organisation type after it stopped reporting "
            + "progress. The later import is the one that counts.";

        /// <summary>
        /// What an administrator is told about a job that was given up on after repeated interruptions.
        /// </summary>
        internal const string AbandonedMessage =
            "The web app restarted while this import was running, too many times or too long ago for it to be "
            + "resumed, so it was stopped. No changes were made. Upload the file again.";

        private readonly IUserOrgImportJobStore _jobs;
        private readonly IUserOrgImportTelemetry _telemetry;

        public UserOrgImportRunner(IUserOrgImportJobStore jobs, IUserOrgImportTelemetry telemetry = null)
        {
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _telemetry = telemetry ?? NullUserOrgImportTelemetry.Instance;
        }

        /// <summary>
        /// Claims and applies a job.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The caller must start this on the thread pool from a web request, never by awaiting it
        /// inline. ASP.NET installs a request-bound <c>SynchronizationContext</c>, and any await in
        /// this call chain that does not say <c>ConfigureAwait(false)</c> would capture it and post its
        /// continuation back - to a context that never pumps again once the request has ended. The
        /// import would then stop mid-flight with no exception and no timeout, leaving the job stuck on
        /// "Running" forever. This is the same failure that took the Copilot Adoption page down in
        /// issue #441.
        /// </para>
        /// <para>
        /// The cancellation token must not be the request's. This run outlives the request that started
        /// it, and a cancelled apply is recorded as a failed import rather than resumed.
        /// </para>
        /// <para>
        /// A refusal - the type changed, or the import would now clear more users than were confirmed -
        /// is recorded on the job and returned, not thrown: it is an answer for the admin, not a fault
        /// for an engineer. Only a genuine fault is rethrown.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The finished job, or the job as-is when another instance had already claimed it.
        /// </returns>
        public async Task<UserOrgImportJob> RunAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var watch = Stopwatch.StartNew();

            var claimed = await _jobs.TryClaimJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                // Somebody else got there first, or the job was already finished or cancelled. Either
                // way this instance must not import the same file a second time.
                Emit(UserOrgImportStages.NotClaimed, jobId, null);
                return await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            }

            var job = await TryGetJobAsync(jobId).ConfigureAwait(false);
            Emit(UserOrgImportStages.Claimed, jobId, job);

            using (var heartbeatStop = new CancellationTokenSource())
            {
                var heartbeat = HeartbeatUntilStopped(jobId, job, heartbeatStop.Token);
                UserOrgImportJob applied;

                try
                {
                    applied = await _jobs.ApplyAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
                catch (UserOrgJobSupersededException)
                {
                    // Not a failure: this job was correctly declined because a later import for the
                    // same org type replaced it, or because it had already been applied by a worker
                    // that was presumed gone but was merely slow. Nothing was changed.
                    heartbeatStop.Cancel();
                    await SwallowAsync(heartbeat).ConfigureAwait(false);

                    // Recorded anyway, because "declined" and "left Running forever" look identical
                    // to an admin otherwise - and its staged rows would sit in the database. Only a job
                    // still Pending or Running is rewritten, so this cannot overwrite the verdict of a
                    // replacement that already retired it, nor the success of a worker that applied it.
                    await RecordFailureAsync(jobId, SupersededMessage, UserOrgImportErrorCodes.Superseded).ConfigureAwait(false);

                    var after = await TryGetJobAsync(jobId).ConfigureAwait(false);
                    Emit(UserOrgImportStages.Superseded, jobId, after ?? job, watch);
                    return after ?? job;
                }
                catch (UserOrgValidationException ex)
                {
                    // A refusal the admin can act on, re-tested inside the apply because the web
                    // request's answer can be stale by the time the worker runs. Nothing was changed,
                    // and the message is written for an IT admin, so it is kept as-is.
                    heartbeatStop.Cancel();
                    await SwallowAsync(heartbeat).ConfigureAwait(false);

                    var code = ex.Code ?? UserOrgImportErrorCodes.Failed;
                    await RecordFailureAsync(jobId, ex.Message, code).ConfigureAwait(false);

                    var after = await TryGetJobAsync(jobId).ConfigureAwait(false);
                    Emit(UserOrgImportStages.Refused, jobId, after ?? job, watch, code);
                    return after ?? job;
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
                    await RecordFailureAsync(jobId, FailureMessage, UserOrgImportErrorCodes.Failed).ConfigureAwait(false);

                    // The one error that is not a failure: the connection dropped after the apply
                    // committed, so the answer never arrived. The apply records its success in its own
                    // transaction, and the failure above only rewrites a job still Pending or Running,
                    // so the job row says which it really was.
                    var after = await TryGetJobAsync(jobId).ConfigureAwait(false);
                    if (after != null && after.Status == UserOrgImportStatus.Succeeded)
                    {
                        await CleanUpAsync(jobId, after).ConfigureAwait(false);
                        Emit(UserOrgImportStages.Succeeded, jobId, after, watch, exceptionType: ex.GetBaseException().GetType().Name);
                        return after;
                    }

                    Emit(UserOrgImportStages.Failed, jobId, after ?? job, watch, UserOrgImportErrorCodes.Failed, ex.GetBaseException().GetType().Name);
                    throw;
                }

                heartbeatStop.Cancel();
                await SwallowAsync(heartbeat).ConfigureAwait(false);

                // The apply has already marked the job Succeeded, in the transaction that made the
                // changes. What is left is housekeeping.
                await CleanUpAsync(jobId, applied ?? job).ConfigureAwait(false);

                if (applied != null)
                {
                    applied.Status = UserOrgImportStatus.Succeeded;
                }

                var result = applied ?? await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                Emit(UserOrgImportStages.Succeeded, jobId, result ?? job, watch);
                return result;
            }
        }

        /// <summary>
        /// Whether a job's worker is presumed gone, so the job should be handed to another: still
        /// <see cref="UserOrgImportStatus.Pending"/> well after it was queued, or still
        /// <see cref="UserOrgImportStatus.Running"/> with a heartbeat that has stopped moving.
        /// </summary>
        public static bool NeedsResume(UserOrgImportJob job, DateTime utcNow)
        {
            if (job == null)
            {
                return false;
            }

            if (job.Status == UserOrgImportStatus.Pending)
            {
                return utcNow - job.QueuedUtc > UserOrgImportJobLimits.LostDispatchGrace;
            }

            return job.Status == UserOrgImportStatus.Running && HeartbeatIsStale(job, utcNow);
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
        /// recycle in that window leaves a job nobody will claim.
        /// </para>
        /// <para>
        /// Such a job is resumed automatically - see <see cref="NeedsResume"/> and
        /// <see cref="IUserOrgImportJobStore.ResumeStaleJobsAsync"/> - which is safe because an import
        /// that did not finish changed nothing. So this is what the portal shows only when resuming has
        /// not happened yet, or could not.
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

            return job.Status == UserOrgImportStatus.Running && HeartbeatIsStale(job, utcNow);
        }

        private static bool HeartbeatIsStale(UserOrgImportJob job, DateTime utcNow)
        {
            var lastSeen = job.HeartbeatUtc ?? job.StartedUtc;
            return lastSeen.HasValue && utcNow - lastSeen.Value > StaleHeartbeatThreshold;
        }

        private async Task HeartbeatUntilStopped(int jobId, UserOrgImportJob job, CancellationToken stop)
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

                    try
                    {
                        await _jobs.HeartbeatAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // One lost beat must not end the loop. The catch used to sit outside the while,
                        // so a single transient SQL error stopped the job reporting progress for the
                        // rest of a merge that was still perfectly healthy - and after
                        // StaleHeartbeatThreshold that makes a live import look abandoned.
                        Emit(UserOrgImportStages.HeartbeatFailed, jobId, job, exceptionType: ex.GetBaseException().GetType().Name);
                    }
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

        private async Task RecordFailureAsync(int jobId, string message, string code)
        {
            try
            {
                await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Failed, message, CancellationToken.None, code)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Recording the outcome is best-effort and must never replace the original fault. A job
                // left Running is resumed or taken over once its heartbeat goes stale.
            }
        }

        /// <summary>Removes a finished job's staged rows.</summary>
        /// <remarks>
        /// Never a reason to report the import as failed: the changes are committed and the job already
        /// says Succeeded. Rows left behind are removed by the next resume sweep.
        /// </remarks>
        private async Task CleanUpAsync(int jobId, UserOrgImportJob job)
        {
            try
            {
                await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Succeeded, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Emit(UserOrgImportStages.CleanupFailed, jobId, job, exceptionType: ex.GetBaseException().GetType().Name);
            }
        }

        private async Task<UserOrgImportJob> TryGetJobAsync(int jobId)
        {
            try
            {
                return await _jobs.GetJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Emit(
            string stage,
            int jobId,
            UserOrgImportJob job,
            Stopwatch watch = null,
            string code = null,
            string exceptionType = null)
        {
            try
            {
                _telemetry.Record(new UserOrgImportTelemetryEvent
                {
                    Stage = stage,
                    JobId = jobId,
                    OrgTypeId = job?.OrgTypeId,
                    Mode = job?.Mode,
                    Code = code ?? job?.ErrorCode,
                    ExceptionType = exceptionType,
                    DurationMs = watch?.ElapsedMilliseconds,
                    Rows = job?.RowsTotal,
                    RowsApplied = job?.RowsApplied,
                    RowsCleared = job?.RowsCleared,
                    RowsUnknownUpn = job?.RowsUnknownUpn,
                    RowsInvalid = job?.RowsInvalid,
                    Attempts = job?.Attempts,
                });
            }
            catch (Exception)
            {
                // A diagnostic must never fail the import it describes.
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
    }
}
