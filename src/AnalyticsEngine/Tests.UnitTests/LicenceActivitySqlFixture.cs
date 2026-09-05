using Common.Entities.LicenceActivity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tests.UnitTests
{
    internal enum LicenceActivityUsageIndexMode
    {
        DateOnly,
        Columnstore,
        BTreeFallback
    }

    /// <summary>
    /// Throwaway, synthetic SQL schema shared by the focused licence-activity integration and scale tests.
    /// Column types and index keys match the shipped schema; no production database is ever opened.
    /// </summary>
    internal sealed class LicenceActivitySqlFixture : IDisposable
    {
        private const string RetainedDatabasePrefix = "UT_LicenceActivityScale_";
        private const string RetainedMarker = "300000-users|50-skus|v2";
        private static readonly Regex RetainedDatabaseName = new Regex(
            "^" + RetainedDatabasePrefix + "[0-9a-f]{32}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static readonly DateTime NowUtc =
            new DateTime(2000, 7, 4, 0, 0, 0, DateTimeKind.Utc);
        internal static readonly DateTime WideFromUtc =
            new DateTime(2000, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        internal static readonly DateTime WideToUtc =
            new DateTime(2000, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        internal static readonly DateTime NarrowFromUtc =
            new DateTime(2000, 6, 19, 0, 0, 0, DateTimeKind.Utc);
        internal static readonly DateTime NarrowToUtc =
            new DateTime(2000, 6, 25, 0, 0, 0, DateTimeKind.Utc);

        private readonly ScratchDatabase _database;
        private readonly bool _retained;

        internal string ConnectionString { get; }

        private LicenceActivitySqlFixture(ScratchDatabase database)
        {
            _database = database;
            ConnectionString = database.ConnectionString;
        }

        private LicenceActivitySqlFixture(string connectionString, bool retained)
        {
            ConnectionString = connectionString;
            _retained = retained;
        }

        internal static LicenceActivitySqlFixture Create(
            string purpose,
            LicenceActivityUsageIndexMode usageIndexMode = LicenceActivityUsageIndexMode.BTreeFallback,
            bool createUsageIndexes = true)
        {
            var fixture = new LicenceActivitySqlFixture(ScratchDatabase.Create(purpose));
            fixture.CreateSchema();
            if (createUsageIndexes) fixture.ApplyUsageIndexes(usageIndexMode);
            return fixture;
        }

        internal static LicenceActivitySqlFixture CreateScale(
            string purpose,
            LicenceActivityUsageIndexMode usageIndexMode = LicenceActivityUsageIndexMode.BTreeFallback)
        {
            if (string.Equals(
                Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_RETAIN_DB"),
                "1",
                StringComparison.Ordinal))
            {
                return CreateOrReuseRetainedScale(usageIndexMode);
            }

            var fixture = Create(purpose, usageIndexMode, createUsageIndexes: false);
            try
            {
                fixture.SeedScale();
                fixture.ApplyUsageIndexes(usageIndexMode);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal static void CleanupRetainedScale()
        {
            var statePath = RetainedStatePath();
            if (!File.Exists(statePath)) return;

            var databaseName = File.ReadAllText(statePath).Trim();
            ValidateRetainedDatabaseName(databaseName);

            var configured = LocalDbConnectionBuilder("master");
            var retained = new SqlConnectionStringBuilder(configured.ConnectionString)
            {
                InitialCatalog = databaseName
            };
            ValidateRetainedMarker(retained.ConnectionString);

            ExecuteOn(
                configured.ConnectionString,
                $@"IF DB_ID(N'{databaseName}') IS NOT NULL
                   BEGIN
                       ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                       DROP DATABASE [{databaseName}];
                   END");
            File.Delete(statePath);
        }

        internal SqlLicenceActivityStore Store()
        {
            return new SqlLicenceActivityStore(ConnectionString);
        }

        internal SqlLicenceActivityStore Store(SqlLicenceActivityStoreInstrumentation instrumentation)
        {
            return new SqlLicenceActivityStore(ConnectionString, instrumentation);
        }

        internal void Execute(string sql)
        {
            ExecuteOn(ConnectionString, sql);
        }

        internal object Scalar(string sql)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
                {
                    var value = command.ExecuteScalar();
                    return value == DBNull.Value ? null : value;
                }
            }
        }

        public void Dispose()
        {
            if (!_retained) _database?.Dispose();
        }

        private static LicenceActivitySqlFixture CreateOrReuseRetainedScale(
            LicenceActivityUsageIndexMode usageIndexMode)
        {
            var statePath = RetainedStatePath();
            if (File.Exists(statePath))
            {
                var databaseName = File.ReadAllText(statePath).Trim();
                ValidateRetainedDatabaseName(databaseName);
                var retained = LocalDbConnectionBuilder(databaseName);
                ValidateRetainedMarker(retained.ConnectionString);
                var retainedFixture =
                    new LicenceActivitySqlFixture(retained.ConnectionString, retained: true);
                if (!retainedFixture.UsageIndexesMatch(usageIndexMode))
                    retainedFixture.ApplyUsageIndexes(usageIndexMode);
                return retainedFixture;
            }

            var database = RetainedDatabasePrefix + Guid.NewGuid().ToString("N");
            ValidateRetainedDatabaseName(database);
            var master = LocalDbConnectionBuilder("master");
            var retainedConnection = LocalDbConnectionBuilder(database);

            ExecuteOn(master.ConnectionString, $"CREATE DATABASE [{database}];");
            var fixture = new LicenceActivitySqlFixture(retainedConnection.ConnectionString, retained: true);
            try
            {
                fixture.CreateSchema();
                fixture.SeedScale();
                fixture.ApplyUsageIndexes(usageIndexMode);
                fixture.Execute(@"
CREATE TABLE dbo.LicenceActivitySyntheticMarker
(
    marker nvarchar(100) NOT NULL PRIMARY KEY
);
INSERT dbo.LicenceActivitySyntheticMarker (marker)
VALUES (N'300000-users|50-skus|v2');");

                var directory = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(statePath, database);
                return fixture;
            }
            catch
            {
                ExecuteOn(
                    master.ConnectionString,
                    $@"IF DB_ID(N'{database}') IS NOT NULL
                       BEGIN
                           ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                           DROP DATABASE [{database}];
                       END");
                throw;
            }
        }

        private static SqlConnectionStringBuilder LocalDbConnectionBuilder(string database)
        {
            var configured = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"];
            if (configured == null)
                throw new InvalidOperationException("The SPOInsightsEntities connection string is required.");

            var builder = new SqlConnectionStringBuilder(configured.ConnectionString);
            if (builder.DataSource.IndexOf("(localdb)", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException(
                    "Retained licence-activity performance databases are allowed only on LocalDB.");
            builder.InitialCatalog = database;
            return builder;
        }

        private static string RetainedStatePath()
        {
            var path = Environment.GetEnvironmentVariable("LICENCE_ACTIVITY_PERF_STATE_FILE");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new InvalidOperationException(
                    "LICENCE_ACTIVITY_PERF_STATE_FILE must be an absolute out-of-band session-state path.");
            return Path.GetFullPath(path);
        }

        private static void ValidateRetainedDatabaseName(string databaseName)
        {
            if (!RetainedDatabaseName.IsMatch(databaseName ?? string.Empty))
                throw new InvalidOperationException("The retained synthetic database name is invalid.");
        }

        private static void ValidateRetainedMarker(string connectionString)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(@"
SELECT marker
FROM dbo.LicenceActivitySyntheticMarker;", connection))
                {
                    var marker = command.ExecuteScalar() as string;
                    if (!string.Equals(marker, RetainedMarker, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "The retained database is not the licence-activity synthetic fixture.");
                }
            }
        }

        private static void ExecuteOn(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
                    command.ExecuteNonQuery();
            }
        }

        internal void ApplyUsageIndexes(LicenceActivityUsageIndexMode mode)
        {
            Execute(@"
DECLARE @tables TABLE (name sysname);
INSERT @tables VALUES
    (N'teams_user_activity_log'),
    (N'outlook_user_activity_log'),
    (N'onedrive_user_activity_log'),
    (N'sharepoint_user_activity_log');

DECLARE @table sysname;
DECLARE tables CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @tables;
OPEN tables;
FETCH NEXT FROM tables INTO @table;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @sql nvarchar(max);
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name = N'IX_date')
    BEGIN
        SET @sql = N'DROP INDEX [IX_date] ON [dbo].[' + @table + N'];';
        EXEC sp_executesql @sql;
    END;
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name LIKE N'NCCI[_]%[_]metrics')
    BEGIN
        SELECT TOP (1) @sql = N'DROP INDEX [' + name + N'] ON [dbo].[' + @table + N'];'
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name LIKE N'NCCI[_]%[_]metrics';
        EXEC sp_executesql @sql;
    END;
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name LIKE N'IX[_]%[_]metrics')
    BEGIN
        SELECT TOP (1) @sql = N'DROP INDEX [' + name + N'] ON [dbo].[' + @table + N'];'
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND name LIKE N'IX[_]%[_]metrics';
        EXEC sp_executesql @sql;
    END;

    SET @sql = N'CREATE NONCLUSTERED INDEX [IX_date] ON [dbo].[' + @table
             + N'] ([date], [last_activity_date]) INCLUDE ([user_id]);';
    EXEC sp_executesql @sql;

    FETCH NEXT FROM tables INTO @table;
END;
CLOSE tables;
DEALLOCATE tables;");

            if (mode == LicenceActivityUsageIndexMode.DateOnly) return;

            if (mode == LicenceActivityUsageIndexMode.Columnstore)
            {
                Execute(@"
CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_teams_user_activity_log_metrics]
ON dbo.teams_user_activity_log
([user_id], [date], [last_activity_date], [private_chat_count], [team_chat_count],
 [post_messages], [reply_messages], [meetings_attended_count], [meetings_organized_count]);

CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_outlook_user_activity_log_metrics]
ON dbo.outlook_user_activity_log
([user_id], [date], [last_activity_date], [email_send_count], [email_read_count]);

CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_sharepoint_user_activity_log_metrics]
ON dbo.sharepoint_user_activity_log
([user_id], [date], [last_activity_date], [viewed_or_edited]);

CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_onedrive_user_activity_log_metrics]
ON dbo.onedrive_user_activity_log
([user_id], [date], [last_activity_date], [viewed_or_edited]);");
                return;
            }

            Execute(@"
CREATE NONCLUSTERED INDEX [IX_teams_user_activity_log_metrics]
ON dbo.teams_user_activity_log ([date])
INCLUDE ([user_id], [last_activity_date], [private_chat_count], [team_chat_count],
         [post_messages], [reply_messages], [meetings_attended_count], [meetings_organized_count]);

CREATE NONCLUSTERED INDEX [IX_outlook_user_activity_log_metrics]
ON dbo.outlook_user_activity_log ([date])
INCLUDE ([user_id], [last_activity_date], [email_send_count], [email_read_count]);

CREATE NONCLUSTERED INDEX [IX_sharepoint_user_activity_log_metrics]
ON dbo.sharepoint_user_activity_log ([date])
INCLUDE ([user_id], [last_activity_date], [viewed_or_edited]);

CREATE NONCLUSTERED INDEX [IX_onedrive_user_activity_log_metrics]
ON dbo.onedrive_user_activity_log ([date])
INCLUDE ([user_id], [last_activity_date], [viewed_or_edited]);");
        }

        private bool UsageIndexesMatch(LicenceActivityUsageIndexMode mode)
        {
            var values = Convert.ToString(Scalar(@"
SELECT CONCAT(
    SUM(CASE WHEN name LIKE N'NCCI[_]%[_]metrics' THEN 1 ELSE 0 END),
    N':',
    SUM(CASE WHEN name LIKE N'IX[_]%[_]metrics' THEN 1 ELSE 0 END))
FROM sys.indexes
WHERE object_id IN
(
    OBJECT_ID(N'dbo.teams_user_activity_log'),
    OBJECT_ID(N'dbo.outlook_user_activity_log'),
    OBJECT_ID(N'dbo.onedrive_user_activity_log'),
    OBJECT_ID(N'dbo.sharepoint_user_activity_log')
);"), CultureInfo.InvariantCulture);
            switch (mode)
            {
                case LicenceActivityUsageIndexMode.Columnstore:
                    return values == "4:0";
                case LicenceActivityUsageIndexMode.BTreeFallback:
                    return values == "0:4";
                default:
                    return values == "0:0";
            }
        }

        internal void SeedScale(int userCount = 300000, int licenceCount = 50)
        {
            if (userCount != 300000 || licenceCount != 50)
                throw new ArgumentException("The release-scale harness is intentionally fixed at 300,000 users and 50 SKUs.");

            Execute(@"
;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
Numbers(n) AS
(
    SELECT TOP (60) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E2 AS a CROSS JOIN E1 AS b
)
INSERT dbo.user_departments (name)
SELECT CASE WHEN n = 60 THEN N'Καλημέρα κόσμε' ELSE N'Synthetic department ' + CAST(n AS nvarchar(10)) END
FROM Numbers;

;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
Numbers(n) AS
(
    SELECT TOP (20) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E2
)
INSERT dbo.user_country_or_region (name)
SELECT N'Synthetic country ' + CAST(n AS nvarchar(10))
FROM Numbers;

;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
E4(n) AS (SELECT 0 FROM E2 AS a CROSS JOIN E2 AS b),
E6(n) AS (SELECT 0 FROM E4 AS a CROSS JOIN E2 AS b),
Numbers(n) AS
(
    SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E6
)
INSERT dbo.users WITH (TABLOCK)
    (user_name, mail, account_enabled, department_id, country_or_region_id)
SELECT 'synthetic' + RIGHT('000000' + CAST(n AS varchar(6)), 6) + '@contoso.example',
       N'synthetic' + RIGHT(N'000000' + CAST(n AS nvarchar(6)), 6) + N'@contoso.example',
       CASE WHEN n % 50 = 0 THEN 0 ELSE 1 END,
       ((n - 1) % 60) + 1,
       ((n - 1) % 20) + 1
FROM Numbers;

;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
E2(n) AS (SELECT 0 FROM E1 AS a CROSS JOIN E1 AS b),
Numbers(n) AS
(
    SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
    FROM E2
)
INSERT dbo.license_types (name, sku_id)
SELECT N'Synthetic SKU ' + RIGHT(N'00' + CAST(n AS nvarchar(2)), 2),
       N'SYNTHETIC_' + RIGHT(N'00' + CAST(n AS nvarchar(2)), 2)
FROM Numbers;

-- Three named selectivities: all-user SKU 1, common SKU 2, and small SKU 3.
INSERT dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id)
SELECT id, 1 FROM dbo.users;

INSERT dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id)
SELECT id, 2 FROM dbo.users WHERE id % 5 <> 0;

INSERT dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id)
SELECT TOP (50) id, 3 FROM dbo.users ORDER BY id;

DECLARE @sparseLicences TABLE
(
    license_type_id int NOT NULL PRIMARY KEY,
    assigned_users int NOT NULL
);
INSERT @sparseLicences (license_type_id, assigned_users)
VALUES (4, 1), (5, 5), (6, 25), (7, 100), (8, 500), (9, 5000), (10, 15000);

INSERT dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id)
SELECT users.id, sparse.license_type_id
FROM @sparseLicences AS sparse
JOIN dbo.users AS users ON users.id <= sparse.assigned_users;

-- The remaining 40 overlapping memberships each cover one eighth of the population:
-- 1.5m rows, and just over 2m current assignments in total.
INSERT dbo.user_license_type_lookups WITH (TABLOCK) (user_id, license_type_id)
SELECT users.id, licences.id
FROM dbo.users AS users
CROSS JOIN dbo.license_types AS licences
WHERE licences.id BETWEEN 11 AND 50
  AND (users.id + licences.id * 13) % 8 = 0;");

            Execute(ScaleUsageSeedSql);

            Execute(@"
INSERT dbo.copilot_usage_report_import_log
    (report_name, report_refresh_date, report_version, report_period, imported_utc,
     rows_read, rows_saved, is_upn_obfuscated, error)
VALUES
    (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-30', N'v2', N'D7',
     '2000-07-01T01:00:00', 300000, 300000, 0, NULL);");
        }

        private void CreateSchema()
        {
            Execute(@"
CREATE TABLE dbo.user_departments
(
    id int IDENTITY NOT NULL CONSTRAINT PK_user_departments PRIMARY KEY,
    name nvarchar(100) NULL
);
CREATE UNIQUE INDEX IX_name ON dbo.user_departments(name);

CREATE TABLE dbo.user_country_or_region
(
    id int IDENTITY NOT NULL CONSTRAINT PK_user_country_or_region PRIMARY KEY,
    name nvarchar(100) NULL
);
CREATE UNIQUE INDEX IX_name ON dbo.user_country_or_region(name);

CREATE TABLE dbo.users
(
    id int IDENTITY NOT NULL CONSTRAINT PK_users PRIMARY KEY,
    user_name varchar(250) NOT NULL,
    mail nvarchar(max) NULL,
    last_updated datetime NULL,
    azure_ad_id nvarchar(max) NULL,
    account_enabled bit NULL,
    postalcode nvarchar(50) NULL,
    company_name_id int NULL,
    state_or_province_id int NULL,
    manager_id int NULL,
    country_or_region_id int NULL,
    office_location_id int NULL,
    usage_location_id int NULL,
    department_id int NULL,
    job_title_id int NULL
);
CREATE INDEX IX_department_id ON dbo.users(department_id);
CREATE INDEX IX_country_or_region_id ON dbo.users(country_or_region_id);

CREATE TABLE dbo.license_types
(
    id int IDENTITY NOT NULL CONSTRAINT PK_license_types PRIMARY KEY,
    sku_id nvarchar(max) NULL,
    name nvarchar(100) NULL
);
CREATE UNIQUE INDEX IX_name ON dbo.license_types(name);

CREATE TABLE dbo.user_license_type_lookups
(
    id int IDENTITY NOT NULL CONSTRAINT PK_user_license_type_lookups PRIMARY KEY,
    user_id int NOT NULL,
    license_type_id int NOT NULL
);
CREATE UNIQUE INDEX IX_license_type_id_user_id
    ON dbo.user_license_type_lookups(license_type_id, user_id);
CREATE INDEX IX_user_id ON dbo.user_license_type_lookups(user_id);

CREATE TABLE dbo.teams_user_activity_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_teams_user_activity_log PRIMARY KEY,
    private_chat_count bigint NOT NULL,
    team_chat_count bigint NOT NULL,
    calls_count bigint NOT NULL,
    meetings_count bigint NOT NULL,
    adhoc_meetings_attended_count bigint NOT NULL,
    adhoc_meetings_organized_count bigint NOT NULL,
    meetings_attended_count bigint NOT NULL,
    meetings_organized_count bigint NOT NULL,
    scheduled_onetime_meetings_attended_count bigint NOT NULL,
    scheduled_onetime_meetings_organized_count bigint NOT NULL,
    scheduled_recurring_meetings_attended_count bigint NOT NULL,
    scheduled_recurring_meetings_organized_count bigint NOT NULL,
    audio_duration_seconds int NOT NULL,
    video_duration_seconds int NOT NULL,
    screenshare_duration_seconds int NOT NULL,
    post_messages bigint NOT NULL,
    reply_messages bigint NOT NULL,
    urgent_messages bigint NOT NULL,
    user_id int NOT NULL,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL
);
CREATE INDEX IX_user_id ON dbo.teams_user_activity_log(user_id);

CREATE TABLE dbo.outlook_user_activity_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_outlook_user_activity_log PRIMARY KEY,
    email_send_count bigint NOT NULL,
    email_receive_count bigint NOT NULL,
    email_read_count bigint NOT NULL,
    meeting_created_count bigint NOT NULL,
    meeting_interacted_count bigint NOT NULL,
    user_id int NOT NULL,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL
);
CREATE INDEX IX_user_id ON dbo.outlook_user_activity_log(user_id);

CREATE TABLE dbo.onedrive_user_activity_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_onedrive_user_activity_log PRIMARY KEY,
    viewed_or_edited bigint NOT NULL,
    synced bigint NOT NULL,
    shared_internally bigint NOT NULL,
    shared_externally bigint NOT NULL,
    user_id int NOT NULL,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL
);
CREATE INDEX IX_user_id ON dbo.onedrive_user_activity_log(user_id);

CREATE TABLE dbo.sharepoint_user_activity_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_sharepoint_user_activity_log PRIMARY KEY,
    viewed_or_edited bigint NOT NULL,
    synced bigint NOT NULL,
    shared_internally bigint NOT NULL,
    shared_externally bigint NOT NULL,
    user_id int NOT NULL,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL
);
CREATE INDEX IX_user_id ON dbo.sharepoint_user_activity_log(user_id);

CREATE TABLE dbo.copilot_usage_report_import_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_copilot_usage_report_import_log PRIMARY KEY,
    report_name nvarchar(100) NULL,
    report_refresh_date datetime NULL,
    report_version nvarchar(10) NULL,
    report_period nvarchar(10) NULL,
    imported_utc datetime NOT NULL,
    rows_read int NOT NULL,
    rows_saved int NOT NULL,
    is_upn_obfuscated bit NOT NULL,
    error nvarchar(1000) NULL
);

CREATE TABLE dbo.copilot_usage_user_activity_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_copilot_usage_user_activity_log PRIMARY KEY,
    report_period_days int NOT NULL,
    prompts_all_apps int NULL,
    prompts_chat_work int NULL,
    prompts_chat_web int NULL,
    active_usage_days int NULL,
    chat_last_activity_date datetime NULL,
    teams_last_activity_date datetime NULL,
    word_last_activity_date datetime NULL,
    excel_last_activity_date datetime NULL,
    powerpoint_last_activity_date datetime NULL,
    outlook_last_activity_date datetime NULL,
    onenote_last_activity_date datetime NULL,
    loop_last_activity_date datetime NULL,
    chat_work_last_activity_date datetime NULL,
    chat_web_last_activity_date datetime NULL,
    m365_copilot_last_activity_date datetime NULL,
    edge_last_activity_date datetime NULL,
    agent_last_activity_date datetime NULL,
    is_upn_obfuscated bit NOT NULL,
    user_id int NOT NULL,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL
);
CREATE UNIQUE INDEX IX_date_user_id_report_period_days
    ON dbo.copilot_usage_user_activity_log([date], user_id, report_period_days);

CREATE TABLE dbo.copilot_chats
(
    event_id uniqueidentifier NOT NULL CONSTRAINT PK_copilot_chats PRIMARY KEY,
    app_host nvarchar(max) NULL,
    agent_id int NULL,
    user_id int NULL,
    time_stamp datetime NULL
);
CREATE INDEX IX_copilot_chats_time_stamp_user_id
    ON dbo.copilot_chats(time_stamp, user_id) INCLUDE(app_host, agent_id);

CREATE TABLE dbo.copilot_interactions
(
    id int IDENTITY NOT NULL CONSTRAINT PK_copilot_interactions PRIMARY KEY,
    graph_interaction_id nvarchar(200) NOT NULL,
    session_id int NOT NULL,
    user_id int NOT NULL,
    request_id nvarchar(200) NULL,
    interaction_type_id int NULL,
    app_class_id int NULL,
    conversation_type_id int NULL,
    locale_id int NULL,
    device_id int NULL,
    created_utc datetime NOT NULL,
    body_char_count int NOT NULL,
    body_word_count int NOT NULL,
    attachment_count int NOT NULL,
    link_count int NOT NULL,
    mention_count int NOT NULL,
    context_count int NOT NULL,
    response_latency_ms int NULL,
    sentiment_score float NULL,
    language_id int NULL
);
CREATE INDEX IX_user_id_created_utc
    ON dbo.copilot_interactions(user_id, created_utc);
CREATE INDEX IX_created_utc
    ON dbo.copilot_interactions(created_utc);

CREATE TABLE dbo.copilot_interaction_import_log
(
    id int IDENTITY NOT NULL CONSTRAINT PK_copilot_interaction_import_log PRIMARY KEY,
    run_started_utc datetime NOT NULL,
    run_finished_utc datetime NULL,
    users_in_scope int NOT NULL,
    users_scanned int NOT NULL,
    users_skipped int NOT NULL,
    users_failed int NOT NULL,
    interactions_read int NOT NULL,
    interactions_saved int NOT NULL,
    cognitive_docs_scored int NOT NULL,
    error nvarchar(1000) NULL
);");
        }

        private const string ScaleUsageSeedSql = @"
CREATE TABLE #SyntheticSamples
(
    sample_number int NOT NULL PRIMARY KEY,
    sample_date date NOT NULL
);

;WITH E1(n) AS
(
    SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS n(n)
),
Numbers(n) AS
(
    SELECT TOP (26) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1
    FROM E1 AS a CROSS JOIN E1 AS b
)
INSERT #SyntheticSamples (sample_number, sample_date)
SELECT n,
       CASE
           WHEN n = 0 THEN CONVERT(date, '20000109', 112)
           WHEN n = 25 THEN CONVERT(date, '20000630', 112)
           ELSE DATEADD(DAY, n * 7 + 6, CONVERT(date, '20000103', 112))
       END
FROM Numbers;

INSERT dbo.teams_user_activity_log WITH (TABLOCK)
(
    private_chat_count, team_chat_count, calls_count, meetings_count,
    adhoc_meetings_attended_count, adhoc_meetings_organized_count,
    meetings_attended_count, meetings_organized_count,
    scheduled_onetime_meetings_attended_count, scheduled_onetime_meetings_organized_count,
    scheduled_recurring_meetings_attended_count, scheduled_recurring_meetings_organized_count,
    audio_duration_seconds, video_duration_seconds, screenshare_duration_seconds,
    post_messages, reply_messages, urgent_messages, user_id, [date], last_activity_date
)
SELECT CASE WHEN activity.is_active = 1 THEN 1 + (users.id + samples.sample_number) % 8 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 3 + samples.sample_number) % 7 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id + samples.sample_number) % 3 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id + samples.sample_number) % 4 ELSE 0 END,
       0, 0,
       CASE WHEN activity.is_active = 1 THEN (users.id + samples.sample_number) % 4 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 5 + samples.sample_number) % 3 ELSE 0 END,
       0, 0, 0, 0, 0, 0, 0,
       CASE WHEN activity.is_active = 1 THEN (users.id * 7 + samples.sample_number) % 6 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 11 + samples.sample_number) % 5 ELSE 0 END,
       0,
       users.id, samples.sample_date,
       CASE WHEN activity.is_active = 1 THEN samples.sample_date ELSE NULL END
FROM dbo.users AS users
CROSS JOIN #SyntheticSamples AS samples
CROSS APPLY
(
    SELECT CAST(CASE
        WHEN (users.id + 1) % 10 <= 2 THEN 1
        WHEN (users.id + 1) % 10 BETWEEN 3 AND 5
         AND samples.sample_number % 2 = users.id % 2 THEN 1
        WHEN (users.id + 1) % 10 BETWEEN 6 AND 7
         AND samples.sample_number % 10 = users.id % 10 THEN 1
        ELSE 0
    END AS int) AS is_active
) AS activity;

INSERT dbo.outlook_user_activity_log WITH (TABLOCK)
(
    email_send_count, email_receive_count, email_read_count,
    meeting_created_count, meeting_interacted_count, user_id, [date], last_activity_date
)
SELECT CASE WHEN activity.is_active = 1 THEN 1 + (users.id + samples.sample_number) % 12 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 3 + samples.sample_number) % 20 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN 1 + (users.id * 5 + samples.sample_number) % 25 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id + samples.sample_number) % 3 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 7 + samples.sample_number) % 4 ELSE 0 END,
       users.id, samples.sample_date,
       CASE WHEN activity.is_active = 1 THEN samples.sample_date ELSE NULL END
FROM dbo.users AS users
CROSS JOIN #SyntheticSamples AS samples
CROSS APPLY
(
    SELECT CAST(CASE
        WHEN (users.id + 3) % 10 <= 2 THEN 1
        WHEN (users.id + 3) % 10 BETWEEN 3 AND 5
         AND samples.sample_number % 2 = users.id % 2 THEN 1
        WHEN (users.id + 3) % 10 BETWEEN 6 AND 7
         AND samples.sample_number % 10 = users.id % 10 THEN 1
        ELSE 0
    END AS int) AS is_active
) AS activity;

INSERT dbo.onedrive_user_activity_log WITH (TABLOCK)
(
    viewed_or_edited, synced, shared_internally, shared_externally,
    user_id, [date], last_activity_date
)
SELECT CASE WHEN activity.is_active = 1 THEN 1 + (users.id * 3 + samples.sample_number) % 18 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id + samples.sample_number) % 5 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 7 + samples.sample_number) % 3 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 11 + samples.sample_number) % 2 ELSE 0 END,
       users.id, samples.sample_date,
       CASE WHEN activity.is_active = 1 THEN samples.sample_date ELSE NULL END
FROM dbo.users AS users
CROSS JOIN #SyntheticSamples AS samples
CROSS APPLY
(
    SELECT CAST(CASE
        WHEN (users.id + 5) % 10 <= 2 THEN 1
        WHEN (users.id + 5) % 10 BETWEEN 3 AND 5
         AND samples.sample_number % 2 = users.id % 2 THEN 1
        WHEN (users.id + 5) % 10 BETWEEN 6 AND 7
         AND samples.sample_number % 10 = users.id % 10 THEN 1
        ELSE 0
    END AS int) AS is_active
) AS activity;

INSERT dbo.sharepoint_user_activity_log WITH (TABLOCK)
(
    viewed_or_edited, synced, shared_internally, shared_externally,
    user_id, [date], last_activity_date
)
SELECT CASE WHEN activity.is_active = 1 THEN 1 + (users.id * 5 + samples.sample_number) % 22 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id + samples.sample_number) % 4 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 7 + samples.sample_number) % 4 ELSE 0 END,
       CASE WHEN activity.is_active = 1 THEN (users.id * 13 + samples.sample_number) % 2 ELSE 0 END,
       users.id, samples.sample_date,
       CASE WHEN activity.is_active = 1 THEN samples.sample_date ELSE NULL END
FROM dbo.users AS users
CROSS JOIN #SyntheticSamples AS samples
CROSS APPLY
(
    SELECT CAST(CASE
        WHEN (users.id + 7) % 10 <= 2 THEN 1
        WHEN (users.id + 7) % 10 BETWEEN 3 AND 5
         AND samples.sample_number % 2 = users.id % 2 THEN 1
        WHEN (users.id + 7) % 10 BETWEEN 6 AND 7
         AND samples.sample_number % 10 = users.id % 10 THEN 1
        ELSE 0
    END AS int) AS is_active
) AS activity;

INSERT dbo.copilot_usage_user_activity_log WITH (TABLOCK)
(
    report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
    user_id, [date], last_activity_date
)
SELECT 7,
       CASE WHEN activity.is_active = 1
            THEN 1 + (users.id * 7 + samples.sample_number) % 30 ELSE 0 END,
       activity.is_active,
       0,
       users.id, samples.sample_date,
       CASE WHEN activity.is_active = 1 THEN samples.sample_date ELSE NULL END
FROM dbo.users AS users
CROSS JOIN #SyntheticSamples AS samples
CROSS APPLY
(
    SELECT CAST(CASE
        WHEN (users.id + 9) % 10 <= 2 THEN 1
        WHEN (users.id + 9) % 10 BETWEEN 3 AND 5
         AND samples.sample_number % 2 = users.id % 2 THEN 1
        WHEN (users.id + 9) % 10 BETWEEN 6 AND 7
         AND samples.sample_number % 10 = users.id % 10 THEN 1
        ELSE 0
    END AS int) AS is_active
) AS activity;

DROP TABLE #SyntheticSamples;";
    }

    internal sealed class LicenceActivitySqlMeasurement
    {
        private static readonly Regex LogicalReads =
            new Regex(@"logical reads\s+(?<reads>[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const int MaximumRetainedMessages = 128;
        private long _totalLogicalReads;
        private int _retainedMessages;
        private readonly ConcurrentDictionary<string, long> _logicalReadsByOperation =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        internal readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
        internal readonly ConcurrentQueue<string> Showplans = new ConcurrentQueue<string>();
        internal readonly ConcurrentQueue<string> Operations = new ConcurrentQueue<string>();

        internal long TotalLogicalReads => Interlocked.Read(ref _totalLogicalReads);
        internal IEnumerable<string> LogicalReadsByOperation =>
            _logicalReadsByOperation
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture));

        private void RecordMessage(string message, string operation = null)
        {
            if (string.IsNullOrEmpty(message)) return;

            long reads = 0;
            foreach (Match match in LogicalReads.Matches(message))
            {
                reads += long.Parse(match.Groups["reads"].Value, CultureInfo.InvariantCulture);
            }
            if (reads != 0)
            {
                Interlocked.Add(ref _totalLogicalReads, reads);
                _logicalReadsByOperation.AddOrUpdate(
                    operation ?? "unlabelled", reads, (key, value) => value + reads);
            }

            Messages.Enqueue(string.IsNullOrEmpty(operation)
                ? message
                : "[" + operation + "] " + message);
            if (Interlocked.Increment(ref _retainedMessages) > MaximumRetainedMessages
                && Messages.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _retainedMessages);
            }
        }

        internal SqlLicenceActivityStoreInstrumentation Instrumentation(
            bool includeShowplan,
            int? commandTimeoutSeconds = null)
        {
            Action<SqlConnection, string> configure = (connection, operation) =>
            {
                connection.InfoMessage += (sender, args) => RecordMessage(args.Message, operation);
                using (var command = new SqlCommand(
                    includeShowplan
                        ? "SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;"
                        : "SET STATISTICS IO ON; SET STATISTICS TIME ON;",
                    connection))
                {
                    command.ExecuteNonQuery();
                }
            };

            return new SqlLicenceActivityStoreInstrumentation
            {
                CommandTimeoutSeconds = commandTimeoutSeconds,
                ConnectionOpened = connection => configure(connection, null),
                ConnectionOpenedForOperation = configure,
                OperationCompleted = (operation, elapsedMs) =>
                    Operations.Enqueue(operation + "=" + elapsedMs.ToString(CultureInfo.InvariantCulture) + "ms"),
                ShowplanReceived = plan => Showplans.Enqueue(plan)
            };
        }
    }
}
