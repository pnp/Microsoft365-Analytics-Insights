param(
    [string]$DatabaseName = "UnitTest495Bench",
    [int[]]$SyntheticUsers = @(1000, 10000),
    [int[]]$SyntheticDays = @(1, 7),
    [int[]]$ChangedRows = @(100, 1000),
    [string]$SqlcmdPath = "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE",
    [string]$LocalDbInstance = "(localdb)\MSSQLLocalDB"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sqlPath = Join-Path $scriptRoot "UsageReportSaveSyntheticBenchmark.sql"
$artifactRoot = Join-Path $scriptRoot "artifacts"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

if (-not (Test-Path $SqlcmdPath)) {
    throw "sqlcmd was not found at '$SqlcmdPath'. Pass -SqlcmdPath if it is installed elsewhere."
}

if ($DatabaseName -notmatch '^[A-Za-z0-9_]+$') {
    throw "Use a simple throwaway LocalDB database name containing only letters, numbers and underscores."
}

& $SqlcmdPath -S $LocalDbInstance -d master -b -Q "IF DB_ID(N'$DatabaseName') IS NOT NULL BEGIN ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DatabaseName]; END; CREATE DATABASE [$DatabaseName] COLLATE Latin1_General_CI_AS;"

$summary = @()
foreach ($userCount in $SyntheticUsers) {
    foreach ($dayCount in $SyntheticDays) {
        foreach ($changedCount in $ChangedRows) {
            $caseName = "users-$userCount-days-$dayCount-changed-$changedCount"
            $outputPath = Join-Path $artifactRoot "$caseName.txt"
            $started = Get-Date

            & $SqlcmdPath `
                -S $LocalDbInstance `
                -d $DatabaseName `
                -b `
                -i $sqlPath `
                -v SyntheticUsers="$userCount" SyntheticDays="$dayCount" ChangedRows="$changedCount" `
                -o $outputPath

            $completed = Get-Date
            $summary += [pscustomobject]@{
                Case = $caseName
                SyntheticUsers = $userCount
                SyntheticDays = $dayCount
                ChangedRows = $changedCount
                WallClockSeconds = [math]::Round(($completed - $started).TotalSeconds, 3)
                Output = $outputPath
            }
        }
    }
}

$summaryPath = Join-Path $artifactRoot "summary.csv"
$summary | Export-Csv -NoTypeInformation -Path $summaryPath
$summary | Format-Table -AutoSize
Write-Host "Synthetic benchmark artifacts written under $artifactRoot"
