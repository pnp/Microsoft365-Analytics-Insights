param(
    [string]$TestAssembly = (Join-Path $PSScriptRoot '..\Tests.UnitTests\bin\Release\net10.0-windows\Tests.UnitTests.dll'),
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\Tests.UnitTests\TestResults\BackfillEvidence'),
    [switch]$RegressionOnly,
    [switch]$SqlRegressionOnly,
    [switch]$SqlScale,
    [ValidateRange(1, 10)][int]$SqlDays = 10,
    [string]$LocalDbVersion = '15.0'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path $TestAssembly)) { throw 'Build Tests.UnitTests in Release first.' }
if (([int]$RegressionOnly.IsPresent + [int]$SqlRegressionOnly.IsPresent + [int]$SqlScale.IsPresent) -gt 1) {
    throw 'Choose one evidence mode.'
}
$old = $env:BACKFILL_SYNTHETIC_SCALE
$oldSql = $env:BACKFILL_SYNTHETIC_SQL_SCALE
$oldDays = $env:BACKFILL_SYNTHETIC_SQL_DAYS
$oldCatalog = $env:BACKFILL_SYNTHETIC_SQL_CATALOG
$oldInstance = $env:BACKFILL_SYNTHETIC_SQL_INSTANCE
$localDbInstance = $null
$instanceCreated = $false
$oldTenant = $env:TenantGUID
$oldClient = $env:ClientID
$oldConnection = $env:ConnectionStrings__SPOInsightsEntities
try {
    # AnalyticsConfig reads environment overrides; net10 never creates or edits XML configuration.
    $env:TenantGUID = '00000000-0000-0000-0000-000000000000'
    $env:ClientID = '00000000-0000-0000-0000-000000000000'
    $env:BACKFILL_SYNTHETIC_SCALE = '1'
    $test = 'Importer_Synthetic200kUserPagingEvidence'
    $label = 'memory'
    if ($SqlScale -or $SqlRegressionOnly) {
        $env:BACKFILL_SYNTHETIC_SQL_SCALE = '1'
        $env:BACKFILL_SYNTHETIC_SQL_DAYS = "$SqlDays"
        $test = 'Importer_SyntheticSql200kUserEvidence'
        $label = 'sql'
        $localDbInstance = 'Backfill_' + [guid]::NewGuid().ToString('N')
        & SqlLocalDB create $localDbInstance $LocalDbVersion -s
        if ($LASTEXITCODE -ne 0) { throw "Could not create the dedicated LocalDB instance (version $LocalDbVersion)." }
        $instanceCreated = $true
        $env:BACKFILL_SYNTHETIC_SQL_INSTANCE = $localDbInstance
        $env:BACKFILL_SYNTHETIC_SQL_CATALOG = 'UnitTestingAnalytics_Backfill_' + [guid]::NewGuid().ToString('N')
        $env:ConnectionStrings__SPOInsightsEntities = "Data Source=(localdb)\$localDbInstance;Initial Catalog=$env:BACKFILL_SYNTHETIC_SQL_CATALOG;Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True;App=EntityFramework"
        Write-Host "Dedicated synthetic instance (stopped on exit, retained): $localDbInstance"
        Write-Host "New synthetic catalog (retained, never reused/dropped): $env:BACKFILL_SYNTHETIC_SQL_CATALOG"
    }
    $filter = "FullyQualifiedName~CopilotAuditBackfillTests.$test"
    if ($RegressionOnly) {
        $filter = 'FullyQualifiedName~CopilotAuditBackfillTests&TestCategory!=SqlIntegration&TestCategory!=SyntheticScale&TestCategory!=SyntheticSqlScale'
        $label = 'regression'
    }
    if ($SqlRegressionOnly) {
        $filter = 'FullyQualifiedName~CopilotAuditBackfillTests&TestCategory=SqlIntegration'
        $label = 'sql-regression'
    }
    & dotnet vstest $TestAssembly "--TestCaseFilter:$filter" "--ResultsDirectory:$ResultsDirectory" "--Logger:trx;LogFileName=backfill-$label-scale.trx"
    if ($LASTEXITCODE -ne 0) { throw "Synthetic evidence failed: exit $LASTEXITCODE" }
}
finally {
    $env:BACKFILL_SYNTHETIC_SCALE = $old
    $env:BACKFILL_SYNTHETIC_SQL_SCALE = $oldSql
    $env:BACKFILL_SYNTHETIC_SQL_DAYS = $oldDays
    $env:BACKFILL_SYNTHETIC_SQL_CATALOG = $oldCatalog
    $env:BACKFILL_SYNTHETIC_SQL_INSTANCE = $oldInstance
    $env:TenantGUID = $oldTenant
    $env:ClientID = $oldClient
    $env:ConnectionStrings__SPOInsightsEntities = $oldConnection
    if ($instanceCreated) {
        & SqlLocalDB stop $localDbInstance
        if ($LASTEXITCODE -ne 0) { Write-Warning "Stop the owned LocalDB instance manually: $localDbInstance" }
    }
}
# This runs production mapping, paging/state and importer orchestration with in-memory persistence.
# Its byte counts are serialized traffic/state, NOT SQL storage; timings are NOT tenant throughput.
# -SqlScale uses real production migrations/persistence, only against a fresh
# LocalDB instance AND catalog with new random names for every invocation.
# Release persistence uses instance-global ## staging tables: separate catalogs alone
# do not isolate parallel importer processes. -LocalDbVersion selects an installed version.
# The fixture rejects existing catalogs before running any migrations. No catalog is dropped/reset.
# -SqlRegressionOnly runs real-persistence overlap/recovery against its own fresh catalog.
