<#
.SYNOPSIS
Starts the runner's LocalDB instance before the .NET tests, and proves that it answers a query.

.DESCRIPTION
Every SQL test connects to (localdb)\MSSQLLocalDB, and LocalDB creates and starts that instance on the first
connection. On the hosted Windows runners that first start is slow, and now and then it fails: "Cannot create an
automatic instance", then "Error occurred during LocalDB instance startup: SQL Server process failed to start" for
every connection after it (actions/runner-images#8164). A shard that hit it failed every SQL test, one connection
attempt at a time; one of them ran on until the job was cancelled at 45 minutes.

This starts the instance before the tests and checks it with a query. When either fails, it prints LocalDB's error
log and the Application event log entries, then, with -Recreate, deletes the instance and creates it again, up to
-Attempts times. If the instance never answers, the step fails within minutes, saying so.

Some runner VMs fail in a way no retry can fix (#855). Their disks report a physical sector size above 4 KB, which no
version of SQL Server supports, LocalDB included: master doesn't start, and LocalDB's error log says "There have been
256 misaligned log IOs which required falling back to synchronous IO", then error 5173. So before it starts anything,
this prints the sector size of each volume LocalDB writes to, and of the workspace. When a volume LocalDB writes to
reports more than 4096 bytes, it moves LocalDB's files to a virtual disk, which reports the sector sizes it was created
with, not those of the disk that holds it (see Move-LocalDbFiles). That changes the machine, so it needs -Recreate, an
elevated administrator and a GitHub-hosted runner, which is thrown away after the job. Without them the step stops at
once, saying why. Set LOCALDB_FORCE_RELOCATION=true to move the files on a runner that doesn't need it, to test the
move.

Without -Recreate nothing is deleted, so running this on a workstation can't remove an instance that holds databases.
#>
[CmdletBinding()]
param(
    [string]$Instance = 'MSSQLLocalDB',
    [ValidateRange(1, 10)][int]$Attempts = 3,
    [ValidateRange(30, 1800)][int]$StartTimeoutSeconds = 300,
    [ValidateRange(5, 600)][int]$QueryTimeoutSeconds = 60,
    [switch]$Recreate,
    # Folders the tests create database files in by path, rather than in LocalDB's default location. They move to the
    # virtual disk with the rest.
    [string[]]$DatabaseFolder = @()
)
$ErrorActionPreference = 'Stop'

$script:LargestSupportedSector = 4096
$script:SectorSizeArticle = 'https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/database-file-operations/troubleshoot-os-4kb-disk-sector-size'

# What `fsutil fsinfo sectorinfo` reports, read through a handle to the path itself, so it describes the volume that
# really holds the path, after every junction and mount point. Compiled on first use, because the CI helper tests
# load this script on Linux.
$script:VolumeProbe = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class LocalDbVolumeProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStorageInfo
    {
        public uint LogicalBytesPerSector;
        public uint PhysicalBytesPerSectorForAtomicity;
        public uint PhysicalBytesPerSectorForPerformance;
        public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
        public uint Flags;
        public uint ByteOffsetForSectorAlignment;
        public uint ByteOffsetForPartitionAlignment;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileStorageInfo info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    // LogicalBytesPerSector, PhysicalBytesPerSectorForAtomicity and PhysicalBytesPerSectorForPerformance.
    public static uint[] Read(string path, out string resolved)
    {
        // FILE_READ_ATTRIBUTES, any sharing, OPEN_EXISTING, and FILE_FLAG_BACKUP_SEMANTICS so that a folder opens too.
        using (var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
        {
            if (handle.IsInvalid) throw new Win32Exception();
            FileStorageInfo info;
            if (!GetFileInformationByHandleEx(handle, 16 /* FileStorageInfo */, out info, (uint)Marshal.SizeOf(typeof(FileStorageInfo))))
                throw new Win32Exception();
            var text = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
            resolved = length == 0 || length >= text.Capacity ? path : text.ToString().Replace(@"\\?\", "");
            return new[] { info.LogicalBytesPerSector, info.PhysicalBytesPerSectorForAtomicity, info.PhysicalBytesPerSectorForPerformance };
        }
    }
}
'@

function Find-SqlLocalDb {
    $command = Get-Command SqlLocalDB.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $installed = Get-ChildItem "$env:ProgramFiles\Microsoft SQL Server\*\Tools\Binn\SqlLocalDB.exe" -ErrorAction SilentlyContinue |
        Sort-Object { $_.Directory.Parent.Parent.Name -as [int] } -Descending |
        Select-Object -First 1
    if ($installed) { return $installed.FullName }
    throw 'SqlLocalDB.exe was not found, so LocalDB is not installed on this machine.'
}

# Runs SqlLocalDB.exe and echoes what it says. Its exit code isn't trusted - it can exit 0 after printing an error - so
# a query, not this, decides whether the instance is up.
function Invoke-SqlLocalDb([string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $info = [Diagnostics.ProcessStartInfo]::new($script:SqlLocalDb)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            Write-Host "SqlLocalDB $($Arguments -join ' ') did not finish within $TimeoutSeconds seconds, so it was stopped."
            return
        }
        $text = ($output.Result + $errors.Result).Trim()
        if ($text) { Write-Host $text }
    }
    finally { $process.Dispose() }
}

# Runs a batch on master and returns its rows, each an object[].
function Invoke-LocalDbQuery([string]$Sql) {
    # A string, not SqlConnectionStringBuilder: PowerShell turns a property set on that dictionary into an unknown key.
    $connection = [Data.SqlClient.SqlConnection]::new(
        "Data Source=(localdb)\$Instance;Initial Catalog=master;Integrated Security=true;Pooling=false;Connect Timeout=$QueryTimeoutSeconds")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = $QueryTimeoutSeconds
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        $rows = [Collections.Generic.List[object[]]]::new()
        while ($reader.Read()) {
            $row = [object[]]::new($reader.FieldCount)
            [void]$reader.GetValues($row)
            $rows.Add($row)
        }
        $reader.Dispose()
        return , $rows
    }
    finally { $connection.Dispose() }
}

function Test-LocalDb {
    $rows = Invoke-LocalDbQuery "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) + N' ' + CONVERT(nvarchar(128), SERVERPROPERTY('Edition'));"
    return [string]$rows[0][0]
}

function Get-InstancesFolder { Join-Path $env:LOCALAPPDATA 'Microsoft\Microsoft SQL Server Local DB\Instances' }

function Write-Diagnostics([datetime]$Since) {
    $log = Join-Path (Get-InstancesFolder) "$Instance\error.log"
    if (Test-Path -LiteralPath $log) {
        Write-Host "::group::Last lines of the instance's error.log"
        Get-Content -LiteralPath $log -Tail 80 | ForEach-Object { Write-Host $_ }
        Write-Host '::endgroup::'
    }
    else { Write-Host "The instance has no error.log yet ($log)." }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $Since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'LocalDB|MSSQL' } | Select-Object -First 20)
    if ($events.Count -gt 0) {
        Write-Host '::group::LocalDB entries in the Application event log'
        foreach ($entry in $events) {
            Write-Host ('{0:u} {1} {2}: {3}' -f $entry.TimeCreated, $entry.ProviderName, $entry.Id, ($entry.Message -replace '\s+', ' '))
        }
        Write-Host '::endgroup::'
    }
}

function Reset-LocalDbInstance {
    Write-Host "Deleting LocalDB instance '$Instance' so that the next attempt starts a new one."
    Invoke-SqlLocalDb @('stop', $Instance, '-k')
    Invoke-SqlLocalDb @('delete', $Instance)
    $directory = Join-Path (Get-InstancesFolder) $Instance
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    # 'start' creates the automatic instance by itself; any other instance has to be created again.
    if ($Instance -ne 'MSSQLLocalDB') { Invoke-SqlLocalDb @('create', $Instance) }
}

# SQL Server's sector size for a volume, as Learn defines it: the larger of the two physical sizes Windows reports.
function Get-SectorSize([long]$Atomicity, [long]$Performance) { [Math]::Max($Atomicity, $Performance) }

# A location, and the sectors of the volume that really holds it. SectorSize is 0 when they couldn't be read.
function Read-Volume([string]$Label, [string]$Path) {
    $volume = [pscustomobject]@{ Label = $Label; Path = $Path; Resolved = $Path; Logical = 0; Atomicity = 0; Performance = 0; SectorSize = 0; Error = '' }
    try {
        if (-not ('LocalDbVolumeProbe' -as [type])) { Add-Type -TypeDefinition $script:VolumeProbe }
        # A new runner has no instance folder yet: it will be created in the nearest folder that exists.
        $existing = $Path
        while ($existing -and -not (Test-Path -LiteralPath $existing)) { $existing = Split-Path -Parent $existing }
        $resolved = $null
        $sizes = [LocalDbVolumeProbe]::Read($existing, [ref]$resolved)
        $volume.Resolved = if ($existing -eq $Path) { $resolved } else { Join-Path $resolved $Path.Substring($existing.Length) }
        $volume.Logical, $volume.Atomicity, $volume.Performance = $sizes
        $volume.SectorSize = Get-SectorSize $sizes[1] $sizes[2]
    }
    catch { $volume.Error = $_.Exception.Message }
    return $volume
}

# Every location LocalDB writes to.
function Get-LocalDbVolumes {
    # LocalDB creates a database that names no files in the user's profile folder, unless Move-LocalDbFiles moved it.
    $databases = if ($script:DatabaseHome) { $script:DatabaseHome } else { $env:USERPROFILE }
    @(
        Read-Volume 'LocalDB instance' (Join-Path (Get-InstancesFolder) $Instance)
        Read-Volume 'Default databases' $databases
        foreach ($folder in $DatabaseFolder) { Read-Volume 'Database folder' ([IO.Path]::GetFullPath($folder)) }
    )
}

function Write-VolumeReport([string]$Title, [object[]]$Volumes) {
    Write-Host "${Title}: sector sizes - the larger of PhysicalBytesPerSectorForAtomicity and PhysicalBytesPerSectorForPerformance, as 'fsutil fsinfo sectorinfo' reports them. SQL Server supports 512 and $script:LargestSupportedSector bytes."
    foreach ($volume in $Volumes) {
        $where = if ($volume.Resolved -ne $volume.Path) { "$($volume.Path) -> $($volume.Resolved)" } else { $volume.Path }
        if ($volume.Error) { Write-Host ('  {0,-22} unknown: {1} ({2})' -f $volume.Label, $where, $volume.Error); continue }
        $verdict = if ($volume.SectorSize -gt $script:LargestSupportedSector) { 'UNSUPPORTED' } else { 'supported' }
        Write-Host ('  {0,-22} {1,6} bytes, {2}: {3} (logical {4}, atomicity {5}, performance {6})' -f $volume.Label, $volume.SectorSize,
            $verdict, $where, $volume.Logical, $volume.Atomicity, $volume.Performance)
    }
}

function Format-SectorSizeFailure([object[]]$Unsupported, [string]$Reason) {
    $volumes = ($Unsupported | ForEach-Object { "$($_.Path) is on a volume whose sector size is $($_.SectorSize) bytes" }) -join '; '
    "LocalDB can't run on this runner VM: $volumes. SQL Server, LocalDB included, supports only 512- and " +
    "$script:LargestSupportedSector-byte sectors, so starting it again can't help: $script:SectorSizeArticle. $($Reason.TrimEnd('.')). " +
    'Re-run the job: another runner VM will usually have a supported disk.'
}

# 'Start' as usual, or 'Move' LocalDB's files to a virtual disk first. When they have to move and can't, this throws at
# once: no number of attempts starts LocalDB on a volume whose sector size it doesn't support.
function Select-LocalDbPlan([object[]]$Volumes, [bool]$Force, [string]$MoveBlocker) {
    $unsupported = @($Volumes | Where-Object { $_.SectorSize -gt $script:LargestSupportedSector })
    if ($unsupported.Count -eq 0 -and -not $Force) { return 'Start' }
    if (-not $MoveBlocker) { return 'Move' }
    if ($unsupported.Count -eq 0) { throw "LOCALDB_FORCE_RELOCATION is set, but LocalDB's files can't be moved to a virtual disk here: $MoveBlocker." }
    throw (Format-SectorSizeFailure $unsupported "Its files can't be moved to a virtual disk here instead: $MoveBlocker")
}

function Get-MoveBlocker {
    if (-not $Recreate) { return 'moving them deletes the instance, and -Recreate was not given' }
    if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        return "moving them attaches a virtual disk and briefly repoints the user's profile folder, which is only done on a GitHub-hosted runner, thrown away after the job"
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { return 'attaching a virtual disk needs an elevated administrator' }
    return ''
}

function Invoke-Diskpart([string[]]$Commands) {
    $commandFile = Join-Path ([IO.Path]::GetTempPath()) "localdb-diskpart-$([Guid]::NewGuid().ToString('N')).txt"
    [IO.File]::WriteAllLines($commandFile, $Commands)
    try {
        $output = & diskpart.exe /s $commandFile 2>&1
        if ($LASTEXITCODE -ne 0) { throw "diskpart failed with exit code ${LASTEXITCODE}: $(($output | Where-Object { $_ -match '\S' }) -join ' ')" }
    }
    finally { Remove-Item -LiteralPath $commandFile -Force }
}

<#
Puts everything LocalDB writes on a new virtual disk. A virtual disk reports the sector sizes it was created with - a
VHDX 512 bytes logical and 4096 physical - not those of the disk its file is on. If one ever didn't, the check after
the move would stop the step.
- The instances. LocalDB keeps each instance's folder, with its system databases, under %LOCALAPPDATA%. That folder
  becomes a junction to the disk. LocalDB records the path the junction resolves to, so SQL Server opens master and
  the rest on the disk itself.
- The default database location. LocalDB creates a database that names no files in the user's profile folder, as the
  registry gives it when the instance starts, and it ignores the DefaultData setting. So Start-LocalDbInstance points
  the user's ProfileImagePath at the disk while the instance starts.
- Each -DatabaseFolder, moved to the disk and replaced by a junction.
SQL Server runs without the administrator's rights, so it can't open a file that only Administrators may write. The
runner's user gets full control of the disk, as it has of its profile folder.
#>
function Move-LocalDbFiles {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-SqlLocalDb @('stop', $Instance, '-k')
    Invoke-SqlLocalDb @('delete', $Instance)
    $instances = Get-InstancesFolder
    $others = @(Get-ChildItem -LiteralPath $instances -Force -ErrorAction SilentlyContinue | Where-Object Name -ne $Instance)
    if ($others.Count -gt 0) { throw "$instances holds other LocalDB instances ($($others.Name -join ', ')), and moving it would lose them." }

    $letter = [char[]]'ZYXWVUTSRQPONMLKJIHGFE' | Where-Object { -not (Test-Path -LiteralPath "${_}:\") } | Select-Object -First 1
    if (-not $letter) { throw 'No drive letter is free for the virtual disk.' }
    $temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $file = Join-Path $temp "localdb-$([Guid]::NewGuid().ToString('N')).vhdx"
    # Expandable, so the file only takes the space the databases use.
    Invoke-Diskpart @("create vdisk file=`"$file`" maximum=65536 type=expandable", "select vdisk file=`"$file`"", 'attach vdisk',
        'create partition primary', 'format fs=ntfs quick label=LocalDB', "assign letter=$letter")
    $root = "${letter}:\"
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $output = & icacls.exe $root /grant "*${user}:(OI)(CI)F" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not give the runner's user full control of ${root}: $output" }

    if (Test-Path -LiteralPath $instances) { Remove-Item -LiteralPath $instances -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $instances), "${root}Instances", "${root}Databases" | Out-Null
    New-Item -ItemType Junction -Path $instances -Target "${root}Instances" | Out-Null
    $script:DatabaseHome = "${root}Databases"

    for ($index = 0; $index -lt $DatabaseFolder.Count; $index++) {
        $source = [IO.Path]::GetFullPath($DatabaseFolder[$index])
        $target = "${root}Folders\$index"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        if (Test-Path -LiteralPath $source) {
            $output = & robocopy.exe $source $target /E /MOVE /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NP
            if ($LASTEXITCODE -ge 8) { throw "Could not move $source to the virtual disk: robocopy exited with ${LASTEXITCODE}: $(($output | Where-Object { $_ -match '\S' }) -join ' ')" }
            # robocopy exits 1 when it has copied files. Left alone, that would become the step's exit code.
            $global:LASTEXITCODE = 0
            if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
        }
        else { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $source) | Out-Null }
        New-Item -ItemType Junction -Path $source -Target $target | Out-Null
    }
    Write-Host ("Moved LocalDB's files to virtual disk {0} ({1}) in {2:N1} seconds." -f $root, $file, $watch.Elapsed.TotalSeconds)
}

function Start-LocalDbInstance {
    if (-not $script:DatabaseHome) {
        Invoke-SqlLocalDb @('start', $Instance) $StartTimeoutSeconds
        return Test-LocalDb
    }
    # LocalDB reads the profile folder, its default database location, when the instance starts. Put the registry back
    # as soon as the instance answers, because the tests read the profile folder too.
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$user", $true)
    if (-not $key) { throw "The registry has no profile for the runner's user ($user)." }
    $kind = $key.GetValueKind('ProfileImagePath')
    $original = $key.GetValue('ProfileImagePath', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    try {
        $key.SetValue('ProfileImagePath', $script:DatabaseHome, $kind)
        Invoke-SqlLocalDb @('start', $Instance) $StartTimeoutSeconds
        $version = Test-LocalDb
    }
    finally {
        $key.SetValue('ProfileImagePath', $original, $kind)
        $key.Dispose()
    }
    # An idle instance stops after 'user instance timeout' minutes, 5 by default, and the next connection would start
    # it again with the default database location back in the profile folder. Never let it stop by itself.
    [void](Invoke-LocalDbQuery "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'user instance timeout', 65535; RECONFIGURE;")
    return $version
}

# Where the instance keeps its files. After a move, a file that isn't on the virtual disk, or whose sectors can't be
# read or aren't supported, means the move didn't work, so the step fails here rather than in the tests.
function Assert-LocalDbFiles {
    $rows = Invoke-LocalDbQuery @"
SELECT N'Default data path', CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultDataPath'))
UNION ALL SELECT N'Default log path', CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultLogPath'))
UNION ALL SELECT DB_NAME(database_id) + N' ' + LOWER(type_desc), physical_name FROM sys.master_files;
"@
    $files = @($rows | ForEach-Object { Read-Volume $_[0] $_[1] })
    Write-VolumeReport "Files of LocalDB instance '$Instance'" $files
    if (-not $script:DatabaseHome) { return }
    $disk = $script:DatabaseHome.Substring(0, 3)
    $misplaced = @($files | Where-Object {
            $_.Error -or $_.SectorSize -gt $script:LargestSupportedSector -or -not $_.Resolved.StartsWith($disk, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($misplaced.Count -gt 0) {
        throw "LocalDB's files were moved to virtual disk $disk, but these aren't on it, or aren't on a supported volume: $(($misplaced | ForEach-Object { "$($_.Label) $($_.Path)" }) -join ', ')."
    }
}

# The CI helper tests dot-source this script for the functions above; only a run starts LocalDB.
if ($MyInvocation.InvocationName -eq '.') { return }

$script:SqlLocalDb = Find-SqlLocalDb
Write-Host "Using $script:SqlLocalDb. Installed LocalDB versions:"
Invoke-SqlLocalDb @('versions')

$volumes = @(Get-LocalDbVolumes)
$workspace = if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { (Get-Location).Path }
Write-VolumeReport 'Where LocalDB will write' ($volumes + (Read-Volume 'Workspace' $workspace))
$force = $env:LOCALDB_FORCE_RELOCATION -eq 'true'
if ((Select-LocalDbPlan $volumes $force (Get-MoveBlocker)) -eq 'Move') {
    $unsupported = @($volumes | Where-Object { $_.SectorSize -gt $script:LargestSupportedSector })
    if ($unsupported.Count -gt 0) { Write-Host "Moving LocalDB's files to a virtual disk, because SQL Server doesn't support the sector size of $($unsupported.Path -join ', ')." }
    else { Write-Host "Moving LocalDB's files to a virtual disk, because LOCALDB_FORCE_RELOCATION is set." }
    try { Move-LocalDbFiles }
    catch {
        if ($unsupported.Count -gt 0) { throw (Format-SectorSizeFailure $unsupported "Moving its files to a virtual disk failed: $($_.Exception.Message)") }
        throw
    }
    $volumes = @(Get-LocalDbVolumes)
    Write-VolumeReport 'Where LocalDB will write now' $volumes
    $unsupported = @($volumes | Where-Object { $_.SectorSize -gt $script:LargestSupportedSector })
    if ($unsupported.Count -gt 0) { throw (Format-SectorSizeFailure $unsupported "Its files were moved to a virtual disk, but that didn't help") }
}

$started = $false
for ($attempt = 1; $attempt -le $Attempts -and -not $started; $attempt++) {
    $since = Get-Date
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "Starting LocalDB instance '$Instance', attempt $attempt of $Attempts..."
    try {
        $version = Start-LocalDbInstance
        Write-Host ("LocalDB instance '{0}' answered after {1:N0} seconds: SQL Server {2}." -f $Instance, $watch.Elapsed.TotalSeconds, $version)
        $started = $true
        continue
    }
    catch { Write-Host "LocalDB instance '$Instance' did not answer a query: $($_.Exception.Message)" }
    Write-Diagnostics $since
    if ($attempt -lt $Attempts) {
        if ($Recreate) { Reset-LocalDbInstance }
        else { Write-Host 'Trying again without deleting the instance, because -Recreate was not given.' }
    }
}
if (-not $started) {
    throw "LocalDB instance '$Instance' did not start after $Attempts attempts, so the SQL tests can't run. Re-run the job; LocalDB's own log is above."
}
Invoke-SqlLocalDb @('info', $Instance)
Assert-LocalDbFiles
