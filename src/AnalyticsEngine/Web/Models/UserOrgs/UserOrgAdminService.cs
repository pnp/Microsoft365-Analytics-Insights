using Common.Entities.UserOrgs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// The portal-facing behaviour of user organisations: mapping to and from the API models, running
    /// the live attribute test, previewing an upload and queueing an import.
    /// </summary>
    /// <remarks>
    /// Everything it depends on is a port, so all of it can be exercised without SQL Server, Microsoft
    /// Graph or an ASP.NET pipeline. The controller is left with model binding and HTTP status codes.
    /// </remarks>
    public sealed class UserOrgAdminService
    {
        /// <summary>How many parsed rows the preview shows. The admin asked for the top ten.</summary>
        public const int PreviewRowCount = 10;

        /// <summary>How many problem rows the preview reports before it stops listing them.</summary>
        public const int PreviewProblemCount = 10;

        /// <summary>
        /// How many unusable rows the preview lists in full, for the admin to download and fix. Far more
        /// than anyone reads on screen, and enough that a file with a genuine handful of bad rows is
        /// never cut short.
        /// </summary>
        public const int MaxUnusableRows = 10000;

        /// <summary>The most past imports one history request returns.</summary>
        public const int MaxHistory = 50;

        private readonly IUserOrgTypeStore _types;
        private readonly IUserOrgAssignmentStore _assignments;
        private readonly IUserOrgImportJobStore _jobs;
        private readonly IUserOrgUserLookup _users;
        private readonly IUserOrgGraphProbe _probe;
        private readonly Action<int> _dispatchImport;
        private readonly Func<DateTime> _utcNow;
        private readonly IUserOrgImportTelemetry _telemetry;
        private readonly UserOrgResumeGate _resumeGate;

        public UserOrgAdminService(
            IUserOrgTypeStore types,
            IUserOrgAssignmentStore assignments,
            IUserOrgImportJobStore jobs,
            IUserOrgUserLookup users,
            IUserOrgGraphProbe probe,
            Action<int> dispatchImport,
            Func<DateTime> utcNow = null,
            IUserOrgImportTelemetry telemetry = null,
            UserOrgResumeGate resumeGate = null)
        {
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _dispatchImport = dispatchImport ?? throw new ArgumentNullException(nameof(dispatchImport));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _telemetry = telemetry ?? NullUserOrgImportTelemetry.Instance;
            _resumeGate = resumeGate ?? UserOrgResumeGate.Shared;
        }

        #region Org types

        public async Task<List<UserOrgTypeModel>> ListAsync(CancellationToken cancellationToken)
        {
            // The admin page's landing call, so an import stranded by a restart is picked up again as soon
            // as anyone looks - before the summaries are read, so they show it resumed.
            await ResumeInterruptedImportsAsync(cancellationToken).ConfigureAwait(false);

            var summaries = await _types.GetSummariesAsync(cancellationToken).ConfigureAwait(false);
            return summaries.Select(ToModel).ToList();
        }

        public async Task<UserOrgTypeModel> CreateAsync(UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            var type = await ValidateAndProbeAsync(model, cancellationToken).ConfigureAwait(false);
            type.Id = await _types.CreateAsync(type, cancellationToken).ConfigureAwait(false);
            return ToModel(new UserOrgTypeSummary { Type = type });
        }

        public async Task<UserOrgTypeModel> UpdateAsync(int id, UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            var existing = await _types.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (existing == null)
            {
                throw new UserOrgValidationException("That organisation type no longer exists.");
            }

            var type = await ValidateAndProbeAsync(model, cancellationToken).ConfigureAwait(false);
            type.Id = id;

            // Changing where a type's values come from invalidates every value it holds: they were read
            // from somewhere that is no longer the source of truth for this dimension. That covers both
            // repointing an Entra type at a different attribute and switching a type between Entra and
            // CSV - the latter matters just as much, because a CSV Merge deliberately leaves users it
            // does not mention alone, so values left over from the old Entra attribute would survive
            // every subsequent import and show as current indefinitely.
            var sourceChanged =
                existing.SourceKind != type.SourceKind
                || (type.SourceKind == UserOrgSourceKind.EntraAttribute
                    && !string.Equals(existing.EntraAttributeName, type.EntraAttributeName, StringComparison.OrdinalIgnoreCase));

            // Disabling does not discard anything - the values stay visible on user lookup until the
            // type is deleted - but it does make the Entra merge fence that type out, while the cycle
            // still commits its delta token. Re-enabling must therefore not land back on the same
            // token, or the users skipped in that cycle are never re-read.
            var enabledChanged = existing.IsEnabled != type.IsEnabled;

            await _types.UpdateAsync(type, sourceChanged, sourceChanged || enabledChanged, cancellationToken)
                .ConfigureAwait(false);

            // Mirrors what the store just did, so the response does not claim a refresh for values the
            // update has discarded.
            type.LastRefreshedUtc = sourceChanged ? null : existing.LastRefreshedUtc;

            return ToModel(new UserOrgTypeSummary { Type = type });
        }

        public Task DeleteAsync(int id, CancellationToken cancellationToken)
        {
            return _types.DeleteAsync(id, cancellationToken);
        }

        /// <summary>
        /// Validates a submitted org type and, for an Entra one, proves the attribute actually works.
        /// </summary>
        /// <remarks>
        /// The parse check alone is not enough, and neither is the portal's own test. A perfectly
        /// well-formed directory extension name can still name a property that does not exist in this
        /// tenant, and Graph answers that with a 400 that fails the entire <c>/users/delta</c> request -
        /// so saving it would break the user import until the importer's fallback noticed.
        ///
        /// The probe therefore runs here, on the server, rather than relying on the dialog having done
        /// it. A gate that lives only in the browser is not a gate: the API is reachable directly, and a
        /// future UI change could quietly drop it.
        ///
        /// It is skipped for a type being saved as <b>disabled</b>, because a disabled type is never
        /// read and so cannot break anything. Probing one anyway makes the feature's own recovery
        /// advice impossible to follow: when a directory extension is deleted from the tenant, the
        /// import logs an error telling the admin to go and fix the type on this page - and the only
        /// non-destructive fix, turning it off, would be refused by the very check that objected to
        /// it. Every other option (delete, repoint, switch to CSV) discards the values.
        /// </remarks>
        private async Task<UserOrgType> ValidateAndProbeAsync(UserOrgTypeSaveModel model, CancellationToken cancellationToken)
        {
            if (model == null)
            {
                throw new UserOrgValidationException("No organisation type was supplied.");
            }

            string name;
            string error;
            if (!UserOrgRules.TryNormaliseOrgTypeName(model.Name, out name, out error))
            {
                throw new UserOrgValidationException(error);
            }

            var sourceKind = ParseSource(model.Source);

            var type = new UserOrgType
            {
                Name = name,
                SourceKind = sourceKind,
                IsEnabled = model.IsEnabled,
            };

            if (sourceKind == UserOrgSourceKind.CsvUpload)
            {
                return type;
            }

            EntraOrgAttributeSpec spec;
            string attributeError;
            if (!EntraOrgAttributeSpec.TryParse(model.EntraAttributeName, out spec, out attributeError))
            {
                throw new UserOrgValidationException(attributeError);
            }

            // Parsed and normalised either way, so what is stored is always canonical; only the live
            // probe is conditional. See UserOrgRules.RequiresLiveAttributeProof for why.
            if (UserOrgRules.RequiresLiveAttributeProof(type.SourceKind, type.IsEnabled))
            {
                await ProveAttributeIsReadableAsync(spec, cancellationToken).ConfigureAwait(false);
            }

            type.EntraAttributeName = spec.Canonical;
            return type;
        }

        /// <summary>
        /// Asks Graph for the property against an arbitrary user, and refuses the save if it will not
        /// answer.
        /// </summary>
        /// <remarks>
        /// Only a rejection of the <b>property</b> blocks the save. A user that cannot be found, a
        /// throttled tenant or a network problem says nothing about whether the attribute is valid, and
        /// refusing the configuration over one of those would be both wrong and impossible for an
        /// administrator to act on.
        /// </remarks>
        private async Task ProveAttributeIsReadableAsync(EntraOrgAttributeSpec spec, CancellationToken cancellationToken)
        {
            UserOrgProbeOutcome outcome;
            try
            {
                outcome = await _probe.ResolveAsync(spec, ProbeUpn, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Graph being unreachable must not stop an administrator configuring the product. The
                // importer's own fallback still protects the user import if the attribute turns out bad.
                return;
            }

            if (outcome != null && !outcome.Succeeded && outcome.RejectedTheProperty)
            {
                throw new UserOrgValidationException(outcome.Message);
            }
        }

        /// <summary>
        /// The user the server-side probe asks about.
        /// </summary>
        /// <remarks>
        /// A synthetic principal that will not exist. That is deliberate and sufficient: Graph
        /// validates <c>$select</c> before it looks the user up, so an unknown property is rejected
        /// with a 400 while a valid one produces a 404 - which is all this check needs to tell apart.
        /// Picking a real user would mean choosing one, and any choice could be the one user the
        /// caller lacks permission to read.
        /// </remarks>
        private const string ProbeUpn = "userorg-attribute-probe@invalid.invalid";

        internal static UserOrgSourceKind ParseSource(string source)
        {
            if (string.Equals(source, "entra", StringComparison.OrdinalIgnoreCase))
            {
                return UserOrgSourceKind.EntraAttribute;
            }

            if (string.Equals(source, "csv", StringComparison.OrdinalIgnoreCase))
            {
                return UserOrgSourceKind.CsvUpload;
            }

            throw new UserOrgValidationException("The organisation source must be either 'entra' or 'csv'.");
        }

        #endregion

        #region Live attribute test

        /// <summary>
        /// Resolves an attribute for one user and reports the raw and normalised values.
        /// </summary>
        public async Task<UserOrgTestResultModel> TestAsync(UserOrgTestRequestModel request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return new UserOrgTestResultModel { Message = "No test request was supplied." };
            }

            EntraOrgAttributeSpec spec;
            string error;
            if (!EntraOrgAttributeSpec.TryParse(request.EntraAttributeName, out spec, out error))
            {
                return new UserOrgTestResultModel { Message = error };
            }

            var result = new UserOrgTestResultModel
            {
                Upn = request.Upn,
                AttributeName = spec.Canonical,
                GraphProperty = spec.SelectFragment,
            };

            var outcome = await _probe.ResolveAsync(spec, request.Upn, cancellationToken).ConfigureAwait(false);

            result.Succeeded = outcome.Succeeded;
            result.Message = outcome.Message;

            if (!outcome.Succeeded)
            {
                return result;
            }

            result.RawValue = outcome.RawValue;
            result.HasNoValue = outcome.HasNoValue;
            result.NormalisedValue = UserOrgRules.NormaliseOrgValue(outcome.RawValue);
            result.WouldTruncate = UserOrgRules.WouldTruncate(outcome.RawValue);

            if (outcome.HasNoValue)
            {
                result.Message =
                    "The attribute was read successfully, but this user has no value for it. "
                    + "During an import that means their organisation value would be cleared.";
            }
            else if (result.WouldTruncate)
            {
                result.Message =
                    $"The value is longer than {UserOrgRules.MaxOrgValueLength} characters and would be shortened when stored.";
            }

            return result;
        }

        public Task<UserOrgAttributeCatalogueModel> DiscoverAttributesAsync(CancellationToken cancellationToken)
        {
            return _probe.DiscoverAsync(cancellationToken);
        }

        #endregion

        #region CSV

        /// <summary>
        /// Parses an upload, stages it as a draft, and reports both a readable sample and the blast
        /// radius of importing it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The whole file is parsed and every user principal name in it resolved, not just the ten rows
        /// shown. That is the point: a ten-row sample cannot tell an administrator that a complete,
        /// correctly formatted export happens to cover only half the tenant - and with Replace, the
        /// half it omits loses its values.
        /// </para>
        /// <para>
        /// The preview is also the upload. The parsed rows are staged as a draft and the counts are
        /// computed in SQL with exactly the matching the import uses, so what the admin is shown and
        /// what is imported cannot disagree - and importing commits the draft by id, so the file is sent
        /// and parsed once, and a file edited on disk after the preview cannot slip in under its
        /// confirmation. Nothing is imported until <see cref="CommitImportAsync"/>.
        /// </para>
        /// </remarks>
        /// <param name="userColumn">The 0-based user column the admin chose, or null to detect it.</param>
        /// <param name="valueColumn">The 0-based value column the admin chose, or null to detect it.</param>
        public async Task<UserOrgCsvPreviewModel> PreviewAsync(
            Stream content,
            string fileName,
            int orgTypeId,
            string startedBy,
            int? userColumn,
            int? valueColumn,
            CancellationToken cancellationToken)
        {
            if (userColumn.HasValue != valueColumn.HasValue
                || (userColumn.HasValue && (userColumn.Value < 0 || valueColumn.Value < 0 || userColumn.Value == valueColumn.Value)))
            {
                throw new UserOrgValidationException(
                    "Choose two different columns: one holding the user principal names, and one holding the values.",
                    UserOrgImportRefusalCodes.InvalidColumns);
            }

            var watch = Stopwatch.StartNew();
            var type = await RequireImportableTypeAsync(orgTypeId, cancellationToken).ConfigureAwait(false);
            long? bytes = content != null && content.CanSeek ? content.Length - content.Position : (long?)null;

            var parsed = UserOrgCsvParser.Parse(content, new UserOrgCsvParseOptions
            {
                OrgTypeName = type.Name,
                UserColumn = userColumn,
                ValueColumn = valueColumn,
            });

            var preview = new UserOrgCsvPreviewModel
            {
                FileName = fileName,
                Delimiter = DescribeDelimiter(parsed.Delimiter),
                HeaderDetected = parsed.HeaderDetected,
                Columns = parsed.Columns == null ? null : parsed.Columns.ToList(),
                ColumnCount = parsed.ColumnCount,
                UserColumnIndex = parsed.UserColumnIndex,
                ValueColumnIndex = parsed.ValueColumnIndex,
                UpnColumnName = parsed.UpnColumnName,
                OrgColumnName = parsed.OrgColumnName,
                TruncatedValueCount = parsed.TruncatedValueCount,
                MaxValueLength = UserOrgRules.MaxOrgValueLength,
                Problems = ToProblemModels(parsed.Problems),
            };

            if (parsed.Blocking != null)
            {
                // Refused outright, and nothing is staged. Every blocking reason means rows would be
                // misread - and in a Replace, a misread row is a user whose value is cleared.
                preview.Blocking = new UserOrgCsvBlockingModel
                {
                    Code = parsed.Blocking.Code,
                    Line = parsed.Blocking.Line,
                    LastLine = parsed.Blocking.LastLine,
                    Max = parsed.Blocking.Code == UserOrgCsvBlockingCodes.TooManyRows ? UserOrgCsvParser.MaxDataLines : (int?)null,
                };
                preview.TotalRows = parsed.Rows.Count;
                preview.UnusableRows = parsed.Problems.Take(MaxUnusableRows).Select(ToUnusableRow).ToList();
                preview.UnusableRowCount = parsed.Problems.Count;

                Emit(new UserOrgImportTelemetryEvent
                {
                    Stage = UserOrgImportStages.PreviewBlocked,
                    OrgTypeId = orgTypeId,
                    Code = parsed.Blocking.Code,
                    Bytes = bytes,
                    Rows = parsed.Rows.Count,
                    RowsInvalid = parsed.Problems.Count,
                    DurationMs = watch.ElapsedMilliseconds,
                });

                return preview;
            }

            var draftId = await _jobs.CreateDraftAsync(
                new UserOrgImportJob
                {
                    OrgTypeId = orgTypeId,
                    FileName = fileName,
                    StartedBy = startedBy,
                    RowsInvalid = parsed.Problems.Count,
                    ExpectedGeneration = type.SourceGeneration,
                },
                parsed.Rows,
                cancellationToken).ConfigureAwait(false);

            var summary = await _jobs.SummariseDraftAsync(draftId, PreviewRowCount, MaxUnusableRows, cancellationToken)
                .ConfigureAwait(false);
            if (summary == null)
            {
                throw new UserOrgValidationException(
                    "The file could not be prepared for import. Choose it again.", UserOrgImportRefusalCodes.DraftNotFound);
            }

            preview.DraftId = draftId;
            preview.TotalRows = summary.RowsTotal;
            preview.MoreRowsExist = summary.RowsTotal > PreviewRowCount;
            preview.UnknownUpnCount = summary.UnknownRows;
            preview.MatchedUserCount = summary.MatchedUsersWithValue;
            preview.CurrentlyAssignedCount = summary.CurrentlyAssigned;
            preview.WouldClearCount = summary.ReplaceWouldClear;
            preview.MergeWouldClearCount = summary.MergeWouldClear;
            preview.Rows = summary.SampleRows.Select(r => new UserOrgCsvPreviewRowModel
            {
                LineNumber = r.LineNumber,
                Upn = r.Upn,
                OrgValue = r.OrgValue,
                UserExists = r.UserExists,
                ClearsValue = r.OrgValue == null,
            }).ToList();

            // Everything that will not be imported, in file order: rows that could not be read, and rows
            // naming nobody. The two cannot overlap - an unreadable row is never staged.
            preview.UnusableRows = parsed.Problems.Select(ToUnusableRow)
                .Concat(summary.UnknownRowList.Select(r => new UserOrgCsvUnusableRowModel
                {
                    LineNumber = r.LineNumber,
                    Upn = r.Upn,
                    OrgValue = r.OrgValue,
                    Code = UserOrgCsvProblemCodes.UnknownUser,
                }))
                .OrderBy(r => r.LineNumber)
                .Take(MaxUnusableRows)
                .ToList();
            preview.UnusableRowCount = parsed.Problems.Count + summary.UnknownRows;

            Emit(new UserOrgImportTelemetryEvent
            {
                Stage = UserOrgImportStages.Previewed,
                JobId = draftId,
                OrgTypeId = orgTypeId,
                Bytes = bytes,
                Rows = summary.RowsTotal,
                RowsInvalid = parsed.Problems.Count,
                RowsUnknownUpn = summary.UnknownRows,
                DurationMs = watch.ElapsedMilliseconds,
            });

            return preview;
        }

        /// <summary>
        /// Imports a previewed draft: admits it as a queued import and starts the background worker.
        /// </summary>
        /// <param name="confirmedClearCount">
        /// How many users the administrator agreed may lose their value - the number the preview showed
        /// for this mode. Refused if the import would now clear more, so a confirmation never covers a
        /// bigger wipe than the one it was given for.
        /// </param>
        /// <exception cref="UserOrgValidationException">
        /// With a code from <see cref="UserOrgImportRefusalCodes"/>, for the portal to word.
        /// </exception>
        public async Task<UserOrgImportQueuedModel> CommitImportAsync(
            int orgTypeId,
            int draftId,
            UserOrgImportMode mode,
            int confirmedClearCount,
            string startedBy,
            CancellationToken cancellationToken)
        {
            await RequireImportableTypeAsync(orgTypeId, cancellationToken).ConfigureAwait(false);

            try
            {
                // Every check that matters happens in here, inside the transaction that admits the
                // import: the draft, the type's configuration, an import already running, whether any
                // row names a user, and the clear count. Checking any of them first, outside it, only
                // opens a window for another administrator to change the answer.
                await _jobs.CommitDraftAsync(draftId, orgTypeId, mode, confirmedClearCount, startedBy, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UserOrgValidationException ex)
            {
                Emit(new UserOrgImportTelemetryEvent
                {
                    Stage = UserOrgImportStages.CommitRefused,
                    JobId = draftId,
                    OrgTypeId = orgTypeId,
                    Mode = mode,
                    Code = ex.Code,
                    Count = confirmedClearCount,
                });
                throw;
            }

            UserOrgImportJob job = null;
            try
            {
                job = await _jobs.GetJobAsync(draftId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the row counts for the response. The import is queued either way, and must still
                // be dispatched.
            }

            Emit(new UserOrgImportTelemetryEvent
            {
                Stage = UserOrgImportStages.Committed,
                JobId = draftId,
                OrgTypeId = orgTypeId,
                Mode = mode,
                Rows = job?.RowsTotal,
                RowsInvalid = job?.RowsInvalid,
                Count = confirmedClearCount,
            });

            // Hand off to the background worker. The dispatcher is injected so this whole method can be
            // tested without starting a thread.
            _dispatchImport(draftId);

            return new UserOrgImportQueuedModel
            {
                JobId = draftId,
                RowsQueued = job?.RowsTotal ?? 0,
                RowsInvalid = job?.RowsInvalid ?? 0,
            };
        }

        /// <summary>
        /// One import, for progress polling. Drafts are not imports, so they are not found.
        /// </summary>
        /// <remarks>
        /// Polling is also what notices an import whose worker died, and hands it to a new one. So a
        /// job interrupted by an App Service recycle carries on by itself while its admin watches,
        /// rather than sitting on "interrupted" waiting to be uploaded again.
        /// </remarks>
        public async Task<UserOrgImportJobModel> GetJobAsync(int jobId, CancellationToken cancellationToken)
        {
            var job = await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job == null || job.Status == UserOrgImportStatus.Draft)
            {
                return null;
            }

            var now = _utcNow();
            if (UserOrgImportRunner.NeedsResume(job, now))
            {
                await ResumeInterruptedImportsAsync(cancellationToken).ConfigureAwait(false);
                job = await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) ?? job;
            }

            return ToModel(job, now);
        }

        /// <summary>An org type's most recent imports, newest first - the audit trail the admin page shows.</summary>
        public async Task<List<UserOrgImportJobModel>> ListImportsAsync(int orgTypeId, int take, CancellationToken cancellationToken)
        {
            var type = await _types.GetAsync(orgTypeId, cancellationToken).ConfigureAwait(false);
            if (type == null)
            {
                return null;
            }

            var jobs = await _jobs
                .ListJobsAsync(orgTypeId, Math.Max(1, Math.Min(take, MaxHistory)), cancellationToken)
                .ConfigureAwait(false);
            var now = _utcNow();
            return jobs.Select(j => ToModel(j, now)).ToList();
        }

        /// <summary>
        /// Hands imports whose worker died back to a new one, and stops those that cannot sensibly be
        /// resumed. Returns the ids it dispatched.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Safe because an import that did not finish changed nothing: the apply records its success in
        /// the same transaction as its changes, and the claim, the org type's application lock and the
        /// apply's status check between them let only one worker apply a file.
        /// </para>
        /// <para>
        /// Called when the web app starts, when the admin page lists the org types, and when an import
        /// being polled looks stranded. Throttled per web process, because the page can call it every
        /// few seconds and the answer rarely changes; <paramref name="force"/> is for start-up, which
        /// must not be skipped.
        /// </para>
        /// <para>
        /// Never throws: a failed sweep is reported and retried next time, and must not fail the page
        /// that happened to trigger it.
        /// </para>
        /// </remarks>
        public async Task<IReadOnlyList<int>> ResumeInterruptedImportsAsync(CancellationToken cancellationToken, bool force = false)
        {
            if (!force && !_resumeGate.TryEnter(_utcNow()))
            {
                return new int[0];
            }

            var watch = Stopwatch.StartNew();
            IReadOnlyList<int> ids;
            try
            {
                ids = await _jobs.ResumeStaleJobsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Emit(new UserOrgImportTelemetryEvent
                {
                    Stage = UserOrgImportStages.ResumeFailed,
                    ExceptionType = ex.GetBaseException().GetType().Name,
                    DurationMs = watch.ElapsedMilliseconds,
                });
                return new int[0];
            }

            foreach (var id in ids)
            {
                _dispatchImport(id);
            }

            if (ids.Count > 0)
            {
                Emit(new UserOrgImportTelemetryEvent
                {
                    Stage = UserOrgImportStages.ResumeSwept,
                    Count = ids.Count,
                    DurationMs = watch.ElapsedMilliseconds,
                });
            }

            return ids;
        }

        /// <summary>
        /// The org type an import targets, refused unless a file can be imported into it.
        /// </summary>
        /// <remarks>
        /// Checked before the file is parsed so an admin learns of it at once rather than after a large
        /// upload - and checked again, authoritatively, inside the transaction that admits the import.
        /// </remarks>
        private async Task<UserOrgType> RequireImportableTypeAsync(int orgTypeId, CancellationToken cancellationToken)
        {
            var type = await _types.GetAsync(orgTypeId, cancellationToken).ConfigureAwait(false);
            if (type == null)
            {
                throw new UserOrgValidationException(
                    "That organisation type no longer exists.", UserOrgImportRefusalCodes.TypeNotFound);
            }

            if (type.SourceKind != UserOrgSourceKind.CsvUpload)
            {
                // Importing a file into an Entra-sourced type would be overwritten by the next user
                // import anyway, so it is refused rather than silently wasted.
                throw new UserOrgValidationException(
                    $"'{type.Name}' takes its values from an Entra attribute, so a file cannot be imported into it. "
                    + "Change its source to CSV upload first.",
                    UserOrgImportRefusalCodes.TypeNotCsv,
                    new Dictionary<string, object> { { "name", type.Name } });
            }

            if (!type.IsEnabled)
            {
                // The admin page labels a disabled type as not imported. Accepting a file into one
                // anyway would make that label a lie, and the values would appear on user lookup with
                // nothing ever refreshing them.
                throw new UserOrgValidationException(
                    $"'{type.Name}' is disabled, so a file cannot be imported into it. Enable it first.",
                    UserOrgImportRefusalCodes.TypeDisabled,
                    new Dictionary<string, object> { { "name", type.Name } });
            }

            return type;
        }

        private static List<UserOrgCsvProblemModel> ToProblemModels(IReadOnlyList<UserOrgCsvRowProblem> problems)
        {
            return problems
                .Take(PreviewProblemCount)
                .Select(p => new UserOrgCsvProblemModel { LineNumber = p.LineNumber, Code = p.Code, Reason = p.Reason })
                .ToList();
        }

        private static UserOrgCsvUnusableRowModel ToUnusableRow(UserOrgCsvRowProblem problem)
        {
            return new UserOrgCsvUnusableRowModel
            {
                LineNumber = problem.LineNumber,
                Upn = problem.Upn,
                OrgValue = problem.OrgValue,
                Code = problem.Code,
            };
        }

        private void Emit(UserOrgImportTelemetryEvent item)
        {
            try
            {
                _telemetry.Record(item);
            }
            catch (Exception)
            {
                // A diagnostic must never fail the request it describes.
            }
        }

        internal static string DescribeDelimiter(char delimiter)
        {
            switch (delimiter)
            {
                case '\t': return "tab";
                case ';': return "semicolon";
                case '|': return "pipe";
                default: return "comma";
            }
        }

        #endregion

        #region Mapping

        internal static UserOrgTypeModel ToModel(UserOrgTypeSummary summary)
        {
            var type = summary.Type;
            return new UserOrgTypeModel
            {
                Id = type.Id,
                Name = type.Name,
                Source = type.SourceKind == UserOrgSourceKind.EntraAttribute ? "entra" : "csv",
                EntraAttributeName = type.EntraAttributeName,
                IsEnabled = type.IsEnabled,
                AssignedUserCount = summary.AssignedUserCount,
                DistinctValueCount = summary.DistinctValueCount,
                CreatedUtc = Iso(type.CreatedUtc),
                ModifiedUtc = type.ModifiedUtc.HasValue ? Iso(type.ModifiedUtc.Value) : null,
                LastRefreshedUtc = type.LastRefreshedUtc.HasValue ? Iso(type.LastRefreshedUtc.Value) : null,
                LastImport = summary.LastImport == null ? null : ToModel(summary.LastImport, DateTime.UtcNow),
            };
        }

        internal static UserOrgImportJobModel ToModel(UserOrgImportJob job, DateTime utcNow)
        {
            return new UserOrgImportJobModel
            {
                Id = job.Id,
                OrgTypeId = job.OrgTypeId,
                Mode = job.Mode == UserOrgImportMode.Replace ? "replace" : "merge",
                Status = DescribeStatus(job, utcNow),
                FileName = job.FileName,
                StartedBy = job.StartedBy,
                QueuedUtc = Iso(job.QueuedUtc),
                StartedUtc = job.StartedUtc.HasValue ? Iso(job.StartedUtc.Value) : null,
                FinishedUtc = job.FinishedUtc.HasValue ? Iso(job.FinishedUtc.Value) : null,
                Attempts = job.Attempts,
                RowsTotal = job.RowsTotal,
                RowsApplied = job.RowsApplied,
                RowsCleared = job.RowsCleared,
                RowsUnknownUpn = job.RowsUnknownUpn,
                RowsInvalid = job.RowsInvalid,
                ErrorCode = job.ErrorCode,
                ErrorMessage = job.ErrorMessage,
            };
        }

        /// <summary>
        /// The status to show, which is not always the status stored.
        /// </summary>
        /// <remarks>
        /// A job whose worker died - almost always an App Service recycle - stays "Running" in the
        /// database until it is resumed, which on screen is indistinguishable from a very slow import.
        /// One that will be resumed is shown as waiting, which is what it is - and keeps the page
        /// polling it until a new worker picks it up. Only one that will not be resumed - interrupted
        /// too often, or too long ago - is shown as interrupted, until the next resume sweep records
        /// that it was stopped.
        /// </remarks>
        internal static string DescribeStatus(UserOrgImportJob job, DateTime utcNow)
        {
            if (UserOrgImportRunner.LooksInterrupted(job, utcNow))
            {
                return WillBeResumed(job, utcNow) ? "pending" : "interrupted";
            }

            switch (job.Status)
            {
                case UserOrgImportStatus.Pending: return "pending";
                case UserOrgImportStatus.Running: return "running";
                case UserOrgImportStatus.Succeeded: return "succeeded";
                case UserOrgImportStatus.Failed: return "failed";
                default: return "cancelled";
            }
        }

        /// <summary>Whether a stranded job is still within the limits the resume sweep hands back to a worker.</summary>
        private static bool WillBeResumed(UserOrgImportJob job, DateTime utcNow)
        {
            return job.Attempts < UserOrgImportJobLimits.MaxAttempts
                && utcNow - job.QueuedUtc < UserOrgImportJobLimits.ResumeWindow;
        }

        private static string Iso(DateTime value)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        }

        #endregion
    }

    /// <summary>
    /// Lets one resume sweep through per interval, per web process.
    /// </summary>
    public sealed class UserOrgResumeGate
    {
        /// <summary>The shortest gap between two sweeps from one process.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        /// <summary>The gate every request in this process shares.</summary>
        public static readonly UserOrgResumeGate Shared = new UserOrgResumeGate();

        private long _nextTicks;

        /// <summary>Whether a sweep may run now; if so, the next one is held back for <see cref="Interval"/>.</summary>
        public bool TryEnter(DateTime utcNow)
        {
            while (true)
            {
                var next = Interlocked.Read(ref _nextTicks);
                if (utcNow.Ticks < next)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _nextTicks, utcNow.Add(Interval).Ticks, next) == next)
                {
                    return true;
                }
            }
        }
    }
}
