using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>What <see cref="UserOrgChangeLogShipper.ShipAsync"/> did.</summary>
    public enum UserOrgChangeLogShipOutcome
    {
        /// <summary>The change log was written and the outbox emptied.</summary>
        Written,

        /// <summary>Another worker is writing this import's change log right now.</summary>
        LeaseHeld,

        /// <summary>There is nothing to write: the import did not apply, or its log is already written.</summary>
        NothingToWrite,
    }

    /// <summary>
    /// Writes one applied import's change list from the SQL outbox to the change log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after the apply has committed, never inside it: writing tens of thousands of entities to
    /// Table Storage must not hold the transaction that locks the assignments. The list is safe in the
    /// outbox until this finishes, so an interrupted write is simply repeated - by the worker, or by the
    /// resume sweep - and because every write is idempotent, repeating it cannot duplicate a change.
    /// </para>
    /// <para>
    /// The summary is written after every change and the outbox emptied after the summary, so a log is
    /// either complete or still pending: never a summary without its changes.
    /// </para>
    /// </remarks>
    public sealed class UserOrgChangeLogShipper
    {
        /// <summary>Changes read from the outbox, and written, per round trip.</summary>
        public const int PageSize = 1000;

        private readonly IUserOrgImportJobStore _jobs;
        private readonly IUserOrgChangeOutbox _outbox;
        private readonly IUserOrgTypeStore _types;
        private readonly Func<IUserOrgChangeLog> _log;
        private readonly IUserOrgImportTelemetry _telemetry;

        /// <param name="log">Chooses the store at write time: Table Storage when usable, otherwise memory.</param>
        public UserOrgChangeLogShipper(
            IUserOrgImportJobStore jobs,
            IUserOrgChangeOutbox outbox,
            IUserOrgTypeStore types,
            Func<IUserOrgChangeLog> log,
            IUserOrgImportTelemetry telemetry = null)
        {
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _telemetry = telemetry ?? NullUserOrgImportTelemetry.Instance;
        }

        public async Task<UserOrgChangeLogShipOutcome> ShipAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var watch = Stopwatch.StartNew();
            UserOrgImportJob job = null;
            IUserOrgChangeLog log = null;

            try
            {
                using (var lease = await _outbox.TryLeaseAsync(jobId, cancellationToken).ConfigureAwait(false))
                {
                    if (lease == null)
                    {
                        return UserOrgChangeLogShipOutcome.LeaseHeld;
                    }

                    // Read under the lease, so a worker that finished while this one waited is seen as done.
                    job = await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                    if (job == null || job.Status != UserOrgImportStatus.Succeeded
                        || job.ChangeLogStatus != UserOrgChangeLogStatus.Pending)
                    {
                        return UserOrgChangeLogShipOutcome.NothingToWrite;
                    }

                    log = _log();
                    var type = await _types.GetAsync(job.OrgTypeId, cancellationToken).ConfigureAwait(false);
                    var import = new UserOrgChangeLogImport
                    {
                        LogId = UserOrgChangeLogKeys.LogId(job.Id, job.QueuedUtc),
                        JobId = job.Id,
                        OrgTypeId = job.OrgTypeId,
                        OrgTypeName = type?.Name,
                        Mode = job.Mode,
                        StartedBy = job.StartedBy,
                        FileName = job.FileName,
                        QueuedUtc = job.QueuedUtc,
                        FinishedUtc = job.FinishedUtc,
                        RowsTotal = job.RowsTotal,
                        RowsUnknownUpn = job.RowsUnknownUpn,
                        RowsInvalid = job.RowsInvalid,
                    };

                    var after = 0;
                    while (true)
                    {
                        var page = await _outbox.ReadAsync(jobId, after, PageSize, cancellationToken).ConfigureAwait(false);
                        if (page.Count == 0)
                        {
                            break;
                        }

                        await log.AppendAsync(import, page, cancellationToken).ConfigureAwait(false);
                        foreach (var change in page)
                        {
                            switch (change.Kind)
                            {
                                case UserOrgChangeKind.Added: import.Added++; break;
                                case UserOrgChangeKind.Changed: import.Changed++; break;
                                default: import.Cleared++; break;
                            }
                        }

                        after = page[page.Count - 1].UserId;
                        if (page.Count < PageSize)
                        {
                            break;
                        }
                    }

                    import.StoredChanges = import.ChangeCount;
                    import.WrittenUtc = DateTime.UtcNow;
                    await log.CompleteAsync(import, cancellationToken).ConfigureAwait(false);
                    await _outbox.CompleteAsync(jobId, log.Destination, cancellationToken).ConfigureAwait(false);

                    Emit(UserOrgImportStages.ChangeLogWritten, job, watch, log.Destination, import.ChangeCount);
                    return UserOrgChangeLogShipOutcome.Written;
                }
            }
            catch (Exception ex)
            {
                // Left pending: the list is still in the outbox, and the resume sweep writes it again.
                Emit(UserOrgImportStages.ChangeLogFailed, job, watch, log?.Destination, null, ex.GetBaseException().GetType().Name);
                throw;
            }
        }

        private void Emit(
            string stage,
            UserOrgImportJob job,
            Stopwatch watch,
            UserOrgChangeLogStatus? destination,
            int? count,
            string exceptionType = null)
        {
            try
            {
                _telemetry.Record(new UserOrgImportTelemetryEvent
                {
                    Stage = stage,
                    JobId = job?.Id,
                    OrgTypeId = job?.OrgTypeId,
                    Mode = job?.Mode,
                    Code = destination == UserOrgChangeLogStatus.TableStorage ? "tableStorage"
                        : destination == UserOrgChangeLogStatus.Memory ? "memory"
                        : null,
                    Count = count,
                    DurationMs = watch.ElapsedMilliseconds,
                    ExceptionType = exceptionType,
                });
            }
            catch (Exception)
            {
                // A diagnostic must never fail the work it describes.
            }
        }
    }
}
