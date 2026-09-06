using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Common.Entities.LicenceActivity
{
    /// <summary>
    /// Builds the bounded SQL batches used by <see cref="SqlLicenceActivityStore"/>.
    /// Only fixed table, column and ordering fragments are interpolated; every request value is a SQL parameter.
    /// </summary>
    internal static class LicenceActivitySql
    {
        internal const int CommandTimeoutSeconds = 20;

        internal const int Teams = 1;
        internal const int Outlook = 2;
        internal const int OneDrive = 3;
        internal const int SharePoint = 4;
        internal const int Copilot = 5;

        internal const string Available = "available";
        internal const string Disabled = "disabled";
        internal const string NotImported = "notImported";
        internal const string MissingCoverage = "missingCoverage";
        internal const string UnmatchableIdentity = "unmatchableIdentity";
        internal const string Partial = "partial";

        internal const string M365ReportSource = "microsoftGraphUsageReport";
        internal const string CopilotReportSource = "microsoftGraphCopilotUsageReport";
        internal const string CopilotAuditSource = "copilotAudit";
        internal const string CopilotInteractionSource = "copilotInteractions";

        internal static string BuildOverview(LicenceActivitySources sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));

            var sql = new StringBuilder(30000);
            sql.Append(OverviewPreamble);

            AppendM365Overview(
                sql, Teams, "teams", "dbo.teams_user_activity_log",
                "average published message and meeting counters across supporting snapshots",
                sources.UsageReports);

            AppendM365Overview(
                sql, Outlook, "outlook", "dbo.outlook_user_activity_log",
                "average published sent and read counters across supporting snapshots",
                sources.UsageReports);

            AppendM365Overview(
                sql, OneDrive, "onedrive", "dbo.onedrive_user_activity_log",
                "average published viewed-or-edited counter across supporting snapshots",
                sources.UsageReports);

            AppendM365Overview(
                sql, SharePoint, "sharepoint", "dbo.sharepoint_user_activity_log",
                "average published viewed-or-edited counter across supporting snapshots",
                sources.UsageReports);

            if (sources.UsageReports) sql.Append(M365OverviewScores);
            AppendCopilotOverview(sql, sources);
            sql.Append(OverviewProjection);
            return sql.ToString();
        }

        internal static string SampleParameterName(int workload, int index)
        {
            return "@sample" + workload.ToString(CultureInfo.InvariantCulture)
                + "_" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static void AppendSampleInserts(StringBuilder sql, LicenceActivityOverview overview)
        {
            foreach (var coverage in overview.Coverage)
            {
                var workload = WorkloadId(coverage.Workload);
                if (workload == 0 || coverage.SnapshotDates == null) continue;

                for (var index = 0; index < coverage.SnapshotDates.Count; index++)
                {
                    sql.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "INSERT #Samples (workload, sample_date) VALUES ({0}, {1});\r\n",
                        workload,
                        SampleParameterName(workload, index));
                }
            }
            sql.Append(@"
UPDATE #Samples
SET m365_from = CASE WHEN DATEADD(DAY,
        -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), sample_date) % 7) + 7) % 7),
        sample_date) < @from THEN @from ELSE DATEADD(DAY,
        -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), sample_date) % 7) + 7) % 7),
        sample_date) END,
    end_exclusive = DATEADD(DAY, 1, sample_date),
    copilot_from = DATEADD(DAY, -6, sample_date);
");
        }

        internal static string BuildUsers(
            LicenceActivityOverview overview,
            LicenceActivityQuery query)
        {
            if (overview == null) throw new ArgumentNullException(nameof(overview));
            if (query == null) throw new ArgumentNullException(nameof(query));

            var sql = new StringBuilder(26000);
            sql.Append(UsersPreamble);
            AppendSampleInserts(sql, overview);

            var selectedCoverage = overview.Coverage
                .FirstOrDefault(c => c.Workload == query.Workload);
            if (selectedCoverage != null)
                AppendWorkloadUsers(sql, selectedCoverage, "#EligibleUsers");

            sql.Append(@"
CREATE UNIQUE CLUSTERED INDEX IX_LicenceActivity_UserScores
    ON #Scores (workload, user_id);
");
            sql.Append(BuildUsersSelection(query));

            foreach (var coverage in overview.Coverage
                .Where(c => c.Workload != query.Workload))
            {
                AppendWorkloadUsers(sql, coverage, "#ReturnedUsers");
            }

            sql.Append(UsersFinalProjection);
            return sql.ToString();
        }

        internal static string BuildOverviewBase(string eligibleTable = "#EligibleUsers")
        {
            if (eligibleTable == "#EligibleUsers") return OverviewBaseSql;
            ValidateSharedEligibleTableName(eligibleTable);
            var start = OverviewBaseSql.IndexOf(
                "CREATE TABLE #Demographics",
                StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("The overview base SQL is malformed.");
            return ("SET NOCOUNT ON;\r\nSET XACT_ABORT ON;\r\n\r\n"
                + OverviewBaseSql.Substring(start)).Replace("#EligibleUsers", eligibleTable);
        }

        internal static string BuildM365Overview(string eligibleTable = "#EligibleUsers")
        {
            return BuildM365OverviewGroup(
                eligibleTable,
                Teams, Outlook, OneDrive, SharePoint);
        }

        internal static string BuildM365OverviewGroup(
            string eligibleTable,
            params int[] workloads)
        {
            var sql = new StringBuilder(26000);
            AppendOverviewPartPreamble(sql, eligibleTable);
            foreach (var workload in workloads.Distinct())
            {
                switch (workload)
                {
                    case Teams:
                        AppendM365Overview(
                            sql, Teams, "teams", "dbo.teams_user_activity_log",
                            "average published message and meeting counters across supporting snapshots",
                            true);
                        AppendM365OverviewScore(
                            sql, Teams, "dbo.teams_user_activity_log");
                        break;
                    case Outlook:
                        AppendM365Overview(
                            sql, Outlook, "outlook", "dbo.outlook_user_activity_log",
                            "average published sent and read counters across supporting snapshots",
                            true);
                        AppendM365OverviewScore(
                            sql, Outlook, "dbo.outlook_user_activity_log");
                        break;
                    case OneDrive:
                        AppendM365Overview(
                            sql, OneDrive, "onedrive", "dbo.onedrive_user_activity_log",
                            "average published viewed-or-edited counter across supporting snapshots",
                            true);
                        AppendM365OverviewScore(
                            sql, OneDrive, "dbo.onedrive_user_activity_log");
                        break;
                    case SharePoint:
                        AppendM365Overview(
                            sql, SharePoint, "sharepoint", "dbo.sharepoint_user_activity_log",
                            "average published viewed-or-edited counter across supporting snapshots",
                            true);
                        AppendM365OverviewScore(
                            sql, SharePoint, "dbo.sharepoint_user_activity_log");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(workloads));
                }
            }
            sql.Append(OverviewProjection);
            return sql.ToString()
                .Replace("#EligibleUsers", eligibleTable)
                .Replace("OPTION (RECOMPILE)", "OPTION (RECOMPILE, MAXDOP 2)");
        }

        internal static string BuildM365OverviewPart(
            int workload,
            string eligibleTable = "#EligibleUsers",
            string bandTable = null,
            int maximumSamples = 27)
        {
            var sql = new StringBuilder(14000);
            AppendOverviewPartPreamble(sql, eligibleTable);

            switch (workload)
            {
                case Teams:
                    AppendM365Overview(
                        sql, Teams, "teams", "dbo.teams_user_activity_log",
                        "average published message and meeting counters across supporting snapshots",
                        true);
                    AppendM365OverviewScore(
                        sql, Teams, "dbo.teams_user_activity_log", maximumSamples);
                    break;
                case Outlook:
                    AppendM365Overview(
                        sql, Outlook, "outlook", "dbo.outlook_user_activity_log",
                        "average published sent and read counters across supporting snapshots",
                        true);
                    AppendM365OverviewScore(
                        sql, Outlook, "dbo.outlook_user_activity_log", maximumSamples);
                    break;
                case OneDrive:
                    AppendM365Overview(
                        sql, OneDrive, "onedrive", "dbo.onedrive_user_activity_log",
                        "average published viewed-or-edited counter across supporting snapshots",
                        true);
                    AppendM365OverviewScore(
                        sql, OneDrive, "dbo.onedrive_user_activity_log", maximumSamples);
                    break;
                case SharePoint:
                    AppendM365Overview(
                        sql, SharePoint, "sharepoint", "dbo.sharepoint_user_activity_log",
                        "average published viewed-or-edited counter across supporting snapshots",
                        true);
                    AppendM365OverviewScore(
                        sql, SharePoint, "dbo.sharepoint_user_activity_log", maximumSamples);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(workload));
            }

            sql.Append(bandTable == null
                ? BuildSingleWorkloadProjection(workload, workload == Teams)
                : BuildBandWriterProjection(workload, bandTable));
            return sql.ToString()
                .Replace("#EligibleUsers", eligibleTable)
                .Replace("OPTION (RECOMPILE)", "OPTION (RECOMPILE, MAXDOP 1)");
        }

        internal static string BuildCopilotOverviewPart(
            LicenceActivitySources sources,
            string eligibleTable = "#EligibleUsers",
            string bandTable = null)
        {
            var sql = new StringBuilder(18000);
            AppendOverviewPartPreamble(sql, eligibleTable);
            AppendCopilotOverview(sql, sources);
            sql.Append(bandTable == null
                ? BuildSingleWorkloadProjection(Copilot, true)
                : BuildBandWriterProjection(Copilot, bandTable));
            return sql.ToString()
                .Replace("#EligibleUsers", eligibleTable)
                .Replace("OPTION (RECOMPILE)", "OPTION (RECOMPILE, MAXDOP 1)");
        }

        internal static string BuildSharedEligibleUsers(
            string tableName,
            IEnumerable<string> bandTables = null)
        {
            ValidateSharedEligibleTableName(tableName);
            var sql = new StringBuilder(@"
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF (SELECT COUNT_BIG(*) FROM dbo.license_types) > 500
BEGIN
    RAISERROR('Licence activity supports at most 500 imported licence types.', 16, 1);
    RETURN;
END;

CREATE TABLE " + tableName + @"
(
    user_id int NOT NULL PRIMARY KEY,
    department_id int NOT NULL,
    country_id int NOT NULL
);

INSERT " + tableName + @" (user_id, department_id, country_id)
SELECT u.id, ISNULL(u.department_id, 0), ISNULL(u.country_or_region_id, 0)
FROM dbo.users AS u
WHERE (@departmentId IS NULL
       OR u.department_id = @departmentId
       OR (@departmentId = 0 AND u.department_id IS NULL))
  AND (@countryId IS NULL
       OR u.country_or_region_id = @countryId
       OR (@countryId = 0 AND u.country_or_region_id IS NULL))
  AND EXISTS
      (SELECT 1 FROM dbo.user_license_type_lookups AS owned WHERE owned.user_id = u.id)
OPTION (RECOMPILE);

-- This shared, connection-scoped index enables batch aggregation without altering source tables.
-- Editions that cannot index temporary tables as columnstores retain the rowstore primary key.
IF (SELECT COUNT(*) FROM " + tableName + @") >= 32768
BEGIN
    BEGIN TRY
        EXEC sp_executesql N'CREATE NONCLUSTERED COLUMNSTORE INDEX IX_LicenceActivity_EligibleBatch
            ON " + tableName + @" (user_id, department_id, country_id);';
    END TRY
    BEGIN CATCH
        PRINT N'Licence activity is using rowstore temporary eligibility.';
    END CATCH;
END;
");
            if (bandTables != null)
            {
                foreach (var bandTable in bandTables)
                {
                    ValidateSharedBandTableName(bandTable);
                    sql.Append(@"
CREATE TABLE " + bandTable + @"
(
    user_id int NOT NULL PRIMARY KEY,
    band tinyint NOT NULL
);
");
                }
            }
            return sql.ToString();
        }

        internal static string BuildOverviewDistributions(
            string eligibleTable,
            IReadOnlyList<string> bandTables)
        {
            ValidateSharedEligibleTableName(eligibleTable);
            if (bandTables == null || bandTables.Count != 5)
                throw new ArgumentException("Exactly five workload-band tables are required.", nameof(bandTables));
            foreach (var table in bandTables) ValidateSharedBandTableName(table);

            return @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

;WITH AllBands AS
(
    SELECT 1 AS workload, user_id, band FROM " + bandTables[0] + @"
    UNION ALL SELECT 2, user_id, band FROM " + bandTables[1] + @"
    UNION ALL SELECT 3, user_id, band FROM " + bandTables[2] + @"
    UNION ALL SELECT 4, user_id, band FROM " + bandTables[3] + @"
    UNION ALL SELECT 5, user_id, band FROM " + bandTables[4] + @"
)
SELECT eligible.user_id,
       eligible.department_id,
       eligible.country_id,
       MAX(CASE WHEN bands.workload = 1 THEN bands.band END) AS teams_band,
       MAX(CASE WHEN bands.workload = 2 THEN bands.band END) AS outlook_band,
       MAX(CASE WHEN bands.workload = 3 THEN bands.band END) AS onedrive_band,
       MAX(CASE WHEN bands.workload = 4 THEN bands.band END) AS sharepoint_band,
       MAX(CASE WHEN bands.workload = 5 THEN bands.band END) AS copilot_band
INTO #UserBands
FROM " + eligibleTable + @" AS eligible
LEFT JOIN AllBands AS bands ON bands.user_id = eligible.user_id
GROUP BY eligible.user_id, eligible.department_id, eligible.country_id
OPTION (RECOMPILE, MAXDOP 2);

CREATE UNIQUE CLUSTERED INDEX IX_LicenceActivity_UserBands ON #UserBands (user_id);

;WITH Memberships AS
(
    SELECT DISTINCT owned.license_type_id, owned.user_id
    FROM dbo.user_license_type_lookups AS owned
    JOIN " + eligibleTable + @" AS eligible ON eligible.user_id = owned.user_id
),
Grouped AS
(
    SELECT members.license_type_id,
           bands.teams_band,
           bands.outlook_band,
           bands.onedrive_band,
           bands.sharepoint_band,
           bands.copilot_band,
           COUNT(*) AS users
    FROM Memberships AS members
    JOIN #UserBands AS bands ON bands.user_id = members.user_id
    GROUP BY members.license_type_id,
             bands.teams_band, bands.outlook_band, bands.onedrive_band,
             bands.sharepoint_band, bands.copilot_band
),
Counts AS
(
    SELECT license_type_id,
           SUM(users) AS assigned_users,
           SUM(CASE WHEN teams_band = 3 THEN users ELSE 0 END) AS teams_high,
           SUM(CASE WHEN teams_band = 2 THEN users ELSE 0 END) AS teams_moderate,
           SUM(CASE WHEN teams_band = 1 THEN users ELSE 0 END) AS teams_low,
           SUM(CASE WHEN teams_band = 0 THEN users ELSE 0 END) AS teams_zero,
           SUM(CASE WHEN teams_band IS NOT NULL THEN users ELSE 0 END) AS teams_known,
           SUM(CASE WHEN outlook_band = 3 THEN users ELSE 0 END) AS outlook_high,
           SUM(CASE WHEN outlook_band = 2 THEN users ELSE 0 END) AS outlook_moderate,
           SUM(CASE WHEN outlook_band = 1 THEN users ELSE 0 END) AS outlook_low,
           SUM(CASE WHEN outlook_band = 0 THEN users ELSE 0 END) AS outlook_zero,
           SUM(CASE WHEN outlook_band IS NOT NULL THEN users ELSE 0 END) AS outlook_known,
           SUM(CASE WHEN onedrive_band = 3 THEN users ELSE 0 END) AS onedrive_high,
           SUM(CASE WHEN onedrive_band = 2 THEN users ELSE 0 END) AS onedrive_moderate,
           SUM(CASE WHEN onedrive_band = 1 THEN users ELSE 0 END) AS onedrive_low,
           SUM(CASE WHEN onedrive_band = 0 THEN users ELSE 0 END) AS onedrive_zero,
           SUM(CASE WHEN onedrive_band IS NOT NULL THEN users ELSE 0 END) AS onedrive_known,
           SUM(CASE WHEN sharepoint_band = 3 THEN users ELSE 0 END) AS sharepoint_high,
           SUM(CASE WHEN sharepoint_band = 2 THEN users ELSE 0 END) AS sharepoint_moderate,
           SUM(CASE WHEN sharepoint_band = 1 THEN users ELSE 0 END) AS sharepoint_low,
           SUM(CASE WHEN sharepoint_band = 0 THEN users ELSE 0 END) AS sharepoint_zero,
           SUM(CASE WHEN sharepoint_band IS NOT NULL THEN users ELSE 0 END) AS sharepoint_known,
           SUM(CASE WHEN copilot_band = 3 THEN users ELSE 0 END) AS copilot_high,
           SUM(CASE WHEN copilot_band = 2 THEN users ELSE 0 END) AS copilot_moderate,
           SUM(CASE WHEN copilot_band = 1 THEN users ELSE 0 END) AS copilot_low,
           SUM(CASE WHEN copilot_band = 0 THEN users ELSE 0 END) AS copilot_zero,
           SUM(CASE WHEN copilot_band IS NOT NULL THEN users ELSE 0 END) AS copilot_known
    FROM Grouped
    GROUP BY license_type_id
)
SELECT licence.id AS LicenceTypeId,
       values_by_workload.workload_name AS Workload,
       values_by_workload.high_count AS High,
       values_by_workload.moderate_count AS Moderate,
       values_by_workload.low_count AS Low,
       values_by_workload.zero_count AS Zero,
       ISNULL(counts.assigned_users, 0) - values_by_workload.known_count AS Unknown
FROM dbo.license_types AS licence
LEFT JOIN Counts AS counts ON counts.license_type_id = licence.id
CROSS APPLY
(
    VALUES
      ('teams', ISNULL(counts.teams_high, 0), ISNULL(counts.teams_moderate, 0),
       ISNULL(counts.teams_low, 0), ISNULL(counts.teams_zero, 0), ISNULL(counts.teams_known, 0)),
      ('outlook', ISNULL(counts.outlook_high, 0), ISNULL(counts.outlook_moderate, 0),
       ISNULL(counts.outlook_low, 0), ISNULL(counts.outlook_zero, 0), ISNULL(counts.outlook_known, 0)),
      ('onedrive', ISNULL(counts.onedrive_high, 0), ISNULL(counts.onedrive_moderate, 0),
       ISNULL(counts.onedrive_low, 0), ISNULL(counts.onedrive_zero, 0), ISNULL(counts.onedrive_known, 0)),
      ('sharepoint', ISNULL(counts.sharepoint_high, 0), ISNULL(counts.sharepoint_moderate, 0),
       ISNULL(counts.sharepoint_low, 0), ISNULL(counts.sharepoint_zero, 0), ISNULL(counts.sharepoint_known, 0)),
      ('copilot', ISNULL(counts.copilot_high, 0), ISNULL(counts.copilot_moderate, 0),
       ISNULL(counts.copilot_low, 0), ISNULL(counts.copilot_zero, 0), ISNULL(counts.copilot_known, 0))
) AS values_by_workload
    (workload_name, high_count, moderate_count, low_count, zero_count, known_count)
ORDER BY licence.id, values_by_workload.workload_name
OPTION (RECOMPILE);

CREATE TABLE #Demographics
(
    dimension tinyint NOT NULL,
    demographic_id int NOT NULL,
    assigned_users int NOT NULL,
    PRIMARY KEY (dimension, demographic_id)
);

;WITH Counts AS
(
    SELECT department_id AS demographic_id, COUNT(*) AS assigned_users
    FROM " + eligibleTable + @"
    GROUP BY department_id
),
Ranked AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_id) AS row_number
    FROM Counts
)
INSERT #Demographics
SELECT 1, demographic_id, assigned_users
FROM Ranked
WHERE row_number <= 50
   OR (@departmentId IS NOT NULL AND demographic_id = @departmentId);

;WITH Counts AS
(
    SELECT country_id AS demographic_id, COUNT(*) AS assigned_users
    FROM " + eligibleTable + @"
    GROUP BY country_id
),
Ranked AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_id) AS row_number
    FROM Counts
)
INSERT #Demographics
SELECT 2, demographic_id, assigned_users
FROM Ranked
WHERE row_number <= 50
   OR (@countryId IS NOT NULL AND demographic_id = @countryId);

;WITH DepartmentGroups AS
(
    SELECT bands.department_id AS demographic_id,
           bands.teams_band,
           bands.outlook_band,
           bands.onedrive_band,
           bands.sharepoint_band,
           bands.copilot_band,
           COUNT(*) AS users
    FROM #UserBands AS bands
    GROUP BY bands.department_id,
             bands.teams_band, bands.outlook_band, bands.onedrive_band,
             bands.sharepoint_band, bands.copilot_band
),
CountryGroups AS
(
    SELECT bands.country_id AS demographic_id,
           bands.teams_band,
           bands.outlook_band,
           bands.onedrive_band,
           bands.sharepoint_band,
           bands.copilot_band,
           COUNT(*) AS users
    FROM #UserBands AS bands
    GROUP BY bands.country_id,
             bands.teams_band, bands.outlook_band, bands.onedrive_band,
             bands.sharepoint_band, bands.copilot_band
),
AllGroups AS
(
    SELECT 1 AS dimension, * FROM DepartmentGroups
    UNION ALL
    SELECT 2, * FROM CountryGroups
),
Expanded AS
(
    SELECT groups.dimension,
           groups.demographic_id,
           values_by_workload.workload_name,
           values_by_workload.band,
           groups.users
    FROM AllGroups AS groups
    CROSS APPLY
    (
        VALUES ('teams', groups.teams_band),
               ('outlook', groups.outlook_band),
               ('onedrive', groups.onedrive_band),
               ('sharepoint', groups.sharepoint_band),
               ('copilot', groups.copilot_band)
    ) AS values_by_workload (workload_name, band)
),
Counts AS
(
    SELECT dimension,
           demographic_id,
           workload_name,
           SUM(CASE WHEN band = 3 THEN users ELSE 0 END) AS high_count,
           SUM(CASE WHEN band = 2 THEN users ELSE 0 END) AS moderate_count,
           SUM(CASE WHEN band = 1 THEN users ELSE 0 END) AS low_count,
           SUM(CASE WHEN band = 0 THEN users ELSE 0 END) AS zero_count,
           SUM(CASE WHEN band IS NOT NULL THEN users ELSE 0 END) AS known_count
    FROM Expanded
    GROUP BY dimension, demographic_id, workload_name
)
SELECT CASE WHEN demographics.dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       demographics.demographic_id AS Id,
       workloads.workload_name AS Workload,
       ISNULL(counts.high_count, 0) AS High,
       ISNULL(counts.moderate_count, 0) AS Moderate,
       ISNULL(counts.low_count, 0) AS Low,
       ISNULL(counts.zero_count, 0) AS Zero,
       demographics.assigned_users - ISNULL(counts.known_count, 0) AS Unknown
FROM #Demographics AS demographics
CROSS JOIN
    (VALUES ('teams'), ('outlook'), ('onedrive'), ('sharepoint'), ('copilot'))
    AS workloads (workload_name)
LEFT JOIN Counts AS counts
  ON counts.dimension = demographics.dimension
 AND counts.demographic_id = demographics.demographic_id
 AND counts.workload_name = workloads.workload_name
ORDER BY demographics.dimension, demographics.demographic_id, workloads.workload_name
OPTION (RECOMPILE);

SELECT (SELECT COUNT(*) FROM " + eligibleTable + @") AS DistinctAssignedUsers,
       CAST(CASE
           WHEN (SELECT COUNT(DISTINCT department_id) FROM " + eligibleTable + @")
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 1)
             OR (SELECT COUNT(DISTINCT country_id) FROM " + eligibleTable + @")
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 2)
           THEN 1 ELSE 0 END AS bit) AS DemographicsTruncated;

SELECT licence.id AS LicenceTypeId,
       licence.name AS Name,
       licence.sku_id AS SkuId,
       0 AS AssignedUsers
FROM dbo.license_types AS licence
ORDER BY licence.name, licence.id;

SELECT CASE WHEN demographics.dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       demographics.demographic_id AS Id,
       CASE WHEN demographics.dimension = 1
            THEN COALESCE(department.name, N'Unknown')
            ELSE COALESCE(country.name, N'Unknown')
       END AS Name,
       demographics.assigned_users AS AssignedUsers
FROM #Demographics AS demographics
LEFT JOIN dbo.user_departments AS department
  ON demographics.dimension = 1 AND department.id = demographics.demographic_id
LEFT JOIN dbo.user_country_or_region AS country
  ON demographics.dimension = 2 AND country.id = demographics.demographic_id
ORDER BY demographics.dimension, demographics.assigned_users DESC, Name, demographics.demographic_id;
";
        }

        internal static string BuildSingleBandDistribution(
            int workload,
            string eligibleTable,
            string bandTable,
            bool includeBase)
        {
            ValidateSharedEligibleTableName(eligibleTable);
            ValidateSharedBandTableName(bandTable);

            var projection = BuildSingleWorkloadProjection(workload, includeBase);
            var knownStart = projection.IndexOf(
                "CREATE TABLE #Known", StringComparison.Ordinal);
            var resultsStart = projection.IndexOf(
                "SELECT coverage.workload_name AS Workload", StringComparison.Ordinal);
            if (knownStart < 0 || resultsStart <= knownStart)
                throw new InvalidOperationException("The workload projection SQL is malformed.");
            projection = projection.Remove(knownStart, resultsStart - knownStart)
                .Replace("#Known", bandTable);

            var sql = new StringBuilder(12000);
            AppendOverviewPartPreamble(sql, eligibleTable);
            sql.AppendFormat(
                CultureInfo.InvariantCulture,
                @"
INSERT #Coverage
(
    workload, workload_name, status, source, measure, granularity, message,
    effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
    report_period_days, expected_samples, observed_samples, unmatched_users
)
VALUES
({0}, '{1}', 'available', '', N'', '', NULL,
 NULL, NULL, NULL, 0, NULL, 0, 0, 0);
",
                workload,
                WorkloadName(workload));
            sql.Append(projection);
            return sql.ToString()
                .Replace("#EligibleUsers", eligibleTable)
                .Replace("OPTION (RECOMPILE)", "OPTION (RECOMPILE, MAXDOP 1)");
        }

        private static void AppendWorkloadUsers(
            StringBuilder sql,
            LicenceActivityCoverage coverage,
            string scopeTable)
        {
            var workload = WorkloadId(coverage.Workload);
            if (workload == Teams)
            {
                AppendM365Users(sql, coverage, Teams, "dbo.teams_user_activity_log",
                    "CAST(ISNULL(activity.private_chat_count, 0) AS float)"
                    + " + CAST(ISNULL(activity.team_chat_count, 0) AS float)"
                    + " + CAST(ISNULL(activity.post_messages, 0) AS float)"
                    + " + CAST(ISNULL(activity.reply_messages, 0) AS float)"
                    + " + CAST(ISNULL(activity.meetings_attended_count, 0) AS float)"
                    + " + CAST(ISNULL(activity.meetings_organized_count, 0) AS float)",
                    scopeTable);
            }
            else if (workload == Outlook)
            {
                AppendM365Users(sql, coverage, Outlook, "dbo.outlook_user_activity_log",
                    "CAST(ISNULL(activity.email_send_count, 0) AS float)"
                    + " + CAST(ISNULL(activity.email_read_count, 0) AS float)",
                    scopeTable);
            }
            else if (workload == OneDrive)
            {
                AppendM365Users(sql, coverage, OneDrive, "dbo.onedrive_user_activity_log",
                    "CAST(ISNULL(activity.viewed_or_edited, 0) AS float)",
                    scopeTable);
            }
            else if (workload == SharePoint)
            {
                AppendM365Users(sql, coverage, SharePoint, "dbo.sharepoint_user_activity_log",
                    "CAST(ISNULL(activity.viewed_or_edited, 0) AS float)",
                    scopeTable);
            }
            else if (workload == Copilot)
            {
                AppendCopilotUsers(sql, coverage, scopeTable);
            }
        }

        internal static int WorkloadId(string workload)
        {
            switch (workload)
            {
                case "teams": return Teams;
                case "outlook": return Outlook;
                case "onedrive": return OneDrive;
                case "sharepoint": return SharePoint;
                case "copilot": return Copilot;
                default: return 0;
            }
        }

        internal static string WorkloadName(int workload)
        {
            switch (workload)
            {
                case Teams: return "teams";
                case Outlook: return "outlook";
                case OneDrive: return "onedrive";
                case SharePoint: return "sharepoint";
                case Copilot: return "copilot";
                default: throw new ArgumentOutOfRangeException(nameof(workload));
            }
        }

        private static void AppendOverviewPartPreamble(StringBuilder sql, string eligibleTable)
        {
            if (eligibleTable == "#EligibleUsers")
            {
                sql.Append(OverviewPreamble);
                return;
            }

            ValidateSharedEligibleTableName(eligibleTable);
            var start = OverviewPreamble.IndexOf("CREATE TABLE #Weeks", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("The overview SQL preamble is malformed.");
            sql.Append("SET NOCOUNT ON;\r\nSET XACT_ABORT ON;\r\nSET DATEFIRST 1;\r\n\r\n");
            sql.Append(OverviewPreamble.Substring(start).Replace("#EligibleUsers", eligibleTable));
        }

        private static void ValidateSharedEligibleTableName(string tableName)
        {
            const string prefix = "##LicenceActivityEligible_";
            if (tableName == null
                || !tableName.StartsWith(prefix, StringComparison.Ordinal)
                || tableName.Length != prefix.Length + 32
                || tableName.Substring(prefix.Length).Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException("Invalid shared eligible-user temp table name.", nameof(tableName));
            }
        }

        private static void ValidateSharedBandTableName(string tableName)
        {
            const string prefix = "##LicenceActivityBand";
            if (tableName == null
                || !tableName.StartsWith(prefix, StringComparison.Ordinal)
                || tableName.Length != prefix.Length + 2 + 32
                || tableName[prefix.Length] < '1'
                || tableName[prefix.Length] > '5'
                || tableName[prefix.Length + 1] != '_'
                || tableName.Substring(prefix.Length + 2).Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException("Invalid shared workload-band temp table name.", nameof(tableName));
            }
        }

        private static readonly string OverviewBaseSql = @"
/* LicenceActivity:OverviewBase */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET DATEFIRST 1;

IF (SELECT COUNT_BIG(*) FROM dbo.license_types) > 500
BEGIN
    RAISERROR('Licence activity supports at most 500 imported licence types.', 16, 1);
    RETURN;
END;

CREATE TABLE #EligibleUsers
(
    user_id int NOT NULL PRIMARY KEY,
    department_id int NOT NULL,
    country_id int NOT NULL
);

INSERT #EligibleUsers
SELECT DISTINCT u.id, ISNULL(u.department_id, 0), ISNULL(u.country_or_region_id, 0)
FROM dbo.users AS u
WHERE (@departmentId IS NULL
       OR u.department_id = @departmentId
       OR (@departmentId = 0 AND u.department_id IS NULL))
  AND (@countryId IS NULL
       OR u.country_or_region_id = @countryId
       OR (@countryId = 0 AND u.country_or_region_id IS NULL))
  AND EXISTS
      (SELECT 1 FROM dbo.user_license_type_lookups AS owned WHERE owned.user_id = u.id)
OPTION (RECOMPILE);

CREATE TABLE #Demographics
(
    dimension tinyint NOT NULL,
    demographic_id int NOT NULL,
    demographic_name nvarchar(100) NOT NULL,
    assigned_users int NOT NULL,
    PRIMARY KEY (dimension, demographic_id)
);

;WITH Counts AS
(
    SELECT eligible.department_id AS demographic_id,
           COALESCE(department.name, N'Unknown') AS demographic_name,
           COUNT(*) AS assigned_users
    FROM #EligibleUsers AS eligible
    LEFT JOIN dbo.user_departments AS department ON department.id = eligible.department_id
    GROUP BY eligible.department_id, department.name
),
Ranked AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_name, demographic_id) AS row_number
    FROM Counts
)
INSERT #Demographics
SELECT 1, demographic_id, demographic_name, assigned_users
FROM Ranked
WHERE row_number <= 50
   OR (@departmentId IS NOT NULL AND demographic_id = @departmentId);

;WITH Counts AS
(
    SELECT eligible.country_id AS demographic_id,
           COALESCE(country.name, N'Unknown') AS demographic_name,
           COUNT(*) AS assigned_users
    FROM #EligibleUsers AS eligible
    LEFT JOIN dbo.user_country_or_region AS country ON country.id = eligible.country_id
    GROUP BY eligible.country_id, country.name
),
Ranked AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_name, demographic_id) AS row_number
    FROM Counts
)
INSERT #Demographics
SELECT 2, demographic_id, demographic_name, assigned_users
FROM Ranked
WHERE row_number <= 50
   OR (@countryId IS NOT NULL AND demographic_id = @countryId);

SELECT (SELECT COUNT(*) FROM #EligibleUsers) AS DistinctAssignedUsers,
       CAST(CASE
           WHEN (SELECT COUNT(DISTINCT department_id) FROM #EligibleUsers)
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 1)
             OR (SELECT COUNT(DISTINCT country_id) FROM #EligibleUsers)
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 2)
           THEN 1 ELSE 0 END AS bit) AS DemographicsTruncated;

SELECT licence.id AS LicenceTypeId,
       licence.name AS Name,
       licence.sku_id AS SkuId,
       0 AS AssignedUsers
FROM dbo.license_types AS licence
ORDER BY licence.name, licence.id;

SELECT CASE WHEN dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       demographic_id AS Id,
       demographic_name AS Name,
       assigned_users AS AssignedUsers
FROM #Demographics
ORDER BY dimension, assigned_users DESC, demographic_name, demographic_id;
";

        private static readonly string OverviewPreamble = @"
/* LicenceActivity:Overview */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET DATEFIRST 1;

IF (SELECT COUNT_BIG(*) FROM dbo.license_types) > 500
BEGIN
    RAISERROR('Licence activity supports at most 500 imported licence types.', 16, 1);
    RETURN;
END;

CREATE TABLE #EligibleUsers
(
    user_id int NOT NULL PRIMARY KEY,
    department_id int NOT NULL,
    country_id int NOT NULL
);

INSERT #EligibleUsers (user_id, department_id, country_id)
SELECT DISTINCT u.id,
       ISNULL(u.department_id, 0),
       ISNULL(u.country_or_region_id, 0)
FROM dbo.users AS u
WHERE (@departmentId IS NULL
       OR u.department_id = @departmentId
       OR (@departmentId = 0 AND u.department_id IS NULL))
  AND (@countryId IS NULL
       OR u.country_or_region_id = @countryId
       OR (@countryId = 0 AND u.country_or_region_id IS NULL))
  AND EXISTS
  (
      SELECT 1
      FROM dbo.user_license_type_lookups AS owned
      WHERE owned.user_id = u.id
  )
OPTION (RECOMPILE);

CREATE TABLE #Weeks
(
    week_start date NOT NULL PRIMARY KEY,
    m365_from date NOT NULL,
    m365_to date NOT NULL,
    copilot_d7_from date NOT NULL,
    copilot_d7_to date NOT NULL
);

DECLARE @firstWeek date =
    DATEADD(DAY,
        -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), @from) % 7) + 7) % 7),
        @from);

;WITH WeekStarts AS
(
    SELECT @firstWeek AS week_start
    UNION ALL
    SELECT DATEADD(DAY, 7, week_start)
    FROM WeekStarts
    WHERE DATEADD(DAY, 7, week_start) <= @to
)
INSERT #Weeks (week_start, m365_from, m365_to, copilot_d7_from, copilot_d7_to)
SELECT week_start,
       CASE WHEN week_start < @from THEN @from ELSE week_start END,
       CASE WHEN DATEADD(DAY, 6, week_start) > @to THEN @to ELSE DATEADD(DAY, 6, week_start) END,
       CASE WHEN week_start < DATEADD(DAY, 6, @from) THEN DATEADD(DAY, 6, @from) ELSE week_start END,
       CASE WHEN DATEADD(DAY, 6, week_start) > @to THEN @to ELSE DATEADD(DAY, 6, week_start) END
FROM WeekStarts
OPTION (MAXRECURSION 0);

CREATE TABLE #Samples
(
    workload tinyint NOT NULL,
    sample_date date NOT NULL,
    PRIMARY KEY (workload, sample_date)
);

CREATE TABLE #Coverage
(
    workload tinyint NOT NULL PRIMARY KEY,
    workload_name varchar(20) NOT NULL,
    status varchar(32) NOT NULL,
    source varchar(64) NOT NULL,
    measure nvarchar(240) NOT NULL,
    granularity varchar(48) NOT NULL,
    message nvarchar(800) NULL,
    effective_from_utc datetime NULL,
    effective_to_utc datetime NULL,
    latest_import_utc datetime NULL,
    lag_days int NOT NULL,
    report_period_days int NULL,
    expected_samples int NOT NULL,
    observed_samples int NOT NULL,
    unmatched_users int NOT NULL
);

-- Report-backed workloads materialise every user who has at least one actual (user, sample-date)
-- row, including explicit zero rows. Per-user observed_samples decides whether zero is complete;
-- an absent or incomplete row set remains unknown and can never enter least-active.
CREATE TABLE #Scores
(
    workload tinyint NOT NULL,
    user_id int NOT NULL,
    active_samples int NOT NULL,
    observed_samples int NOT NULL,
    frequency_known bit NOT NULL
);
";

        private static void AppendM365Overview(
            StringBuilder sql,
            int workload,
            string workloadName,
            string table,
            string measure,
            bool enabled)
        {
            var suffix = workload.ToString(CultureInfo.InvariantCulture);
            sql.AppendFormat(
                CultureInfo.InvariantCulture,
                @"
DECLARE @expected{0} int =
(
    SELECT COUNT(*)
    FROM #Weeks
    WHERE m365_from <= m365_to
);
",
                suffix);

            if (!enabled)
            {
                sql.AppendFormat(
                    CultureInfo.InvariantCulture,
                    @"
INSERT #Coverage
(
    workload, workload_name, status, source, measure, granularity, message,
    effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
    report_period_days, expected_samples, observed_samples, unmatched_users
)
VALUES
(
    {0}, '{1}', 'disabled', 'microsoftGraphUsageReport',
    N'{2}', 'weeklySupportingSnapshot',
    N'The Microsoft 365 usage-report import is disabled. Absence cannot be interpreted as zero.',
    NULL, NULL, NULL, 0, NULL, @expected{0}, 0, 0
);
",
                    suffix, workloadName, measure);
                return;
            }

            sql.AppendFormat(
                CultureInfo.InvariantCulture,
                @"
DECLARE @latest{0} datetime = (SELECT MAX([date]) FROM {1});

INSERT #Samples (workload, sample_date)
SELECT {0}, selected.sample_date
FROM #Weeks AS weeks
CROSS APPLY
(
    SELECT TOP (1) CAST(available.[date] AS date) AS sample_date
    FROM {1} AS available WITH (INDEX(IX_date))
    WHERE available.[date] >= weeks.m365_from
      AND available.[date] < DATEADD(DAY, 1, weeks.m365_to)
      AND available.[date] < DATEADD(DAY, 1, @settled)
    ORDER BY available.[date] DESC
) AS selected
WHERE weeks.m365_from <= weeks.m365_to
  AND weeks.m365_from <= @settled
OPTION (RECOMPILE);

DECLARE @observed{0} int =
    (SELECT COUNT(*) FROM #Samples WHERE workload = {0});
DECLARE @complete{0} int =
(
    SELECT COUNT(*)
    FROM #Weeks AS weeks
    JOIN #Samples AS samples
      ON samples.workload = {0}
     AND samples.sample_date = weeks.m365_to
    WHERE weeks.m365_from <= weeks.m365_to
      AND weeks.m365_to <= @settled
);
DECLARE @status{0} varchar(32) =
    CASE
        WHEN @latest{0} IS NULL THEN 'notImported'
        WHEN @expected{0} = 0 OR @observed{0} = 0 THEN 'missingCoverage'
        WHEN @observed{0} < @expected{0} OR @complete{0} < @expected{0} THEN 'partial'
        ELSE 'available'
    END;

INSERT #Coverage
(
    workload, workload_name, status, source, measure, granularity, message,
    effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
    report_period_days, expected_samples, observed_samples, unmatched_users
)
SELECT {0}, '{2}', @status{0}, 'microsoftGraphUsageReport',
       N'{3}', 'weeklySupportingSnapshot',
       CASE @status{0}
           WHEN 'available' THEN N'One settled report-date snapshot was sampled per calendar week. Frequency uses same-week last_activity_date only. Published counters are averaged as snapshot evidence and are never summed or relabelled as daily events. These tables store report dates, not an import-completion timestamp.'
           WHEN 'partial' THEN N'At least one requested Monday-week portion lacks a settled snapshot on its exact end date. Earlier snapshots are as-of evidence only; all bands remain unknown and no user is ranked least-active.'
           WHEN 'notImported' THEN N'The import is enabled but this workload has not stored a report yet.'
           ELSE N'No settled report snapshot covers this requested range.'
       END,
       MIN(CAST(CASE WHEN sampled_week.week_start < @from
                     THEN @from ELSE sampled_week.week_start END AS datetime)),
       MAX(CAST(samples.sample_date AS datetime)),
       NULL,
       CASE WHEN MAX(samples.sample_date) IS NULL THEN 0
            ELSE DATEDIFF(DAY, MAX(samples.sample_date), @now) END,
       NULL, @expected{0}, @observed{0}, 0
FROM (SELECT sample_date FROM #Samples WHERE workload = {0}) AS samples
CROSS APPLY
(
    SELECT DATEADD(DAY,
        -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), samples.sample_date) % 7) + 7) % 7),
        samples.sample_date) AS week_start
) AS sampled_week;
",
                suffix, table, workloadName, measure);
        }

        private static void AppendM365OverviewScore(
            StringBuilder sql,
            int workload,
            string table,
            int maximumSamples = 27)
        {
            if (maximumSamples < 1 || maximumSamples > 27)
                throw new ArgumentOutOfRangeException(nameof(maximumSamples));
            var samples = Enumerable.Range(1, maximumSamples).ToArray();
            var aggregates = string.Join(",\r\n", samples.Select(sample =>
                "MAX(CASE WHEN chosen.sample_number = " + sample.ToString(CultureInfo.InvariantCulture)
                + @" THEN CASE WHEN activity.last_activity_date >= chosen.m365_from
                                AND activity.last_activity_date < chosen.end_exclusive
                               THEN 2 ELSE 1 END ELSE 0 END) AS sample" + sample));
            var observed = string.Join(" + ", samples.Select(sample =>
                "CASE WHEN sample" + sample + " > 0 THEN 1 ELSE 0 END"));
            var active = string.Join(" + ", samples.Select(sample =>
                "CASE WHEN sample" + sample + " = 2 THEN 1 ELSE 0 END"));
            sql.AppendFormat(
                CultureInfo.InvariantCulture,
                @"
IF @status{0} = 'available'
BEGIN
    IF @expected{0} = 1
    BEGIN
        INSERT #Scores
            (workload, user_id, active_samples, observed_samples, frequency_known)
        SELECT {0},
               activity.user_id,
               MAX(CASE
                       WHEN activity.last_activity_date >= weeks.m365_from
                        AND activity.last_activity_date < DATEADD(DAY, 1, weeks.m365_to)
                        AND activity.last_activity_date < DATEADD(DAY, 1, chosen.sample_date)
                       THEN 1 ELSE 0
                   END),
               1,
               -- One pinned sample day: GROUP BY user already collapses duplicate report rows.
               CAST(1 AS bit)
        FROM #Samples AS chosen
        JOIN #Weeks AS weeks
          ON chosen.sample_date >= weeks.m365_from
         AND chosen.sample_date <= weeks.m365_to
        JOIN {1} AS activity WITH (INDEX(IX_date))
          ON activity.[date] >= chosen.sample_date
         AND activity.[date] < DATEADD(DAY, 1, chosen.sample_date)
        JOIN #EligibleUsers AS eligible ON eligible.user_id = activity.user_id
        WHERE chosen.workload = {0}
        GROUP BY activity.user_id
        OPTION (RECOMPILE, MAXDOP 1);
    END
    ELSE
    BEGIN
        -- A bounded pivot has one group per user, not one per user/report day.
        -- MAX retains duplicate-day evidence: 0 absent, 1 observed zero, 2 positive.
        -- Preserve the eligible population through aggregation so sample-join selectivity
        -- cannot shrink its group estimate and memory grant. Remove absent groups afterward.
        ;WITH Chosen AS
        (
            SELECT samples.sample_date, weeks.m365_from,
                   DATEADD(DAY, 1, samples.sample_date) AS end_exclusive,
                   ROW_NUMBER() OVER (ORDER BY samples.sample_date) AS sample_number
            FROM #Samples AS samples
            JOIN #Weeks AS weeks
              ON samples.sample_date = weeks.m365_to
            WHERE samples.workload = {0}
        ),
        PerUser AS
        (
            SELECT eligible.user_id, {2}
            FROM #EligibleUsers AS eligible
            LEFT JOIN
            (
                {1} AS activity
                JOIN Chosen AS chosen
                  ON CAST(activity.[date] AS date) = chosen.sample_date
                 AND activity.[date] >= @from
                 AND activity.[date] < @endExclusive
            ) ON activity.user_id = eligible.user_id
            GROUP BY eligible.user_id
        ),
        Totals AS
        (
            SELECT user_id, {3} AS active_samples, {4} AS observed_samples
            FROM PerUser
        )
        INSERT #Scores
            (workload, user_id, active_samples, observed_samples, frequency_known)
        SELECT {0},
                   per_user.user_id,
                   active_samples,
                   observed_samples,
                   CAST(CASE WHEN observed_samples = @expected{0}
                             THEN 1 ELSE 0 END AS bit)
        FROM Totals AS per_user
        WHERE observed_samples > 0
        OPTION (RECOMPILE, MAXDOP 1);
    END;
END;
",
                workload, table, aggregates, active, observed);
        }

        private static void AppendCopilotOverview(StringBuilder sql, LicenceActivitySources sources)
        {
            if (sources.CopilotUsageReports)
            {
                sql.Append(CopilotOfficialOverview);
            }
            else
            {
                var fallbackSource = sources.CopilotAudit
                    ? CopilotAuditSource
                    : sources.CopilotInteractions ? CopilotInteractionSource : CopilotReportSource;
                var initialStatus = sources.CopilotAudit || sources.CopilotInteractions
                    ? NotImported
                    : Disabled;
                var initialMessage = sources.CopilotAudit || sources.CopilotInteractions
                    ? "The enabled Copilot event source has not stored evidence in this range."
                    : "Every per-user Copilot source is disabled.";

                sql.AppendFormat(
                    CultureInfo.InvariantCulture,
                    @"
DECLARE @copilotPreferredStatus varchar(32) = '{0}';
DECLARE @copilotPreferredSource varchar(64) = '{1}';
DECLARE @copilotPreferredMessage nvarchar(800) = N'{2}';
DECLARE @copilotLatestImport datetime = NULL;
DECLARE @copilotUnmatched int = 0;
DECLARE @copilotNeedsFallback bit = 1;
",
                    initialStatus, fallbackSource, initialMessage);
            }

            if (sources.CopilotAudit)
            {
                sql.Append(CopilotAuditFallbackOverview);
            }

            if (sources.CopilotInteractions)
            {
                sql.Append(CopilotInteractionFallbackOverview);
            }

            if (!sources.CopilotUsageReports && sources.CopilotAudit)
            {
                sql.Append(@"
IF @copilotNeedsFallback = 1
   AND EXISTS (SELECT 1 FROM dbo.copilot_chats WHERE time_stamp IS NOT NULL)
BEGIN
    SET @copilotPreferredStatus = 'missingCoverage';
    SET @copilotPreferredMessage =
        N'Copilot audit data exists, but no event falls in the requested range; absence cannot be interpreted as zero.';
END;
");
                if (sources.CopilotInteractions)
                {
                    sql.Append(@"
IF @copilotNeedsFallback = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chats WHERE time_stamp IS NOT NULL)
   AND EXISTS (SELECT 1 FROM dbo.copilot_interactions)
BEGIN
    SET @copilotPreferredStatus = 'missingCoverage';
    SET @copilotPreferredSource = 'copilotInteractions';
    SET @copilotPreferredMessage =
        N'Copilot interaction history exists, but no event falls in the requested range; absence cannot be interpreted as zero.';
END;
");
                }
            }
            else if (!sources.CopilotUsageReports && sources.CopilotInteractions)
            {
                sql.Append(@"
IF @copilotNeedsFallback = 1
   AND EXISTS (SELECT 1 FROM dbo.copilot_interactions)
BEGIN
    SET @copilotPreferredStatus = 'missingCoverage';
    SET @copilotPreferredMessage =
        N'Copilot interaction history exists, but no event falls in the requested range; absence cannot be interpreted as zero.';
END;
");
            }

            sql.Append(@"
IF @copilotNeedsFallback = 1
BEGIN
    INSERT #Coverage
    (
        workload, workload_name, status, source, measure, granularity, message,
        effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
        report_period_days, expected_samples, observed_samples, unmatched_users
    )
    VALUES
    (
        5, 'copilot', @copilotPreferredStatus, @copilotPreferredSource,
        N'positive Copilot evidence only', 'unknown',
        @copilotPreferredMessage, NULL, NULL, @copilotLatestImport, 0, NULL,
        (SELECT COUNT(*) FROM #Weeks), 0,
        @copilotUnmatched
    );
END;
");
        }

        private static readonly string M365OverviewScores = @"
;WITH PerSample AS
(
    SELECT 1 AS workload,
           activity.user_id,
           chosen.sample_date,
           MAX(CASE
                   WHEN activity.last_activity_date >= weeks.m365_from
                    AND activity.last_activity_date < DATEADD(DAY, 1, weeks.m365_to)
                    AND activity.last_activity_date < DATEADD(DAY, 1, chosen.sample_date)
                   THEN 1 ELSE 0
               END) AS was_active
    FROM #Samples AS chosen
    JOIN #Weeks AS weeks
      ON chosen.sample_date >= weeks.m365_from
     AND chosen.sample_date <= weeks.m365_to
    JOIN dbo.teams_user_activity_log AS activity
      ON activity.[date] >= chosen.sample_date
     AND activity.[date] < DATEADD(DAY, 1, chosen.sample_date)
    JOIN #EligibleUsers AS eligible ON eligible.user_id = activity.user_id
    WHERE chosen.workload = 1
    GROUP BY activity.user_id, chosen.sample_date

    UNION ALL

    SELECT 2,
           activity.user_id,
           chosen.sample_date,
           MAX(CASE
                   WHEN activity.last_activity_date >= weeks.m365_from
                    AND activity.last_activity_date < DATEADD(DAY, 1, weeks.m365_to)
                    AND activity.last_activity_date < DATEADD(DAY, 1, chosen.sample_date)
                   THEN 1 ELSE 0
               END)
    FROM #Samples AS chosen
    JOIN #Weeks AS weeks
      ON chosen.sample_date >= weeks.m365_from
     AND chosen.sample_date <= weeks.m365_to
    JOIN dbo.outlook_user_activity_log AS activity
      ON activity.[date] >= chosen.sample_date
     AND activity.[date] < DATEADD(DAY, 1, chosen.sample_date)
    JOIN #EligibleUsers AS eligible ON eligible.user_id = activity.user_id
    WHERE chosen.workload = 2
    GROUP BY activity.user_id, chosen.sample_date

    UNION ALL

    SELECT 3,
           activity.user_id,
           chosen.sample_date,
           MAX(CASE
                   WHEN activity.last_activity_date >= weeks.m365_from
                    AND activity.last_activity_date < DATEADD(DAY, 1, weeks.m365_to)
                    AND activity.last_activity_date < DATEADD(DAY, 1, chosen.sample_date)
                   THEN 1 ELSE 0
               END)
    FROM #Samples AS chosen
    JOIN #Weeks AS weeks
      ON chosen.sample_date >= weeks.m365_from
     AND chosen.sample_date <= weeks.m365_to
    JOIN dbo.onedrive_user_activity_log AS activity
      ON activity.[date] >= chosen.sample_date
     AND activity.[date] < DATEADD(DAY, 1, chosen.sample_date)
    JOIN #EligibleUsers AS eligible ON eligible.user_id = activity.user_id
    WHERE chosen.workload = 3
    GROUP BY activity.user_id, chosen.sample_date

    UNION ALL

    SELECT 4,
           activity.user_id,
           chosen.sample_date,
           MAX(CASE
                   WHEN activity.last_activity_date >= weeks.m365_from
                    AND activity.last_activity_date < DATEADD(DAY, 1, weeks.m365_to)
                    AND activity.last_activity_date < DATEADD(DAY, 1, chosen.sample_date)
                   THEN 1 ELSE 0
               END)
    FROM #Samples AS chosen
    JOIN #Weeks AS weeks
      ON chosen.sample_date >= weeks.m365_from
     AND chosen.sample_date <= weeks.m365_to
    JOIN dbo.sharepoint_user_activity_log AS activity
      ON activity.[date] >= chosen.sample_date
     AND activity.[date] < DATEADD(DAY, 1, chosen.sample_date)
    JOIN #EligibleUsers AS eligible ON eligible.user_id = activity.user_id
    WHERE chosen.workload = 4
    GROUP BY activity.user_id, chosen.sample_date
),
PerUser AS
(
    SELECT workload,
           user_id,
           SUM(was_active) AS active_samples,
           COUNT(*) AS observed_samples
    FROM PerSample
    GROUP BY workload, user_id
)
INSERT #Scores
    (workload, user_id, active_samples, observed_samples, frequency_known)
SELECT workload,
       user_id,
       active_samples,
       observed_samples,
       CAST(CASE
                WHEN observed_samples =
                     CASE workload
                         WHEN 1 THEN @expected1
                         WHEN 2 THEN @expected2
                         WHEN 3 THEN @expected3
                         ELSE @expected4
                     END
                THEN 1 ELSE 0
            END AS bit)
FROM PerUser
OPTION (RECOMPILE);
";

        private static string CopilotD7ActivityExpression(string from, string endExclusive)
        {
            // Counters describe this period; the user-level date is only a v1-shaped fallback.
            return @"CASE
                WHEN report.active_usage_days BETWEEN 1 AND 7
                  OR report.prompts_all_apps > 0 THEN 1
                WHEN report.active_usage_days IS NULL
                 AND report.prompts_all_apps IS NULL
                 AND report.last_activity_date >= " + from + @"
                 AND report.last_activity_date < " + endExclusive + @" THEN 1
                ELSE 0
            END";
        }

        private static readonly string CopilotOfficialOverview = @"
DECLARE @copilotLogImported datetime = NULL;
DECLARE @copilotLogObfuscated bit = 0;
DECLARE @copilotLogRowsRead int = 0;
DECLARE @copilotLogError nvarchar(1000) = NULL;

SELECT TOP (1)
       @copilotLogImported = imported_utc,
       @copilotLogObfuscated = is_upn_obfuscated,
       @copilotLogRowsRead = rows_read,
       @copilotLogError = error
FROM dbo.copilot_usage_report_import_log
WHERE report_name = N'getMicrosoft365CopilotUsageUserDetail'
ORDER BY imported_utc DESC, id DESC;

DECLARE @copilotPreferredStatus varchar(32) =
    CASE
        WHEN @copilotLogObfuscated = 1 THEN 'unmatchableIdentity'
        WHEN @copilotLogImported IS NULL THEN 'notImported'
        WHEN @copilotLogError IS NOT NULL THEN 'partial'
        ELSE 'missingCoverage'
    END;
DECLARE @copilotPreferredSource varchar(64) = 'microsoftGraphCopilotUsageReport';
DECLARE @copilotPreferredMessage nvarchar(800) =
    CASE
        WHEN @copilotLogObfuscated = 1
            THEN N'The official Copilot report concealed every user identity, so it cannot be joined to licence holders.'
        WHEN @copilotLogImported IS NULL
            THEN N'The official per-user Copilot usage report has not been imported.'
        WHEN @copilotLogError IS NOT NULL
            THEN N'The latest official per-user Copilot report import failed; absence cannot be interpreted as zero.'
        ELSE N'No fully-contained official Copilot report window covers the requested range.'
    END;
DECLARE @copilotLatestImport datetime = @copilotLogImported;
DECLARE @copilotUnmatched int =
    CASE WHEN @copilotLogObfuscated = 1 THEN @copilotLogRowsRead ELSE 0 END;
DECLARE @copilotNeedsFallback bit = 1;

IF @copilotLogObfuscated = 0 AND @copilotLogError IS NULL
BEGIN
    DECLARE @copilotD7Expected int =
    (
        SELECT COUNT(*)
        FROM #Weeks
        WHERE copilot_d7_from <= copilot_d7_to
    );

    IF EXISTS
    (
        SELECT 1
        FROM dbo.copilot_usage_user_activity_log
        WHERE report_period_days = 7
          AND [date] >= DATEADD(DAY, 6, @from)
          AND [date] < DATEADD(DAY, 1, @to)
          AND [date] < DATEADD(DAY, 1, @settled)
    )
    BEGIN
        INSERT #Samples (workload, sample_date)
        SELECT 5, selected.sample_date
        FROM #Weeks AS weeks
        CROSS APPLY
        (
            SELECT TOP (1) CAST(available.[date] AS date) AS sample_date
            FROM dbo.copilot_usage_user_activity_log AS available
                 WITH (INDEX(IX_date_user_id_report_period_days))
            WHERE available.report_period_days = 7
              AND available.[date] >= weeks.copilot_d7_from
              AND available.[date] < DATEADD(DAY, 1, weeks.copilot_d7_to)
              AND available.[date] < DATEADD(DAY, 1, @settled)
            ORDER BY available.[date] DESC
        ) AS selected
        WHERE weeks.copilot_d7_from <= weeks.copilot_d7_to
          AND weeks.copilot_d7_from <= @settled
        OPTION (RECOMPILE);
    END;

    DECLARE @copilotD7Observed int =
        (SELECT COUNT(*) FROM #Samples WHERE workload = 5);
    DECLARE @copilotD7EffectiveFrom date =
        (SELECT DATEADD(DAY, -6, MIN(sample_date)) FROM #Samples WHERE workload = 5);
    DECLARE @copilotD7EffectiveTo date =
        (SELECT MAX(sample_date) FROM #Samples WHERE workload = 5);
    DECLARE @copilotD7Complete int =
    (
        SELECT COUNT(*)
        FROM #Weeks AS weeks
        JOIN #Samples AS samples
          ON samples.workload = 5
         AND samples.sample_date = weeks.copilot_d7_to
        WHERE weeks.copilot_d7_from <= weeks.copilot_d7_to
          AND weeks.copilot_d7_to <= @settled
    );

    IF @copilotD7Observed > 0
    BEGIN
        SET @copilotNeedsFallback = 0;
        SET @copilotPreferredStatus =
            CASE WHEN @copilotD7Expected = @copilotD7Observed
                       AND @copilotD7Expected = @copilotD7Complete
                       AND @copilotD7EffectiveFrom = @from
                       AND @copilotD7EffectiveTo = @to
                 THEN 'available' ELSE 'partial' END;

        INSERT #Coverage
        (
            workload, workload_name, status, source, measure, granularity, message,
            effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
            report_period_days, expected_samples, observed_samples, unmatched_users
        )
        SELECT 5, 'copilot', @copilotPreferredStatus, 'microsoftGraphCopilotUsageReport',
               N'average prompts in sampled rolling 7-day reports',
               'weeklySampleOfRolling7DayReport',
               CASE WHEN @copilotPreferredStatus = 'available'
                    THEN N'The latest settled, fully-contained D7 report was sampled once per calendar week; overlapping counts are averaged, never summed. Missing user rows and rows without v2 counters remain unknown. The official report covers Copilot-licensed users only.'
                    ELSE N'At least one requested D7 window lacks a settled snapshot on its exact end date. Earlier snapshots are as-of evidence only; all bands remain unknown and no user is ranked least-active.'
               END,
               CAST(@copilotD7EffectiveFrom AS datetime),
               CAST(@copilotD7EffectiveTo AS datetime),
               @copilotLogImported,
               DATEDIFF(DAY, MAX(samples.sample_date), @now),
               7, @copilotD7Expected, @copilotD7Observed, 0
        FROM (SELECT sample_date FROM #Samples WHERE workload = 5) AS samples;

        IF @copilotPreferredStatus = 'available'
        BEGIN
            IF @copilotD7Expected = 1
            BEGIN
                DECLARE @copilotSingleSample date =
                    (SELECT sample_date FROM #Samples WHERE workload = 5);
                INSERT #Scores
                    (workload, user_id, active_samples, observed_samples, frequency_known)
                SELECT 5,
                       report.user_id,
                       " + CopilotD7ActivityExpression(
                           "DATEADD(DAY, -6, report.[date])", "DATEADD(DAY, 1, report.[date])") + @",
                       1,
                       CAST(CASE
                           WHEN report.active_usage_days BETWEEN 0 AND 7
                            AND (report.prompts_all_apps IS NULL OR report.prompts_all_apps >= 0)
                           THEN 1 ELSE 0
                       END AS bit)
                FROM dbo.copilot_usage_user_activity_log AS report
                JOIN #EligibleUsers AS eligible ON eligible.user_id = report.user_id
                WHERE report.report_period_days = 7
                  AND report.[date] >= @copilotSingleSample
                  AND report.[date] < DATEADD(DAY, 1, @copilotSingleSample)
                OPTION (RECOMPILE, MAXDOP 1);
            END
            ELSE
            BEGIN
                INSERT #Scores
                    (workload, user_id, active_samples, observed_samples, frequency_known)
                SELECT 5, report.user_id,
                       SUM(" + CopilotD7ActivityExpression(
                           "DATEADD(DAY, -6, report.[date])", "DATEADD(DAY, 1, report.[date])") + @"),
                       COUNT(*),
                       CAST(CASE
                                WHEN COUNT(*) = @copilotD7Expected
                                 AND MIN(CASE WHEN report.active_usage_days BETWEEN 0 AND 7
                                               AND (report.prompts_all_apps IS NULL
                                                    OR report.prompts_all_apps >= 0)
                                              THEN 1 ELSE 0 END) = 1
                                THEN 1 ELSE 0
                            END AS bit)
                FROM dbo.copilot_usage_user_activity_log AS report WITH (INDEX(0))
                JOIN #Samples AS chosen
                  ON chosen.workload = 5
                 AND CAST(report.[date] AS date) = chosen.sample_date
                JOIN #EligibleUsers AS eligible ON eligible.user_id = report.user_id
                WHERE report.report_period_days = 7
                  AND report.[date] >= DATEADD(DAY, 6, @from)
                  AND report.[date] < @endExclusive
                  AND report.[date] < DATEADD(DAY, 1, @settled)
                GROUP BY report.user_id
                OPTION (RECOMPILE, MAXDOP 4);
            END
        END
    END
    ELSE
    BEGIN
        -- Stored periods are read from the (date, user, report_period_days) key. A longer rolling
        -- window is never repeated or summed: at most one fully-contained snapshot is exposed,
        -- with its exact effective range.
        DECLARE @copilotLongPeriod int = NULL;
        DECLARE @copilotLongDate date = NULL;

        SELECT TOP (1)
               @copilotLongPeriod = report.report_period_days,
               @copilotLongDate = CAST(report.[date] AS date)
        FROM dbo.copilot_usage_user_activity_log AS report
        WHERE report.report_period_days IN (28, 90, 180)
          AND report.[date] < DATEADD(DAY, 1, @settled)
          AND report.[date] < DATEADD(DAY, 1, @to)
          AND DATEADD(DAY, 1 - report.report_period_days, CAST(report.[date] AS date)) >= @from
        GROUP BY report.report_period_days, CAST(report.[date] AS date)
        ORDER BY CASE
                     WHEN CAST(report.[date] AS date) = @to
                      AND DATEADD(DAY, 1 - report.report_period_days, CAST(report.[date] AS date)) = @from
                     THEN 0 ELSE 1
                 END,
                 report.report_period_days DESC,
                 CAST(report.[date] AS date) DESC
        OPTION (RECOMPILE);

        IF @copilotLongPeriod IS NOT NULL
        BEGIN
            SET @copilotNeedsFallback = 0;
            SET @copilotPreferredStatus =
                CASE WHEN @copilotLongDate = @to
                       AND DATEADD(DAY, 1 - @copilotLongPeriod, @copilotLongDate) = @from
                     THEN 'available' ELSE 'missingCoverage' END;

            INSERT #Samples (workload, sample_date) VALUES (5, @copilotLongDate);

            INSERT #Coverage
            (
                workload, workload_name, status, source, measure, granularity, message,
                effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
                report_period_days, expected_samples, observed_samples, unmatched_users
            )
            VALUES
            (
                5, 'copilot', @copilotPreferredStatus, 'microsoftGraphCopilotUsageReport',
                N'prompts and active usage days in one rolling report',
                'singleRollingWindow',
                CASE WHEN @copilotPreferredStatus = 'available'
                     THEN N'The requested dates exactly match one official rolling Copilot report window. Missing user rows and rows without active_usage_days remain unknown. The official report covers Copilot-licensed users only.'
                     ELSE N'The source only has a longer rolling window inside the request. Its evidence is shown with the exact effective dates, but bands remain unknown for the custom range.'
                END,
                DATEADD(DAY, 1 - @copilotLongPeriod, CAST(@copilotLongDate AS datetime)),
                CAST(@copilotLongDate AS datetime),
                @copilotLogImported,
                DATEDIFF(DAY, @copilotLongDate, @now),
                @copilotLongPeriod, @copilotLongPeriod, @copilotLongPeriod, 0
            );

            INSERT #Scores
                (workload, user_id, active_samples, observed_samples, frequency_known)
            SELECT 5,
                   report.user_id,
                   CASE
                       WHEN report.active_usage_days BETWEEN 0 AND @copilotLongPeriod
                           THEN report.active_usage_days
                       WHEN report.prompts_all_apps > 0
                         OR (report.last_activity_date >= DATEADD(DAY, 1 - @copilotLongPeriod, @copilotLongDate)
                             AND report.last_activity_date < DATEADD(DAY, 1, @copilotLongDate))
                           THEN 1 ELSE 0
                   END,
                   CASE
                       WHEN report.active_usage_days BETWEEN 0 AND @copilotLongPeriod
                        AND (report.prompts_all_apps IS NULL OR report.prompts_all_apps >= 0)
                           THEN @copilotLongPeriod ELSE 0
                   END,
                   CAST(CASE
                       WHEN report.active_usage_days BETWEEN 0 AND @copilotLongPeriod
                        AND (report.prompts_all_apps IS NULL OR report.prompts_all_apps >= 0)
                           THEN 1 ELSE 0
                   END AS bit)
            FROM dbo.copilot_usage_user_activity_log AS report WITH (INDEX(0))
            JOIN #EligibleUsers AS eligible ON eligible.user_id = report.user_id
            WHERE report.report_period_days = @copilotLongPeriod
              AND report.[date] >= @copilotLongDate
              AND report.[date] < DATEADD(DAY, 1, @copilotLongDate)
            OPTION (RECOMPILE);
        END
    END
END;
";

        private static readonly string CopilotAuditFallbackOverview = @"
IF @copilotNeedsFallback = 1
   AND EXISTS
   (
       SELECT 1
       FROM dbo.copilot_chats
       WHERE time_stamp >= @from
         AND time_stamp < @endExclusive
         AND user_id IS NOT NULL
   )
BEGIN
    SET @copilotNeedsFallback = 0;
    SET @copilotPreferredStatus =
        CASE WHEN @copilotPreferredStatus = 'unmatchableIdentity'
             THEN 'unmatchableIdentity' ELSE 'partial' END;
    SET @copilotPreferredMessage =
        CASE WHEN @copilotPreferredStatus = 'unmatchableIdentity'
             THEN N'The official report identities are concealed. Audit events provide positive evidence, but their absence is not a measured zero.'
             ELSE N'Copilot audit events provide positive evidence only; no database signal proves complete event coverage, so absent users remain unknown.'
        END;

    ;WITH EventWeeks AS
    (
        SELECT chats.user_id,
               DATEADD(DAY,
                   -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(chats.time_stamp AS date)) % 7) + 7) % 7),
                   CAST(chats.time_stamp AS date)) AS week_start,
               COUNT_BIG(*) AS actions,
               MAX(chats.time_stamp) AS last_activity_utc
        FROM dbo.copilot_chats AS chats
        JOIN #EligibleUsers AS eligible ON eligible.user_id = chats.user_id
        WHERE chats.time_stamp >= @from
          AND chats.time_stamp < @endExclusive
        GROUP BY chats.user_id,
                 DATEADD(DAY,
                    -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(chats.time_stamp AS date)) % 7) + 7) % 7),
                    CAST(chats.time_stamp AS date))
    )
    INSERT #Scores
        (workload, user_id, active_samples, observed_samples, frequency_known)
    SELECT 5, user_id, COUNT(*), 0, 0
    FROM EventWeeks
    GROUP BY user_id
    OPTION (RECOMPILE);

    INSERT #Coverage
    (
        workload, workload_name, status, source, measure, granularity, message,
        effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
        report_period_days, expected_samples, observed_samples, unmatched_users
    )
    SELECT 5, 'copilot', @copilotPreferredStatus, 'copilotAudit',
           N'interactions per active calendar week', 'eventPositiveOnly',
           @copilotPreferredMessage, MIN(time_stamp), MAX(time_stamp),
           NULL,
           DATEDIFF(DAY, MAX(CAST(time_stamp AS date)), @now),
           NULL, (SELECT COUNT(*) FROM #Weeks), 0, @copilotUnmatched
    FROM dbo.copilot_chats
    WHERE time_stamp >= @from AND time_stamp < @endExclusive;
END;
";

        private static readonly string CopilotInteractionFallbackOverview = @"
IF @copilotNeedsFallback = 1
   AND EXISTS
   (
       SELECT 1
       FROM dbo.copilot_interactions
       WHERE created_utc >= @from
         AND created_utc < @endExclusive
   )
BEGIN
    SET @copilotNeedsFallback = 0;
    SET @copilotPreferredStatus =
        CASE WHEN @copilotPreferredStatus = 'unmatchableIdentity'
             THEN 'unmatchableIdentity' ELSE 'partial' END;
    SET @copilotPreferredMessage =
        CASE WHEN @copilotPreferredStatus = 'unmatchableIdentity'
             THEN N'The official report identities are concealed. Interaction history provides positive evidence, but its absence is not a measured zero.'
             ELSE N'Copilot interaction history provides positive evidence only; no database signal proves complete history for every user, so absent users remain unknown.'
        END;

    ;WITH EventWeeks AS
    (
        SELECT interactions.user_id,
               DATEADD(DAY,
                   -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(interactions.created_utc AS date)) % 7) + 7) % 7),
                   CAST(interactions.created_utc AS date)) AS week_start,
               COUNT_BIG(*) AS actions,
               MAX(interactions.created_utc) AS last_activity_utc
        FROM dbo.copilot_interactions AS interactions
        JOIN #EligibleUsers AS eligible ON eligible.user_id = interactions.user_id
        WHERE interactions.created_utc >= @from
          AND interactions.created_utc < @endExclusive
        GROUP BY interactions.user_id,
                 DATEADD(DAY,
                    -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(interactions.created_utc AS date)) % 7) + 7) % 7),
                    CAST(interactions.created_utc AS date))
    )
    INSERT #Scores
        (workload, user_id, active_samples, observed_samples, frequency_known)
    SELECT 5, user_id, COUNT(*), 0, 0
    FROM EventWeeks
    GROUP BY user_id
    OPTION (RECOMPILE);

    INSERT #Coverage
    (
        workload, workload_name, status, source, measure, granularity, message,
        effective_from_utc, effective_to_utc, latest_import_utc, lag_days,
        report_period_days, expected_samples, observed_samples, unmatched_users
    )
    SELECT 5, 'copilot', @copilotPreferredStatus, 'copilotInteractions',
           N'interaction rows per active calendar week', 'eventPositiveOnly',
           @copilotPreferredMessage, MIN(created_utc), MAX(created_utc),
           (SELECT MAX(run_finished_utc) FROM dbo.copilot_interaction_import_log),
           CASE WHEN MAX(CAST(created_utc AS date)) IS NULL THEN 0
                ELSE DATEDIFF(DAY, MAX(CAST(created_utc AS date)), @now) END,
           NULL, (SELECT COUNT(*) FROM #Weeks), 0, @copilotUnmatched
    FROM dbo.copilot_interactions
    WHERE created_utc >= @from AND created_utc < @endExclusive;
END;
";

        private static string BuildSingleWorkloadProjection(int workload, bool includeBase)
        {
            var result = string.Format(
                CultureInfo.InvariantCulture,
                @"
CREATE TABLE #Demographics
(
    dimension tinyint NOT NULL,
    demographic_id int NOT NULL,
    assigned_users int NOT NULL,
    PRIMARY KEY (dimension, demographic_id)
);

;WITH DepartmentCounts AS
(
    SELECT department_id AS demographic_id, COUNT(*) AS assigned_users
    FROM #EligibleUsers
    GROUP BY department_id
),
Ranked AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_id) AS row_number
    FROM DepartmentCounts
)
INSERT #Demographics
SELECT 1, demographic_id, assigned_users
FROM Ranked
WHERE row_number <= 50
   OR (@departmentId IS NOT NULL AND demographic_id = @departmentId);

;WITH CountryCounts AS
(
    SELECT country_id AS demographic_id, COUNT(*) AS assigned_users
    FROM #EligibleUsers
    GROUP BY country_id
),
Ranked AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_id) AS row_number
    FROM CountryCounts
)
INSERT #Demographics
SELECT 2, demographic_id, assigned_users
FROM Ranked
WHERE row_number <= 50
   OR (@countryId IS NOT NULL AND demographic_id = @countryId);

CREATE TABLE #Known
(
    user_id int NOT NULL,
    band tinyint NOT NULL
);

INSERT #Known (user_id, band)
SELECT scores.user_id,
       CASE
           WHEN scores.active_samples = 0 THEN 0
           WHEN CAST(scores.active_samples AS bigint) * 4 < coverage.expected_samples THEN 1
           WHEN CAST(scores.active_samples AS bigint) * 4
                < CAST(coverage.expected_samples AS bigint) * 3 THEN 2
           ELSE 3
       END
FROM #Scores AS scores
JOIN #Coverage AS coverage ON coverage.workload = {0}
WHERE scores.workload = {0}
  AND coverage.status = 'available'
  AND
  (
      (coverage.source IN ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
       AND scores.frequency_known = 1
       AND scores.observed_samples = coverage.expected_samples)
      OR coverage.source NOT IN ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
  )
OPTION (RECOMPILE);

SELECT coverage.workload_name AS Workload,
       coverage.status AS Status,
       coverage.source AS Source,
       coverage.measure AS Measure,
       coverage.granularity AS Granularity,
       coverage.message AS Message,
       coverage.effective_from_utc AS EffectiveFromUtc,
       coverage.effective_to_utc AS EffectiveToUtc,
       coverage.latest_import_utc AS LatestImportUtc,
       coverage.lag_days AS LagDays,
       coverage.report_period_days AS ReportPeriodDays,
       coverage.expected_samples AS ExpectedSamples,
       coverage.observed_samples AS ObservedSamples,
       coverage.unmatched_users AS UnmatchedUsers
FROM #Coverage AS coverage
WHERE coverage.workload = {0};

SELECT coverage.workload_name AS WorkloadName,
       samples.sample_date AS SnapshotDate
FROM #Samples AS samples
JOIN #Coverage AS coverage ON coverage.workload = samples.workload
WHERE samples.workload = {0}
ORDER BY samples.sample_date;

;WITH Memberships AS
(
    SELECT DISTINCT owned.license_type_id, owned.user_id
    FROM dbo.user_license_type_lookups AS owned
    JOIN #EligibleUsers AS eligible ON eligible.user_id = owned.user_id
),
LicenceBands AS
(
    SELECT members.license_type_id,
           known.band,
           COUNT(*) AS users
    FROM Memberships AS members
    LEFT JOIN #Known AS known ON known.user_id = members.user_id
    GROUP BY members.license_type_id, known.band
),
LicenceCounts AS
(
    SELECT license_type_id,
           SUM(users) AS assigned_users,
           SUM(CASE WHEN band = 3 THEN users ELSE 0 END) AS high_count,
           SUM(CASE WHEN band = 2 THEN users ELSE 0 END) AS moderate_count,
           SUM(CASE WHEN band = 1 THEN users ELSE 0 END) AS low_count,
           SUM(CASE WHEN band = 0 THEN users ELSE 0 END) AS zero_count,
           SUM(CASE WHEN band IS NOT NULL THEN users ELSE 0 END) AS known_count
    FROM LicenceBands
    GROUP BY license_type_id
)
SELECT licence.id AS LicenceTypeId,
       coverage.workload_name AS Workload,
       ISNULL(counts.high_count, 0) AS High,
       ISNULL(counts.moderate_count, 0) AS Moderate,
       ISNULL(counts.low_count, 0) AS Low,
       ISNULL(counts.zero_count, 0) AS Zero,
       ISNULL(counts.assigned_users, 0) - ISNULL(counts.known_count, 0) AS Unknown
FROM dbo.license_types AS licence
CROSS JOIN #Coverage AS coverage
LEFT JOIN LicenceCounts AS counts ON counts.license_type_id = licence.id
WHERE coverage.workload = {0}
ORDER BY licence.id
OPTION (RECOMPILE);

;WITH DepartmentBands AS
(
    SELECT eligible.department_id AS demographic_id,
           known.band,
           COUNT(*) AS users
    FROM #EligibleUsers AS eligible
    LEFT JOIN #Known AS known ON known.user_id = eligible.user_id
    GROUP BY eligible.department_id, known.band
),
CountryBands AS
(
    SELECT eligible.country_id AS demographic_id,
           known.band,
           COUNT(*) AS users
    FROM #EligibleUsers AS eligible
    LEFT JOIN #Known AS known ON known.user_id = eligible.user_id
    GROUP BY eligible.country_id, known.band
),
AllBands AS
(
    SELECT 1 AS dimension, demographic_id, band, users FROM DepartmentBands
    UNION ALL
    SELECT 2, demographic_id, band, users FROM CountryBands
),
Counts AS
(
    SELECT dimension,
           demographic_id,
           SUM(CASE WHEN band = 3 THEN users ELSE 0 END) AS high_count,
           SUM(CASE WHEN band = 2 THEN users ELSE 0 END) AS moderate_count,
           SUM(CASE WHEN band = 1 THEN users ELSE 0 END) AS low_count,
           SUM(CASE WHEN band = 0 THEN users ELSE 0 END) AS zero_count,
           SUM(CASE WHEN band IS NOT NULL THEN users ELSE 0 END) AS known_count
    FROM AllBands
    GROUP BY dimension, demographic_id
)
SELECT CASE WHEN demographics.dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       demographics.demographic_id AS Id,
       coverage.workload_name AS Workload,
       ISNULL(counts.high_count, 0) AS High,
       ISNULL(counts.moderate_count, 0) AS Moderate,
       ISNULL(counts.low_count, 0) AS Low,
       ISNULL(counts.zero_count, 0) AS Zero,
       demographics.assigned_users - ISNULL(counts.known_count, 0) AS Unknown
FROM #Demographics AS demographics
CROSS JOIN #Coverage AS coverage
LEFT JOIN Counts AS counts
  ON counts.dimension = demographics.dimension
 AND counts.demographic_id = demographics.demographic_id
WHERE coverage.workload = {0}
ORDER BY demographics.dimension, demographics.demographic_id
OPTION (RECOMPILE);
",
                workload);
            return includeBase ? result + SingleWorkloadBaseResults : result;
        }

        private static readonly string SingleWorkloadBaseResults = @"
SELECT (SELECT COUNT(*) FROM #EligibleUsers) AS DistinctAssignedUsers,
       CAST(CASE
           WHEN (SELECT COUNT(DISTINCT department_id) FROM #EligibleUsers)
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 1)
             OR (SELECT COUNT(DISTINCT country_id) FROM #EligibleUsers)
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 2)
           THEN 1 ELSE 0 END AS bit) AS DemographicsTruncated;

SELECT licence.id AS LicenceTypeId,
       licence.name AS Name,
       licence.sku_id AS SkuId,
       0 AS AssignedUsers
FROM dbo.license_types AS licence
ORDER BY licence.name, licence.id;

SELECT CASE WHEN demographics.dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       demographics.demographic_id AS Id,
       CASE WHEN demographics.dimension = 1
            THEN COALESCE(department.name, N'Unknown')
            ELSE COALESCE(country.name, N'Unknown')
       END AS Name,
       demographics.assigned_users AS AssignedUsers
FROM #Demographics AS demographics
LEFT JOIN dbo.user_departments AS department
  ON demographics.dimension = 1 AND department.id = demographics.demographic_id
LEFT JOIN dbo.user_country_or_region AS country
  ON demographics.dimension = 2 AND country.id = demographics.demographic_id
ORDER BY demographics.dimension, demographics.assigned_users DESC, Name, demographics.demographic_id;
";

        private static string BuildBandWriterProjection(int workload, string bandTable)
        {
            ValidateSharedBandTableName(bandTable);
            return string.Format(
                CultureInfo.InvariantCulture,
                @"
INSERT {1} (user_id, band)
SELECT scores.user_id,
       CASE
           WHEN scores.active_samples = 0 THEN 0
           WHEN CAST(scores.active_samples AS bigint) * 4 < coverage.expected_samples THEN 1
           WHEN CAST(scores.active_samples AS bigint) * 4
                < CAST(coverage.expected_samples AS bigint) * 3 THEN 2
           ELSE 3
       END
FROM #Scores AS scores
JOIN #Coverage AS coverage ON coverage.workload = {0}
WHERE scores.workload = {0}
  AND coverage.status = 'available'
  AND
  (
      (coverage.source IN ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
       AND scores.frequency_known = 1
       AND scores.observed_samples = coverage.expected_samples)
      OR coverage.source NOT IN ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
  )
OPTION (RECOMPILE);

SELECT coverage.workload_name AS Workload,
       coverage.status AS Status,
       coverage.source AS Source,
       coverage.measure AS Measure,
       coverage.granularity AS Granularity,
       coverage.message AS Message,
       coverage.effective_from_utc AS EffectiveFromUtc,
       coverage.effective_to_utc AS EffectiveToUtc,
       coverage.latest_import_utc AS LatestImportUtc,
       coverage.lag_days AS LagDays,
       coverage.report_period_days AS ReportPeriodDays,
       coverage.expected_samples AS ExpectedSamples,
       coverage.observed_samples AS ObservedSamples,
       coverage.unmatched_users AS UnmatchedUsers
FROM #Coverage AS coverage
WHERE coverage.workload = {0};

SELECT coverage.workload_name AS WorkloadName,
       samples.sample_date AS SnapshotDate
FROM #Samples AS samples
JOIN #Coverage AS coverage ON coverage.workload = samples.workload
WHERE samples.workload = {0}
ORDER BY samples.sample_date;
",
                workload,
                bandTable);
        }

        private static readonly string OverviewProjection = @"
CREATE TABLE #LicenceCounts
(
    licence_type_id int NOT NULL PRIMARY KEY,
    assigned_users int NOT NULL
);

CREATE TABLE #LicenceDistributions
(
    licence_type_id int NOT NULL,
    workload tinyint NOT NULL,
    high_count int NOT NULL,
    moderate_count int NOT NULL,
    low_count int NOT NULL,
    zero_count int NOT NULL,
    unknown_count int NOT NULL,
    PRIMARY KEY (licence_type_id, workload)
);

;WITH KnownScores AS
(
    SELECT scores.workload,
           scores.user_id,
           CASE
               WHEN scores.active_samples = 0 THEN 0
               WHEN CAST(scores.active_samples AS bigint) * 4 < coverage.expected_samples THEN 1
               WHEN CAST(scores.active_samples AS bigint) * 4
                    < CAST(coverage.expected_samples AS bigint) * 3 THEN 2
               ELSE 3
           END AS band
    FROM #Scores AS scores
    JOIN #Coverage AS coverage ON coverage.workload = scores.workload
    WHERE coverage.status = 'available'
      AND
      (
          (coverage.source IN ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
           AND scores.frequency_known = 1
           AND scores.observed_samples = coverage.expected_samples)
          OR coverage.source NOT IN ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
      )
)
SELECT eligible.user_id,
       eligible.department_id,
       eligible.country_id,
       MAX(CASE WHEN known.workload = 1 THEN known.band END) AS teams_band,
       MAX(CASE WHEN known.workload = 2 THEN known.band END) AS outlook_band,
       MAX(CASE WHEN known.workload = 3 THEN known.band END) AS onedrive_band,
       MAX(CASE WHEN known.workload = 4 THEN known.band END) AS sharepoint_band,
       MAX(CASE WHEN known.workload = 5 THEN known.band END) AS copilot_band
INTO #UserBands
FROM #EligibleUsers AS eligible
LEFT JOIN KnownScores AS known ON known.user_id = eligible.user_id
GROUP BY eligible.user_id, eligible.department_id, eligible.country_id
OPTION (RECOMPILE);

CREATE UNIQUE CLUSTERED INDEX IX_LicenceActivity_UserBands ON #UserBands (user_id);

;WITH Memberships AS
(
    SELECT DISTINCT license_type_id, user_id
    FROM dbo.user_license_type_lookups
),
MembershipBandGroups AS
(
    SELECT members.license_type_id,
           bands.teams_band,
           bands.outlook_band,
           bands.onedrive_band,
           bands.sharepoint_band,
           bands.copilot_band,
           COUNT(*) AS users
    FROM Memberships AS members
    JOIN #UserBands AS bands ON bands.user_id = members.user_id
    GROUP BY members.license_type_id,
             bands.teams_band,
             bands.outlook_band,
             bands.onedrive_band,
             bands.sharepoint_band,
             bands.copilot_band
),
BandCounts AS
(
    SELECT license_type_id AS licence_type_id,
           SUM(users) AS assigned_users,
           SUM(CASE WHEN teams_band = 3 THEN users ELSE 0 END) AS teams_high,
           SUM(CASE WHEN teams_band = 2 THEN users ELSE 0 END) AS teams_moderate,
           SUM(CASE WHEN teams_band = 1 THEN users ELSE 0 END) AS teams_low,
           SUM(CASE WHEN teams_band = 0 THEN users ELSE 0 END) AS teams_zero,
           SUM(CASE WHEN teams_band IS NOT NULL THEN users ELSE 0 END) AS teams_known,
           SUM(CASE WHEN outlook_band = 3 THEN users ELSE 0 END) AS outlook_high,
           SUM(CASE WHEN outlook_band = 2 THEN users ELSE 0 END) AS outlook_moderate,
           SUM(CASE WHEN outlook_band = 1 THEN users ELSE 0 END) AS outlook_low,
           SUM(CASE WHEN outlook_band = 0 THEN users ELSE 0 END) AS outlook_zero,
           SUM(CASE WHEN outlook_band IS NOT NULL THEN users ELSE 0 END) AS outlook_known,
           SUM(CASE WHEN onedrive_band = 3 THEN users ELSE 0 END) AS onedrive_high,
           SUM(CASE WHEN onedrive_band = 2 THEN users ELSE 0 END) AS onedrive_moderate,
           SUM(CASE WHEN onedrive_band = 1 THEN users ELSE 0 END) AS onedrive_low,
           SUM(CASE WHEN onedrive_band = 0 THEN users ELSE 0 END) AS onedrive_zero,
           SUM(CASE WHEN onedrive_band IS NOT NULL THEN users ELSE 0 END) AS onedrive_known,
           SUM(CASE WHEN sharepoint_band = 3 THEN users ELSE 0 END) AS sharepoint_high,
           SUM(CASE WHEN sharepoint_band = 2 THEN users ELSE 0 END) AS sharepoint_moderate,
           SUM(CASE WHEN sharepoint_band = 1 THEN users ELSE 0 END) AS sharepoint_low,
           SUM(CASE WHEN sharepoint_band = 0 THEN users ELSE 0 END) AS sharepoint_zero,
           SUM(CASE WHEN sharepoint_band IS NOT NULL THEN users ELSE 0 END) AS sharepoint_known,
           SUM(CASE WHEN copilot_band = 3 THEN users ELSE 0 END) AS copilot_high,
           SUM(CASE WHEN copilot_band = 2 THEN users ELSE 0 END) AS copilot_moderate,
           SUM(CASE WHEN copilot_band = 1 THEN users ELSE 0 END) AS copilot_low,
           SUM(CASE WHEN copilot_band = 0 THEN users ELSE 0 END) AS copilot_zero,
           SUM(CASE WHEN copilot_band IS NOT NULL THEN users ELSE 0 END) AS copilot_known
    FROM MembershipBandGroups
    GROUP BY license_type_id
)
SELECT *
INTO #LicenceBandCounts
FROM BandCounts
OPTION (RECOMPILE);

INSERT #LicenceCounts (licence_type_id, assigned_users)
SELECT licence.id, ISNULL(bands.assigned_users, 0)
FROM dbo.license_types AS licence
LEFT JOIN #LicenceBandCounts AS bands ON bands.licence_type_id = licence.id;

INSERT #LicenceDistributions
SELECT counts.licence_type_id,
       values_by_workload.workload,
       values_by_workload.high_count,
       values_by_workload.moderate_count,
       values_by_workload.low_count,
       values_by_workload.zero_count,
       counts.assigned_users - values_by_workload.known_count
FROM #LicenceCounts AS counts
LEFT JOIN #LicenceBandCounts AS bands ON bands.licence_type_id = counts.licence_type_id
CROSS APPLY
(
    VALUES
      (1, ISNULL(bands.teams_high, 0), ISNULL(bands.teams_moderate, 0),
          ISNULL(bands.teams_low, 0), ISNULL(bands.teams_zero, 0), ISNULL(bands.teams_known, 0)),
      (2, ISNULL(bands.outlook_high, 0), ISNULL(bands.outlook_moderate, 0),
          ISNULL(bands.outlook_low, 0), ISNULL(bands.outlook_zero, 0), ISNULL(bands.outlook_known, 0)),
      (3, ISNULL(bands.onedrive_high, 0), ISNULL(bands.onedrive_moderate, 0),
          ISNULL(bands.onedrive_low, 0), ISNULL(bands.onedrive_zero, 0), ISNULL(bands.onedrive_known, 0)),
      (4, ISNULL(bands.sharepoint_high, 0), ISNULL(bands.sharepoint_moderate, 0),
          ISNULL(bands.sharepoint_low, 0), ISNULL(bands.sharepoint_zero, 0), ISNULL(bands.sharepoint_known, 0)),
      (5, ISNULL(bands.copilot_high, 0), ISNULL(bands.copilot_moderate, 0),
          ISNULL(bands.copilot_low, 0), ISNULL(bands.copilot_zero, 0), ISNULL(bands.copilot_known, 0))
) AS values_by_workload
    (workload, high_count, moderate_count, low_count, zero_count, known_count)
OPTION (RECOMPILE);

DROP TABLE #LicenceBandCounts;

CREATE TABLE #Demographics
(
    dimension tinyint NOT NULL,
    demographic_id int NOT NULL,
    demographic_name nvarchar(100) NOT NULL,
    assigned_users int NOT NULL,
    PRIMARY KEY (dimension, demographic_id)
);

;WITH DepartmentCounts AS
(
    SELECT eligible.department_id AS demographic_id,
           COALESCE(department.name, N'Unknown') AS demographic_name,
           COUNT(*) AS assigned_users
    FROM #EligibleUsers AS eligible
    LEFT JOIN dbo.user_departments AS department ON department.id = eligible.department_id
    GROUP BY eligible.department_id, department.name
),
RankedDepartments AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_name, demographic_id) AS row_number
    FROM DepartmentCounts
)
INSERT #Demographics (dimension, demographic_id, demographic_name, assigned_users)
SELECT 1, demographic_id, demographic_name, assigned_users
FROM RankedDepartments
WHERE row_number <= 50
   OR (@departmentId IS NOT NULL AND demographic_id = @departmentId);

IF @departmentId IS NOT NULL
   AND NOT EXISTS
       (SELECT 1 FROM #Demographics WHERE dimension = 1 AND demographic_id = @departmentId)
BEGIN
    INSERT #Demographics (dimension, demographic_id, demographic_name, assigned_users)
    SELECT 1, @departmentId, COALESCE(department.name, N'Unknown'), 0
    FROM (SELECT @departmentId AS id) AS selected
    LEFT JOIN dbo.user_departments AS department ON department.id = selected.id;
END;

;WITH CountryCounts AS
(
    SELECT eligible.country_id AS demographic_id,
           COALESCE(country.name, N'Unknown') AS demographic_name,
           COUNT(*) AS assigned_users
    FROM #EligibleUsers AS eligible
    LEFT JOIN dbo.user_country_or_region AS country ON country.id = eligible.country_id
    GROUP BY eligible.country_id, country.name
),
RankedCountries AS
(
    SELECT *, ROW_NUMBER() OVER
        (ORDER BY assigned_users DESC, demographic_name, demographic_id) AS row_number
    FROM CountryCounts
)
INSERT #Demographics (dimension, demographic_id, demographic_name, assigned_users)
SELECT 2, demographic_id, demographic_name, assigned_users
FROM RankedCountries
WHERE row_number <= 50
   OR (@countryId IS NOT NULL AND demographic_id = @countryId);

IF @countryId IS NOT NULL
   AND NOT EXISTS
       (SELECT 1 FROM #Demographics WHERE dimension = 2 AND demographic_id = @countryId)
BEGIN
    INSERT #Demographics (dimension, demographic_id, demographic_name, assigned_users)
    SELECT 2, @countryId, COALESCE(country.name, N'Unknown'), 0
    FROM (SELECT @countryId AS id) AS selected
    LEFT JOIN dbo.user_country_or_region AS country ON country.id = selected.id;
END;

CREATE TABLE #DemographicDistributions
(
    dimension tinyint NOT NULL,
    demographic_id int NOT NULL,
    workload tinyint NOT NULL,
    high_count int NOT NULL,
    moderate_count int NOT NULL,
    low_count int NOT NULL,
    zero_count int NOT NULL,
    unknown_count int NOT NULL,
    PRIMARY KEY (dimension, demographic_id, workload)
);

;WITH DepartmentCounts AS
(
    SELECT band_values.workload,
           bands.department_id AS demographic_id,
           SUM(CASE WHEN band_values.band = 3 THEN 1 ELSE 0 END) AS high_count,
           SUM(CASE WHEN band_values.band = 2 THEN 1 ELSE 0 END) AS moderate_count,
           SUM(CASE WHEN band_values.band = 1 THEN 1 ELSE 0 END) AS low_count,
           SUM(CASE WHEN band_values.band = 0 THEN 1 ELSE 0 END) AS zero_count,
           COUNT(band_values.band) AS known_count
    FROM #UserBands AS bands
    CROSS APPLY
    (
        VALUES (1, bands.teams_band),
               (2, bands.outlook_band),
               (3, bands.onedrive_band),
               (4, bands.sharepoint_band),
               (5, bands.copilot_band)
    ) AS band_values (workload, band)
    GROUP BY band_values.workload, bands.department_id
)
INSERT #DemographicDistributions
SELECT 1,
       demographics.demographic_id,
       coverage.workload,
       ISNULL(counts.high_count, 0),
       ISNULL(counts.moderate_count, 0),
       ISNULL(counts.low_count, 0),
       ISNULL(counts.zero_count, 0),
       demographics.assigned_users - ISNULL(counts.known_count, 0)
FROM #Demographics AS demographics
CROSS JOIN #Coverage AS coverage
LEFT JOIN DepartmentCounts AS counts
  ON counts.demographic_id = demographics.demographic_id
 AND counts.workload = coverage.workload
WHERE demographics.dimension = 1
OPTION (RECOMPILE);

;WITH CountryCounts AS
(
    SELECT band_values.workload,
           bands.country_id AS demographic_id,
           SUM(CASE WHEN band_values.band = 3 THEN 1 ELSE 0 END) AS high_count,
           SUM(CASE WHEN band_values.band = 2 THEN 1 ELSE 0 END) AS moderate_count,
           SUM(CASE WHEN band_values.band = 1 THEN 1 ELSE 0 END) AS low_count,
           SUM(CASE WHEN band_values.band = 0 THEN 1 ELSE 0 END) AS zero_count,
           COUNT(band_values.band) AS known_count
    FROM #UserBands AS bands
    CROSS APPLY
    (
        VALUES (1, bands.teams_band),
               (2, bands.outlook_band),
               (3, bands.onedrive_band),
               (4, bands.sharepoint_band),
               (5, bands.copilot_band)
    ) AS band_values (workload, band)
    GROUP BY band_values.workload, bands.country_id
)
INSERT #DemographicDistributions
SELECT 2,
       demographics.demographic_id,
       coverage.workload,
       ISNULL(counts.high_count, 0),
       ISNULL(counts.moderate_count, 0),
       ISNULL(counts.low_count, 0),
       ISNULL(counts.zero_count, 0),
       demographics.assigned_users - ISNULL(counts.known_count, 0)
FROM #Demographics AS demographics
CROSS JOIN #Coverage AS coverage
LEFT JOIN CountryCounts AS counts
  ON counts.demographic_id = demographics.demographic_id
 AND counts.workload = coverage.workload
WHERE demographics.dimension = 2
OPTION (RECOMPILE);

SELECT (SELECT COUNT(*) FROM #EligibleUsers) AS DistinctAssignedUsers,
       CAST(CASE
           WHEN (SELECT COUNT(DISTINCT department_id) FROM #EligibleUsers)
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 1)
             OR (SELECT COUNT(DISTINCT country_id) FROM #EligibleUsers)
                    > (SELECT COUNT(*) FROM #Demographics WHERE dimension = 2)
           THEN 1 ELSE 0 END AS bit) AS DemographicsTruncated;

SELECT workload_name AS Workload,
       status AS Status,
       source AS Source,
       measure AS Measure,
       granularity AS Granularity,
       message AS Message,
       effective_from_utc AS EffectiveFromUtc,
       effective_to_utc AS EffectiveToUtc,
       latest_import_utc AS LatestImportUtc,
       lag_days AS LagDays,
       report_period_days AS ReportPeriodDays,
       expected_samples AS ExpectedSamples,
       observed_samples AS ObservedSamples,
       unmatched_users AS UnmatchedUsers
FROM #Coverage
ORDER BY workload;

SELECT WorkloadName = coverage.workload_name,
       SnapshotDate = samples.sample_date
FROM #Samples AS samples
JOIN #Coverage AS coverage ON coverage.workload = samples.workload
ORDER BY samples.workload, samples.sample_date;

SELECT licence.id AS LicenceTypeId,
       licence.name AS Name,
       licence.sku_id AS SkuId,
       counts.assigned_users AS AssignedUsers
FROM dbo.license_types AS licence
JOIN #LicenceCounts AS counts ON counts.licence_type_id = licence.id
ORDER BY licence.name, licence.id;

SELECT distributions.licence_type_id AS LicenceTypeId,
       coverage.workload_name AS Workload,
       distributions.high_count AS High,
       distributions.moderate_count AS Moderate,
       distributions.low_count AS Low,
       distributions.zero_count AS Zero,
       distributions.unknown_count AS Unknown
FROM #LicenceDistributions AS distributions
JOIN #Coverage AS coverage ON coverage.workload = distributions.workload
ORDER BY distributions.licence_type_id, distributions.workload;

SELECT CASE WHEN dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       demographic_id AS Id,
       demographic_name AS Name,
       assigned_users AS AssignedUsers
FROM #Demographics
ORDER BY dimension, assigned_users DESC, demographic_name, demographic_id;

SELECT CASE WHEN distributions.dimension = 1 THEN 'department' ELSE 'country' END AS Dimension,
       distributions.demographic_id AS Id,
       coverage.workload_name AS Workload,
       distributions.high_count AS High,
       distributions.moderate_count AS Moderate,
       distributions.low_count AS Low,
       distributions.zero_count AS Zero,
       distributions.unknown_count AS Unknown
FROM #DemographicDistributions AS distributions
JOIN #Coverage AS coverage ON coverage.workload = distributions.workload
ORDER BY distributions.dimension, distributions.demographic_id, distributions.workload;
";

        private static readonly string UsersPreamble = @"
/* LicenceActivity:Users */
SET NOCOUNT ON;
SET XACT_ABORT ON;

CREATE TABLE #EligibleUsers
(
    user_id int NOT NULL PRIMARY KEY,
    user_name varchar(250) NOT NULL,
    department_name nvarchar(100) NULL,
    country_name nvarchar(100) NULL,
    account_enabled bit NULL
);

INSERT #EligibleUsers (user_id, user_name, department_name, country_name, account_enabled)
SELECT DISTINCT u.id, u.user_name, department.name, country.name, u.account_enabled
FROM dbo.user_license_type_lookups AS owned
JOIN dbo.users AS u ON u.id = owned.user_id
LEFT JOIN dbo.user_departments AS department ON department.id = u.department_id
LEFT JOIN dbo.user_country_or_region AS country ON country.id = u.country_or_region_id
WHERE owned.license_type_id = @licenceTypeId
  AND (@departmentId IS NULL
       OR u.department_id = @departmentId
       OR (@departmentId = 0 AND u.department_id IS NULL))
  AND (@countryId IS NULL
       OR u.country_or_region_id = @countryId
       OR (@countryId = 0 AND u.country_or_region_id IS NULL))
  AND (@searchPattern = N''
       OR u.user_name LIKE @searchPattern ESCAPE N'\'
       OR u.mail LIKE @searchPattern ESCAPE N'\')
OPTION (RECOMPILE);

IF (SELECT COUNT(*) FROM #EligibleUsers) >= 32768
BEGIN
    BEGIN TRY
        EXEC sp_executesql N'CREATE NONCLUSTERED COLUMNSTORE INDEX IX_LicenceActivity_EligibleBatch
            ON #EligibleUsers (user_id);';
    END TRY
    BEGIN CATCH
        PRINT N'Licence activity is using rowstore temporary eligibility.';
    END CATCH;
END;

CREATE TABLE #Coverage
(
    workload tinyint NOT NULL PRIMARY KEY,
    status varchar(32) NOT NULL,
    source varchar(64) NOT NULL,
    measure nvarchar(240) NOT NULL,
    expected_samples int NOT NULL,
    observed_samples int NOT NULL,
    report_period_days int NULL
);

INSERT #Coverage
(
    workload, status, source, measure, expected_samples, observed_samples, report_period_days
)
VALUES
    (1, @status1, @source1, @measure1, @expected1, @observed1, @period1),
    (2, @status2, @source2, @measure2, @expected2, @observed2, @period2),
    (3, @status3, @source3, @measure3, @expected3, @observed3, @period3),
    (4, @status4, @source4, @measure4, @expected4, @observed4, @period4),
    (5, @status5, @source5, @measure5, @expected5, @observed5, @period5);

CREATE TABLE #Samples
(
    workload tinyint NOT NULL,
    sample_date date NOT NULL,
    m365_from date NULL,
    end_exclusive date NULL,
    copilot_from date NULL,
    PRIMARY KEY (workload, sample_date)
);

CREATE TABLE #Scores
(
    workload tinyint NOT NULL,
    user_id int NOT NULL,
    active_samples int NOT NULL,
    observed_samples int NOT NULL,
    frequency_known bit NOT NULL,
    average_actions float NULL,
    last_activity_utc datetime NULL
);
";

        private static void AppendM365Users(
            StringBuilder sql,
            LicenceActivityCoverage coverage,
            int workload,
            string table,
            string actionExpression,
            string scopeTable)
        {
            if (coverage.SnapshotDates == null || coverage.SnapshotDates.Count == 0)
                return;

            var samples = Enumerable.Range(0, coverage.SnapshotDates.Count).ToArray();
            var aggregates = string.Join("\r\nUNION ALL\r\n", samples.Select(sample =>
            {
                var date = SampleParameterName(workload, sample);
                return @"SELECT eligible.user_id, MAX(" + actionExpression + @") AS actions,
                    MAX(CASE WHEN activity.last_activity_date >= chosen.m365_from
                              AND activity.last_activity_date < chosen.end_exclusive
                             THEN activity.last_activity_date END) AS last_activity_utc,
                    MAX(CASE WHEN activity.last_activity_date >= chosen.m365_from
                              AND activity.last_activity_date < chosen.end_exclusive
                             THEN 1 ELSE 0 END) AS was_active
                FROM " + table + @" AS activity
                JOIN " + scopeTable + @" AS eligible ON eligible.user_id = activity.user_id
                JOIN #Samples AS chosen ON chosen.workload = " + workload + @"
                    AND chosen.sample_date = " + date + @"
                WHERE activity.[date] >= " + date + @"
                  AND activity.[date] < DATEADD(DAY, 1, " + date + @")
                GROUP BY eligible.user_id";
            }));
            if (scopeTable == "#ReturnedUsers")
            {
                // At most 300 returned users: this hash is bounded to 300 x 27 samples.
                aggregates = @"SELECT eligible.user_id, MAX(" + actionExpression + @") AS actions,
                    MAX(CASE WHEN activity.last_activity_date >= chosen.m365_from
                              AND activity.last_activity_date < chosen.end_exclusive
                             THEN activity.last_activity_date END) AS last_activity_utc,
                    MAX(CASE WHEN activity.last_activity_date >= chosen.m365_from
                              AND activity.last_activity_date < chosen.end_exclusive
                             THEN 1 ELSE 0 END) AS was_active
                FROM " + table + @" AS activity
                JOIN #ReturnedUsers AS eligible ON eligible.user_id = activity.user_id
                JOIN #Samples AS chosen ON chosen.workload = " + workload + @"
                    AND CAST(activity.[date] AS date) = chosen.sample_date
                WHERE activity.[date] >= @from AND activity.[date] < @endExclusive
                GROUP BY eligible.user_id, chosen.sample_date";
            }
            sql.AppendFormat(
                CultureInfo.InvariantCulture,
                @"
;WITH PerSample AS
(
    {1}
)
INSERT #Scores
    (workload, user_id, active_samples, observed_samples, frequency_known, average_actions, last_activity_utc)
SELECT {0}, user_id,
       SUM(was_active),
       COUNT(*),
       CAST(CASE WHEN COUNT(*) = @expected{0} THEN 1 ELSE 0 END AS bit),
       AVG(actions),
       MAX(last_activity_utc)
FROM PerSample
GROUP BY user_id
OPTION (RECOMPILE);
",
                workload, aggregates);
        }

        private static void AppendCopilotUsers(
            StringBuilder sql,
            LicenceActivityCoverage coverage,
            string scopeTable)
        {
            if (coverage.Source == CopilotReportSource
                && coverage.SnapshotDates != null
                && coverage.SnapshotDates.Count > 0
                && coverage.ReportPeriodDays.HasValue)
            {
                if (coverage.ReportPeriodDays.Value == 7)
                {
                    if (scopeTable == "#ReturnedUsers")
                    {
                        sql.Append(@"
CREATE TABLE #CopilotReportIds (id int NOT NULL PRIMARY KEY);
INSERT #CopilotReportIds (id)
SELECT report.id
FROM dbo.copilot_usage_user_activity_log AS report WITH (INDEX(IX_date_user_id_report_period_days))
JOIN #Samples AS chosen
  ON chosen.workload = 5
 AND report.[date] >= chosen.sample_date
 AND report.[date] < DATEADD(DAY, 1, chosen.sample_date)
JOIN #ReturnedUsers AS eligible ON eligible.user_id = report.user_id
WHERE report.report_period_days = 7
OPTION (RECOMPILE);
");
                        sql.Append(CopilotD7Users.Replace(
                            "AS report WITH (INDEX(0))",
                            "AS report JOIN #CopilotReportIds AS ids ON ids.id = report.id")
                            .Replace("#ActivityScope", scopeTable));
                    }
                    else
                    {
                        sql.Append(CopilotD7Users.Replace("#ActivityScope", scopeTable));
                    }
                }
                else
                    sql.Append(CopilotLongUsers.Replace("#ActivityScope", scopeTable));
                return;
            }

            if (coverage.Source == CopilotAuditSource)
            {
                sql.Append(CopilotAuditUsers.Replace("#ActivityScope", scopeTable));
            }
            else if (coverage.Source == CopilotInteractionSource)
            {
                sql.Append(CopilotInteractionUsers.Replace("#ActivityScope", scopeTable));
            }
        }

        private static readonly string CopilotD7Users = @"
DECLARE @copilotSampleFrom date =
    (SELECT MIN(sample_date) FROM #Samples WHERE workload = 5);
DECLARE @copilotSampleEnd date =
    (SELECT MAX(end_exclusive) FROM #Samples WHERE workload = 5);
INSERT #Scores
    (workload, user_id, active_samples, observed_samples, frequency_known, average_actions, last_activity_utc)
SELECT 5, report.user_id,
       SUM(" + CopilotD7ActivityExpression("chosen.copilot_from", "chosen.end_exclusive") + @"),
       COUNT(*),
       CAST(CASE
                WHEN COUNT(*) = @expected5
                 AND MIN(CASE
                             WHEN report.active_usage_days BETWEEN 0 AND 7
                              AND (report.prompts_all_apps IS NULL
                                   OR report.prompts_all_apps >= 0)
                             THEN 1 ELSE 0
                         END) = 1
                THEN 1 ELSE 0
            END AS bit),
       CASE
           WHEN MIN(CASE WHEN report.prompts_all_apps >= 0 THEN 1 ELSE 0 END) = 1
               THEN AVG(CAST(report.prompts_all_apps AS float))
       END,
       MAX(CASE
               WHEN report.last_activity_date >= chosen.copilot_from
                AND report.last_activity_date < chosen.end_exclusive
               THEN report.last_activity_date
           END)
FROM dbo.copilot_usage_user_activity_log AS report WITH (INDEX(0))
JOIN #Samples AS chosen
  ON chosen.workload = 5
 AND CAST(report.[date] AS date) = chosen.sample_date
JOIN #ActivityScope AS eligible ON eligible.user_id = report.user_id
WHERE report.report_period_days = 7
  AND report.[date] >= @copilotSampleFrom
  AND report.[date] < @copilotSampleEnd
GROUP BY report.user_id
OPTION (RECOMPILE);
";

        private static readonly string CopilotLongUsers = @"
DECLARE @copilotSnapshot date =
    (SELECT MAX(sample_date) FROM #Samples WHERE workload = 5);

INSERT #Scores
    (workload, user_id, active_samples, observed_samples, frequency_known, average_actions, last_activity_utc)
SELECT 5,
       report.user_id,
       CASE
           WHEN report.active_usage_days BETWEEN 0 AND @period5
               THEN report.active_usage_days
           WHEN report.prompts_all_apps > 0
             OR (report.last_activity_date >= DATEADD(DAY, 1 - @period5, @copilotSnapshot)
                 AND report.last_activity_date < DATEADD(DAY, 1, @copilotSnapshot))
               THEN 1 ELSE 0
       END,
       CASE
           WHEN report.active_usage_days BETWEEN 0 AND @period5
            AND (report.prompts_all_apps IS NULL OR report.prompts_all_apps >= 0)
               THEN @period5 ELSE 0
       END,
       CAST(CASE
           WHEN report.active_usage_days BETWEEN 0 AND @period5
            AND (report.prompts_all_apps IS NULL OR report.prompts_all_apps >= 0)
               THEN 1 ELSE 0
       END AS bit),
       CASE WHEN report.prompts_all_apps >= 0
            THEN CAST(report.prompts_all_apps AS float) END,
       CASE
           WHEN report.last_activity_date >= DATEADD(DAY, 1 - @period5, @copilotSnapshot)
            AND report.last_activity_date < DATEADD(DAY, 1, @copilotSnapshot)
               THEN report.last_activity_date
       END
FROM dbo.copilot_usage_user_activity_log AS report
JOIN #ActivityScope AS eligible ON eligible.user_id = report.user_id
WHERE report.report_period_days = @period5
  AND report.[date] >= @copilotSnapshot
  AND report.[date] < DATEADD(DAY, 1, @copilotSnapshot)
OPTION (RECOMPILE);
";

        private static readonly string CopilotAuditUsers = @"
;WITH EventWeeks AS
(
    SELECT chats.user_id,
           DATEADD(DAY,
               -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(chats.time_stamp AS date)) % 7) + 7) % 7),
               CAST(chats.time_stamp AS date)) AS week_start,
           COUNT_BIG(*) AS actions,
           MAX(chats.time_stamp) AS last_activity_utc
    FROM dbo.copilot_chats AS chats
    JOIN #ActivityScope AS eligible ON eligible.user_id = chats.user_id
    WHERE chats.time_stamp >= @from
      AND chats.time_stamp < @endExclusive
    GROUP BY chats.user_id,
             DATEADD(DAY,
                -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(chats.time_stamp AS date)) % 7) + 7) % 7),
                CAST(chats.time_stamp AS date))
)
INSERT #Scores
    (workload, user_id, active_samples, observed_samples, frequency_known, average_actions, last_activity_utc)
SELECT 5, user_id, COUNT(*), 0, 0, AVG(CAST(actions AS float)), MAX(last_activity_utc)
FROM EventWeeks
GROUP BY user_id
OPTION (RECOMPILE);
";

        private static readonly string CopilotInteractionUsers = @"
;WITH EventWeeks AS
(
    SELECT interactions.user_id,
           DATEADD(DAY,
               -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(interactions.created_utc AS date)) % 7) + 7) % 7),
               CAST(interactions.created_utc AS date)) AS week_start,
           COUNT_BIG(*) AS actions,
           MAX(interactions.created_utc) AS last_activity_utc
    FROM dbo.copilot_interactions AS interactions
    JOIN #ActivityScope AS eligible ON eligible.user_id = interactions.user_id
    WHERE interactions.created_utc >= @from
      AND interactions.created_utc < @endExclusive
    GROUP BY interactions.user_id,
             DATEADD(DAY,
                -(((DATEDIFF(DAY, CONVERT(date, '19000101', 112), CAST(interactions.created_utc AS date)) % 7) + 7) % 7),
                CAST(interactions.created_utc AS date))
)
INSERT #Scores
    (workload, user_id, active_samples, observed_samples, frequency_known, average_actions, last_activity_utc)
SELECT 5, user_id, COUNT(*), 0, 0, AVG(CAST(actions AS float)), MAX(last_activity_utc)
FROM EventWeeks
GROUP BY user_id
OPTION (RECOMPILE);
";

        private static string BuildUsersSelection(LicenceActivityQuery query)
        {
            var selected = WorkloadId(query.Workload);
            var pageOrder = UserPageOrder(query);

            return string.Format(
                CultureInfo.InvariantCulture,
                @"
CREATE TABLE #RankBase
(
    user_id int NOT NULL PRIMARY KEY,
    user_name varchar(250) NOT NULL,
    department_name nvarchar(100) NULL,
    country_name nvarchar(100) NULL,
    account_enabled bit NULL,
    selected_status varchar(32) NOT NULL,
    selected_active_samples int NOT NULL,
    selected_average_actions float NULL,
    selected_last_activity_utc datetime NULL,
    can_rank_most bit NOT NULL,
    can_rank_least bit NOT NULL
);

INSERT #RankBase
SELECT eligible.user_id,
       eligible.user_name,
       eligible.department_name,
       eligible.country_name,
       eligible.account_enabled,
       CASE
           WHEN coverage.status <> 'available' THEN coverage.status
           WHEN coverage.source IN
                    ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
            AND score.user_id IS NULL THEN 'missingCoverage'
           WHEN coverage.source IN
                    ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
            AND (score.frequency_known = 0 OR score.observed_samples <> coverage.expected_samples)
               THEN 'partial'
           ELSE 'available'
       END,
       ISNULL(score.active_samples, 0),
       CASE WHEN score.user_id IS NULL
                  AND coverage.status = 'available'
                  AND coverage.source NOT IN
                      ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
            THEN CAST(0 AS float) ELSE score.average_actions END,
       score.last_activity_utc,
       CAST(CASE
                WHEN (coverage.status = 'available'
                      AND NOT (coverage.source IN
                                   ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
                               AND (score.user_id IS NULL
                                    OR score.frequency_known = 0
                                    OR score.observed_samples <> coverage.expected_samples)))
                  OR ISNULL(score.active_samples, 0) > 0
                 THEN 1 ELSE 0 END AS bit),
       CAST(CASE
                WHEN coverage.status = 'available'
                 AND NOT (coverage.source IN
                              ('microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport')
                          AND (score.user_id IS NULL
                               OR score.frequency_known = 0
                               OR score.observed_samples <> coverage.expected_samples))
                THEN 1 ELSE 0 END AS bit)
FROM #EligibleUsers AS eligible
CROSS JOIN
(
    SELECT status, source, expected_samples
    FROM #Coverage
    WHERE workload = {0}
) AS coverage
LEFT JOIN #Scores AS score
  ON score.workload = {0}
 AND score.user_id = eligible.user_id
OPTION (RECOMPILE);

SELECT COUNT(*) AS TotalUsers,
       ISNULL(SUM(CASE WHEN can_rank_most = 1 THEN 1 ELSE 0 END), 0) AS RankedUsers
FROM #RankBase;

CREATE TABLE #Picked
(
    list_kind tinyint NOT NULL,
    ordinal int NOT NULL,
    user_id int NOT NULL,
    PRIMARY KEY (list_kind, ordinal),
    UNIQUE (list_kind, user_id)
);

INSERT #Picked (list_kind, ordinal, user_id)
SELECT 1,
       ROW_NUMBER() OVER
       (
           ORDER BY CASE
                        WHEN selected_status = 'available' AND selected_active_samples > 0 THEN 0
                        WHEN selected_status <> 'available' AND selected_active_samples > 0 THEN 1
                        WHEN selected_status = 'available' THEN 2
                        ELSE 3
                    END,
                    selected_active_samples DESC,
                    selected_average_actions DESC,
                    selected_last_activity_utc DESC,
                    user_name,
                    user_id
       ),
       user_id
FROM
(
    SELECT TOP (@top) *
    FROM #RankBase
    WHERE can_rank_most = 1
    ORDER BY CASE
                 WHEN selected_status = 'available' AND selected_active_samples > 0 THEN 0
                 WHEN selected_status <> 'available' AND selected_active_samples > 0 THEN 1
                 WHEN selected_status = 'available' THEN 2
                 ELSE 3
             END,
             selected_active_samples DESC,
             selected_average_actions DESC,
             selected_last_activity_utc DESC,
             user_name,
             user_id
) AS most_active
OPTION (RECOMPILE);

INSERT #Picked (list_kind, ordinal, user_id)
SELECT 2,
       ROW_NUMBER() OVER
       (
           ORDER BY selected_active_samples,
                    selected_average_actions,
                    selected_last_activity_utc,
                    user_name,
                    user_id
       ),
       user_id
FROM
(
    SELECT TOP (@top) *
    FROM #RankBase
    WHERE can_rank_least = 1
    ORDER BY selected_active_samples,
             selected_average_actions,
             selected_last_activity_utc,
             user_name,
             user_id
) AS least_active
OPTION (RECOMPILE);

;WITH Paged AS
(
    SELECT user_id,
           ROW_NUMBER() OVER (ORDER BY {1}) AS row_number
    FROM #RankBase
)
INSERT #Picked (list_kind, ordinal, user_id)
SELECT 3, row_number - @offset, user_id
FROM Paged
WHERE row_number > @offset
  AND row_number <= @offset + @pageSize
OPTION (RECOMPILE);

CREATE TABLE #ReturnedUsers
(
    user_id int NOT NULL PRIMARY KEY
);

INSERT #ReturnedUsers (user_id)
SELECT DISTINCT user_id
FROM #Picked;
",
                selected, pageOrder);
        }

        private static readonly string UsersFinalProjection = @"
SELECT picked.list_kind AS ListKind,
       picked.ordinal AS Ordinal,
       users.user_id AS UserId,
       users.user_name AS UserPrincipalName,
       users.department_name AS Department,
       users.country_name AS Country,
       users.account_enabled AS AccountEnabled,
       ISNULL(teams.active_samples, 0) AS TeamsActiveSamples,
       CAST(CASE WHEN teams.user_id IS NULL THEN 0 ELSE 1 END AS bit) AS TeamsRowPresent,
       ISNULL(teams.observed_samples, 0) AS TeamsObservedSamples,
       ISNULL(teams.frequency_known, 0) AS TeamsFrequencyKnown,
       teams.average_actions AS TeamsAverageActions,
       teams.last_activity_utc AS TeamsLastActivityUtc,
       ISNULL(outlook.active_samples, 0) AS OutlookActiveSamples,
       CAST(CASE WHEN outlook.user_id IS NULL THEN 0 ELSE 1 END AS bit) AS OutlookRowPresent,
       ISNULL(outlook.observed_samples, 0) AS OutlookObservedSamples,
       ISNULL(outlook.frequency_known, 0) AS OutlookFrequencyKnown,
       outlook.average_actions AS OutlookAverageActions,
       outlook.last_activity_utc AS OutlookLastActivityUtc,
       ISNULL(onedrive.active_samples, 0) AS OneDriveActiveSamples,
       CAST(CASE WHEN onedrive.user_id IS NULL THEN 0 ELSE 1 END AS bit) AS OneDriveRowPresent,
       ISNULL(onedrive.observed_samples, 0) AS OneDriveObservedSamples,
       ISNULL(onedrive.frequency_known, 0) AS OneDriveFrequencyKnown,
       onedrive.average_actions AS OneDriveAverageActions,
       onedrive.last_activity_utc AS OneDriveLastActivityUtc,
       ISNULL(sharepoint.active_samples, 0) AS SharePointActiveSamples,
       CAST(CASE WHEN sharepoint.user_id IS NULL THEN 0 ELSE 1 END AS bit) AS SharePointRowPresent,
       ISNULL(sharepoint.observed_samples, 0) AS SharePointObservedSamples,
       ISNULL(sharepoint.frequency_known, 0) AS SharePointFrequencyKnown,
       sharepoint.average_actions AS SharePointAverageActions,
       sharepoint.last_activity_utc AS SharePointLastActivityUtc,
       ISNULL(copilot.active_samples, 0) AS CopilotActiveSamples,
       CAST(CASE WHEN copilot.user_id IS NULL THEN 0 ELSE 1 END AS bit) AS CopilotRowPresent,
       ISNULL(copilot.observed_samples, 0) AS CopilotObservedSamples,
       ISNULL(copilot.frequency_known, 0) AS CopilotFrequencyKnown,
       copilot.average_actions AS CopilotAverageActions,
       copilot.last_activity_utc AS CopilotLastActivityUtc
FROM #Picked AS picked
JOIN #EligibleUsers AS users ON users.user_id = picked.user_id
LEFT JOIN #Scores AS teams ON teams.workload = 1 AND teams.user_id = users.user_id
LEFT JOIN #Scores AS outlook ON outlook.workload = 2 AND outlook.user_id = users.user_id
LEFT JOIN #Scores AS onedrive ON onedrive.workload = 3 AND onedrive.user_id = users.user_id
LEFT JOIN #Scores AS sharepoint ON sharepoint.workload = 4 AND sharepoint.user_id = users.user_id
LEFT JOIN #Scores AS copilot ON copilot.workload = 5 AND copilot.user_id = users.user_id
ORDER BY picked.list_kind, picked.ordinal
OPTION (RECOMPILE);
";

        private static string UserPageOrder(LicenceActivityQuery query)
        {
            var direction = query.Direction == "desc" ? "DESC" : "ASC";
            if (query.Sort == "activity")
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "CASE WHEN selected_status = 'available' AND selected_active_samples > 0 THEN 0 "
                    + "WHEN selected_status <> 'available' AND selected_active_samples > 0 THEN 1 "
                    + "WHEN selected_status = 'available' THEN 2 ELSE 3 END, "
                    + "selected_active_samples {0}, selected_average_actions {0}, "
                    + "selected_last_activity_utc {0}, user_name, user_id",
                    direction);
            }

            if (query.Sort == "lastActivity")
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "CASE WHEN selected_status = 'available' AND selected_active_samples > 0 THEN 0 "
                    + "WHEN selected_status <> 'available' AND selected_active_samples > 0 THEN 1 "
                    + "WHEN selected_status = 'available' THEN 2 ELSE 3 END, "
                    + "selected_last_activity_utc {0}, selected_active_samples {0}, "
                    + "selected_average_actions {0}, user_name, user_id",
                    direction);
            }

            return "user_name " + direction + ", user_id";
        }
    }
}
