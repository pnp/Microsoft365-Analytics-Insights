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

    With -RunDbUpgrade it also runs the two-step database schema upgrade (EF migrations +
    custom SQL scripts) that AnalyticsInstaller.exe normally handles, and optionally seeds
    org URLs when -OrgUrls is supplied. The upgrade runs entirely inside the App Service as
    a triggered web-job, so no local execution of the installer is needed. The web-job is
    built on the fly from the ControlPanelApp.zip release asset (no new binary to deploy);
    it is removed automatically after a successful run and left in place on failure so you
    can inspect it in the Kudu dashboard.

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
    to pin the SCM host). Handy for triaging a 403, or an 'Unable to connect to the
    remote server', from the machine you deploy from.

.PARAMETER RunDbUpgrade
    After deploying content, run the database schema upgrade (EF migrations + custom SQL
    scripts, plus org-URL seeding when -OrgUrls is supplied) inside the App Service as a
    triggered web-job.

    The web-job is assembled on the fly from ControlPanelApp.zip (the signed
    AnalyticsInstaller.exe release asset) plus a PowerShell run.ps1 wrapper, deployed
    to the App Service triggered web-jobs folder, triggered via the Kudu API, and polled
    until it completes. The run.ps1 wrapper launches AnalyticsInstaller.exe with
    Start-Process (the exe is a GUI-subsystem app, so the plain call operator would not
    wait for it or capture its exit code), streams its output live, and propagates its real
    exit code. The full job log is echoed to the console and the script exits non-zero if
    the upgrade fails. On success the web-job is removed; on failure it is left in place for
    inspection.

    Requires the App Service 'SPOInsightsEntities' connection string to be already
    configured (Portal -> Configuration -> Connection strings). The connection string
    identity must have DDL rights (ALTER TABLE, CREATE TABLE, etc.) as migrations may
    run schema changes.

    This switch can be combined with -SkipWebsite/-SkipWebJobs to run only the DB upgrade
    without deploying web-job or website content (e.g. when binaries were already deployed
    and only a schema upgrade is needed).

    NOTE: a triggered web-job is stopped by App Service after WEBJOBS_IDLE_TIMEOUT seconds
    (default 120) of no output. The wrapper emits a periodic heartbeat while the migration
    runs to avoid this, but for very long migrations also consider raising WEBJOBS_IDLE_TIMEOUT
    in the App Service application settings.

.PARAMETER OrgUrls
    Optional list of organisation SharePoint root URLs to seed into the org_urls table as
    part of -RunDbUpgrade (mirrors the org-URL seeding the installer performs). When omitted,
    no org URLs are seeded and only the schema upgrade (EF migrations + custom SQL) runs.

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

    Names are resolved the same way the deployment resolves them, so HOSTS-file entries
    count; one is reported next to what the DNS server itself answers. A system proxy is
    reported too, because a proxy looks the name up itself and HOSTS entries then do not
    apply. The TCP test tells a refused connection from one nobody answered: a PRIVATE
    address that times out means this machine has no network path to the private
    endpoint, which no DNS or HOSTS change can fix.

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

    Private endpoints: the app's private endpoint also serves its SCM (Kudu) host, on the
    same private IP, and 'Public network access: Disabled' closes SCM to the internet as
    well. Deploy from a machine that both resolves the SCM host to the private endpoint IP
    (the privatelink.azurewebsites.net zone, or a HOSTS entry) AND can route to that IP
    (the app's VNet, a peered VNet, or VPN/ExpressRoute). A HOSTS entry only provides the
    first, and is ignored when a proxy is in use. If the SCM check fails without an HTTP
    response, the script reports which of the two is missing; -DiagnoseOnly runs the same
    checks on their own.
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
    [string[]] $OrgUrls = @(),
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

function Get-InnermostExceptionMessage {
    param([System.Exception] $Exception)
    $e = $Exception
    while ($null -ne $e -and $null -ne $e.InnerException) { $e = $e.InnerException }
    if ($null -eq $e) { return $null }
    return $e.Message
}

function Get-ExceptionSummary {
    param($ErrorRecord)
    $status = Get-HttpStatus $ErrorRecord
    $msg = $ErrorRecord.Exception.Message
    # Windows PowerShell 5.1 reports every failed connection as just "Unable to connect to the remote
    # server". The inner SocketException says what happened and which address was tried - e.g. "...failed
    # to respond 10.0.0.4:443" (nothing answered) or "...actively refused it 10.0.0.4:443" - so keep it.
    $inner = Get-InnermostExceptionMessage $ErrorRecord.Exception
    if ($inner) {
        $inner = ($inner -replace '\s+', ' ').Trim()
        if ($msg.IndexOf($inner, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { $msg = '{0}: {1}' -f $msg, $inner }
    }
    if ($null -ne $status) { return ('HTTP {0}: {1}' -f $status, $msg) }
    return $msg
}

function Get-ResponseHeaderValue {
    param($ErrorRecord, [string] $Name)
    try {
        $resp = $ErrorRecord.Exception.Response
        if ($null -ne $resp) {
            $map = ConvertTo-HeaderMap $resp.Headers
            if ($map.ContainsKey($Name)) { return [string]$map[$Name] }
        }
    } catch { }
    return $null
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
        $err = $_
        $status = Get-HttpStatus $err
        $forbiddenIp = $null
        if ($status -eq 403) { $forbiddenIp = Get-ResponseHeaderValue $err 'x-ms-forbidden-ip' }
        if ($forbiddenIp) {
            # App Service's own network-layer block carries this header; Kudu rejecting the credentials does not.
            $fip = $forbiddenIp.Trim(' ', '[', ']')
            if (Get-EmbeddedIpv4 $fip) {
                throw "$ScmHost blocked this request at the network layer (HTTP 403, x-ms-forbidden-ip: $fip) although it arrived through a private endpoint. This is not an authentication problem: check the SCM site's access restrictions."
            }
            throw "$ScmHost blocked this request at the network layer (HTTP 403, x-ms-forbidden-ip: $fip). This is not an authentication problem: the request reached the app's PUBLIC endpoint, where public network access is disabled or the SCM site's access restrictions do not allow this client. Deploy from a machine that resolves $ScmHost to the app's private endpoint IP and can route to it, or allow this client in the SCM site's access restrictions."
        }
        if ($status -eq 401 -or $status -eq 403) {
            throw "Authentication to $ScmHost failed (HTTP $status). If SCM basic authentication is disabled by policy, use an AAD token (-AccessToken or -ResourceGroup with az/Az)."
        }
        if ($null -eq $status) { Write-ScmConnectivityDiagnosis -ScmHost $ScmHost }
        throw "Cannot reach SCM endpoint $ScmHost ($(Get-ExceptionSummary $err)). Check the app name, network access restrictions / private endpoints, or use a VM on the app's VNet."
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
    SQL scripts, and (optionally) org-URL seeding.
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
    $host.UI.WriteErrorLine("Could not find connection string 'SPOInsightsEntities'. " +
        "Ensure it is set in App Service -> Configuration -> Connection strings " +
        "(type SQL Azure, SQL Server, or Custom; name exactly 'SPOInsightsEntities').")
    exit 1
}

# Org URLs to seed. New-DbUpgradeZip substitutes the __ORGURLS__ token with a quoted,
# comma-separated list built from the caller's -OrgUrls parameter (empty by default, in
# which case DatabaseUpgradeInfo.EnsureOrgURLs is a no-op and no seeding is attempted).
$orgUrls = @(__ORGURLS__)

# Serialize DatabaseUpgradeInfo to JSON and base64-encode it.
# This matches App.ControlPanel.Engine.Models.DatabaseUpgradeInfo / Base64Serialisable<T>.
$json   = ConvertTo-Json -Compress @{ ConnectionString = $connStr; OrgURLs = $orgUrls }
$base64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))

# AnalyticsInstaller.exe must be in the same folder as this script.
$exe = Join-Path $PSScriptRoot 'AnalyticsInstaller.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    $host.UI.WriteErrorLine("AnalyticsInstaller.exe not found at '$exe'.")
    exit 1
}

Write-Ts 'Launching: AnalyticsInstaller.exe --initdb <connection-string-redacted>'

# IMPORTANT: AnalyticsInstaller.exe is a GUI-subsystem (WinExe) application. The PowerShell
# call operator (& $exe) does NOT wait for such a process and does NOT surface its real exit
# code or stdout - it returns immediately with $LASTEXITCODE = 0, which would make a failed
# migration look successful and truncate the web-job before the upgrade actually finishes.
# Launch via Start-Process -PassThru (no -Wait) so we can (a) read the true ExitCode after it
# exits, and (b) stream its redirected stdout/stderr live below. The live output also keeps
# resetting the App Service web-job idle timer (WEBJOBS_IDLE_TIMEOUT) so a long-running
# migration is not killed for being "idle".
$outFile = Join-Path $PSScriptRoot 'dbupgrade.stdout.log'
$errFile = Join-Path $PSScriptRoot 'dbupgrade.stderr.log'
foreach ($f in @($outFile, $errFile)) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }

$proc = Start-Process -FilePath $exe -ArgumentList @('--initdb', $base64) `
    -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile

# Cache the process handle immediately: in Windows PowerShell 5.1 (the web-job runtime),
# Start-Process -PassThru disposes the underlying handle once the process exits, after which
# $proc.ExitCode comes back $null. Touching .Handle here forces it to be retained so we can
# read the real exit code below.
$null = $proc.Handle

# Tail the redirected logs. Files are opened share-read-write so we can read while the exe
# is still writing; $offsets tracks how far we have already echoed for each file.
$offsets = @{ $outFile = [long]0; $errFile = [long]0 }
function Show-NewText {
    param([string] $Path, [string] $Prefix)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try { $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite) }
    catch { return }   # transient sharing violation - pick it up on the next poll / final flush
    try {
        [void]$fs.Seek($offsets[$Path], [System.IO.SeekOrigin]::Begin)
        $sr   = New-Object System.IO.StreamReader($fs)
        $text = $sr.ReadToEnd()
        $offsets[$Path] = $fs.Position
        $sr.Dispose()
        foreach ($line in ($text -split "`r?`n")) { if ($line.Length -gt 0) { Write-Host ($Prefix + $line) } }
    } finally { $fs.Dispose() }
}

$lastBeat = Get-Date
while (-not $proc.HasExited) {
    Start-Sleep -Seconds 3
    Show-NewText -Path $outFile -Prefix '  '
    Show-NewText -Path $errFile -Prefix '  [stderr] '
    if (((Get-Date) - $lastBeat).TotalSeconds -ge 30) {
        Write-Ts ('...still upgrading (elapsed {0:hh\:mm\:ss})...' -f ((Get-Date) - $proc.StartTime))
        $lastBeat = Get-Date
    }
}
$proc.WaitForExit()
# Final flush of anything written between the last poll and process exit.
Show-NewText -Path $outFile -Prefix '  '
Show-NewText -Path $errFile -Prefix '  [stderr] '

$rc = $proc.ExitCode
if ($rc -ne 0) {
    $host.UI.WriteErrorLine("Database upgrade failed (AnalyticsInstaller.exe exited $rc).")
    exit $rc
}
Write-Ts 'Database upgrade completed successfully.'
exit 0
'@

function New-DbUpgradeZip {
    # Combines a normalised ControlPanelApp zip (installer exe + deps) with the embedded
    # run.ps1 wrapper into a single zip suitable for deployment as a triggered web-job.
    param([string] $InstallerZip, [string] $DestZip, [string[]] $OrgUrls = @())

    if (Test-Path -LiteralPath $DestZip) { Remove-Item -LiteralPath $DestZip -Force }

    # Build the run.ps1 text, substituting the org-URL list into the __ORGURLS__ placeholder
    # as a quoted, comma-separated PowerShell array literal (single quotes doubled to escape).
    $orgUrlsLiteral = ''
    if ($OrgUrls -and @($OrgUrls).Count -gt 0) {
        $orgUrlsLiteral = (@($OrgUrls) | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ', '
    }
    $runScriptText = $script:DbUpgradeRunScript.Replace('__ORGURLS__', $orgUrlsLiteral)

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
            $scriptBytes = [System.Text.Encoding]::UTF8.GetBytes($runScriptText)
            $runEntry  = $dest.CreateEntry('run.ps1', [System.IO.Compression.CompressionLevel]::Optimal)
            $runStream = $runEntry.Open()
            try { $runStream.Write($scriptBytes, 0, $scriptBytes.Length) } finally { $runStream.Dispose() }
        } finally { $dest.Dispose() }
    } finally { $src.Dispose() }
    return $DestZip
}

function Remove-DbUpgradeWebJob {
    # Best-effort removal of the triggered web-job (and the embedded signed installer) so we
    # don't leave the binary deployed on the App Service. Non-fatal: a leftover job is harmless.
    param([string] $ScmHost, [hashtable] $Headers, [string] $JobName)
    try {
        Invoke-RestMethod -Method Delete -Uri "https://$ScmHost/api/triggeredwebjobs/$JobName" -Headers $Headers -TimeoutSec 30 | Out-Null
        Write-Ok "  Removed $JobName web-job."
    } catch {
        Write-WarnMsg "Could not remove $JobName web-job (leftover under /site/wwwroot/app_data/jobs/triggered/$JobName): $(Get-ExceptionSummary $_)"
    }
}

function Invoke-DbUpgrade {
    # Deploys the DbUpgrade triggered web-job, fires it, polls until completion, echoes
    # the Kudu log, and throws on failure so the caller's catch block handles the exit.
    # On success the web-job is removed; on failure it is left in place for inspection.
    param(
        [string]    $ScmHost,
        [hashtable] $Headers,
        [string]    $InstallerZip,
        [string]    $WorkDir,
        [int]       $TimeoutMinutes,
        [string[]]  $OrgUrls = @()
    )

    $jobName    = 'DbUpgrade'
    $jobPath    = "/site/wwwroot/app_data/jobs/triggered/$jobName/"
    $dbZipPath  = Join-Path $WorkDir "DbUpgrade.zip"
    $historyUri = "https://$ScmHost/api/triggeredwebjobs/$jobName/history"

    # Build the web-job package on the fly.
    Write-Info "Building $jobName web-job package..."
    New-DbUpgradeZip -InstallerZip $InstallerZip -DestZip $dbZipPath -OrgUrls $OrgUrls | Out-Null
    Write-Ok  "  Package ready: $dbZipPath"

    # Deploy the package to the triggered web-job path.
    Write-Info "Deploying $jobName triggered web-job to $jobPath ..."
    Invoke-KuduZipDeploy -ScmHost $ScmHost -Headers $Headers -RemotePath $jobPath -ZipPath $dbZipPath
    Write-Ok "  $jobName web-job deployed."

    # Record any pre-existing run ids so we can tell the run we are about to trigger apart from
    # a historical run left over from a previous (e.g. failed) attempt, and never report a stale
    # run's result.
    $priorRunIds = @()
    try {
        $existing = Invoke-RestMethod -Method Get -Uri $historyUri -Headers $Headers -TimeoutSec 30
        if ($existing.PSObject.Properties['runs']) { $priorRunIds = @($existing.runs | ForEach-Object { $_.id }) }
    } catch { }

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

    # Poll history until the new run finishes or we time out.
    Write-Step "Waiting for $jobName web-job to complete (timeout: ${TimeoutMinutes}m)"
    $deadline     = (Get-Date).AddMinutes($TimeoutMinutes)
    $pollInterval = 10    # seconds
    $latestRun    = $null

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $pollInterval
        try {
            $history = Invoke-RestMethod -Method Get -Uri $historyUri -Headers $Headers -TimeoutSec 30
            $allRuns = if ($history.PSObject.Properties['runs']) { @($history.runs) } else { @() }
            # Ignore runs that already existed before we triggered.
            $newRuns = @($allRuns | Where-Object { $priorRunIds -notcontains $_.id })
            if ($newRuns.Count -gt 0) {
                $latestRun   = $newRuns | Sort-Object -Property start_time -Descending | Select-Object -First 1
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

    # Evaluate result. On any non-success outcome, leave the web-job deployed so the operator
    # can inspect or re-run it from the Kudu dashboard.
    if (-not $latestRun) {
        Write-WarnMsg "$jobName web-job left deployed at $jobPath for inspection."
        throw "$jobName web-job did not produce a history entry within ${TimeoutMinutes} minutes."
    }
    $finalStatus = if ($latestRun.PSObject.Properties['status']) { $latestRun.status } else { 'Unknown' }
    if ($finalStatus -eq 'Running') {
        Write-WarnMsg "$jobName web-job left deployed at $jobPath for inspection."
        throw "$jobName web-job is still running after ${TimeoutMinutes} minutes; check the Kudu dashboard."
    }
    if ($finalStatus -ne 'Success') {
        Write-WarnMsg "$jobName web-job left deployed at $jobPath for inspection."
        throw "$jobName web-job finished with status '$finalStatus'. See the log above for details."
    }
    Write-Ok "$jobName web-job completed successfully (status: $finalStatus)."

    # Success: remove the web-job (and its embedded installer) from the App Service.
    Remove-DbUpgradeWebJob -ScmHost $ScmHost -Headers $Headers -JobName $jobName
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

function Get-HostsFileIps {
    # The addresses the local HOSTS file maps this exact name to (case-insensitive); empty when it has no entry.
    param([string] $HostName)
    $path = if ($env:SystemRoot) { Join-Path $env:SystemRoot 'System32\drivers\etc\hosts' } else { '/etc/hosts' }
    $ips = @()
    try {
        foreach ($line in [System.IO.File]::ReadAllLines($path)) {
            $fields = @((($line -split '#', 2)[0]).Trim() -split '\s+' | Where-Object { $_ })
            if ($fields.Count -ge 2 -and @($fields[1..($fields.Count - 1)]) -contains $HostName) { $ips += $fields[0] }
        }
    } catch { }
    return @($ips | Select-Object -Unique)
}

function Resolve-HostIps {
    param([string] $HostName)
    # The addresses a connection will actually use. [System.Net.Dns] goes through the same resolver as
    # Invoke-RestMethod and TcpClient, so HOSTS-file entries count.
    $ips = @()
    try { $ips = @([System.Net.Dns]::GetHostAddresses($HostName) | ForEach-Object { $_.IPAddressToString }) } catch { }

    # What the DNS server itself answers: the CNAME chain (for the privatelink note), and the answer to show
    # next to a HOSTS entry. Resolve-DnsName -DnsOnly skips the HOSTS file, so it is never the address used.
    $dnsIps = @(); $cnames = @()
    if (Get-Command Resolve-DnsName -ErrorAction SilentlyContinue) {
        try {
            foreach ($r in @(Resolve-DnsName -Name $HostName -Type A -DnsOnly -ErrorAction Stop)) {
                if ($r.PSObject.Properties['NameHost'] -and $r.NameHost) { $cnames += $r.NameHost }
                if ($r.PSObject.Properties['IPAddress'] -and $r.IPAddress) { $dnsIps += $r.IPAddress }
            }
        } catch { }
    }
    return [pscustomobject]@{
        Ips      = @($ips | Select-Object -Unique)
        CNames   = @($cnames | Select-Object -Unique)
        DnsIps   = @($dnsIps | Select-Object -Unique)
        HostsIps = @(Get-HostsFileIps -HostName $HostName)
    }
}

function Get-SystemProxyFor {
    # The proxy Invoke-RestMethod sends this request through, or $null for a direct connection. A proxy looks
    # the name up itself, so neither this machine's DNS nor its HOSTS file decides where the request goes.
    param([Uri] $Uri)
    try {
        if ($PSVersionTable.PSEdition -eq 'Core') { $proxy = [System.Net.Http.HttpClient]::DefaultProxy }
        else { $proxy = [System.Net.WebRequest]::DefaultWebProxy }
        if ($null -eq $proxy -or $proxy.IsBypassed($Uri)) { return $null }
        $via = $proxy.GetProxy($Uri)
        if ($null -eq $via -or $via.Authority -eq $Uri.Authority) { return $null }
        return $via
    } catch { return $null }
}

function Test-TcpPort {
    # One TCP connection attempt, classified by how it ended:
    #   Open    - accepted
    #   Refused - something at that address actively rejected the port
    #   Timeout - nothing answered at all: no route to the address, or a firewall/NSG silently dropping it
    # A refusal comes back within a couple of seconds, so a short wait is enough to tell the two apart
    # (Windows itself gives up on an unanswered connection after about 21 seconds).
    param([string] $Address, [int] $Port = 443, [int] $TimeoutMs = 5000)
    $client = $null
    try {
        $ip = [System.Net.IPAddress]::Parse($Address)
        $client = [System.Net.Sockets.TcpClient]::new($ip.AddressFamily)
        $iar = $client.BeginConnect($ip, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { return 'Timeout' }
        try { $client.EndConnect($iar); return 'Open' }
        catch {
            $se = $_.Exception
            while ($null -ne $se -and -not ($se -is [System.Net.Sockets.SocketException])) { $se = $se.InnerException }
            if ($null -eq $se) { return 'Error' }
            switch ($se.SocketErrorCode) {
                'ConnectionRefused'  { return 'Refused' }
                'TimedOut'           { return 'Timeout' }
                'HostUnreachable'    { return 'Unreachable' }
                'NetworkUnreachable' { return 'Unreachable' }
                default              { return [string]$se.SocketErrorCode }
            }
        }
    } catch { return 'Error' }
    finally { if ($client) { $client.Close() } }
}

function Format-IpList {
    param([string[]] $Ips)
    return (@($Ips | ForEach-Object { '{0} [{1}]' -f $_, (Get-IpClass $_) }) -join ', ')
}

function Get-HostConnectivity {
    # Everything that decides where https://<HostName>/ goes from this machine, and whether TCP 443 gets there.
    param([string] $HostName, [switch] $SkipTcp)
    $res = Resolve-HostIps -HostName $HostName
    $tcp = [ordered]@{}
    if (-not $SkipTcp) { foreach ($ip in $res.Ips) { $tcp[$ip] = Test-TcpPort -Address $ip -Port 443 } }
    $proxy = $null
    try { $proxy = Get-SystemProxyFor -Uri ([Uri]"https://$HostName/") } catch { }
    return [pscustomobject]@{
        Host     = $HostName
        Ips      = $res.Ips
        CNames   = $res.CNames
        DnsIps   = $res.DnsIps
        HostsIps = $res.HostsIps
        Proxy    = $proxy
        Tcp      = $tcp
    }
}

function Format-HostConnectivity {
    param($Info, [string] $Label)
    if (-not $Info.Ips) { return ('{0,-3} {1} -> does not resolve on this machine' -f $Label, $Info.Host) }
    $notes = @()
    if ($Info.CNames -match 'privatelink') { $notes += 'privatelink CNAME present' }
    if ($Info.HostsIps) {
        $dns = if ($Info.DnsIps) { $Info.DnsIps -join ', ' } else { 'no address' }
        $notes += "from the HOSTS file; the DNS server answers $dns"
    }
    if ($Info.Proxy) { $notes += "requests go via proxy $($Info.Proxy.Authority)" }
    $line = '{0,-3} {1} -> {2}' -f $Label, $Info.Host, (Format-IpList $Info.Ips)
    if ($notes) { $line += ' (' + ($notes -join '; ') + ')' }
    if ($Info.Tcp.Count -gt 0) { $line += '  TCP443=' + (@($Info.Tcp.Values | Select-Object -Unique) -join '/') }
    return $line
}

function Get-ConnectivityVerdict {
    # What the facts from Get-HostConnectivity mean, as lines for the operator. Nothing when the path looks
    # healthy (private address, TCP 443 open, no proxy).
    param($Info)
    $h = $Info.Host
    if (-not $Info.Ips) {
        return "$h does not resolve on this machine. Check the DNS server it uses, and the spelling of any HOSTS entry (it must be this exact host name)."
    }
    $out = @()
    if ($Info.Proxy) {
        $out += "Requests to $h go through the proxy $($Info.Proxy.Authority), which looks the name up itself: this machine's DNS and HOSTS file do not decide where they go. Add $h to the proxy bypass list, or make sure the proxy can reach the app's private endpoint."
    }
    $results = @($Info.Tcp.Values)
    if ($results.Count -eq 0) { return $out }
    $where = $Info.Ips -join ', '
    $isPrivate = @($Info.Ips | Where-Object { (Get-IpClass $_) -eq 'private' }).Count -gt 0
    if ($results -contains 'Open') {
        if (-not $isPrivate) {
            $out += "$h resolves to the app's PUBLIC endpoint ($where). If the app has public network access disabled it answers HTTP 403 there, so point $h at the private endpoint IP instead: an A record in the privatelink.azurewebsites.net zone (the app and its .scm host each need one) that this machine's DNS can see, or a HOSTS entry for testing."
        }
    } elseif ($results -contains 'Timeout') {
        if ($isPrivate) {
            $out += "Nothing answered on TCP 443 at $where (private) within 5s. Name resolution is not the problem: this machine has no working network path to the private endpoint, and no DNS or HOSTS change will fix that."
            $out += "Deploy from a machine on the app's VNet (or a peered VNet, or on-premises over VPN/ExpressRoute), and check that no NSG, firewall or route table drops TCP 443 to that address and that the private endpoint connection is Approved."
        } else {
            $out += "Nothing answered on TCP 443 at $where (public) within 5s, so outbound HTTPS from this machine is blocked (firewall or proxy). If the app has public network access disabled you need the private endpoint path anyway."
        }
    } elseif ($results -contains 'Refused') {
        $out += "$where actively refused TCP 443. App Service always listens on 443, so check that this really is the app's private endpoint IP (a stale DNS record or HOSTS entry?) and whether a firewall is rejecting the connection."
    } elseif ($results -contains 'Unreachable') {
        $out += "This machine has no route to $where (host or network unreachable). It must be on, or routed to, the app's VNet."
    } else {
        $out += "TCP 443 to $where failed ($($results -join ', '))."
    }
    return $out
}

function Write-ScmAddress {
    # Where requests to the SCM host go from this machine. Cheap: no connection attempt. Purely informational,
    # so it must never stop a deployment.
    param([string] $ScmHost)
    try {
        $info = Get-HostConnectivity -HostName $ScmHost -SkipTcp
        if (-not $info.Ips) { Write-WarnMsg "SCM address    : $ScmHost does not resolve on this machine"; return }
        $addr = Format-IpList $info.Ips
        if ($info.HostsIps) { $addr += ' (from the HOSTS file)' }
        Write-Info "SCM address    : $addr"
        if ($info.Proxy) { Write-Info "Proxy          : $($info.Proxy.Authority) (it resolves the SCM host itself; HOSTS entries do not apply)" }
    } catch { Write-WarnMsg "Could not work out the SCM address: $($_.Exception.Message)" }
}

function Write-ScmConnectivityDiagnosis {
    # For an SCM check that got no HTTP response at all: is it DNS, a proxy, or the network path? Runs while an
    # error is being reported, so it must never throw and replace that error.
    param([string] $ScmHost)
    try {
        Write-Info 'No HTTP response from SCM; checking DNS, proxy and TCP 443 from this machine...'
        $info = Get-HostConnectivity -HostName $ScmHost
        Write-WarnMsg (Format-HostConnectivity -Info $info -Label 'SCM')
        $verdict = @(Get-ConnectivityVerdict -Info $info)
        if ($verdict.Count -eq 0) {
            $verdict = @("TCP 443 to $($info.Ips -join ', ') works from this machine, so the network path is fine and the failure is above TCP (TLS inspection or certificate, for example) - see the error below.")
        }
        foreach ($v in $verdict) { Write-WarnMsg $v }
    } catch { Write-WarnMsg "Could not diagnose the SCM connection: $($_.Exception.Message)" }
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

    $allHealthy = $true
    $targets = @(
        [pscustomobject]@{ Label = 'App'; Host = $mainHost }
        [pscustomobject]@{ Label = 'SCM'; Host = $ScmHost }
    )
    foreach ($t in $targets) {
        $info = Get-HostConnectivity -HostName $t.Host
        $verdict = @(Get-ConnectivityVerdict -Info $info)
        $line = Format-HostConnectivity -Info $info -Label $t.Label
        if ($verdict.Count -eq 0) { Write-Ok $line } else { Write-WarnMsg $line; $allHealthy = $false }
        foreach ($v in $verdict) { Write-Info "       -> $v" }
    }
    if ($allHealthy) { Write-Ok 'Both hostnames resolve to private IPs and accept TCP 443 (private-endpoint path).' }

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
    Write-ScmAddress -ScmHost $auth.ScmHost
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
                -TimeoutMinutes $DbUpgradeTimeoutMin `
                -OrgUrls        $OrgUrls
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
