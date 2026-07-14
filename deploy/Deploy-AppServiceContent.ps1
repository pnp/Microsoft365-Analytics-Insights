#Requires -Version 5.1
<#
.SYNOPSIS
    Deploys the Microsoft 365 Analytics Insights web-jobs and website content to an
    Azure App Service, without needing the WinForms installer (AnalyticsInstaller.exe).

.DESCRIPTION
    This script replicates the *app-service content install* step of the installer
    (App.ControlPanel.Engine InstallAppServiceContentsTask) for customers where the
    installer is blocked by policy / SmartScreen / AppLocker.

    It does NOT provision Azure resources, configure app settings, or initialise the
    database - it only pushes the compiled solution content:

        Office365ActivityImporter.zip -> /site/wwwroot/app_data/jobs/continuous/Office365ActivityImporter/
        AppInsightsImporter.zip       -> /site/wwwroot/app_data/jobs/continuous/AppInsightsImporter/
        Website.zip                   -> /site/wwwroot/

    With -RunDbUpgrade it also runs the three-step database upgrade (EF migrations +
    custom SQL scripts + org-URL seeding) that AnalyticsInstaller.exe normally handles.
    The upgrade runs entirely inside the App Service as a triggered web-job, so no local
    execution of the installer is needed. The web-job is built on the fly from the
    ControlPanelApp.zip release asset (no new binary to deploy) and removed automatically
    after the run reports success or failure.

    Sources are either:
      * downloaded from this repo's GitHub Releases (like LatestStableSoftwarePackageDownloadTask), or
      * taken from a local folder of pre-downloaded zips (-SourceFolder).

    The push is performed over HTTPS against the Kudu/SCM 'zip' API (PUT /api/zip/{path}),
    which extracts a zip into a target folder, overwriting files but not wiping siblings -
    matching the installer's per-file FTP overwrite semantics. Pure HTTPS means it works
    where the installer's FTPS upload (or the exe itself) is blocked.

    Authentication (in Auto mode, first available wins):
      1. -DeployUserName / -DeployPassword          (App Service publishing credentials, Basic auth)
      2. -PublishProfilePath <*.PublishSettings>     (download from the portal, parsed for creds + SCM host)
      3. -AccessToken <token>                        (AAD bearer token for https://management.azure.com)
      4. Azure CLI (az) or Az PowerShell             (auto-fetch publish profile + AAD token; needs -ResourceGroup)

    An AAD bearer token (options 3/4) is preferred when available because it still works
    when SCM basic authentication has been disabled by tenant policy.

.PARAMETER WebAppName
    Name of the target App Service (web app). Required.

.PARAMETER ResourceGroup
    Resource group of the web app. Required only for the Azure CLI / Az PowerShell auth fallback.

.PARAMETER SourceFolder
    Folder containing pre-downloaded release zips. If supplied, GitHub is not contacted.

.PARAMETER ReleaseTag
    Deploy a specific GitHub release tag instead of the latest.

.PARAMETER IncludePrerelease
    Consider pre-release builds (e.g. 'dev' branch builds) when picking the latest release.

.PARAMETER GitHubToken
    Optional GitHub token (raises the anonymous rate limit / allows private release assets).

.PARAMETER SkipWebsite
    Do not deploy the website content.

.PARAMETER SkipWebJobs
    Do not deploy the web-jobs.

.PARAMETER RestartWebJobs
    Explicitly stop each continuous web-job before its upload and start it afterwards.
    Off by default: App Service shadow-copies running continuous web-jobs, so Kudu picks
    up new binaries and auto-restarts the job on content change (this also matches the
    installer, which never stops/starts). Some locked-down configurations return HTTP 403
    for the stop/start API, which is why this is opt-in.

.PARAMETER DownloadOnly
    Acquire and normalise the packages but do not deploy anything.

.PARAMETER DiagnoseOnly
    Do not download or deploy anything; just run the reachability/DNS/HTTP diagnostic
    (see -VerifySiteReachable) against the App and SCM hostnames and exit. Needs no
    credentials - only -WebAppName (and optionally -ScmHostName or -PublishProfilePath
    to pin the SCM host). Handy for triaging a 403 from a VM on the VNet.

.PARAMETER RunDbUpgrade
    After deploying content, run the database upgrade (EF migrations + custom SQL scripts
    + org-URL seeding) inside the App Service as a triggered web-job.

    The web-job is assembled on the fly from ControlPanelApp.zip (the signed
    AnalyticsInstaller.exe release asset) plus a PowerShell run.ps1 wrapper, deployed
    to the App Service triggered web-jobs folder, triggered via the Kudu API, and polled
    until it completes. The full job log is echoed to the console and the script exits
    non-zero if the upgrade fails.

    Requires the App Service 'SPOInsightsEntities' connection string to be already
    configured (Portal -> Configuration -> Connection strings). The connection string
    identity must have DDL rights (ALTER TABLE, CREATE TABLE, etc.) as migrations may
    run schema changes.

    This switch can be combined with -SkipWebsite/-SkipWebJobs to run only the DB upgrade
    without deploying web-job or website content (e.g. when binaries were already deployed
    and only a schema upgrade is needed).

.PARAMETER DbUpgradeTimeoutMin
    Maximum minutes to wait for the database upgrade web-job to complete. Default 1440 (24 hours).
    SQL migrations on large databases can run for many hours; lower this only if you are confident
    migrations complete quickly in your environment.

.PARAMETER PublishProfilePath
    Path to a downloaded App Service publish profile (*.PublishSettings XML).

.PARAMETER DeployUserName
.PARAMETER DeployPassword
    Explicit App Service (Kudu) publishing credentials for Basic auth.

.PARAMETER AccessToken
    AAD access token for https://management.azure.com to use as a Kudu bearer token.

.PARAMETER ScmHostName
    Override the SCM/Kudu host (e.g. contoso-app.scm.azurewebsites.net). Normally derived
    from the publish profile or from '<WebAppName>.scm.azurewebsites.net'.

.PARAMETER AuthMode
    Auto (default), Basic, or Bearer.

.PARAMETER WorkFolder
    Working directory for downloads / normalised zips. Defaults to a unique temp folder.

.PARAMETER KeepWorkFolder
    Do not delete the working directory when finished (useful for debugging).

.PARAMETER TimeoutSec
    Per-request timeout for uploads. Default 600.

.PARAMETER RetryCount
    Maximum attempts for transient (5xx / 408 / 429 / network) failures. Default 5.

.PARAMETER VerifySiteReachable
    After deploying, resolve DNS and test TCP 443 for the App (main) and SCM hostnames,
    classify each resolved IP as private/public, and do a best-effort HTTP GET of the App
    site to report the status it returns. Useful on private-networking setups: a PUBLIC
    resolve with public access disabled explains a 403 (hitting the public interface);
    a PRIVATE resolve that still returns 403 points at main-site Access Restrictions or
    app configuration rather than DNS.

.EXAMPLE
    # Download latest stable release and deploy everything, auth via portal publish profile.
    .\Deploy-AppServiceContent.ps1 -WebAppName contoso-analytics -PublishProfilePath .\contoso-analytics.PublishSettings

.EXAMPLE
    # Use az/Az to auto-authenticate (works even if SCM basic auth is disabled).
    .\Deploy-AppServiceContent.ps1 -WebAppName contoso-analytics -ResourceGroup rg-analytics

.EXAMPLE
    # Deploy from pre-downloaded zips, web-jobs only, preview actions first.
    .\Deploy-AppServiceContent.ps1 -WebAppName contoso-analytics -SourceFolder .\release -SkipWebsite -WhatIf

.EXAMPLE
    # Deploy and then diagnose whether the app is reachable privately from this machine.
    .\Deploy-AppServiceContent.ps1 -WebAppName contoso-analytics -PublishProfilePath .\p.PublishSettings -VerifySiteReachable

.EXAMPLE
    # Deploy everything AND run the DB upgrade in one pass.
    .\Deploy-AppServiceContent.ps1 -WebAppName contoso-analytics -PublishProfilePath .\contoso-analytics.PublishSettings -RunDbUpgrade

.EXAMPLE
    # Run ONLY the DB upgrade (skip web-job/website content - binaries already current).
    .\Deploy-AppServiceContent.ps1 -WebAppName contoso-analytics -PublishProfilePath .\contoso-analytics.PublishSettings -SkipWebsite -SkipWebJobs -RunDbUpgrade

.NOTES
    Requires network access to api.github.com (unless -SourceFolder) and to the
    App Service SCM endpoint (https://<app>.scm.azurewebsites.net).
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [string] $WebAppName,

    [string] $ResourceGroup,

    # --- Source selection ---
    [string] $SourceFolder,
    [string] $RepoOwner = 'pnp',
    [string] $RepoName = 'Microsoft365-Analytics-Insights',
    [string] $ReleaseTag,
    [switch] $IncludePrerelease,
    [string] $GitHubToken,

    # --- What to deploy ---
    [switch] $SkipWebsite,
    [switch] $SkipWebJobs,
    [switch] $RestartWebJobs,
    [switch] $RunDbUpgrade,
    [int]    $DbUpgradeTimeoutMin = 1440,
    [switch] $DownloadOnly,
    [switch] $DiagnoseOnly,

    # --- Authentication ---
    [string] $PublishProfilePath,
    [string] $DeployUserName,
    [string] $DeployPassword,
    [string] $AccessToken,
    [string] $ScmHostName,
    [ValidateSet('Auto', 'Basic', 'Bearer')]
    [string] $AuthMode = 'Auto',

    # --- Behaviour ---
    [switch] $VerifySiteReachable,
    [string] $WorkFolder,
    [switch] $KeepWorkFolder,
    [int]    $TimeoutSec = 600,
    [int]    $RetryCount = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # dramatically speeds up Invoke-WebRequest on PS 5.1

# Ensure a modern TLS is negotiated on Windows PowerShell 5.1.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch { }

# .NET zip APIs (present by default on PS 7; needs loading on 5.1).
try { Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue } catch { }
try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue } catch { }

# ------------------------------------------------------------------ logging ---
function Write-Log {
    param([string] $Message, [ConsoleColor] $Color = [ConsoleColor]::Gray)
    Write-Host ('[{0}] {1}' -f (Get-Date).ToString('HH:mm:ss'), $Message) -ForegroundColor $Color
}
function Write-Step    { param([string] $m) Write-Host ''; Write-Host ('=== {0} ===' -f $m) -ForegroundColor Cyan }
function Write-Info    { param([string] $m) Write-Log $m ([ConsoleColor]::Gray) }
function Write-Ok      { param([string] $m) Write-Log $m ([ConsoleColor]::Green) }
function Write-WarnMsg { param([string] $m) Write-Log ("WARN: $m") ([ConsoleColor]::Yellow) }
function Write-ErrMsg  { param([string] $m) Write-Log ("ERROR: $m") ([ConsoleColor]::Red) }

# ------------------------------------------------------- HTTP / retry helpers ---
function Get-HttpStatus {
    param($ErrorRecord)
    try {
        $resp = $ErrorRecord.Exception.Response
        if ($null -ne $resp -and $null -ne $resp.StatusCode) { return [int]$resp.StatusCode }
    } catch { }
    return $null
}

function Get-HttpErrorBody {
    param($ErrorRecord)
    # PowerShell 7 exposes the response body here.
    try {
        if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
            return $ErrorRecord.ErrorDetails.Message
        }
    } catch { }
    # Windows PowerShell 5.1: read the response stream.
    try {
        $resp = $ErrorRecord.Exception.Response
        if ($null -ne $resp) {
            $stream = $resp.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
    } catch { }
    return $null
}

function Get-ExceptionSummary {
    param($ErrorRecord)
    $status = Get-HttpStatus $ErrorRecord
    $msg = $ErrorRecord.Exception.Message
    if ($null -ne $status) { return ('HTTP {0}: {1}' -f $status, $msg) }
    return $msg
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)][scriptblock] $Script,
        [string] $What = 'operation',
        [int] $MaxAttempts = $RetryCount,
        [int] $InitialDelaySec = 3
    )
    $attempt = 0
    while ($true) {
        $attempt++
        try {
            return & $Script
        } catch {
            $status = Get-HttpStatus $_
            $isTransient = ($null -eq $status) -or ($status -ge 500) -or ($status -eq 408) -or ($status -eq 429)
            if ($attempt -ge $MaxAttempts -or -not $isTransient) { throw }
            $delay = [math]::Min(60, [int]($InitialDelaySec * [math]::Pow(2, $attempt - 1)))
            Write-WarnMsg ("$What failed (attempt $attempt/$MaxAttempts): $(Get-ExceptionSummary $_). Retrying in ${delay}s...")
            Start-Sleep -Seconds $delay
        }
    }
}

# ------------------------------------------------------------- component model ---
function Get-Components {
    $all = @(
        [pscustomobject]@{ Name = 'Website';                   ZipFile = 'Website.zip';                   Kind = 'website';   JobName = $null;                        RemotePath = '/site/wwwroot/' }
        [pscustomobject]@{ Name = 'Office365ActivityImporter'; ZipFile = 'Office365ActivityImporter.zip'; Kind = 'webjob';    JobName = 'Office365ActivityImporter';  RemotePath = '/site/wwwroot/app_data/jobs/continuous/Office365ActivityImporter/' }
        [pscustomobject]@{ Name = 'AppInsightsImporter';       ZipFile = 'AppInsightsImporter.zip';       Kind = 'webjob';    JobName = 'AppInsightsImporter';        RemotePath = '/site/wwwroot/app_data/jobs/continuous/AppInsightsImporter/' }
        [pscustomobject]@{ Name = 'ControlPanelApp';           ZipFile = 'ControlPanelApp.zip';           Kind = 'installer'; JobName = $null;                        RemotePath = $null }
    )
    $selected = $all | Where-Object {
        ($_.Kind -eq 'website'   -and -not $SkipWebsite) -or
        ($_.Kind -eq 'webjob'    -and -not $SkipWebJobs) -or
        ($_.Kind -eq 'installer' -and $RunDbUpgrade)
    }
    $deployableContent = @($selected | Where-Object { $_.Kind -ne 'installer' })
    if ($deployableContent.Count -eq 0 -and -not $RunDbUpgrade) {
        throw 'Nothing to deploy: both -SkipWebsite and -SkipWebJobs were specified.'
    }
    if ($deployableContent.Count -eq 0 -and $RunDbUpgrade) {
        # DB-upgrade-only run: still need to acquire the installer package.
    }
    return , @($selected)
}

# --------------------------------------------------------------- GitHub source ---
function Get-GitHubHeaders {
    $h = @{ 'User-Agent' = 'M365AnalyticsInsights-Deploy'; 'Accept' = 'application/vnd.github+json' }
    if ($GitHubToken) { $h['Authorization'] = "Bearer $GitHubToken" }
    return $h
}

function Get-Release {
    $headers = Get-GitHubHeaders
    $base = "https://api.github.com/repos/$RepoOwner/$RepoName"
    if ($ReleaseTag) {
        $url = "$base/releases/tags/$ReleaseTag"
        Write-Info "Fetching release '$ReleaseTag' from $RepoOwner/$RepoName..."
        return Invoke-WithRetry -What 'GitHub release lookup' -Script { Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 60 }
    }
    if ($IncludePrerelease) {
        $url = "$base/releases?per_page=30"
        Write-Info "Fetching latest release (incl. pre-release) from $RepoOwner/$RepoName..."
        $list = Invoke-WithRetry -What 'GitHub releases list' -Script { Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 60 }
        $rel = $list | Where-Object { -not $_.draft } | Select-Object -First 1
        if (-not $rel) { throw "No published releases found for $RepoOwner/$RepoName." }
        return $rel
    }
    $url = "$base/releases/latest"
    Write-Info "Fetching latest stable release from $RepoOwner/$RepoName..."
    return Invoke-WithRetry -What 'GitHub latest release' -Script { Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 60 }
}

function Save-ReleaseAsset {
    param([string] $Url, [string] $Destination)
    $headers = @{ 'User-Agent' = 'M365AnalyticsInsights-Deploy' }
    if ($GitHubToken) { $headers['Authorization'] = "Bearer $GitHubToken" }
    Invoke-WithRetry -What "download $(Split-Path $Destination -Leaf)" -Script {
        Invoke-WebRequest -Uri $Url -Headers $headers -OutFile $Destination -TimeoutSec $TimeoutSec -UseBasicParsing
    }
    Assert-ValidZip -Path $Destination
}

function Assert-ValidZip {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Expected zip not found: $Path" }
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -le 0) { throw "Downloaded zip is empty: $Path" }
    try {
        $z = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try { $null = $z.Entries.Count } finally { $z.Dispose() }
    } catch {
        throw "File is not a valid zip archive: $Path ($($_.Exception.Message))"
    }
}

function Resolve-Sources {
    param([object[]] $Components, [string] $DownloadDir)

    $map = @{}
    if ($SourceFolder) {
        Write-Info "Using local source folder: $SourceFolder"
        if (-not (Test-Path -LiteralPath $SourceFolder)) { throw "Source folder not found: $SourceFolder" }
        foreach ($c in $Components) {
            $path = Join-Path $SourceFolder $c.ZipFile
            if (-not (Test-Path -LiteralPath $path)) {
                throw "Required package '$($c.ZipFile)' not found in $SourceFolder"
            }
            Assert-ValidZip -Path $path
            $map[$c.ZipFile] = $path
            Write-Ok "  Found $($c.ZipFile)"
        }
        return $map
    }

    $release = Get-Release
    $tag = if ($release.PSObject.Properties['tag_name']) { $release.tag_name } else { '(unknown)' }
    $name = if ($release.PSObject.Properties['name']) { $release.name } else { '' }
    Write-Ok "Using release '$tag' $name"

    $assetUrls = @{}
    foreach ($a in @($release.assets)) {
        if ($a.name -and $a.browser_download_url) { $assetUrls[$a.name] = $a.browser_download_url }
    }
    foreach ($c in $Components) {
        if (-not $assetUrls.ContainsKey($c.ZipFile)) {
            throw "Release '$tag' is missing expected asset '$($c.ZipFile)'."
        }
    }
    foreach ($c in $Components) {
        $dest = Join-Path $DownloadDir $c.ZipFile
        Write-Info "  Downloading $($c.ZipFile)..."
        Save-ReleaseAsset -Url $assetUrls[$c.ZipFile] -Destination $dest
        $map[$c.ZipFile] = $dest
        Write-Ok ("  Downloaded $($c.ZipFile) ({0:N1} MB)" -f ((Get-Item -LiteralPath $dest).Length / 1MB))
    }
    return $map
}

# ---------------------------------------------------- zip normalisation (strip) ---
# Release zips wrap their payload in a single top folder (Office365ActivityImporter/,
# AppInsightsImporter/, Web/). The installer descends to that content root before
# uploading (ZipFileTasks.FindContentsRoot). We replicate this as a streaming
# zip-to-zip transform (no disk extraction => no MAX_PATH problems) so the produced
# zip's root is the actual content.
function Get-ZipContentRootPrefix {
    param([System.IO.Compression.ZipArchiveEntry[]] $Entries)
    $prefix = ''
    while ($true) {
        $filesAtLevel = 0
        $dirs = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($e in $Entries) {
            $full = $e.FullName
            if ($prefix -and -not $full.StartsWith($prefix, [System.StringComparison]::Ordinal)) { continue }
            $rem = if ($prefix) { $full.Substring($prefix.Length) } else { $full }
            if ([string]::IsNullOrEmpty($rem)) { continue }
            $slash = $rem.IndexOf('/')
            if ($slash -lt 0) {
                $filesAtLevel++
            } else {
                [void]$dirs.Add($rem.Substring(0, $slash))
            }
        }
        if ($filesAtLevel -eq 0 -and $dirs.Count -eq 1) {
            $only = @($dirs)[0]
            $prefix = "$prefix$only/"
        } else {
            break
        }
    }
    return $prefix
}

function New-NormalizedZip {
    param([string] $SourceZip, [string] $DestZip)

    if (Test-Path -LiteralPath $DestZip) { Remove-Item -LiteralPath $DestZip -Force }

    $src = [System.IO.Compression.ZipFile]::OpenRead($SourceZip)
    try {
        $entries = @($src.Entries)
        $prefix = Get-ZipContentRootPrefix -Entries $entries
        if ($prefix) { Write-Info "  Stripping wrapper folder '$prefix'" }

        $dest = [System.IO.Compression.ZipFile]::Open($DestZip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $fileCount = 0
            foreach ($e in $entries) {
                # Directory entries have an empty Name; they are recreated implicitly from file paths.
                if ([string]::IsNullOrEmpty($e.Name)) { continue }
                $full = $e.FullName
                if ($prefix -and -not $full.StartsWith($prefix, [System.StringComparison]::Ordinal)) { continue }
                $rel = if ($prefix) { $full.Substring($prefix.Length) } else { $full }
                if ([string]::IsNullOrEmpty($rel)) { continue }

                $newEntry = $dest.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $inStream = $e.Open()
                $outStream = $newEntry.Open()
                try { $inStream.CopyTo($outStream) } finally { $outStream.Dispose(); $inStream.Dispose() }
                $fileCount++
            }
            if ($fileCount -eq 0) { throw "Source zip '$SourceZip' contained no files after normalisation." }
            Write-Info "  Normalised $fileCount file(s)"
        } finally { $dest.Dispose() }
    } finally { $src.Dispose() }
    return $DestZip
}

# ------------------------------------------------------------ publish profile ---
function Get-ScmHostFromPublishUrl {
    param([string] $Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    if ($Url -match '://') { return ([Uri]$Url).Host }
    return ($Url -split ':')[0]
}

function ConvertFrom-PublishProfileXml {
    param([string] $Xml)
    [xml] $doc = $Xml
    $profiles = @($doc.publishData.publishProfile)
    $md = $profiles | Where-Object { $_.publishMethod -eq 'MSDeploy' } | Select-Object -First 1
    if (-not $md) { $md = $profiles | Where-Object { $_.publishMethod -eq 'ZipDeploy' } | Select-Object -First 1 }
    if (-not $md) { throw 'Publish profile contains no MSDeploy/ZipDeploy entry.' }
    return [pscustomobject]@{
        ScmHost  = Get-ScmHostFromPublishUrl $md.publishUrl
        UserName = $md.userName
        Password = $md.userPWD
    }
}

function Read-PublishProfile {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Publish profile not found: $Path" }
    return ConvertFrom-PublishProfileXml -Xml (Get-Content -LiteralPath $Path -Raw)
}

function Get-PublishProfileAuto {
    if (-not $ResourceGroup) { return $null }

    if (Get-Command az -ErrorAction SilentlyContinue) {
        Write-Info 'Fetching publish profile via Azure CLI...'
        try {
            $xml = az webapp deployment list-publishing-profiles --name $WebAppName --resource-group $ResourceGroup --xml 2>$null
            if ($LASTEXITCODE -eq 0 -and $xml) { return ConvertFrom-PublishProfileXml -Xml ($xml -join "`n") }
        } catch { Write-WarnMsg "az publish-profile fetch failed: $($_.Exception.Message)" }
    }
    if (Get-Command Get-AzWebAppPublishingProfile -ErrorAction SilentlyContinue) {
        Write-Info 'Fetching publish profile via Az PowerShell...'
        try {
            $tmp = New-TemporaryFile
            try {
                $null = Get-AzWebAppPublishingProfile -Name $WebAppName -ResourceGroupName $ResourceGroup -Format WebDeploy -OutputFile $tmp.FullName
                return Read-PublishProfile $tmp.FullName
            } finally { Remove-Item -LiteralPath $tmp.FullName -Force -ErrorAction SilentlyContinue }
        } catch { Write-WarnMsg "Az publish-profile fetch failed: $($_.Exception.Message)" }
    }
    return $null
}

function Get-ArmAccessTokenAuto {
    if (Get-Command az -ErrorAction SilentlyContinue) {
        try {
            $t = az account get-access-token --resource https://management.azure.com --query accessToken -o tsv 2>$null
            if ($LASTEXITCODE -eq 0 -and $t) { return ($t | Select-Object -First 1).Trim() }
        } catch { }
    }
    if (Get-Command Get-AzAccessToken -ErrorAction SilentlyContinue) {
        try {
            $t = (Get-AzAccessToken -ResourceUrl 'https://management.azure.com' -ErrorAction Stop).Token
            if ($t) {
                if ($t -is [System.Security.SecureString]) {
                    $t = [System.Net.NetworkCredential]::new('', $t).Password
                }
                return $t
            }
        } catch { }
    }
    return $null
}

# ------------------------------------------------------------- Kudu auth model ---
function New-BasicAuthHeader {
    param([string] $User, [string] $Pass)
    $pair = '{0}:{1}' -f $User, $Pass
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes($pair))
    return @{ Authorization = "Basic $b64" }
}

function Resolve-KuduAuth {
    $scm = $ScmHostName
    $basicUser = $null; $basicPass = $null; $bearer = $null

    if ($DeployUserName -and $DeployPassword) {
        Write-Info 'Auth: explicit publishing credentials.'
        $basicUser = $DeployUserName; $basicPass = $DeployPassword
    } elseif ($PublishProfilePath) {
        Write-Info "Auth: publish profile '$PublishProfilePath'."
        $p = Read-PublishProfile $PublishProfilePath
        if (-not $scm) { $scm = $p.ScmHost }
        $basicUser = $p.UserName; $basicPass = $p.Password
    } elseif ($AccessToken) {
        Write-Info 'Auth: supplied AAD access token.'
        $bearer = $AccessToken
    } else {
        $p = Get-PublishProfileAuto
        if ($p) {
            if (-not $scm) { $scm = $p.ScmHost }
            $basicUser = $p.UserName; $basicPass = $p.Password
            $tok = Get-ArmAccessTokenAuto     # prefer bearer: survives SCM basic-auth being disabled
            if ($tok) { $bearer = $tok; Write-Info 'Auth: AAD token (auto) + publish profile fallback.' }
            else { Write-Info 'Auth: publish profile (auto).' }
        } else {
            $tok = Get-ArmAccessTokenAuto
            if ($tok) { $bearer = $tok; Write-Info 'Auth: AAD token (auto).' }
        }
    }

    if (-not $scm) { $scm = "$WebAppName.scm.azurewebsites.net" }

    $headers = $null; $kind = $null
    switch ($AuthMode) {
        'Basic' {
            if (-not ($basicUser -and $basicPass)) { throw 'AuthMode=Basic but no publishing credentials were resolved. Provide -DeployUserName/-DeployPassword or -PublishProfilePath.' }
            $headers = New-BasicAuthHeader $basicUser $basicPass; $kind = 'Basic'
        }
        'Bearer' {
            if (-not $bearer) { throw 'AuthMode=Bearer but no access token was resolved. Provide -AccessToken or install/login az / Az PowerShell.' }
            $headers = @{ Authorization = "Bearer $bearer" }; $kind = 'Bearer'
        }
        default {
            if ($bearer) { $headers = @{ Authorization = "Bearer $bearer" }; $kind = 'Bearer' }
            elseif ($basicUser -and $basicPass) { $headers = New-BasicAuthHeader $basicUser $basicPass; $kind = 'Basic' }
            else {
                throw @"
Could not resolve any App Service credentials. Use one of:
  -PublishProfilePath <file>   (download the publish profile from the Azure Portal)
  -DeployUserName / -DeployPassword
  -AccessToken <aad-token>
  -ResourceGroup <rg>          (auto-fetch via 'az' or 'Az' PowerShell)
"@
            }
        }
    }
    return [pscustomobject]@{ ScmHost = $scm; Headers = $headers; Kind = $kind }
}

# ----------------------------------------------------------------- Kudu deploy ---
function Test-KuduReachable {
    param([string] $ScmHost, [hashtable] $Headers)
    $uri = "https://$ScmHost/api/continuouswebjobs"
    try {
        Invoke-WithRetry -What 'SCM connectivity check' -Script {
            Invoke-RestMethod -Method Get -Uri $uri -Headers $Headers -TimeoutSec 60
        } | Out-Null
        return $true
    } catch {
        $status = Get-HttpStatus $_
        if ($status -eq 401 -or $status -eq 403) {
            throw "Authentication to $ScmHost failed (HTTP $status). If SCM basic authentication is disabled by policy, use an AAD token (-AccessToken or -ResourceGroup with az/Az)."
        }
        throw "Cannot reach SCM endpoint $ScmHost ($(Get-ExceptionSummary $_)). Check the app name, network access restrictions / private endpoints, or use a VM on the app's VNet."
    }
}

function Invoke-KuduZipDeploy {
    param([string] $ScmHost, [hashtable] $Headers, [string] $RemotePath, [string] $ZipPath)
    $path = $RemotePath
    if (-not $path.EndsWith('/')) { $path += '/' }
    $uri = "https://$ScmHost/api/zip$path"
    $hdr = @{ } + $Headers
    $hdr['If-Match'] = '*'
    Invoke-WithRetry -What "upload to $path" -Script {
        Invoke-RestMethod -Method Put -Uri $uri -InFile $ZipPath -ContentType 'application/zip' -Headers $hdr -TimeoutSec $TimeoutSec
    } | Out-Null
}

function Set-WebJobState {
    param([string] $ScmHost, [hashtable] $Headers, [string] $JobName, [ValidateSet('start', 'stop')][string] $Action)
    $uri = "https://$ScmHost/api/continuouswebjobs/$JobName/$Action"
    try {
        Invoke-RestMethod -Method Post -Uri $uri -Headers $Headers -TimeoutSec 60 | Out-Null
        Write-Info "  web-job '$JobName' $Action requested"
    } catch {
        $status = Get-HttpStatus $_
        if ($status -eq 404) {
            Write-Info "  web-job '$JobName' not registered yet; skipping $Action"
        } elseif ($status -eq 403 -or $status -eq 409) {
            # Common on locked-down apps; the deploy still works because App Service
            # shadow-copies the running job and auto-restarts it on content change.
            Write-Info "  web-job '$JobName' $Action not permitted here (HTTP $status); relying on auto-restart"
        } else {
            Write-WarnMsg "web-job '$JobName' $Action failed: $(Get-ExceptionSummary $_) (non-fatal)"
        }
    }
}

function Get-ContinuousWebJobs {
    param([string] $ScmHost, [hashtable] $Headers)
    try {
        return @(Invoke-RestMethod -Method Get -Uri "https://$ScmHost/api/continuouswebjobs" -Headers $Headers -TimeoutSec 60)
    } catch { return @() }
}

# ---------------------------------------------------------- DB upgrade web-job ---
# The PowerShell entry-point embedded into every DbUpgrade triggered web-job package.
# It is written to a temp file by New-DbUpgradeZip and included in the deployed zip.
$script:DbUpgradeRunScript = @'
#Requires -Version 5.1
<#
.SYNOPSIS
    DbUpgrade triggered web-job entry point.
    Reads the SQL connection string that App Service injects from the portal
    Configuration -> Connection strings, constructs a DatabaseUpgradeInfo payload,
    and delegates to AnalyticsInstaller.exe --initdb to run EF migrations, custom
    SQL scripts, and org-URL seeding.
    Exit 0 = success; non-zero = failure (Kudu records the run as Failed).
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

function Write-Ts { param([string]$m) Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }

Write-Ts 'DbUpgrade web-job starting.'

# App Service exposes named connection strings as environment variables with a type
# prefix. Try each variant that operators might have used for SPOInsightsEntities.
$connStr = $null
foreach ($prefix in @('SQLAZURECONNSTR_', 'SQLCONNSTR_', 'CUSTOMCONNSTR_')) {
    $v = [System.Environment]::GetEnvironmentVariable("${prefix}SPOInsightsEntities")
    if ($v) { $connStr = $v; Write-Ts "Found connection string via prefix '$prefix'."; break }
}
if (-not $connStr) {
    Write-Error ("Could not find connection string 'SPOInsightsEntities'. " +
        "Ensure it is set in App Service -> Configuration -> Connection strings " +
        "(type SQL Azure, SQL Server, or Custom; name exactly 'SPOInsightsEntities').")
    exit 1
}

# Serialize DatabaseUpgradeInfo to JSON and base64-encode it.
# This matches App.ControlPanel.Engine.Models.DatabaseUpgradeInfo / Base64Serialisable<T>.
$json   = ConvertTo-Json -Compress @{ ConnectionString = $connStr; OrgURLs = @() }
$base64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))

# AnalyticsInstaller.exe must be in the same folder as this script.
$exe = Join-Path $PSScriptRoot 'AnalyticsInstaller.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Error "AnalyticsInstaller.exe not found at '$exe'."
    exit 1
}

Write-Ts 'Launching: AnalyticsInstaller.exe --initdb <connection-string-redacted>'
& $exe --initdb $base64
$rc = $LASTEXITCODE
if ($rc -ne 0) {
    Write-Error "Database upgrade failed (AnalyticsInstaller.exe exited $rc)."
    exit $rc
}
Write-Ts 'Database upgrade completed successfully.'
exit 0
'@

function New-DbUpgradeZip {
    # Combines a normalised ControlPanelApp zip (installer exe + deps) with the embedded
    # run.ps1 wrapper into a single zip suitable for deployment as a triggered web-job.
    param([string] $InstallerZip, [string] $DestZip)

    if (Test-Path -LiteralPath $DestZip) { Remove-Item -LiteralPath $DestZip -Force }

    $src = [System.IO.Compression.ZipFile]::OpenRead($InstallerZip)
    try {
        $dest = [System.IO.Compression.ZipFile]::Open($DestZip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            # Copy all installer files (AnalyticsInstaller.exe + dependencies).
            foreach ($e in @($src.Entries)) {
                if ([string]::IsNullOrEmpty($e.Name)) { continue }   # skip directory entries
                $newEntry = $dest.CreateEntry($e.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
                $inStream = $e.Open(); $outStream = $newEntry.Open()
                try { $inStream.CopyTo($outStream) } finally { $outStream.Dispose(); $inStream.Dispose() }
            }
            # Add the PowerShell entry-point wrapper.
            $scriptBytes = [System.Text.Encoding]::UTF8.GetBytes($script:DbUpgradeRunScript)
            $runEntry  = $dest.CreateEntry('run.ps1', [System.IO.Compression.CompressionLevel]::Optimal)
            $runStream = $runEntry.Open()
            try { $runStream.Write($scriptBytes, 0, $scriptBytes.Length) } finally { $runStream.Dispose() }
        } finally { $dest.Dispose() }
    } finally { $src.Dispose() }
    return $DestZip
}

function Invoke-DbUpgrade {
    # Deploys the DbUpgrade triggered web-job, fires it, polls until completion, echoes
    # the Kudu log, and throws on failure so the caller's catch block handles the exit.
    param(
        [string]    $ScmHost,
        [hashtable] $Headers,
        [string]    $InstallerZip,
        [string]    $WorkDir,
        [int]       $TimeoutMinutes
    )

    $jobName   = 'DbUpgrade'
    $jobPath   = "/site/wwwroot/app_data/jobs/triggered/$jobName/"
    $dbZipPath = Join-Path $WorkDir "DbUpgrade.zip"

    # Build the web-job package on the fly.
    Write-Info "Building $jobName web-job package..."
    New-DbUpgradeZip -InstallerZip $InstallerZip -DestZip $dbZipPath | Out-Null
    Write-Ok  "  Package ready: $dbZipPath"

    # Deploy the package to the triggered web-job path.
    Write-Info "Deploying $jobName triggered web-job to $jobPath ..."
    Invoke-KuduZipDeploy -ScmHost $ScmHost -Headers $Headers -RemotePath $jobPath -ZipPath $dbZipPath
    Write-Ok "  $jobName web-job deployed."

    # Trigger the job.
    Write-Info "Triggering $jobName web-job..."
    $triggerUri = "https://$ScmHost/api/triggeredwebjobs/$jobName/run"
    try {
        Invoke-RestMethod -Method Post -Uri $triggerUri -Headers $Headers -TimeoutSec 30 | Out-Null
    } catch {
        $status = Get-HttpStatus $_
        if ($status -eq 200 -or $status -eq 202) { <# success: some hosts return 200 instead of 202 #> }
        else { throw "Failed to trigger $jobName web-job: $(Get-ExceptionSummary $_)" }
    }
    Write-Ok "  $jobName web-job triggered."

    # Poll history until the run finishes or we time out.
    Write-Step "Waiting for $jobName web-job to complete (timeout: ${TimeoutMinutes}m)"
    $historyUri   = "https://$ScmHost/api/triggeredwebjobs/$jobName/history"
    $deadline     = (Get-Date).AddMinutes($TimeoutMinutes)
    $pollInterval = 10    # seconds
    $latestRun    = $null

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $pollInterval
        try {
            $history = Invoke-RestMethod -Method Get -Uri $historyUri -Headers $Headers -TimeoutSec 30
            $runs    = if ($history.PSObject.Properties['runs']) { @($history.runs) } else { @() }
            if ($runs.Count -gt 0) {
                $latestRun   = $runs | Sort-Object -Property start_time -Descending | Select-Object -First 1
                $runStatus   = if ($latestRun.PSObject.Properties['status']) { $latestRun.status } else { 'Unknown' }
                Write-Info "  [$jobName] status: $runStatus"
                if ($runStatus -ne 'Running') { break }
            } else {
                Write-Info "  [$jobName] waiting for run to register..."
            }
        } catch {
            Write-WarnMsg "Polling $jobName history failed ($(Get-ExceptionSummary $_)); will retry..."
        }
    }

    # Fetch and echo the job log.
    if ($latestRun -and $latestRun.PSObject.Properties['output_url'] -and $latestRun.output_url) {
        Write-Step "$jobName web-job output"
        try {
            $log = Invoke-RestMethod -Method Get -Uri $latestRun.output_url -Headers $Headers -TimeoutSec 60
            ($log -split "`n") | ForEach-Object { Write-Host "  $_" }
        } catch {
            Write-WarnMsg "Could not fetch $jobName log: $(Get-ExceptionSummary $_)"
        }
    }

    # Evaluate result.
    if (-not $latestRun) {
        throw "$jobName web-job did not produce a history entry within ${TimeoutMinutes} minutes."
    }
    $finalStatus = if ($latestRun.PSObject.Properties['status']) { $latestRun.status } else { 'Unknown' }
    if ($finalStatus -eq 'Running') {
        throw "$jobName web-job is still running after ${TimeoutMinutes} minutes; check the Kudu dashboard."
    }
    if ($finalStatus -ne 'Success') {
        throw "$jobName web-job finished with status '$finalStatus'. See the log above for details."
    }
    Write-Ok "$jobName web-job completed successfully (status: $finalStatus)."
}


function Get-IpClass {
    param([string] $Ip)
    try { $addr = [System.Net.IPAddress]::Parse($Ip) } catch { return 'unknown' }
    if ($addr.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
        $b6 = $addr.GetAddressBytes()
        if (($b6[0] -band 0xFE) -eq 0xFC) { return 'private' }   # fc00::/7 unique local
        return 'public'
    }
    $b = $addr.GetAddressBytes()
    if ($b[0] -eq 10) { return 'private' }
    if ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) { return 'private' }
    if ($b[0] -eq 192 -and $b[1] -eq 168) { return 'private' }
    if ($b[0] -eq 127) { return 'loopback' }
    if ($b[0] -eq 169 -and $b[1] -eq 254) { return 'link-local' }
    return 'public'
}

function Get-EmbeddedIpv4 {
    # App Service presents private-endpoint traffic as a ULA IPv6 whose last 32 bits are
    # the real client IPv4 (e.g. fd40:...:0a01:0203 => 10.1.2.3). Decode it back.
    param([string] $Ip)
    try {
        $addr = [System.Net.IPAddress]::Parse($Ip)
        if ($addr.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
            $b = $addr.GetAddressBytes()
            if (($b[0] -band 0xFE) -eq 0xFC) { return ($b[12..15] -join '.') }
        }
    } catch { }
    return $null
}

function Resolve-HostIps {
    param([string] $HostName)
    $ips = @(); $cnames = @()
    if (Get-Command Resolve-DnsName -ErrorAction SilentlyContinue) {
        try {
            $recs = Resolve-DnsName -Name $HostName -Type A -DnsOnly -ErrorAction Stop
            foreach ($r in $recs) {
                if ($r.PSObject.Properties['NameHost'] -and $r.NameHost) { $cnames += $r.NameHost }
                if ($r.PSObject.Properties['IPAddress'] -and $r.IPAddress) { $ips += $r.IPAddress }
            }
        } catch { }
    }
    if (-not $ips) {
        try { $ips = [System.Net.Dns]::GetHostAddresses($HostName) | ForEach-Object { $_.IPAddressToString } } catch { }
    }
    return [pscustomobject]@{
        Ips    = @($ips | Select-Object -Unique)
        CNames = @($cnames | Select-Object -Unique)
    }
}

function Test-Tcp443 {
    param([string] $HostName, [int] $TimeoutMs = 5000)
    $client = $null
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $iar = $client.BeginConnect($HostName, 443, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TimeoutMs) -and $client.Connected) {
            $client.EndConnect($iar); return $true
        }
        return $false
    } catch { return $false }
    finally { if ($client) { $client.Close() } }
}

function ConvertTo-HeaderMap {
    param($HeaderObj)
    $map = @{}   # PowerShell hashtables are case-insensitive, so header lookups are too
    if ($null -eq $HeaderObj) { return $map }
    try {
        if ($HeaderObj -is [System.Net.WebHeaderCollection]) {
            foreach ($k in $HeaderObj.AllKeys) { $map[$k] = $HeaderObj[$k] }
        } elseif ($HeaderObj -is [System.Collections.IDictionary]) {
            foreach ($k in $HeaderObj.Keys) { $map[[string]$k] = (@($HeaderObj[$k]) -join ', ') }
        } else {
            foreach ($kv in $HeaderObj) { $map[[string]$kv.Key] = (@($kv.Value) -join ', ') }
        }
    } catch { }
    return $map
}

function Get-HttpProbe {
    param([string] $Url, [int] $TimeoutSec = 20)
    $status = $null; $headers = @{}
    try {
        $r = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec $TimeoutSec -MaximumRedirection 0 -UseBasicParsing -ErrorAction Stop
        $status = [int]$r.StatusCode
        $headers = ConvertTo-HeaderMap $r.Headers
    } catch {
        $status = Get-HttpStatus $_
        $resp = $null; try { $resp = $_.Exception.Response } catch { }
        if ($resp) { try { $headers = ConvertTo-HeaderMap $resp.Headers } catch { } }
    }
    return [pscustomobject]@{ Status = $status; Headers = $headers }
}

function Resolve-ScmHostOnly {
    if ($ScmHostName) { return $ScmHostName }
    if ($PublishProfilePath -and (Test-Path -LiteralPath $PublishProfilePath)) {
        try { return (Read-PublishProfile $PublishProfilePath).ScmHost } catch { }
    }
    return "$WebAppName.scm.azurewebsites.net"
}

function Test-SiteReachability {
    param([string] $ScmHost)
    Write-Step 'Checking site reachability / DNS'
    $mainHost = $ScmHost -replace '\.scm\.', '.'
    if ($mainHost -eq $ScmHost) { $mainHost = "$WebAppName.azurewebsites.net" }

    $anyPublic = $false
    $anyResolved = $false
    $targets = @(
        [pscustomobject]@{ Label = 'App'; Host = $mainHost }
        [pscustomobject]@{ Label = 'SCM'; Host = $ScmHost }
    )
    foreach ($t in $targets) {
        $res = Resolve-HostIps -HostName $t.Host
        if (-not $res.Ips) {
            Write-WarnMsg ("{0,-3} {1} -> DNS did not resolve" -f $t.Label, $t.Host)
            continue
        }
        $anyResolved = $true
        $classes = @($res.Ips | ForEach-Object { Get-IpClass $_ } | Select-Object -Unique)
        if ($classes -contains 'public') { $anyPublic = $true }
        $tcp = Test-Tcp443 -HostName $t.Host
        $plNote = if ($res.CNames -match 'privatelink') { ' (privatelink CNAME present)' } else { '' }
        $line = ("{0,-3} {1} -> {2} [{3}]{4}  TCP443={5}" -f `
            $t.Label, $t.Host, ($res.Ips -join ', '), ($classes -join '/'), $plNote, $(if ($tcp) { 'open' } else { 'closed' }))
        if ($classes -contains 'public') { Write-WarnMsg $line } else { Write-Ok $line }
    }

    if ($anyPublic) {
        Write-WarnMsg 'A hostname resolves to a PUBLIC IP from this machine, so traffic goes out the public interface.'
        Write-WarnMsg 'If public network access is disabled on the app, the App (main) site returns HTTP 403 that way.'
        Write-WarnMsg "To reach it privately: link a 'privatelink.azurewebsites.net' Private DNS Zone to this VNet with"
        Write-WarnMsg "A records for both '$mainHost' and '$ScmHost' -> the private-endpoint IP, and use the VNet DNS."
    } elseif ($anyResolved) {
        Write-Ok 'Hostnames resolve to private IPs (private-endpoint path).'
    } else {
        Write-WarnMsg 'Neither hostname resolved from this machine - check DNS server / network connectivity.'
    }

    # App-layer probe: shows what a browser on this machine actually gets back.
    $probe = Get-HttpProbe -Url "https://$mainHost/"
    $appStatus = $probe.Status
    if ($null -eq $appStatus) {
        Write-Info "App  GET https://$mainHost/ -> no HTTP response (timeout or connection blocked)"
        return
    }
    $interp = ''
    switch ($appStatus) {
        { $_ -ge 200 -and $_ -lt 300 } { $interp = 'OK - app is serving'; break }
        { $_ -ge 300 -and $_ -lt 400 } { $interp = 'redirect - app is serving (likely to sign-in)'; break }
        401 { $interp = 'unauthorized - app is serving; sign-in required'; break }
        403 { $interp = 'forbidden - FIRST check the app is STARTED (a stopped App Service 403s its main site while SCM/Kudu keeps working); then main-site Access Restrictions / public network access / auth'; break }
        503 { $interp = 'service unavailable - app stopped or failing to start (check app settings / logs)'; break }
        default { $interp = '' }
    }
    $line = "App  GET https://$mainHost/ -> HTTP $appStatus$(if ($interp) { " ($interp)" })"
    if ($appStatus -ge 400) { Write-WarnMsg $line } else { Write-Ok $line }

    foreach ($hk in @('Location', 'WWW-Authenticate', 'x-ms-forbidden-ip', 'x-ms-forbidden-reason')) {
        if ($probe.Headers.ContainsKey($hk)) { Write-Info ("       {0}: {1}" -f $hk, $probe.Headers[$hk]) }
    }
    if ($probe.Headers.ContainsKey('WWW-Authenticate') -or
        ($probe.Headers.ContainsKey('Location') -and $probe.Headers['Location'] -match '/\.auth/')) {
        Write-Info '       -> signature of App Service Authentication (Easy Auth): unauthenticated requests are being blocked'
    }
    if ($probe.Headers.ContainsKey('x-ms-forbidden-ip')) {
        $fip = ([string]$probe.Headers['x-ms-forbidden-ip']).Trim(' ', '[', ']')
        $decoded = Get-EmbeddedIpv4 $fip
        Write-Info "       -> blocked at the NETWORK layer (403 x-ms-forbidden-ip: $fip)."
        if ($decoded) {
            Write-Info "          That IPv6 embeds the IPv4 $decoded (your CLIENT IP), but the app filters this traffic by the"
            Write-Info '          MAPPED IPv6 - so an IPv4 allow rule will NOT match it. Allow the unique-local range instead:'
            Write-Info '          az webapp config access-restriction add -g <rg> -n <app> --action Allow --priority 200 --ip-address fc00::/7'
            Write-Info '          (Better long-term: find why private-endpoint traffic is not exempt - e.g. give the PE its own subnet.)'
        } else {
            Write-Info '          Check: (a) access restrictions (az webapp config access-restriction show) and (b) publicNetworkAccess.'
        }
        Write-Info '          Note: a STOPPED app, or "Public network access = Disabled", also returns this 403 - verify the'
        Write-Info '          app is Started (SCM/Kudu keeps working when it is stopped, so a deploy can still succeed).'
    }
}

# ============================================================================ ---
#  Main
# ============================================================================ ---
# Run the deployment only when invoked directly. When dot-sourced (InvocationName is
# '.') the script only defines the functions above, so they can be unit-tested.
if ($MyInvocation.InvocationName -ne '.') {
$script:workDir = $null
try {
    Write-Step 'Microsoft 365 Analytics Insights - App Service content deploy'
    Write-Info "Target web app : $WebAppName"
    if ($ResourceGroup) { Write-Info "Resource group : $ResourceGroup" }

    # Diagnostics-only mode: no source, no auth, no deploy - just the reachability check.
    if ($DiagnoseOnly) {
        Test-SiteReachability -ScmHost (Resolve-ScmHostOnly)
        return
    }

    Write-Info ("Source         : {0}" -f ($(if ($SourceFolder) { "local ($SourceFolder)" } else { "GitHub $RepoOwner/$RepoName" })))
    Write-Info ("Deploying      : {0}" -f (@(
        if (-not $SkipWebsite) { 'website' }
        if (-not $SkipWebJobs) { 'web-jobs' }
        if ($RunDbUpgrade)     { 'DB upgrade' }
    ) -join ', '))
    if ($DownloadOnly) { Write-Info 'Mode           : download/normalise only (no deploy)' }

    $components = Get-Components

    # Working directory.
    if ($WorkFolder) { $script:workDir = $WorkFolder }
    else { $script:workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("m365ai-deploy-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
    $downloadDir = Join-Path $script:workDir 'download'
    $normalizedDir = Join-Path $script:workDir 'normalized'
    New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
    New-Item -ItemType Directory -Path $normalizedDir -Force | Out-Null
    Write-Info "Work folder    : $script:workDir"

    # 1) Acquire source zips.
    Write-Step 'Acquiring packages'
    $sourceMap = Resolve-Sources -Components $components -DownloadDir $downloadDir

    # 2) Normalise (strip wrapper folder).
    Write-Step 'Preparing packages'
    foreach ($c in $components) {
        Write-Info "Normalising $($c.ZipFile)..."
        $normZip = Join-Path $normalizedDir ("norm-" + $c.ZipFile)
        $c | Add-Member -NotePropertyName NormalizedZip -NotePropertyValue (New-NormalizedZip -SourceZip $sourceMap[$c.ZipFile] -DestZip $normZip) -Force
    }
    Write-Ok 'All packages prepared.'

    if ($DownloadOnly) {
        Write-Step 'Done (download only)'
        foreach ($c in $components) { Write-Info ("  {0} -> {1}" -f $c.Name, $c.NormalizedZip) }
        if (-not $KeepWorkFolder) { Write-WarnMsg "Normalised packages are under '$normalizedDir'. Use -KeepWorkFolder or copy them out; the work folder is retained in DownloadOnly mode." }
        $KeepWorkFolder = $true
        return
    }

    # 3) Resolve auth + connectivity.
    Write-Step 'Connecting to App Service (Kudu/SCM)'
    $auth = Resolve-KuduAuth
    Write-Info "SCM host       : $($auth.ScmHost)"
    Write-Info "Auth mode      : $($auth.Kind)"
    Test-KuduReachable -ScmHost $auth.ScmHost -Headers $auth.Headers | Out-Null
    Write-Ok 'SCM endpoint reachable and authenticated.'

    # 4) Deploy. Website first so subsequent web-job uploads are never clobbered.
    #    Installer components are handled separately by the DB upgrade step.
    $deployComponents = @($components | Where-Object { $_.Kind -ne 'installer' })
    if ($deployComponents.Count -gt 0) {
        Write-Step 'Deploying content'
    }
    foreach ($c in $deployComponents) {
        $target = "https://$($auth.ScmHost) => $($c.RemotePath)"
        if (-not $PSCmdlet.ShouldProcess($target, "Deploy $($c.Name)")) {
            Write-Info "  [WhatIf] would deploy $($c.Name) to $($c.RemotePath)"
            continue
        }
        Write-Info "Deploying $($c.Name) -> $($c.RemotePath)"
        if ($c.Kind -eq 'webjob' -and $RestartWebJobs) { Set-WebJobState -ScmHost $auth.ScmHost -Headers $auth.Headers -JobName $c.JobName -Action 'stop' }
        Invoke-KuduZipDeploy -ScmHost $auth.ScmHost -Headers $auth.Headers -RemotePath $c.RemotePath -ZipPath $c.NormalizedZip
        if ($c.Kind -eq 'webjob' -and $RestartWebJobs) { Set-WebJobState -ScmHost $auth.ScmHost -Headers $auth.Headers -JobName $c.JobName -Action 'start' }
        Write-Ok "  Deployed $($c.Name)."
    }

    # 5) Verify continuous web-jobs (best effort).
    if (-not $SkipWebJobs -and $PSCmdlet.ShouldProcess($auth.ScmHost, 'Verify continuous web-jobs')) {
        Write-Step 'Verifying web-jobs'
        $jobs = Get-ContinuousWebJobs -ScmHost $auth.ScmHost -Headers $auth.Headers
        if ($jobs.Count -eq 0) {
            Write-WarnMsg 'No continuous web-jobs reported yet (they may take a moment to register).'
        } else {
            foreach ($j in $jobs) {
                $status = if ($j.PSObject.Properties['status']) { $j.status } else { '?' }
                Write-Info ("  {0}: {1}" -f $j.name, $status)
            }
        }
    }

    # 6) Optional DB upgrade (triggered web-job inside the App Service).
    if ($RunDbUpgrade) {
        Write-Step 'Running database upgrade'
        $installerComp = @($components | Where-Object { $_.Kind -eq 'installer' }) | Select-Object -First 1
        if (-not $installerComp -or -not $installerComp.NormalizedZip) {
            throw 'Internal error: installer component not found; this is a bug in Get-Components.'
        }
        if (-not $PSCmdlet.ShouldProcess("https://$($auth.ScmHost)", 'Run DbUpgrade triggered web-job')) {
            Write-Info '  [WhatIf] would deploy DbUpgrade web-job, trigger it, and poll for completion.'
        } else {
            Invoke-DbUpgrade `
                -ScmHost        $auth.ScmHost `
                -Headers        $auth.Headers `
                -InstallerZip   $installerComp.NormalizedZip `
                -WorkDir        $normalizedDir `
                -TimeoutMinutes $DbUpgradeTimeoutMin
        }
    }

    # 7) Optional private-networking diagnostic.
    if ($VerifySiteReachable) {
        Test-SiteReachability -ScmHost $auth.ScmHost
    }

    Write-Step 'Deployment complete'
    Write-Ok "https://$WebAppName.azurewebsites.net/"
    if (-not $DownloadOnly) {
        $notes = @()
        if (-not $RunDbUpgrade) { $notes += 'DB schema upgrade: run again with -RunDbUpgrade, or use the installer.' }
        $notes += 'App settings and connection strings are managed separately (portal / ARM / installer).'
        $notes | ForEach-Object { Write-Info "Note: $_" }
    }
} catch {
    Write-Host ''
    Write-ErrMsg (Get-ExceptionSummary $_)
    $body = Get-HttpErrorBody $_
    if ($body) { Write-ErrMsg "Response: $body" }
    if ($_.ScriptStackTrace) { Write-Verbose $_.ScriptStackTrace }
    exit 1
} finally {
    if ($script:workDir -and (Test-Path -LiteralPath $script:workDir)) {
        if ($KeepWorkFolder) { Write-Info "Work folder kept: $script:workDir" }
        else { Remove-Item -LiteralPath $script:workDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
}
