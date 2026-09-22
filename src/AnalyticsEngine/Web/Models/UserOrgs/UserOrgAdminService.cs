using Common.Entities.UserOrgs;
using System;
using System.Collections.Generic;
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

        private readonly IUserOrgTypeStore _types;
        private readonly IUserOrgAssignmentStore _assignments;
        private readonly IUserOrgImportJobStore _jobs;
        private readonly IUserOrgUserLookup _users;
        private readonly IUserOrgGraphProbe _probe;
        private readonly Action<int> _dispatchImport;
        private readonly Func<DateTime> _utcNow;

        public UserOrgAdminService(
            IUserOrgTypeStore types,
            IUserOrgAssignmentStore assignments,
            IUserOrgImportJobStore jobs,
            IUserOrgUserLookup users,
            IUserOrgGraphProbe probe,
            Action<int> dispatchImport,
            Func<DateTime> utcNow = null)
        {
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _dispatchImport = dispatchImport ?? throw new ArgumentNullException(nameof(dispatchImport));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        #region Org types

        public async Task<List<UserOrgTypeModel>> ListAsync(CancellationToken cancellationToken)
        {
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

            await _types.UpdateAsync(type, sourceChanged, cancellationToken).ConfigureAwait(false);

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

            await ProveAttributeIsReadableAsync(spec, cancellationToken).ConfigureAwait(false);

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
        /// Parses an upload and reports both a readable sample and the blast radius of importing it.
        /// </summary>
        /// <remarks>
        /// The whole file is parsed and every user principal name in it resolved, not just the ten rows
        /// shown. That is the point: a ten-row sample cannot tell an administrator that a complete,
        /// correctly formatted export happens to cover only half the tenant - and with Replace, the
        /// half it omits loses its values. Persists nothing.
        /// </remarks>
        public async Task<UserOrgCsvPreviewModel> PreviewAsync(
            Stream content,
            string fileName,
            int orgTypeId,
            CancellationToken cancellationToken)
        {
            var parsed = UserOrgCsvParser.Parse(content);

            var preview = new UserOrgCsvPreviewModel
            {
                FileName = fileName,
                Delimiter = DescribeDelimiter(parsed.Delimiter),
                HeaderDetected = parsed.HeaderDetected,
                UpnColumnName = parsed.UpnColumnName,
                OrgColumnName = parsed.OrgColumnName,
                MoreRowsExist = parsed.Rows.Count > PreviewRowCount,
                TotalRows = parsed.Rows.Count,
            };

            if (parsed.UnterminatedQuote)
            {
                preview.Problems = new List<UserOrgCsvProblemModel>
                {
                    new UserOrgCsvProblemModel
                    {
                        LineNumber = 0,
                        Reason = "the file has a quotation mark that is never closed, so it cannot be read as rows",
                    },
                };
                preview.Rows = new List<UserOrgCsvPreviewRowModel>();
                return preview;
            }

            var existing = await _users
                .FindExistingUpnsAsync(parsed.Rows.Select(r => r.Upn).ToList(), cancellationToken)
                .ConfigureAwait(false);
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

            var matchedWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in parsed.Rows)
            {
                if (!existingSet.Contains(row.Upn))
                {
                    unknown.Add(row.Upn);
                }
                else if (row.OrgValue != null)
                {
                    matchedWithValue.Add(row.Upn);
                }
                else
                {
                    // A later blank line for the same person is a deliberate clear, so they stop
                    // counting as "kept".
                    matchedWithValue.Remove(row.Upn);
                }
            }

            preview.UnknownUpnCount = unknown.Count;

            var summaries = await _types.GetSummariesAsync(cancellationToken).ConfigureAwait(false);
            var summary = summaries.FirstOrDefault(s => s.Type != null && s.Type.Id == orgTypeId);
            preview.CurrentlyAssignedCount = summary == null ? 0 : summary.AssignedUserCount;

            preview.WouldClearCount = await CountWouldClearAsync(orgTypeId, parsed.Rows, cancellationToken)
                .ConfigureAwait(false);
            preview.MatchedUserCount = matchedWithValue.Count;
            preview.TruncatedValueCount = parsed.TruncatedValueCount;

            var shown = parsed.Rows.Take(PreviewRowCount).ToList();
            preview.Rows = shown.Select(r => new UserOrgCsvPreviewRowModel
            {
                LineNumber = r.LineNumber,
                Upn = r.Upn,
                OrgValue = r.OrgValue,
                UserExists = existingSet.Contains(r.Upn),
                ClearsValue = r.OrgValue == null,
            }).ToList();

            preview.Problems = parsed.Problems
                .Take(PreviewProblemCount)
                .Select(p => new UserOrgCsvProblemModel { LineNumber = p.LineNumber, Reason = p.Reason })
                .ToList();

            return preview;
        }

        /// <summary>
        /// Parses an upload in full, stages it and queues the background import.
        /// </summary>
        /// <param name="confirmClear">
        /// Whether the administrator has acknowledged how many users a Replace will clear.
        /// </param>
        public async Task<UserOrgImportQueuedModel> QueueImportAsync(
            int orgTypeId,
            UserOrgImportMode mode,
            Stream content,
            string fileName,
            string startedBy,
            bool confirmClear,
            CancellationToken cancellationToken)
        {
            var type = await _types.GetAsync(orgTypeId, cancellationToken).ConfigureAwait(false);
            if (type == null)
            {
                throw new UserOrgValidationException("That organisation type no longer exists.");
            }

            if (type.SourceKind != UserOrgSourceKind.CsvUpload)
            {
                // Importing a file into an Entra-sourced type would be overwritten by the next user
                // import anyway, so it is refused rather than silently wasted.
                throw new UserOrgValidationException(
                    $"'{type.Name}' takes its values from an Entra attribute, so a file cannot be imported into it. "
                    + "Change its source to CSV upload first.");
            }

            var active = await _jobs.GetActiveJobForTypeAsync(orgTypeId, cancellationToken).ConfigureAwait(false);
            if (active != null && !UserOrgImportRunner.LooksInterrupted(active, _utcNow()))
            {
                throw new UserOrgValidationException(
                    $"An import for '{type.Name}' is already in progress. Wait for it to finish before starting another.");
            }

            var parsed = UserOrgCsvParser.Parse(content);

            if (parsed.UnterminatedQuote)
            {
                // Refused outright rather than imported partially. Everything after the stray quote was
                // swallowed into one value, so those users look absent - and in Replace mode absent
                // means their value is cleared. Importing a file we cannot read is the one outcome
                // worse than not importing it.
                throw new UserOrgValidationException(
                    "That file has a quotation mark that is never closed, so the rest of it could not be read as "
                    + "rows. Fix the quoting and upload it again.");
            }

            if (parsed.Truncated)
            {
                throw new UserOrgValidationException(
                    $"That file has more than {UserOrgCsvParser.MaxDataLines:N0} rows, which is more than this import supports. "
                    + "Split it and import the parts separately.");
            }

            if (parsed.Rows.Count == 0)
            {
                throw new UserOrgValidationException(
                    parsed.Problems.Count > 0
                        ? "No usable rows were found in that file. Check the first reported problem and the column names."
                        : "That file contains no rows.");
            }

            if (mode == UserOrgImportMode.Replace)
            {
                // Recomputed here from the file actually being imported and the assignments as they
                // are right now, rather than trusting the preview. The preview is a separate request
                // that may be minutes old, and the endpoint is reachable directly - so a gate that
                // lives only in the browser is not a gate at all. This also catches the case where
                // somebody else changed the assignments between the preview and the import.
                var wouldClear = await CountWouldClearAsync(orgTypeId, parsed.Rows, cancellationToken)
                    .ConfigureAwait(false);

                if (wouldClear > 0 && !confirmClear)
                {
                    throw new UserOrgValidationException(
                        $"This import would clear the {type.Name} value of {wouldClear:N0} user(s) that the file does "
                        + "not give a value to. Review the preview and confirm before continuing, or use Merge to "
                        + "leave them as they are.");
                }
            }

            var jobId = await _jobs.CreateJobWithRowsAsync(
                new UserOrgImportJob
                {
                    OrgTypeId = orgTypeId,
                    Mode = mode,
                    FileName = fileName,
                    StartedBy = startedBy,
                    RowsInvalid = parsed.Problems.Count,
                },
                parsed.Rows,
                cancellationToken).ConfigureAwait(false);

            // Hand off to the background worker. The dispatcher is injected so this whole method can be
            // tested without starting a thread.
            _dispatchImport(jobId);

            return new UserOrgImportQueuedModel
            {
                JobId = jobId,
                RowsQueued = parsed.Rows.Count,
                RowsInvalid = parsed.Problems.Count,
            };
        }

        public async Task<UserOrgImportJobModel> GetJobAsync(int jobId, CancellationToken cancellationToken)
        {
            var job = await _jobs.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            return job == null ? null : ToModel(job, _utcNow());
        }

        /// <summary>
        /// How many users would lose their value for this org type if the parsed file were imported
        /// with Replace.
        /// </summary>
        /// <remarks>
        /// One implementation, used by both the preview and the import, so what an administrator is
        /// shown and what the import enforces cannot disagree.
        ///
        /// The count is the users currently assigned <b>minus</b> those the file both covers and gives
        /// a value to. Subtracting "users the file covers" instead would be a different question with
        /// a dangerously wrong answer: a file covering a large population that barely overlaps the
        /// assigned one subtracts to zero, suppressing the warning at the exact moment the import is
        /// about to wipe everybody.
        /// </remarks>
        private async Task<int> CountWouldClearAsync(
            int orgTypeId,
            IReadOnlyList<UserOrgStagedRow> rows,
            CancellationToken cancellationToken)
        {
            var summaries = await _types.GetSummariesAsync(cancellationToken).ConfigureAwait(false);
            var summary = summaries.FirstOrDefault(s => s.Type != null && s.Type.Id == orgTypeId);
            var assigned = summary == null ? 0 : summary.AssignedUserCount;

            if (assigned == 0)
            {
                return 0;
            }

            // Last line wins, matching the merge: a later blank line for the same person is a clear,
            // so they stop counting as kept.
            var keptUpns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                keptUpns[row.Upn] = row.OrgValue != null;
            }

            var covered = keptUpns.Where(p => p.Value).Select(p => p.Key).ToList();
            if (covered.Count == 0)
            {
                return assigned;
            }

            var kept = await _users
                .FindAssignedUpnsAsync(orgTypeId, covered, cancellationToken)
                .ConfigureAwait(false);

            return Math.Max(0, assigned - kept.Count);
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
                FinishedUtc = job.FinishedUtc.HasValue ? Iso(job.FinishedUtc.Value) : null,
                RowsTotal = job.RowsTotal,
                RowsApplied = job.RowsApplied,
                RowsCleared = job.RowsCleared,
                RowsUnknownUpn = job.RowsUnknownUpn,
                RowsInvalid = job.RowsInvalid,
                ErrorMessage = job.ErrorMessage,
            };
        }

        /// <summary>
        /// The status to show, which is not always the status stored.
        /// </summary>
        /// <remarks>
        /// A job whose worker died - almost always an App Service recycle - stays "Running" in the
        /// database forever, which on screen is indistinguishable from a very slow import. Reporting it
        /// as interrupted is the difference between an admin waiting indefinitely and an admin
        /// re-uploading.
        /// </remarks>
        internal static string DescribeStatus(UserOrgImportJob job, DateTime utcNow)
        {
            if (UserOrgImportRunner.LooksInterrupted(job, utcNow))
            {
                return "interrupted";
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

        private static string Iso(DateTime value)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
