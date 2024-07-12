# Returns statistics on the profiling database

param(
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

$SqlCmd = @"
-- Query 1
SELECT schemas.name, tables.name, partitions.rows
FROM sys.partitions AS partitions
  INNER JOIN sys.tables AS tables ON partitions.[object_id] = tables.[object_id]
  INNER JOIN sys.schemas AS schemas ON schemas.[schema_id] = tables.[schema_id]
WHERE (
    schemas.name = N'profiling'
    OR tables.name LIKE '%_user_activity_log'
    OR tables.name LIKE 'event_copilot_%'
) AND partitions.index_id IN (0,1)
ORDER BY schemas.name, tables.name;

-- Query 2
SELECT MIN(MetricDate) WeeklyActivitiesFrom, MAX(MetricDate) WeeklyActivitiesTo FROM [profiling].[ActivitiesWeekly];

-- Query 3
SELECT MIN([date]) WeeklyActivitiesColumnsFrom, MAX([date]) WeeklyActivitiesColumnsTo FROM [profiling].[ActivitiesWeeklyColumns];

-- Query 4
SELECT MIN([date]) WeeklyUsageFrom, MAX([date]) WeeklyUsageTo FROM [profiling].[UsageWeekly];
"@

try {
    $DatabaseConnection.Open()
    $Cmd = New-Object System.Data.SqlClient.SqlCommand
    $Cmd.Connection = $DatabaseConnection
    $Cmd.CommandTimeout = 10800 # 3 hours

    $Cmd.CommandText = $SqlCmd
    $results = $cmd.ExecuteReader()

    # Query 1
    Write-Output "Profiling table sizes"
    while ($results.Read()) {
        $schema = $results.GetString(0)
        $table = $results.GetString(1)
        $rows = $results.GetInt64(2)
        Write-Output "$schema.$($table): $rows rows"
    }
    Write-Output ""
    # Query 2
    if ($results.NextResult()) {
        while ($results.Read()) {
            $from = $results.GetDateTime(0)
            $to = $results.GetDateTime(1)
            Write-Output "ActivitiesWeekly from $(fd $from) to $(fd $to)"
        }
        Write-Output ""
    }
    # Query 3
    if ($results.NextResult()) {
        while ($results.Read()) {
            $from = $results.GetDateTime(0)
            $to = $results.GetDateTime(1)
            Write-Output "ActivitiesWeeklyColumns from $(fd $from) to $(fd $to)"
        }
        Write-Output ""
    }
    # Query 4
    if ($results.NextResult()) {
        while ($results.Read()) {
            $from = $results.GetDateTime(0)
            $to = $results.GetDateTime(1)
            Write-Output "UsageWeekly from $(fd $from) to $(fd $to)"
        }
        Write-Output ""
    }

    $results.Close()
} catch {
    $LastError = $_.Exception
} finally {
    $DatabaseConnection.Close()
    $DatabaseConnection.Dispose()
}

if ($LastError) { Write-Error $LastError }
