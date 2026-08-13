# Index and statistics maintenance for the database.
# Depends on the SPs created by this solution: https://ola.hallengren.com/sql-server-index-and-statistics-maintenance.html
#
# MaintenanceType:
#   weekly      - maintains ALL indexes + statistics across the whole database
#                 (core import tables incl. audit_events / users / sites and all
#                 copilot_* + event_meta_* tables, plus profiling + activity logs).
#                 This is the one to schedule weekly.
#   activitylog - legacy, targeted run for just the *_activity_log usage tables
#                 (kept for back-compat; 'weekly' already covers these).

param(
    [Parameter(Mandatory=$true)]
    [string]$MaintenanceType
)
$ErrorActionPreference = 'Stop'
$VerbosePreference = 'SilentlyContinue'

function fd($date) { $date.ToString('yyyy-MM-dd') }

$SqlCredential = Get-AutomationPSCredential -Name "SqlCredential"
$SqlServer = Get-AutomationVariable -Name "SqlServer"
$SqlDatabase = Get-AutomationVariable -Name "SqlDatabase"
$SqlUsername = $SqlCredential.UserName
$SqlPass = $SqlCredential.GetNetworkCredential().Password
$SqlConnectionString = "`
data source=$SqlServer;`
initial catalog=$SqlDatabase;`
user id=$SqlUsername;`
password=$SqlPass;`
persist security info=True;`
MultipleActiveResultSets=True;`
Encrypt=True;`
Connection Timeout=60"
$DatabaseConnection = New-Object System.Data.SqlClient.SqlConnection($SqlConnectionString)

$SqlCmdWeekly = @"
-- Maintain EVERY index + all statistics across the database. Ola's IndexOptimize
-- only touches indexes above the fragmentation thresholds, so after the first
-- pass the weekly run is cheap. @TimeLimit stops it gracefully within the
-- maintenance window (below the SqlCommand timeout) and it resumes next week.
EXECUTE dbo.IndexOptimize
@Databases = 'USER_DATABASES',
@Indexes = 'ALL_INDEXES',
@FragmentationLow = NULL,
@FragmentationMedium = 'INDEX_REORGANIZE,INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
@FragmentationHigh = 'INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
@FragmentationLevel1 = 5,
@FragmentationLevel2 = 30,
@UpdateStatistics = 'ALL',
@TimeLimit = 9000,
@MaxDOP = 0
"@

$SqlCmdActivityLog = @"
EXECUTE dbo.IndexOptimize
@Databases = 'USER_DATABASES',
@Indexes = '%.dbo.%_activity_log.%',
@FragmentationLow = NULL,
@FragmentationMedium = 'INDEX_REORGANIZE,INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
@FragmentationHigh = 'INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
@FragmentationLevel1 = 5,
@FragmentationLevel2 = 30,
@UpdateStatistics = 'ALL',
@MaxDOP = 0;
EXECUTE dbo.IndexOptimize
@Databases = 'USER_DATABASES',
@Indexes = '%.dbo.teams_user_device_usage_log.%',
@FragmentationLow = NULL,
@FragmentationMedium = 'INDEX_REORGANIZE,INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
@FragmentationHigh = 'INDEX_REBUILD_ONLINE,INDEX_REBUILD_OFFLINE',
@FragmentationLevel1 = 5,
@FragmentationLevel2 = 30,
@UpdateStatistics = 'ALL',
@MaxDOP = 0;
"@

switch ($MaintenanceType.ToLower()) {
    "weekly" { $SqlCmd = $SqlCmdWeekly }
    "activitylog" { $SqlCmd = $SqlCmdActivityLog }
    Default {
        throw "Invalid maintenance type: $MaintenanceType"
    }
}

try {
    $DatabaseConnection.Open()
    $Cmd = New-Object system.Data.SqlClient.SqlCommand
    $Cmd.Connection = $DatabaseConnection
    $Cmd.CommandTimeout = 10500 # 3 hours

    $Cmd.CommandText = $SqlCmd
    $Cmd.ExecuteNonQuery() | Out-Null

    Write-Output "Index maintenance $MaintenanceType completed"
}
catch {
    $LastError = $_.Exception
}
finally {
    $DatabaseConnection.Close()
    $DatabaseConnection.Dispose()
}

if ($LastError) { Write-Error $LastError }
