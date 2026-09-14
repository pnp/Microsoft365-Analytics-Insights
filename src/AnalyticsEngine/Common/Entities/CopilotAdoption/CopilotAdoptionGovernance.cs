using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Runtime governance controls for Copilot Adoption individual-level data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is deliberately no role separation here.</b> Every authenticated portal user who can
    /// reach Copilot Adoption sees the same thing, including the per-user lists and every export. That
    /// matches the rest of the product - no controller in the Web project carries
    /// <c>[Authorize(Roles = ...)]</c> - and introducing a role gate for this one surface would have made
    /// it behave unlike every other page while giving an incomplete impression of protection. Role
    /// separation is tracked as its own piece of work (#538) rather than bolted onto this feature.
    /// </para>
    /// <para>
    /// What is here instead are two deployment-wide switches and an audit trail, none of which treat one
    /// user differently from another, and all of which are OFF by default so a deployment shows
    /// everything to everyone unless an administrator decides otherwise:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="DisableIndividualData"/> - turn the individual layer off entirely, leaving the
    /// aggregate dashboard. For tenants whose legal position is simply "no individual analytics".</item>
    /// <item><see cref="PseudonymiseIndividualData"/> - replace direct identifiers with stable
    /// surrogates while keeping the cohort columns.</item>
    /// </list>
    /// <para>
    /// These are App Service settings rather than installer JSON properties, which is why
    /// <c>CONFIG_VERSION</c> is untouched by them.
    /// </para>
    /// </remarks>
    public sealed class CopilotAdoptionGovernanceSettings
    {
        public const string DisableIndividualDataSettingName = "CopilotAdoptionDisableIndividualData";
        public const string PseudonymiseIndividualDataSettingName = "CopilotAdoptionPseudonymiseIndividualData";

        /// <summary>Off-switch for the whole individual layer. Deployment-wide, default off.</summary>
        public bool DisableIndividualData { get; set; }

        /// <summary>
        /// Replace direct identifiers with stable surrogates. Deployment-wide, default OFF: everyone who
        /// can reach the page sees the same named data, which is the product's current position.
        /// </summary>
        public bool PseudonymiseIndividualData { get; set; }

        public Guid TenantId { get; set; }

        /// <summary>
        /// Whether per-user lists and exports are available. Deliberately not a function of who is
        /// asking - see the class remarks. The parameter is kept so the call sites do not have to change
        /// if per-user access control is added later.
        /// </summary>
        public bool HasIndividualDataAccess(IPrincipal principal)
        {
            return !DisableIndividualData;
        }

        public string IndividualDataDeniedMessage(IPrincipal principal)
        {
            return "Copilot Adoption individual-user lists and exports are turned off for this deployment "
                   + "(" + DisableIndividualDataSettingName + "). The aggregate dashboard remains available.";
        }
    }

    /// <summary>
    /// Redacts direct identifiers in the per-user adoption lists while preserving stable row identity
    /// and the cohort columns administrators need for aggregate action planning.
    /// </summary>
    public static class CopilotAdoptionPseudonymiser
    {
        public static List<LicensedUserAdoptionRow> Pseudonymise(
            IEnumerable<LicensedUserAdoptionRow> rows, CopilotAdoptionGovernanceSettings settings)
        {
            // One hash instance for the whole batch. A new SHA256 per row is a disposable object and a
            // native handle each time, and this runs over the entire scored population on every request
            // that is not served from the result cache - up to MaxLicensedUsersScored rows, before any
            // filtering or paging.
            using (var sha = SHA256.Create())
            {
                return (rows ?? Enumerable.Empty<LicensedUserAdoptionRow>())
                    .Select(r => Pseudonymise(r, settings, sha))
                    .ToList();
            }
        }

        public static List<LicenceOpportunityRow> Pseudonymise(
            IEnumerable<LicenceOpportunityRow> rows, CopilotAdoptionGovernanceSettings settings)
        {
            using (var sha = SHA256.Create())
            {
                return (rows ?? Enumerable.Empty<LicenceOpportunityRow>())
                    .Select(r => Pseudonymise(r, settings, sha))
                    .ToList();
            }
        }

        public static LicensedUserAdoptionRow Pseudonymise(
            LicensedUserAdoptionRow r, CopilotAdoptionGovernanceSettings settings)
        {
            using (var sha = SHA256.Create())
            {
                return Pseudonymise(r, settings, sha);
            }
        }

        private static LicensedUserAdoptionRow Pseudonymise(
            LicensedUserAdoptionRow r, CopilotAdoptionGovernanceSettings settings, SHA256 sha)
        {
            if (r == null) return null;

            // Copy first, then redact - NOT the other way round.
            //
            // This used to build a fresh row and copy an allow-list of properties across. That is the
            // wrong default for a redaction step that is ON unless an administrator turns it off: any
            // column added to the row afterwards is silently dropped for almost every deployment, with
            // nothing failing to say so. Merging the reclaim-eligibility and licence-qualification work
            // into this branch added fifteen such columns at once, and every one of them would have
            // arrived blank on screen and in the CSV.
            //
            // Cloning inverts the failure mode: a new column is carried through unless somebody
            // deliberately adds it to the redaction list below. CopilotAdoptionTests reflects over the
            // row type and fails if a property is neither carried nor knowingly redacted, so the
            // decision cannot be skipped by accident.
            var copy = r.ShallowCopy();

            copy.UserPrincipalName = Surrogate(r.UserId, r.UserPrincipalName, settings, sha);
            copy.Mail = null;
            copy.JobTitle = null;
            copy.OfficeLocation = null;
            copy.CompanyName = null;
            copy.ManagerUserPrincipalName = null;
            // The administrator who recorded a reclaim exclusion is a named person too, and the note is
            // free text a reviewer wrote about why - "on parental leave until March", "shared mailbox
            // for the Contoso helpdesk" - which is exactly the kind of detail that re-identifies the
            // subject and often says something about their circumstances. The reason code, the dates
            // and the tier are what an operator needs on a pseudonymised page; these two are not, and
            // an export that still carries them is not pseudonymised.
            copy.ReclaimExcludedBy = null;
            copy.ReclaimExclusionNote = null;
            return copy;
        }

        /// <summary>
        /// Clone then redact - see the note on the licensed-user overload above.
        /// </summary>
        public static LicenceOpportunityRow Pseudonymise(
            LicenceOpportunityRow r, CopilotAdoptionGovernanceSettings settings)
        {
            using (var sha = SHA256.Create())
            {
                return Pseudonymise(r, settings, sha);
            }
        }

        private static LicenceOpportunityRow Pseudonymise(
            LicenceOpportunityRow r, CopilotAdoptionGovernanceSettings settings, SHA256 sha)
        {
            if (r == null) return null;

            // Clone then redact - see the note on the licensed-user overload above.
            var copy = r.ShallowCopy();

            copy.UserPrincipalName = Surrogate(r.UserId, r.UserPrincipalName, settings, sha);
            copy.Mail = null;
            copy.JobTitle = null;
            copy.OfficeLocation = null;
            copy.CompanyName = null;
            copy.ManagerUserPrincipalName = null;
            return copy;
        }

        /// <summary>
        /// Direct identifiers this redaction removes. Everything else on a row is carried through.
        /// Exposed so a test can assert the two lists together account for every property on the row,
        /// which is what stops a newly added column being silently dropped or silently leaked.
        /// </summary>
        public static readonly string[] RedactedPropertyNames =
        {
            nameof(LicensedUserAdoptionRow.UserPrincipalName),
            nameof(LicensedUserAdoptionRow.Mail),
            nameof(LicensedUserAdoptionRow.JobTitle),
            nameof(LicensedUserAdoptionRow.OfficeLocation),
            nameof(LicensedUserAdoptionRow.CompanyName),
            nameof(LicensedUserAdoptionRow.ManagerUserPrincipalName),
            nameof(LicensedUserAdoptionRow.ReclaimExcludedBy),
            nameof(LicensedUserAdoptionRow.ReclaimExclusionNote),
        };

        private static string Surrogate(
            int userId, string userPrincipalName, CopilotAdoptionGovernanceSettings settings, SHA256 sha)
        {
            var input = string.Format(
                "{0}|{1}|{2}",
                settings?.TenantId.ToString("D") ?? Guid.Empty.ToString("D"),
                userId,
                userPrincipalName ?? string.Empty);

            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(12);
            for (var i = 0; i < 6; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }

            return "User " + sb;
        }
    }

    public sealed class CopilotAdoptionExportAuditRecord
    {
        public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
        public string Actor { get; set; }
        public string Endpoint { get; set; }
        public string Parameters { get; set; }
        public int? WindowDays { get; set; }
        public string OptionsJson { get; set; }
        public int? RowCount { get; set; }
        public bool Truncated { get; set; }
        public bool Succeeded { get; set; }
        public int StatusCode { get; set; }
        public string FailureReason { get; set; }
        public bool Pseudonymised { get; set; }
        public bool IndividualDataDisabled { get; set; }
    }

    public interface ICopilotAdoptionExportAuditSink
    {
        bool Write(CopilotAdoptionExportAuditRecord record);
    }

    /// <summary>
    /// Writes one audit row for each Copilot Adoption export attempt.
    /// </summary>
    /// <remarks>
    /// Export auditing deliberately avoids an EF entity because adding one without regenerating the EF6
    /// model snapshot would make the runtime auto-migration compare the current model against a stale
    /// EDMX and can throw AutomaticDataLossException before a page loads. The migration creates the
    /// table with raw SQL and this writer uses a parameterised command against it.
    /// </remarks>
    public sealed class SqlCopilotAdoptionExportAuditSink : ICopilotAdoptionExportAuditSink
    {
        public bool Write(CopilotAdoptionExportAuditRecord record)
        {
            if (record == null) return false;

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var connection = db.Database.Connection;
                    if (connection.State != ConnectionState.Open)
                    {
                        connection.Open();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO dbo.copilot_adoption_export_audit
    (occurred_utc, actor, endpoint, parameters, window_days, options_json, row_count,
     truncated, succeeded, status_code, failure_reason, pseudonymised, individual_data_disabled)
VALUES
    (@occurred_utc, @actor, @endpoint, @parameters, @window_days, @options_json, @row_count,
     @truncated, @succeeded, @status_code, @failure_reason, @pseudonymised, @individual_data_disabled);";

                        Add(command, "@occurred_utc", record.OccurredUtc);
                        Add(command, "@actor", record.Actor);
                        Add(command, "@endpoint", record.Endpoint);
                        Add(command, "@parameters", record.Parameters);
                        Add(command, "@window_days", record.WindowDays);
                        Add(command, "@options_json", record.OptionsJson);
                        Add(command, "@row_count", record.RowCount);
                        Add(command, "@truncated", record.Truncated);
                        Add(command, "@succeeded", record.Succeeded);
                        Add(command, "@status_code", record.StatusCode);
                        Add(command, "@failure_reason", record.FailureReason);
                        Add(command, "@pseudonymised", record.Pseudonymised);
                        Add(command, "@individual_data_disabled", record.IndividualDataDisabled);

                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void Add(IDbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
