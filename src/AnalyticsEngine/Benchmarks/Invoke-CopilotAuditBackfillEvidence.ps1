param(
    [string]$TestAssembly = (Join-Path $PSScriptRoot '..\Tests.UnitTests\bin\Release\Tests.UnitTests.dll'),
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\Tests.UnitTests\TestResults\BackfillEvidence'),
    [switch]$RegressionOnly,
    [switch]$SqlRegressionOnly,
    [switch]$SqlScale,
    [ValidateRange(1, 10)][int]$SqlDays = 10,
    [string]$LocalDbVersion = '15.0'
)
$ErrorActionPreference = 'Stop'
$runner = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
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
$configPath = (Resolve-Path "$TestAssembly.config").Path
$originalConfig = [System.IO.File]::ReadAllBytes($configPath)
try {
    [xml]$config = [System.IO.File]::ReadAllText($configPath)
    ($config.configuration.appSettings.add | Where-Object key -eq 'TenantGUID').value = '00000000-0000-0000-0000-000000000000'
    ($config.configuration.appSettings.add | Where-Object key -eq 'ClientID').value = '00000000-0000-0000-0000-000000000000'
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
        ($config.configuration.connectionStrings.add | Where-Object name -eq 'SPOInsightsEntities').connectionString = "Data Source=(localdb)\$localDbInstance;Initial Catalog=$env:BACKFILL_SYNTHETIC_SQL_CATALOG;Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True;App=EntityFramework"
        Write-Host "Dedicated synthetic instance (stopped on exit, retained): $localDbInstance"
        Write-Host "New synthetic catalog (retained, never reused/dropped): $env:BACKFILL_SYNTHETIC_SQL_CATALOG"
    }
    $config.Save($configPath)
    $filter = "FullyQualifiedName~CopilotAuditBackfillTests.$test"
    if ($RegressionOnly) {
        $filter = 'FullyQualifiedName~CopilotAuditBackfillTests&TestCategory!=SqlIntegration&TestCategory!=SyntheticScale&TestCategory!=SyntheticSqlScale'
        $label = 'regression'
    }
    if ($SqlRegressionOnly) {
        $filter = 'FullyQualifiedName~CopilotAuditBackfillTests&TestCategory=SqlIntegration'
        $label = 'sql-regression'
    }
    & $runner $TestAssembly "/TestCaseFilter:$filter" "/ResultsDirectory:$ResultsDirectory" "/Logger:trx;LogFileName=backfill-$label-scale.trx"
    if ($LASTEXITCODE -ne 0) { throw "Synthetic evidence failed: exit $LASTEXITCODE" }
}
finally {
    $env:BACKFILL_SYNTHETIC_SCALE = $old
    $env:BACKFILL_SYNTHETIC_SQL_SCALE = $oldSql
    $env:BACKFILL_SYNTHETIC_SQL_DAYS = $oldDays
    $env:BACKFILL_SYNTHETIC_SQL_CATALOG = $oldCatalog
    $env:BACKFILL_SYNTHETIC_SQL_INSTANCE = $oldInstance
    [System.IO.File]::WriteAllBytes($configPath, $originalConfig)
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
