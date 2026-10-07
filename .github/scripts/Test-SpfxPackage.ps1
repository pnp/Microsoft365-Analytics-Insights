<#
.SYNOPSIS
    Fails if the committed SPFx package isn't the build of the extension's current source version.

.DESCRIPTION
    CI never builds the SharePoint Framework extension (src/SPO/ModernPagesAITrackerExtension). The release zips
    the package committed at src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg into AITrackerInstaller.zip,
    and the installer deploys it to the tenant app catalog as it is. So a change to the extension that wasn't
    rebuilt and committed would ship the old package, with nothing else to notice.

    This checks that all of these carry the same version:
      * solution.version in config/package-solution.json: the version the source says it is;
      * AITRACKER_MODERN_VERSION in AiTrackerModernApplicationCustomizer.ts, which the extension logs to the
        browser console;
      * AppManifest.xml in the committed package;
      * the extension's bundle in the committed package, which has AITRACKER_MODERN_VERSION compiled into it. That
        shows the package was built from the source with that version, rather than just labelled with it.

    It relies on the version being bumped with every change to the extension (see
    src/SPO/.github/copilot-instructions.md): a change made without a new version isn't detected.

.EXAMPLE
    ./.github/scripts/Test-SpfxPackage.ps1
#>
[CmdletBinding()]
param(
    # The root of the repository, or of a copy laid out the same way. Defaults to this repository.
    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'

# Not a parameter default: Windows PowerShell leaves $PSScriptRoot empty there when run with -File
if (-not $RepoRoot) {
    $RepoRoot = Join-Path $PSScriptRoot '../..'
}

$extensionRoot = Join-Path $RepoRoot 'src/SPO/ModernPagesAITrackerExtension'
$solutionFile = Join-Path $extensionRoot 'config/package-solution.json'
$customizerFile = Join-Path $extensionRoot 'src/extensions/aiTrackerModern/AiTrackerModernApplicationCustomizer.ts'
$packageFile = Join-Path $RepoRoot 'src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg'

function Read-ZipEntryText($entry) {
    $reader = New-Object System.IO.StreamReader($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

$version = (Get-Content -LiteralPath $solutionFile -Raw | ConvertFrom-Json).solution.version
if (-not $version) {
    throw "No solution.version in $solutionFile."
}

$failures = New-Object System.Collections.Generic.List[string]

$constant = [regex]::Match((Get-Content -LiteralPath $customizerFile -Raw), 'AITRACKER_MODERN_VERSION\s*(?::\s*string\s*)?=\s*["'']([^"'']+)["'']')
if (-not $constant.Success) {
    $failures.Add("AITRACKER_MODERN_VERSION wasn't found in AiTrackerModernApplicationCustomizer.ts.")
}
elseif ($constant.Groups[1].Value -ne $version) {
    $failures.Add("AITRACKER_MODERN_VERSION is $($constant.Groups[1].Value), but package-solution.json says $version. Keep the two identical.")
}

if (-not (Test-Path -LiteralPath $packageFile)) {
    $failures.Add("There is no committed package at src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg.")
}
else {
    # ZipArchive over a stream rather than ZipFile, so this runs the same in Windows PowerShell and in pwsh on Linux
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $packageFile).ProviderPath)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Read)
        try {
            $manifestEntry = $archive.GetEntry('AppManifest.xml')
            if ($null -eq $manifestEntry) {
                $failures.Add("The committed package has no AppManifest.xml.")
            }
            else {
                [xml] $manifest = Read-ZipEntryText $manifestEntry
                $packageVersion = $manifest.App.Version
                if ($packageVersion -ne $version) {
                    $failures.Add("The committed package is version $packageVersion, but package-solution.json says $version.")
                }
            }

            $bundles = @($archive.Entries | Where-Object { $_.FullName -like 'ClientSideAssets/*.js' })
            if ($bundles.Count -eq 0) {
                $failures.Add("The committed package has no extension bundle (ClientSideAssets/*.js).")
            }
            else {
                $versionLiteral = '["'']' + [regex]::Escape($version) + '["'']'
                $builtWithVersion = $false
                foreach ($bundle in $bundles) {
                    if ([regex]::IsMatch((Read-ZipEntryText $bundle), $versionLiteral)) {
                        $builtWithVersion = $true
                        break
                    }
                }
                if (-not $builtWithVersion) {
                    $failures.Add("The extension bundle in the committed package wasn't built with version ${version}: AITRACKER_MODERN_VERSION $version isn't in it.")
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host "::error title=SPFx package doesn't match its source::$failure"
        }
        else {
            Write-Host "ERROR: $failure"
        }
    }
    throw ("The committed SPFx package doesn't match the extension's source, and CI doesn't build the extension. " +
        "Build it with Node 22 (npm ci, then npm run build, in src/SPO/ModernPagesAITrackerExtension), copy " +
        "sharepoint/solution/spoinsights-modern-ui-aitracker.sppkg to src/SPO/AITracker/, and commit it.")
}

Write-Host "The committed SPFx package, its bundle, AITRACKER_MODERN_VERSION and package-solution.json are all version $version."
