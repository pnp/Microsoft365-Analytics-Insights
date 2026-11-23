$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$scratchParent = Join-Path $repoRoot '_scratch'
New-Item -ItemType Directory -Force -Path $scratchParent | Out-Null
$scratch = Join-Path $scratchParent ('usage-report-coverage-benchmark-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $scratch) { throw 'Refusing to reuse an existing benchmark scratch directory.' }
New-Item -ItemType Directory -Path $scratch | Out-Null

$azuriteProcess = $null
$previousOptIn = $env:RUN_USAGE_REPORT_COVERAGE_BENCHMARK
$previousPort = $env:USAGE_REPORT_COVERAGE_AZURITE_PORT

try {
    $npmRoot = Join-Path $scratch 'npm'
    $dataDirectory = Join-Path $scratch 'data'
    New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
    npm install --prefix $npmRoot --no-save --package-lock=false --no-audit --no-fund `
        azurite@3.38.0 --registry=https://registry.npmjs.org/
    if ($LASTEXITCODE -ne 0) { throw 'Private Azurite installation failed.' }

    $azuriteEntryPoint = Join-Path $npmRoot 'node_modules\azurite\dist\src\table\main.js'
    if (-not (Test-Path -LiteralPath $azuriteEntryPoint)) { throw 'The pinned Azurite Table entry point was not installed.' }
    $node = (Get-Command node.exe -ErrorAction Stop).Source

    $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $port = $portProbe.LocalEndpoint.Port
    $portProbe.Stop()

    $stdout = Join-Path $scratch 'azurite.stdout.log'
    $stderr = Join-Path $scratch 'azurite.stderr.log'
    $arguments = '"{0}" --silent --disableTelemetry --location "{1}" --tableHost 127.0.0.1 --tablePort {2}' -f `
        $azuriteEntryPoint, $dataDirectory, $port
    $azuriteProcess = Start-Process -FilePath $node -ArgumentList $arguments -WorkingDirectory $scratch `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $deadline = (Get-Date).AddSeconds(60)
    $listening = $false
    while (-not $listening) {
        if ($azuriteProcess.HasExited) {
            $details = if (Test-Path -LiteralPath $stderr) { Get-Content -Raw $stderr } else { '' }
            throw "Task-local Azurite exited before listening. $details"
        }

        $probe = [System.Net.Sockets.TcpClient]::new()
        try {
            $pending = $probe.BeginConnect('127.0.0.1', $port, $null, $null)
            if ($pending.AsyncWaitHandle.WaitOne(250)) {
                $probe.EndConnect($pending)
                $listening = $true
            }
        }
        catch { }
        finally { $probe.Dispose() }

        if (-not $listening) {
            if ((Get-Date) -ge $deadline) { throw "Task-local Azurite did not bind 127.0.0.1:$port." }
            Start-Sleep -Milliseconds 250
        }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio vswhere.exe was not found.' }
    $installations = & $vswhere -all -products '*' -property installationPath
    $tooling = $null
    foreach ($installation in $installations) {
        $msbuildCandidate = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
        $webTasksPath = Join-Path $installation 'MSBuild\Microsoft\VisualStudio'
        $vstestCandidate = Join-Path $installation 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
        if (-not (Test-Path -LiteralPath $vstestCandidate)) {
            $vstestCandidate = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe'
        }
        $webTasks = if (Test-Path -LiteralPath $webTasksPath) {
            Get-ChildItem -Path $webTasksPath -Filter Microsoft.Web.Publishing.Tasks.dll -File -Recurse -ErrorAction SilentlyContinue |
                Select-Object -First 1
        }
        if ((Test-Path -LiteralPath $msbuildCandidate) -and $webTasks -and (Test-Path -LiteralPath $vstestCandidate)) {
            $tooling = [pscustomobject]@{ MSBuild = $msbuildCandidate; VSTest = $vstestCandidate }
            break
        }
    }
    if (-not $tooling) { throw 'No Visual Studio instance with MSBuild, Web publishing tasks, and vstest.console.exe was found.' }

    $env:RUN_USAGE_REPORT_COVERAGE_BENCHMARK = 'true'
    $env:USAGE_REPORT_COVERAGE_AZURITE_PORT = [string]$port
    $project = Join-Path $PSScriptRoot 'Tests.UnitTests.csproj'
    & $tooling.MSBuild $project /restore /p:Configuration=Release /p:Platform=AnyCPU /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw 'The unit-test project did not build.' }

    $assembly = Join-Path $PSScriptRoot 'bin\Release\Tests.UnitTests.dll'
    & $tooling.VSTest $assembly `
        '/TestCaseFilter:FullyQualifiedName~UsageReportCoverageAzuriteBenchmarkTests' `
        '/Logger:console;Verbosity=detailed' /Platform:x64
    if ($LASTEXITCODE -ne 0) { throw 'The Azurite coverage benchmark failed.' }
}
finally {
    if ($null -eq $previousOptIn) { Remove-Item Env:RUN_USAGE_REPORT_COVERAGE_BENCHMARK -ErrorAction SilentlyContinue }
    else { $env:RUN_USAGE_REPORT_COVERAGE_BENCHMARK = $previousOptIn }
    if ($null -eq $previousPort) { Remove-Item Env:USAGE_REPORT_COVERAGE_AZURITE_PORT -ErrorAction SilentlyContinue }
    else { $env:USAGE_REPORT_COVERAGE_AZURITE_PORT = $previousPort }

    if ($azuriteProcess -and -not $azuriteProcess.HasExited) {
        Stop-Process -Id $azuriteProcess.Id -Force
        Wait-Process -Id $azuriteProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    if ((Test-Path -LiteralPath $scratch) -and
        ([IO.Path]::GetFullPath($scratch).StartsWith([IO.Path]::GetFullPath($scratchParent), [StringComparison]::OrdinalIgnoreCase))) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
