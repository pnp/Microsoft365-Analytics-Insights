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
            await _types.UpdateAsync(type, cancellationToken).ConfigureAwait(false);

            // Repointing an Entra type at a different attribute invalidates every value it holds: they
            // were read from a property that is no longer the source of truth for this dimension.
            // Leaving them would show stale values indefinitely for any user who never changes again.
            if (existing.SourceKind == UserOrgSourceKind.EntraAttribute
                && type.SourceKind == UserOrgSourceKind.EntraAttribute
                && !string.Equals(existing.EntraAttributeName, type.EntraAttributeName, StringComparison.OrdinalIgnoreCase))
            {
                await _assignments.ClearAllForTypeAsync(id, cancellationToken).ConfigureAwait(false);
            }

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
        /// The parse check alone is not enough. A perfectly well-formed directory extension name can
        /// still name a property that does not exist in this tenant, and Graph answers that with a 400
        /// that fails the entire <c>/users/delta</c> request. Saving it would therefore break the user
        /// import until the importer's fallback noticed. The probe is what stops that reaching disk.
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

            type.EntraAttributeName = spec.Canonical;
            return type;
        }

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
        /// Parses the first few rows of an upload and says which of them match a real user.
        /// </summary>
        /// <remarks>
        /// Persists nothing. The point is to let an admin see that (say) their export used the wrong
        /// column or a stale UPN format <b>before</b> they run a Replace that would clear everybody.
        /// </remarks>
        public async Task<UserOrgCsvPreviewModel> PreviewAsync(Stream content, string fileName, CancellationToken cancellationToken)
        {
            // Read one more than shown so "there are more rows" is known without reading a 200,000-row
            // file into memory just to preview ten of them.
            var parsed = UserOrgCsvParser.Parse(content, PreviewRowCount + 1);

            var preview = new UserOrgCsvPreviewModel
            {
                FileName = fileName,
                Delimiter = DescribeDelimiter(parsed.Delimiter),
                HeaderDetected = parsed.HeaderDetected,
                UpnColumnName = parsed.UpnColumnName,
                OrgColumnName = parsed.OrgColumnName,
                MoreRowsExist = parsed.Rows.Count > PreviewRowCount || parsed.Truncated,
            };

            var shown = parsed.Rows.Take(PreviewRowCount).ToList();
            var existing = await _users
                .FindExistingUpnsAsync(shown.Select(r => r.Upn).ToList(), cancellationToken)
                .ConfigureAwait(false);
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

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
        public async Task<UserOrgImportQueuedModel> QueueImportAsync(
            int orgTypeId,
            UserOrgImportMode mode,
            Stream content,
            string fileName,
            string startedBy,
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
