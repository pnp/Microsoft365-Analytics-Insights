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

$SqlCredential = Get-AutomationPSCredential -Name "SqlCredential" -ErrorAction SilentlyContinue
$SqlServer = Get-AutomationVariable -Name "SqlServer"
$SqlDatabase = Get-AutomationVariable -Name "SqlDatabase"

# The "SqlCredential" automation credential only exists while the database uses SQL Server
# authentication. When the installer configures Microsoft Entra ID authentication there is no login to
# store, so this Automation account's own managed identity is used instead. See issue #117.
$UseManagedIdentity = ($null -eq $SqlCredential)
if ($UseManagedIdentity) {
    $SqlUsername = ''
    $SqlPass = ''
} else {
    $SqlUsername = $SqlCredential.UserName
    $SqlPass = $SqlCredential.GetNetworkCredential().Password
}
$SqlConnectionString = "`
data source=$SqlServer;`
initial catalog=$SqlDatabase;`
user id=$SqlUsername;`
password=$SqlPass;`
persist security info=True;`
MultipleActiveResultSets=True;`
Encrypt=True;`
Connection Timeout=60"
if ($UseManagedIdentity) {
    # No SQL login to put in the connection string: build a credential-free one and attach a Microsoft
    # Entra ID access token. SqlClient rejects an access token alongside a user id/password.
    $SqlConnectionString = "data source=$SqlServer;initial catalog=$SqlDatabase;persist security info=False;MultipleActiveResultSets=True;Encrypt=True;Connection Timeout=60"
}
$DatabaseConnection = New-Object System.Data.SqlClient.SqlConnection($SqlConnectionString)
if ($UseManagedIdentity) {
    Connect-AzAccount -Identity | Out-Null
    $SqlAccessToken = Get-AzAccessToken -ResourceUrl "https://database.windows.net/"
    if ($SqlAccessToken.Token -is [System.Security.SecureString]) {
        # Newer Az modules return the token as a SecureString.
        $DatabaseConnection.AccessToken = [System.Net.NetworkCredential]::new('', $SqlAccessToken.Token).Password
    } else {
        $DatabaseConnection.AccessToken = $SqlAccessToken.Token
    }
}

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
