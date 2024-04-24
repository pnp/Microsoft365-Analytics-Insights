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
SELECT s.name, t.name, p.rows FROM sys.partitions AS p
  INNER JOIN sys.tables AS t ON p.[object_id] = t.[object_id]
  INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE (s.name = N'profiling' OR t.name LIKE '%_user_activity_log') AND p.index_id IN (0,1)
ORDER BY s.name, t.name;

SELECT MIN(MetricDate) WeeklyActivitiesFrom, MAX(MetricDate) WeeklyActivitiesTo FROM [profiling].[ActivitiesWeekly];

SELECT MIN([date]) WeeklyActivitiesColumnsFrom, MAX([date]) WeeklyActivitiesColumnsTo FROM [profiling].[ActivitiesWeeklyColumns];

SELECT MIN([date]) WeeklyUsageFrom, MAX([date]) WeeklyUsageTo FROM [profiling].[UsageWeekly];
"@

try {
    $DatabaseConnection.Open()
    $Cmd = New-Object system.Data.SqlClient.SqlCommand
    $Cmd.Connection = $DatabaseConnection
    $Cmd.CommandTimeout = 10800 # 3 hours

    $Cmd.CommandText = $SqlCmd
    $results = $cmd.ExecuteReader()

    Write-Output "Profiling table sizes"
    while ($results.Read()) {
        $schema = $results.GetString(0)
        $table = $results.GetString(1)
        $rows = $results.GetInt64(2)
        Write-Output "$schema.$($table): $rows rows"
    }

    $results.NextResult() | Out-Null
    while ($results.Read()) {
        $from = $results.GetDateTime(0)
        $to = $results.GetDateTime(1)
        Write-Output "ActivitiesWeekly from $(fd $from) to $(fd $to)"
    }
    $results.NextResult() | Out-Null
    while ($results.Read()) {
        $from = $results.GetDateTime(0)
        $to = $results.GetDateTime(1)
        Write-Output "ActivitiesWeeklyColumns from $(fd $from) to $(fd $to)"
    }
    $results.NextResult() | Out-Null
    while ($results.Read()) {
        $from = $results.GetDateTime(0)
        $to = $results.GetDateTime(1)
        Write-Output "UsageWeekly from $(fd $from) to $(fd $to)"
    }

    $results.Close()
}
catch {
    $LastError = $_.Exception
}
finally {
    $DatabaseConnection.Close()
    $DatabaseConnection.Dispose()
}

if ($LastError) { Write-Error $LastError }
