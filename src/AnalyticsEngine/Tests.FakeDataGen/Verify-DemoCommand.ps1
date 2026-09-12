param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exe = Join-Path $PSScriptRoot "bin\$Configuration\Tests.FakeDataGen.exe"
$run = [Guid]::NewGuid().ToString('N')
$database = "ContosoDemo_CI_$run"
$artifacts = ".artifacts-demo-ci-$run"
New-Item -ItemType Directory -Path $artifacts | Out-Null

function Assert-CommandSucceeded {
    if ($LASTEXITCODE -ne 0) { throw "Demo command failed with exit code $LASTEXITCODE." }
}

try {
    & $exe demo --help
    Assert-CommandSucceeded
    # Deliberately the smallest shape the parser accepts (--users 1, --days 31). This is a smoke test of
    # the shipped EXE and its exit codes, not a scale test: measured locally, essentially all of the
    # wall-clock is the one-off DatabaseUpgrader schema apply on the new LocalDB database (~7s), which no
    # option can avoid, while dropping 40 users/35 days to the minimum removed ~3s of pure row generation
    # for no loss of coverage. Generating more only makes CI slower. Scale is covered separately by the
    # opt-in RUN_DEMO_SCALE benchmark, and generation against the real schema by DemoGeneratorSqlTests.
    # --as-of stays fixed so the run is reproducible; 31 days still spans three complete profiling weeks,
    # so the CompletedProfileWeeks assertion below keeps its meaning.
    & $exe demo --preview --users 1 --days 31 --as-of 2026-09-01 --output "$artifacts\preview.json"
    Assert-CommandSucceeded
    $preview = Get-Content "$artifacts\preview.json" -Raw | ConvertFrom-Json
    if ($preview.Status -ne 'Preview' -or $preview.TotalRows -le 0) { throw 'Preview did not produce a source-row summary.' }

    $arguments = @('demo', '--database', $database, '--users', '1', '--days', '31', '--as-of', '2026-09-01')
    & $exe @arguments --output "$artifacts\generated.json"
    Assert-CommandSucceeded
    $generated = Get-Content "$artifacts\generated.json" -Raw | ConvertFrom-Json
    if ($generated.Status -ne 'Complete' -or $generated.CompletedProfileWeeks -le 0) {
        throw 'One-command generation did not complete its database and Power BI profiles.'
    }
    & $exe @arguments --output "$artifacts\repeat.json"
    Assert-CommandSucceeded
    $repeat = Get-Content "$artifacts\repeat.json" -Raw | ConvertFrom-Json
    if ($repeat.Status -ne 'AlreadyComplete' -or $repeat.TotalRows -ne 0) { throw 'Identical rerun was not a read-only no-op.' }

    & $exe @arguments --seed 43
    if ($LASTEXITCODE -eq 0) { throw 'A changed generation was incorrectly accepted for an existing target.' }
    Write-Output 'Demo CLI smoke passed: help, preview, new target, profiles, identical rerun and changed-input refusal.'
}
finally {
    # Only clean up this run's unpredictable name, and only with the generator's ownership marker.
    $master = [System.Data.SqlClient.SqlConnection]::new('Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=True;Pooling=False')
    try {
        $master.Open()
        $command = $master.CreateCommand()
        $command.CommandText = 'SELECT DB_ID(@name);'
        [void]$command.Parameters.AddWithValue('@name', $database)
        $exists = $command.ExecuteScalar() -isnot [DBNull]
        $command.Dispose()
        if ($exists) {
            $target = [System.Data.SqlClient.SqlConnection]::new("Server=(localdb)\MSSQLLocalDB;Database=$database;Integrated Security=True;Pooling=False")
            try {
                $target.Open()
                $command = $target.CreateCommand()
                $command.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE class=0 AND name=N'M365AnalyticsSyntheticDemo' AND CONVERT(nvarchar(100),value)=@format;"
                [void]$command.Parameters.AddWithValue('@format', $preview.FormatVersion)
                $owned = $command.ExecuteScalar() -eq 1
                $command.Dispose()
            }
            finally { $target.Dispose() }
            if (-not $owned) { throw 'Refusing cleanup of a database without the exact synthetic ownership marker.' }
            $command = $master.CreateCommand()
            $command.CommandText = "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database];"
            [void]$command.ExecuteNonQuery()
            $command.Dispose()
        }
    }
    finally {
        $master.Dispose()
        Remove-Item -LiteralPath $artifacts -Recurse -Force
    }
}
exit 0
