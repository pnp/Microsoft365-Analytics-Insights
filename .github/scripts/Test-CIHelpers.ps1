$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path ([IO.Path]::GetTempPath()) ('contoso-ci-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Assert-Fails([scriptblock]$Action, [string]$Expected) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    if (-not $failure -or $failure.Exception.Message -notlike "*$Expected*") {
        throw "Expected a failure containing '$Expected'."
    }
}
function New-TestZip([string]$Name, [hashtable]$Entries) {
    $file = Join-Path $root $Name
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    $zip = [IO.Compression.ZipFile]::Open($file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($Entries[$name]) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}
try {
    Assert-Fails { & "$PSScriptRoot\Test-ReleasePackages.ps1" -Path $root } 'Missing release package'
    New-TestZip 'AITrackerInstaller.zip' @{
        'AITracker/aitracker.js' = 'Synthetic tracker'
        'AITracker/spoinsights-modern-ui-aitracker.sppkg' = 'Synthetic extension'
    }
    New-TestZip 'AppInsightsImporter.zip' @{
        'AppInsightsImporter/SPOInsights.WebJob.AppInsightsImporter.exe' = 'Synthetic importer'
        'AppInsightsImporter/settings.job' = '{"is_singleton":true,"stopping_wait_time":30}'
    }
    New-TestZip 'Office365ActivityImporter.zip' @{
        'Office365ActivityImporter/SPOInsights.WebJob.Office365ActivityImport.exe' = 'Synthetic importer'
        'Office365ActivityImporter/settings.job' = '{"is_singleton":true,"stopping_wait_time":30}'
    }
    New-TestZip 'ControlPanelApp.zip' @{ 'ControlPanelApp/AnalyticsInstaller.exe' = 'Synthetic installer' }
    New-TestZip 'Website.zip' @{
        'Web/bin/Web.AnalyticsWeb.dll' = 'Synthetic website'
        'Web/Scripts/portal/build/index.html' = 'Synthetic portal'
    }
    & "$PSScriptRoot\Test-ReleasePackages.ps1" -Path $root | Out-Null
    New-TestZip 'AppInsightsImporter.zip' @{
        'AppInsightsImporter/SPOInsights.WebJob.AppInsightsImporter.exe' = 'Synthetic importer'
        'AppInsightsImporter/settings.job' = '{"is_singleton":false,"stopping_wait_time":30}'
    }
    Assert-Fails { & "$PSScriptRoot\Test-ReleasePackages.ps1" -Path $root } 'singleton'
    $results = Join-Path $root 'results'
    New-Item -ItemType Directory -Path $results | Out-Null
    $summary = Join-Path $root 'summary.txt'
    Assert-Fails { & "$PSScriptRoot\Write-TestSummary.ps1" -ResultsDirectory $results -Name example -SummaryPath $summary } 'No TRX'
    [IO.File]::WriteAllText((Join-Path $results 'example.trx'), @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Contoso|slow" duration="00:00:02.500" outcome="Passed" />
    <UnitTestResult testName="Contoso skipped" outcome="NotExecuted" />
  </Results>
</TestRun>
'@)
    & "$PSScriptRoot\Write-TestSummary.ps1" -ResultsDirectory $results -Name example -SummaryPath $summary
    $text = [IO.File]::ReadAllText($summary)
    if (-not $text.Contains('Contoso\|slow | 2.50') -or -not $text.Contains('| NotExecuted | 1 |')) {
        throw 'TRX summary did not report durations, skipped tests and escaped names.'
    }
    Write-Output 'Package and TRX helper contracts passed.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
