using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Holds entity-reference dictionaries for each lookup type used by the bulk user update.
    /// Entity IDs are valid after the context has been saved.
    /// </summary>
    /// <remarks>
    /// Moved out of <c>UserBatchProcessor</c> (where it was a nested type) so
    /// <see cref="UserBulkUpdateRules"/> can be used without a database context. Behaviour and
    /// contents are unchanged.
    /// </remarks>
    internal class LookupEntityMaps
    {
        public Dictionary<string, UserDepartment> Departments = new Dictionary<string, UserDepartment>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, UserJobTitle> JobTitles = new Dictionary<string, UserJobTitle>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, UserOfficeLocation> OfficeLocations = new Dictionary<string, UserOfficeLocation>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, UserUsageLocation> UsageLocations = new Dictionary<string, UserUsageLocation>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CountryOrRegion> Countries = new Dictionary<string, CountryOrRegion>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, StateOrProvince> StatesOrProvinces = new Dictionary<string, StateOrProvince>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CompanyName> CompanyNames = new Dictionary<string, CompanyName>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pure rules behind the bulk existing-user update: which <c>dbo.users</c> rows go into the
    /// batch, what value each column gets, and how a manager's foreign key is resolved.
    ///
    /// Extracted from <c>UserBatchProcessor.BuildUpdateDataTable</c> for issues #371 / #381. It was
    /// previously interleaved with <c>SqlConnection</c> / <c>SqlBulkCopy</c>, so none of it could be
    /// asserted without a live SQL Server - and the manager precedence chain in particular had no
    /// test at all. Everything here runs with zero SQL Server and zero Graph dependency.
    /// </summary>
    internal static class UserBulkUpdateRules
    {
        /// <summary>
        /// The columns of the bulk-update batch, in order. <see cref="SqlUserBulkUpdateWriter"/>
        /// generates its <c>SqlBulkCopy</c> column mappings from this list; the <see cref="DataTable"/>
        /// built here and the <c>#user_updates</c> temp table are still spelled out separately, and are
        /// pinned against it by <c>UserBulkUpdateRulesTests</c>. Before the extraction the same fourteen
        /// names appeared in three independently maintained places with nothing checking they agreed.
        /// </summary>
        public static readonly IReadOnlyList<string> UpdateTableColumns = new[]
        {
            "id",
            "azure_ad_id",
            "account_enabled",
            "mail",
            "postalcode",
            "department_id",
            "job_title_id",
            "office_location_id",
            "usage_location_id",
            "country_or_region_id",
            "state_or_province_id",
            "company_name_id",
            "manager_id",
            "last_updated",
        };

        /// <summary>Creates the empty batch table with the column names and CLR types the writer expects.</summary>
        public static DataTable CreateUpdateTable()
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Columns.Add("azure_ad_id", typeof(string));
            dt.Columns.Add("account_enabled", typeof(bool));
            dt.Columns.Add("mail", typeof(string));
            dt.Columns.Add("postalcode", typeof(string));
            dt.Columns.Add("department_id", typeof(int));
            dt.Columns.Add("job_title_id", typeof(int));
            dt.Columns.Add("office_location_id", typeof(int));
            dt.Columns.Add("usage_location_id", typeof(int));
            dt.Columns.Add("country_or_region_id", typeof(int));
            dt.Columns.Add("state_or_province_id", typeof(int));
            dt.Columns.Add("company_name_id", typeof(int));
            dt.Columns.Add("manager_id", typeof(int));
            dt.Columns.Add("last_updated", typeof(DateTime));
            return dt;
        }

        /// <summary>
        /// Builds one bulk-update batch.
        /// </summary>
        /// <param name="lastUpdated">
        /// The value stamped into <c>users.last_updated</c> for every row. Supplied by the caller,
        /// which still reads <c>DateTime.Now</c> - note that is local time, not UTC. #371 suggests
        /// moving it behind <c>IClock</c>, but <c>IClock</c> only exposes <c>UtcNow</c>, so doing so
        /// would change every stored value on a non-UTC host. That is a behavioural change and is
        /// deliberately out of scope here, exactly as it was for the mapping rules in #371 part 1.
        /// </param>
        /// <remarks>
        /// A Graph user with no UPN, or whose UPN has no saved <c>dbo.users</c> row, is skipped: the
        /// update joins on the primary key, so there is nothing to update.
        ///
        /// No null guards - the code this replaced dereferenced these arguments directly, and the
        /// resulting <see cref="NullReferenceException"/> is operator-facing telemetry.
        /// </remarks>
        public static DataTable BuildUpdateTable(
            List<GraphUser> graphUsers,
            LookupEntityMaps maps,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, GraphUser> graphUsersByAadId,
            DateTime lastUpdated)
        {
            var dt = CreateUpdateTable();

            foreach (var graphUser in graphUsers)
            {
                var upn = graphUser.UserPrincipalName;
                if (string.IsNullOrEmpty(upn))
                    continue;

                // Find the DB user to get the PK
                if (!dbUsersByUpn.TryGetValue(upn, out var dbUser) || dbUser.ID == 0)
                    continue;

                // Same mapping rule the EF path uses, so the two write paths cannot drift (#371).
                var plan = UserMetadataMappingRules.BuildPlan(graphUser);

                var row = dt.NewRow();
                row["id"] = dbUser.ID;
                row["azure_ad_id"] = (object)plan.AzureAdId ?? DBNull.Value;
                row["account_enabled"] = plan.AccountEnabled.HasValue ? (object)plan.AccountEnabled.Value : DBNull.Value;
                row["mail"] = (object)plan.Mail ?? DBNull.Value;
                row["postalcode"] = (object)plan.PostalCode ?? DBNull.Value;
                row["department_id"] = ResolveLookupId(plan.DepartmentName, maps.Departments);
                row["job_title_id"] = ResolveLookupId(plan.JobTitleName, maps.JobTitles);
                row["office_location_id"] = ResolveLookupId(plan.OfficeLocationName, maps.OfficeLocations);
                row["usage_location_id"] = ResolveLookupId(plan.UsageLocationName, maps.UsageLocations);
                row["country_or_region_id"] = ResolveLookupId(plan.CountryName, maps.Countries);
                row["state_or_province_id"] = ResolveLookupId(plan.StateOrProvinceName, maps.StatesOrProvinces);
                row["company_name_id"] = ResolveLookupId(plan.CompanyName, maps.CompanyNames);
                row["manager_id"] = ResolveManagerId(plan.ManagerAadId, dbUsersByAadId, dbUsersByUpn, graphUsersByAadId);
                row["last_updated"] = lastUpdated;

                dt.Rows.Add(row);
            }

            return dt;
        }

        /// <summary>
        /// Turns an already-normalised lookup name into the foreign key to store, or
        /// <see cref="DBNull"/> when the value is absent or the lookup row has not been saved yet
        /// (<c>ID == 0</c>), which clears the column.
        /// </summary>
        public static object ResolveLookupId<T>(string normalisedName, Dictionary<string, T> map) where T : AbstractEFEntityWithName
        {
            if (!string.IsNullOrEmpty(normalisedName) && map.TryGetValue(normalisedName, out var entity) && entity.ID > 0)
                return entity.ID;
            return DBNull.Value;
        }

        /// <summary>
        /// The manager foreign-key precedence chain for the bulk path, in order:
        /// <list type="number">
        /// <item>the manager's Entra id is already in the DB-users-by-AAD-id map;</item>
        /// <item>otherwise look the manager up in the current Graph batch by Entra id, take its UPN
        /// and find that UPN in the DB-users-by-UPN map;</item>
        /// <item>otherwise leave the column NULL, which clears any previously stored manager.</item>
        /// </list>
        /// A user Graph reports no manager for also clears the column. In every case the candidate
        /// must already be saved (<c>ID &gt; 0</c>), because the value becomes a foreign key.
        /// </summary>
        /// <param name="managerAadId">
        /// The manager's Entra object id from <see cref="UserMetadataMappingRules.BuildPlan"/>, or
        /// null when Graph reported no manager.
        /// </param>
        public static object ResolveManagerId(
            string managerAadId,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, GraphUser> graphUsersByAadId)
        {
            if (managerAadId == null)
                return DBNull.Value;

            if (dbUsersByAadId.TryGetValue(managerAadId, out var manager) && manager.ID > 0)
                return manager.ID;

            if (graphUsersByAadId != null &&
                graphUsersByAadId.TryGetValue(managerAadId, out var mgrGraph) &&
                !string.IsNullOrEmpty(mgrGraph.UserPrincipalName) &&
                dbUsersByUpn.TryGetValue(mgrGraph.UserPrincipalName, out var mgrByUpn) && mgrByUpn.ID > 0)
            {
                return mgrByUpn.ID;
            }

            return DBNull.Value;
        }
    }
}
