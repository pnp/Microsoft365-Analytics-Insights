<#
.SYNOPSIS
    Prunes a release package's build output, checks it for dependencies it must never carry, and zips it.

.DESCRIPTION
    ci.yml (releases) and pr.yml (pull request builds) call this in place of a bare Compress-Archive,
    so a package is shaped the same way in both.

    It removes from the package's bin folder:
      * satellite resource assemblies for languages the product does not ship. The portal ships in
        English and Spanish, English resources are built into each assembly, so only the
        -KeepCultures folders stay. Today these are all Microsoft.Data.SqlClient's, in 13 languages.
      * .pdb files for assemblies this repository does not build (third-party symbols such as
        Microsoft.ApplicationInsights.pdb). Our own symbols stay, so our stack traces keep line numbers.
      * Microsoft.Data.SqlClient's ARM64 native SNI library, from the packages that run on App
        Service, which only offers x86 and x64 Windows workers. The installer keeps it: it runs on the
        admin's own PC, which may be ARM64.

    It then fails if a package contains a dependency it must never carry - each one was once dragged
    into packages that never use it by a project reference - or is missing its entry point.

    Finally it zips the folder exactly as Compress-Archive always has (the folder itself is the zip's
    top-level entry) and, on GitHub Actions, writes the package size to the job summary.

.EXAMPLE
    ./.github/scripts/Build-ReleasePackage.ps1 -Path $env:RUNNER_TEMP\AppInsightsImporter -Package AppInsightsImporter -DestinationPath $env:RUNNER_TEMP\zips\AppInsightsImporter.zip
#>
[CmdletBinding()]
param(
    # The folder that becomes the zip. For the website this is the published site; its assemblies are in bin.
    [Parameter(Mandatory)]
    [string] $Path,

    [Parameter(Mandatory)]
    [ValidateSet('AppInsightsImporter', 'Office365ActivityImporter', 'Website', 'ControlPanelApp')]
    [string] $Package,

    [Parameter(Mandatory)]
    [string] $DestinationPath,

    [string[]] $KeepCultures = @('es'),

    # Where the solution's projects are, to tell our own .pdb files from third-party ones.
    [string] $SourceRoot = (Join-Path $PSScriptRoot '..\..\src\AnalyticsEngine')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$binPath = if ($Package -eq 'Website') { Join-Path $Path 'bin' } else { $Path }
if (-not (Test-Path -LiteralPath $binPath -PathType Container)) {
    throw "Package folder '$binPath' does not exist."
}

$entryPoints = @{
    AppInsightsImporter       = @('SPOInsights.WebJob.AppInsightsImporter.exe')
    Office365ActivityImporter = @('SPOInsights.WebJob.Office365ActivityImport.exe')
    Website                   = @('Web.AnalyticsWeb.dll')
    ControlPanelApp           = @('AnalyticsInstaller.exe')
}

$neverShipped = @(
    @{ Pattern = 'Microsoft.Azure.Cosmos*.dll'; Reason = 'the Cosmos DB SDK is only used by the maintainer-side telemetry service in src/TelemetryService' }
)
$installerOnly = @(
    @{ Pattern = 'BouncyCastle.Cryptography.dll'; Reason = 'only the installer encrypts configuration secrets (StringCipher)' }
    @{ Pattern = 'Azure.ResourceManager*.dll'; Reason = 'only the installer deploys Azure resources' }
    @{ Pattern = 'App.ControlPanel.Engine.dll'; Reason = 'the installer engine is only used by the installer' }
    @{ Pattern = 'CloudInstallEngine.dll'; Reason = 'the installer engine is only used by the installer' }
)
$notInWebsite = @(
    @{ Pattern = 'Microsoft.Graph.dll'; Reason = 'the web app reads the calls webhook subscription over the Graph REST API, not the Graph SDK' }
    @{ Pattern = 'WebJob.*.Engine.dll'; Reason = 'the web app does not reference the importer engines' }
)
$forbidden = switch ($Package) {
    'ControlPanelApp' { $neverShipped }
    'Website' { $neverShipped + $installerOnly + $notInWebsite }
    default { $neverShipped + $installerOnly }
}

$removed = New-Object System.Collections.Generic.List[object]
function Remove-PackageFile([System.IO.FileInfo] $File, [string] $Reason) {
    $removed.Add([pscustomobject]@{ Name = $File.FullName.Substring($binPath.Length).TrimStart('\', '/'); Bytes = $File.Length; Reason = $Reason })
    Remove-Item -LiteralPath $File.FullName -Force
}

# 1. Satellite resource assemblies for languages the product does not ship.
$cultures = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($culture in [Globalization.CultureInfo]::GetCultures([Globalization.CultureTypes]::AllCultures)) {
    if ($culture.Name) { [void]$cultures.Add($culture.Name) }
}
foreach ($folder in Get-ChildItem -LiteralPath $binPath -Directory) {
    if (-not $cultures.Contains($folder.Name) -or $KeepCultures -contains $folder.Name) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $folder.FullName -Filter '*.resources.dll' -File) {
        Remove-PackageFile $file 'satellite resources for a language the product does not ship'
    }
    if (-not (Get-ChildItem -LiteralPath $folder.FullName -Force)) {
        Remove-Item -LiteralPath $folder.FullName -Force
    }
}

# 2. Debug symbols for assemblies this repository does not build.
$ownAssemblies = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$projects = Get-ChildItem -LiteralPath $SourceRoot -Filter '*.csproj' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
foreach ($project in $projects) {
    $assemblyName = ([xml](Get-Content -LiteralPath $project.FullName -Raw)).SelectSingleNode("//*[local-name()='AssemblyName']")
    [void]$ownAssemblies.Add($(if ($null -ne $assemblyName) { $assemblyName.InnerText.Trim() } else { $project.BaseName }))
}
if ($ownAssemblies.Count -eq 0) {
    throw "Found no projects under '$SourceRoot', so own and third-party symbols can't be told apart."
}
foreach ($file in Get-ChildItem -LiteralPath $binPath -Filter '*.pdb' -File) {
    if (-not $ownAssemblies.Contains($file.BaseName)) {
        Remove-PackageFile $file 'third-party debug symbols'
    }
}

# 3. ARM64 native code in packages that only ever run on App Service.
if ($Package -ne 'ControlPanelApp') {
    foreach ($file in Get-ChildItem -LiteralPath $binPath -Filter 'Microsoft.Data.SqlClient.SNI.arm64.*' -File) {
        Remove-PackageFile $file 'ARM64 native code; App Service has no ARM64 Windows workers'
    }
}

foreach ($group in $removed | Group-Object Reason) {
    Write-Host ("Pruned {0} file(s), {1:N1} MB: {2}" -f $group.Count, (($group.Group | Measure-Object Bytes -Sum).Sum / 1MB), $group.Name)
    foreach ($item in $group.Group) { Write-Host "  - $($item.Name)" }
}

# Content check.
$problems = New-Object System.Collections.Generic.List[string]
foreach ($rule in $forbidden) {
    foreach ($file in Get-ChildItem -LiteralPath $binPath -Filter $rule.Pattern -File) {
        $problems.Add("$($file.Name) must not ship in the $Package package: $($rule.Reason). Check which project reference brought it in.")
    }
}
foreach ($entryPoint in $entryPoints[$Package]) {
    if (-not (Test-Path -LiteralPath (Join-Path $binPath $entryPoint) -PathType Leaf)) {
        $problems.Add("The $Package package is missing its entry point $entryPoint.")
    }
}
if ($problems.Count -gt 0) {
    foreach ($problem in $problems) { Write-Host "::error::$problem" }
    throw "The $Package package failed its content check with $($problems.Count) problem(s)."
}

Compress-Archive -Force -Path $Path -DestinationPath $DestinationPath

$zip = Get-Item -LiteralPath $DestinationPath
$fileCount = @(Get-ChildItem -LiteralPath $Path -Recurse -File).Count
$prunedMB = (($removed | Measure-Object Bytes -Sum).Sum) / 1MB
$summary = "| {0} | {1:N1} MB | {2} | {3} file(s), {4:N1} MB uncompressed |" -f $zip.Name, ($zip.Length / 1MB), $fileCount, $removed.Count, $prunedMB
Write-Host "Packaged $($zip.Name): $('{0:N1}' -f ($zip.Length / 1MB)) MB, $fileCount file(s)."

if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value @(
        '| Package | Size | Files | Pruned |'
        '|---|---|---|---|'
        $summary
    )
}
