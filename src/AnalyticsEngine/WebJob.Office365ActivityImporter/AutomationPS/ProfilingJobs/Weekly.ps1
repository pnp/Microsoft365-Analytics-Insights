<#
.SYNOPSIS
    Aggregates the activities for each week
.DESCRIPTION
    Aggregates all the weeks that can be aggregated
#>

param(
)
$ErrorActionPreference = 'Stop'
$VerbosePreference = 'SilentlyContinue'

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

$WeeksToKeep = Get-AutomationVariable -Name "WeeksToKeep"

try {
    $DatabaseConnection.Open()
    $cmd = New-Object System.Data.SqlClient.SqlCommand
    $cmd.Connection = $DatabaseConnection
    $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
    $Cmd.CommandTimeout = 10500 # 3 hours
    $cmd.CommandText = "[profiling].[usp_CompileWeekly]"
    $cmd.Parameters.AddWithValue("@WeeksToKeep", $WeeksToKeep) | Out-Null
    $returnValueParameter = New-Object System.Data.SqlClient.SqlParameter("@returnValue", [System.Data.SqlDbType]::Int)
    $returnValueParameter.Direction = [System.Data.ParameterDirection]::ReturnValue
    $cmd.Parameters.Add($returnValueParameter) | Out-Null

    $cmd.ExecuteNonQuery() | Out-Null
    $returnedValue = [int] $returnValueParameter.Value
    switch ($returnedValue) {
        0 { Write-Output "Weekly aggregation completed" }
        Default { throw "Some error happened of which I don't have more information" }
    }
}
catch {
    $LastError = $_.Exception
}
finally {
    $DatabaseConnection.Close()
    $DatabaseConnection.Dispose()
}

if ($LastError) { Write-Error $LastError }
