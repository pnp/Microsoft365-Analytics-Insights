<#
.SYNOPSIS
Starts the runner's LocalDB instance before the .NET tests, and proves that it answers a query.

.DESCRIPTION
Every SQL test connects to (localdb)\MSSQLLocalDB, and LocalDB creates and starts that instance on the first
connection. On the hosted Windows runners that first start is slow, and now and then it fails: "Cannot create an
automatic instance", then "Error occurred during LocalDB instance startup: SQL Server process failed to start" for
every connection after it (actions/runner-images#8164). A shard that hit it failed every SQL test, one connection
attempt at a time; one of them ran on until the job was cancelled at 45 minutes.

This starts the instance before the tests and checks it with a query. When either fails, it prints LocalDB's error
log and the Application event log entries, then, with -Recreate, deletes the instance and creates it again, up to
-Attempts times. If the instance never answers, the step fails within minutes, saying so.

Without -Recreate nothing is deleted, so running this on a workstation can't remove an instance that holds databases.
#>
[CmdletBinding()]
param(
    [string]$Instance = 'MSSQLLocalDB',
    [ValidateRange(1, 10)][int]$Attempts = 3,
    [ValidateRange(30, 1800)][int]$StartTimeoutSeconds = 300,
    [ValidateRange(5, 600)][int]$QueryTimeoutSeconds = 60,
    [switch]$Recreate
)
$ErrorActionPreference = 'Stop'

function Find-SqlLocalDb {
    $command = Get-Command SqlLocalDB.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $installed = Get-ChildItem "$env:ProgramFiles\Microsoft SQL Server\*\Tools\Binn\SqlLocalDB.exe" -ErrorAction SilentlyContinue |
        Sort-Object { $_.Directory.Parent.Parent.Name -as [int] } -Descending |
        Select-Object -First 1
    if ($installed) { return $installed.FullName }
    throw 'SqlLocalDB.exe was not found, so LocalDB is not installed on this machine.'
}

# Runs SqlLocalDB.exe and echoes what it says. Its exit code isn't trusted - it can exit 0 after printing an error - so
# a query, not this, decides whether the instance is up.
function Invoke-SqlLocalDb([string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $info = [Diagnostics.ProcessStartInfo]::new($script:SqlLocalDb)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            Write-Host "SqlLocalDB $($Arguments -join ' ') did not finish within $TimeoutSeconds seconds, so it was stopped."
            return
        }
        $text = ($output.Result + $errors.Result).Trim()
        if ($text) { Write-Host $text }
    }
    finally { $process.Dispose() }
}

function Test-LocalDb {
    # A string, not SqlConnectionStringBuilder: PowerShell turns a property set on that dictionary into an unknown key.
    $connection = [Data.SqlClient.SqlConnection]::new(
        "Data Source=(localdb)\$Instance;Initial Catalog=master;Integrated Security=true;Pooling=false;Connect Timeout=$QueryTimeoutSeconds")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = $QueryTimeoutSeconds
        $command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) + N' ' + CONVERT(nvarchar(128), SERVERPROPERTY('Edition'));"
        return [string]$command.ExecuteScalar()
    }
    finally { $connection.Dispose() }
}

function Write-Diagnostics([datetime]$Since) {
    $log = Join-Path $env:LOCALAPPDATA "Microsoft\Microsoft SQL Server Local DB\Instances\$Instance\error.log"
    if (Test-Path -LiteralPath $log) {
        Write-Host "::group::Last lines of the instance's error.log"
        Get-Content -LiteralPath $log -Tail 80 | ForEach-Object { Write-Host $_ }
        Write-Host '::endgroup::'
    }
    else { Write-Host "The instance has no error.log yet ($log)." }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $Since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'LocalDB|MSSQL' } | Select-Object -First 20)
    if ($events.Count -gt 0) {
        Write-Host '::group::LocalDB entries in the Application event log'
        foreach ($entry in $events) {
            Write-Host ('{0:u} {1} {2}: {3}' -f $entry.TimeCreated, $entry.ProviderName, $entry.Id, ($entry.Message -replace '\s+', ' '))
        }
        Write-Host '::endgroup::'
    }
}

function Reset-LocalDbInstance {
    Write-Host "Deleting LocalDB instance '$Instance' so that the next attempt starts a new one."
    Invoke-SqlLocalDb @('stop', $Instance, '-k')
    Invoke-SqlLocalDb @('delete', $Instance)
    $directory = Join-Path $env:LOCALAPPDATA "Microsoft\Microsoft SQL Server Local DB\Instances\$Instance"
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    # 'start' creates the automatic instance by itself; any other instance has to be created again.
    if ($Instance -ne 'MSSQLLocalDB') { Invoke-SqlLocalDb @('create', $Instance) }
}

$script:SqlLocalDb = Find-SqlLocalDb
Write-Host "Using $script:SqlLocalDb. Installed LocalDB versions:"
Invoke-SqlLocalDb @('versions')

for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    $since = Get-Date
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "Starting LocalDB instance '$Instance', attempt $attempt of $Attempts..."
    Invoke-SqlLocalDb @('start', $Instance) $StartTimeoutSeconds
    try {
        $version = Test-LocalDb
        Write-Host ("LocalDB instance '{0}' answered after {1:N0} seconds: SQL Server {2}." -f $Instance, $watch.Elapsed.TotalSeconds, $version)
        Invoke-SqlLocalDb @('info', $Instance)
        return
    }
    catch { Write-Host "LocalDB instance '$Instance' did not answer a query: $($_.Exception.Message)" }
    Write-Diagnostics $since
    if ($attempt -lt $Attempts) {
        if ($Recreate) { Reset-LocalDbInstance }
        else { Write-Host 'Trying again without deleting the instance, because -Recreate was not given.' }
    }
}
throw "LocalDB instance '$Instance' did not start after $Attempts attempts, so the SQL tests can't run. Re-run the job; LocalDB's own log is above."
