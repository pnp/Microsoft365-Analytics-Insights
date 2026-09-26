using Newtonsoft.Json;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    // Every property here carries an explicit [JsonProperty] name. The Web project has no camelCase
    // contract resolver, so a property without one serialises in PascalCase and the SPA reads
    // undefined - a failure that shows up as a blank page rather than an error.

    /// <summary>One org type as the admin page sees it.</summary>
    public class UserOrgTypeModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>"entra" or "csv".</summary>
        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("entraAttributeName")]
        public string EntraAttributeName { get; set; }

        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonProperty("assignedUserCount")]
        public int AssignedUserCount { get; set; }

        [JsonProperty("distinctValueCount")]
        public int DistinctValueCount { get; set; }

        [JsonProperty("createdUtc")]
        public string CreatedUtc { get; set; }

        [JsonProperty("modifiedUtc")]
        public string ModifiedUtc { get; set; }

        /// <summary>
        /// When the type's values were last brought up to date from its source, or null if never. See
        /// <c>UserOrgType.LastRefreshedUtc</c> for what that means for each source.
        /// </summary>
        [JsonProperty("lastRefreshedUtc")]
        public string LastRefreshedUtc { get; set; }

        [JsonProperty("lastImport")]
        public UserOrgImportJobModel LastImport { get; set; }
    }

    /// <summary>A CSV import job, for progress polling and the audit trail.</summary>
    public class UserOrgImportJobModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("orgTypeId")]
        public int OrgTypeId { get; set; }

        /// <summary>"replace" or "merge".</summary>
        [JsonProperty("mode")]
        public string Mode { get; set; }

        /// <summary>"pending", "running", "succeeded", "failed", "cancelled" or "interrupted".</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("startedBy")]
        public string StartedBy { get; set; }

        [JsonProperty("queuedUtc")]
        public string QueuedUtc { get; set; }

        [JsonProperty("finishedUtc")]
        public string FinishedUtc { get; set; }

        [JsonProperty("rowsTotal")]
        public int RowsTotal { get; set; }

        /// <summary>Assignments created or changed. Re-importing an identical file reports zero.</summary>
        [JsonProperty("rowsApplied")]
        public int RowsApplied { get; set; }

        [JsonProperty("rowsCleared")]
        public int RowsCleared { get; set; }

        [JsonProperty("rowsUnknownUpn")]
        public int RowsUnknownUpn { get; set; }

        [JsonProperty("rowsInvalid")]
        public int RowsInvalid { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }
    }

    /// <summary>A create/update request from the admin page.</summary>
    public class UserOrgTypeSaveModel
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("entraAttributeName")]
        public string EntraAttributeName { get; set; }

        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>A request to resolve one attribute for one user.</summary>
    public class UserOrgTestRequestModel
    {
        /// <summary>
        /// The attribute to test. Accepted unsaved, so an admin can validate before committing - which
        /// is the whole point, since a bad attribute would otherwise break the next user import.
        /// </summary>
        [JsonProperty("entraAttributeName")]
        public string EntraAttributeName { get; set; }

        [JsonProperty("upn")]
        public string Upn { get; set; }
    }

    /// <summary>What a test resolved, shown raw and normalised so truncation and trimming are visible.</summary>
    public class UserOrgTestResultModel
    {
        [JsonProperty("succeeded")]
        public bool Succeeded { get; set; }

        [JsonProperty("upn")]
        public string Upn { get; set; }

        /// <summary>The canonical attribute name, which may differ from what was typed.</summary>
        [JsonProperty("attributeName")]
        public string AttributeName { get; set; }

        /// <summary>The Graph property that was actually requested in $select.</summary>
        [JsonProperty("graphProperty")]
        public string GraphProperty { get; set; }

        /// <summary>The value exactly as Graph returned it, or null when the user has none.</summary>
        [JsonProperty("rawValue")]
        public string RawValue { get; set; }

        /// <summary>What would be stored: trimmed, and capped at the column width.</summary>
        [JsonProperty("normalisedValue")]
        public string NormalisedValue { get; set; }

        [JsonProperty("wouldTruncate")]
        public bool WouldTruncate { get; set; }

        /// <summary>True when the user exists but simply has no value for this attribute.</summary>
        [JsonProperty("hasNoValue")]
        public bool HasNoValue { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    /// <summary>The first few parsed rows of an upload, so an admin can sanity-check it.</summary>
    public class UserOrgCsvPreviewModel
    {
        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("delimiter")]
        public string Delimiter { get; set; }

        [JsonProperty("headerDetected")]
        public bool HeaderDetected { get; set; }

        [JsonProperty("upnColumnName")]
        public string UpnColumnName { get; set; }

        [JsonProperty("orgColumnName")]
        public string OrgColumnName { get; set; }

        [JsonProperty("rows")]
        public List<UserOrgCsvPreviewRowModel> Rows { get; set; } = new List<UserOrgCsvPreviewRowModel>();

        [JsonProperty("problems")]
        public List<UserOrgCsvProblemModel> Problems { get; set; } = new List<UserOrgCsvProblemModel>();

        /// <summary>Whether more rows exist beyond the sample shown.</summary>
        [JsonProperty("moreRowsExist")]
        public bool MoreRowsExist { get; set; }

        /// <summary>Usable rows in the whole file, not just the sample.</summary>
        [JsonProperty("totalRows")]
        public int TotalRows { get; set; }

        /// <summary>Rows in the whole file whose user principal name matches nobody.</summary>
        [JsonProperty("unknownUpnCount")]
        public int UnknownUpnCount { get; set; }

        /// <summary>
        /// How many users could lose their value if this file were imported with Replace.
        /// </summary>
        /// <remarks>
        /// The number that actually matters before a destructive import, and it cannot be judged from a
        /// ten-row sample. A complete, correctly formatted file that happens to cover half the tenant
        /// is a successful wipe of the other half, and nothing about the first ten rows would hint at
        /// it.
        /// </remarks>
        [JsonProperty("wouldClearCount")]
        public int WouldClearCount { get; set; }

        /// <summary>How many users hold a value for this org type today.</summary>
        [JsonProperty("currentlyAssignedCount")]
        public int CurrentlyAssignedCount { get; set; }

        /// <summary>How many existing users the file gives a value to.</summary>
        [JsonProperty("matchedUserCount")]
        public int MatchedUserCount { get; set; }

        /// <summary>
        /// How many rows carry an organisation name too long for the column, which will be stored
        /// shortened.
        /// </summary>
        [JsonProperty("truncatedValueCount")]
        public int TruncatedValueCount { get; set; }

        /// <summary>
        /// The longest organisation name that is stored in full. Sent rather than written into the
        /// portal's text, so the warning about shortened names cannot drift from the real limit.
        /// </summary>
        [JsonProperty("maxValueLength")]
        public int MaxValueLength { get; set; }
    }

    public class UserOrgCsvPreviewRowModel
    {
        [JsonProperty("lineNumber")]
        public int LineNumber { get; set; }

        [JsonProperty("upn")]
        public string Upn { get; set; }

        [JsonProperty("orgValue")]
        public string OrgValue { get; set; }

        /// <summary>Whether this UPN matches a user already in the database.</summary>
        [JsonProperty("userExists")]
        public bool UserExists { get; set; }

        /// <summary>True when the row will clear the user's value rather than set one.</summary>
        [JsonProperty("clearsValue")]
        public bool ClearsValue { get; set; }
    }

    public class UserOrgCsvProblemModel
    {
        [JsonProperty("lineNumber")]
        public int LineNumber { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    /// <summary>The outcome of queueing an import.</summary>
    public class UserOrgImportQueuedModel
    {
        [JsonProperty("jobId")]
        public int JobId { get; set; }

        [JsonProperty("rowsQueued")]
        public int RowsQueued { get; set; }

        [JsonProperty("rowsInvalid")]
        public int RowsInvalid { get; set; }
    }

    /// <summary>A directory extension discovered in the tenant.</summary>
    public class UserOrgDiscoveredAttributeModel
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("dataType")]
        public string DataType { get; set; }

        [JsonProperty("isSyncedFromOnPremises")]
        public bool IsSyncedFromOnPremises { get; set; }
    }

    /// <summary>What attribute discovery found, with an honest account of its limits.</summary>
    public class UserOrgAttributeCatalogueModel
    {
        /// <summary>The fifteen on-premises extension attribute slots - always available.</summary>
        [JsonProperty("extensionAttributes")]
        public List<string> ExtensionAttributes { get; set; } = new List<string>();

        /// <summary>Built-in properties offered as org sources.</summary>
        [JsonProperty("builtInProperties")]
        public List<string> BuiltInProperties { get; set; } = new List<string>();

        /// <summary>The employeeOrgData sub-properties.</summary>
        [JsonProperty("employeeOrgDataProperties")]
        public List<string> EmployeeOrgDataProperties { get; set; } = new List<string>();

        /// <summary>Directory extensions found in the tenant. Best-effort; see <see cref="DiscoveryWarning"/>.</summary>
        [JsonProperty("directoryExtensions")]
        public List<UserOrgDiscoveredAttributeModel> DirectoryExtensions { get; set; } = new List<UserOrgDiscoveredAttributeModel>();

        /// <summary>
        /// Why the directory extension list may be incomplete or empty. Surfaced rather than hidden,
        /// because Microsoft documents the discovery API as returning nothing at all on tenants with
        /// more than 1,000 service principals - and an empty list would otherwise read as
        /// "this tenant has no directory extensions".
        /// </summary>
        [JsonProperty("discoveryWarning")]
        public string DiscoveryWarning { get; set; }
    }

    /// <summary>One page of an org type's organisations, largest first, for "who is in each organisation".</summary>
    public class UserOrgValuePageModel
    {
        [JsonProperty("orgTypeId")]
        public int OrgTypeId { get; set; }

        /// <summary>The page actually returned, after clamping, 1-based.</summary>
        [JsonProperty("page")]
        public int Page { get; set; }

        /// <summary>The page size actually used, after clamping.</summary>
        [JsonProperty("pageSize")]
        public int PageSize { get; set; }

        /// <summary>Organisations matching the search, across every page.</summary>
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("items")]
        public List<UserOrgValueRowModel> Items { get; set; } = new List<UserOrgValueRowModel>();
    }

    /// <summary>One organisation and how many users are in it.</summary>
    public class UserOrgValueRowModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>The organisation exactly as stored - tenant data, never translated.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("memberCount")]
        public int MemberCount { get; set; }
    }

    /// <summary>One page of the users in an organisation, by user principal name.</summary>
    public class UserOrgMemberPageModel
    {
        [JsonProperty("orgTypeId")]
        public int OrgTypeId { get; set; }

        [JsonProperty("valueId")]
        public int ValueId { get; set; }

        /// <summary>The organisation's name exactly as stored - tenant data, never translated.</summary>
        [JsonProperty("valueName")]
        public string ValueName { get; set; }

        /// <summary>The page actually returned, after clamping, 1-based.</summary>
        [JsonProperty("page")]
        public int Page { get; set; }

        /// <summary>The page size actually used, after clamping.</summary>
        [JsonProperty("pageSize")]
        public int PageSize { get; set; }

        /// <summary>Users matching the search, across every page.</summary>
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("items")]
        public List<UserOrgMemberModel> Items { get; set; } = new List<UserOrgMemberModel>();
    }

    /// <summary>One user in an organisation. Every field is tenant data, shown as stored.</summary>
    public class UserOrgMemberModel
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        /// <summary><c>false</c> for a disabled account, <c>null</c> when the import has not recorded it.</summary>
        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }
    }
}
