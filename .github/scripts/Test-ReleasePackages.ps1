[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required = @{
    'AITrackerInstaller.zip' = @('AITracker/aitracker.js', 'AITracker/spoinsights-modern-ui-aitracker.sppkg')
    'AppInsightsImporter.zip' = @('AppInsightsImporter/SPOInsights.WebJob.AppInsightsImporter.exe', 'AppInsightsImporter/settings.job')
    'Office365ActivityImporter.zip' = @('Office365ActivityImporter/SPOInsights.WebJob.Office365ActivityImport.exe', 'Office365ActivityImporter/settings.job')
    'ControlPanelApp.zip' = @('ControlPanelApp/AnalyticsInstaller.exe')
    'Website.zip' = @('Web/bin/Web.AnalyticsWeb.dll', 'Web/Scripts/portal/build/index.html')
}
foreach ($name in $required.Keys) {
    $file = Join-Path $Path $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release package: $name." }
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        foreach ($entryName in $required[$name]) {
            $entries = @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $entryName })
            if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) { throw "$name is missing a non-empty $entryName." }
            if ($entryName.EndsWith('/settings.job')) {
                $reader = [IO.StreamReader]::new($entries[0].Open())
                try { $settings = $reader.ReadToEnd() | ConvertFrom-Json }
                finally { $reader.Dispose() }
                if ($settings.is_singleton -ne $true -or $settings.stopping_wait_time -ne 30) {
                    throw "$name must run as a singleton WebJob with a 30-second stopping wait."
                }
            }
        }
    }
    finally { $zip.Dispose() }
}
Write-Output 'All five release packages contain their required runtime files.'
