<#
.SYNOPSIS
    Measures the DLP page's Copilot governance queries (#648) at the scale of a large tenant.

.DESCRIPTION
    The governance section reads four of the largest Copilot tables - copilot_chats,
    copilot_event_messages, copilot_event_accessed_resources and the model / plugin junctions - over
    the page's reporting window. It ships without a schema change, so the question is not "does an
    index help" but "is the query cheap enough on the indexes customers already have". This script
    answers it the way the repository's rules for schema changes ask:

      * the REAL statements, read out of Web/Controllers/DlpAPIController.cs (every
        `internal const string Governance...Sql`), so the measurement cannot drift from the code;
      * at a narrow and a wide window (28 and 90 days by default);
      * logical reads AND elapsed time, as the median of several warm runs, the first (cold) run of
        each set discarded, and every statement run with OPTION (RECOMPILE) - the shipped statements
        carry it themselves;
      * the physical operators used against each table, from the actual execution plan, so the
        result says WHY it costs what it costs (a seek per interaction, or one scan of the table).

    The issue's prototype query is measured beside the shipped statements as a reference point.

    The fixture lives in its own schema ([govbench]) of the database you name, so it can share a
    development catalog without touching dbo, and it is dropped at the end unless -KeepFixture is set.
    The statements are rewritten from "dbo." to "govbench." to read it.

.PARAMETER DatabaseName
    An existing LocalDB / SQL Server database to build the fixture in. Use a development catalog of
    your own, never a customer database.

.PARAMETER SyntheticUsers
    People in the synthetic tenant.

.PARAMETER SyntheticInteractions
    copilot_chats rows, spread across SyntheticDays. Detail rows are generated per interaction (see
    CopilotGovernanceBenchmark.sql for the shape).

.PARAMETER SyntheticDays
    Days of history. Keep it well beyond the widest window, so a narrow window is genuinely selective.

.PARAMETER FlagDays
    Recent days that carry the jailbreak / XPIA flags. Older rows keep NULL, as on a tenant whose
    history predates the import of those flags.

.PARAMETER CandidatesPath
    Optional .sql file of alternative query shapes to measure beside the shipped ones, each introduced
    by a line "-- statement: <Name>". Use it to compare a rewrite against the same fixture before
    changing the controller.

.PARAMETER Only
    Optional regular expression: measure only the statements whose name matches it.

.EXAMPLE
    .\Invoke-CopilotGovernanceBenchmark.ps1 -DatabaseName MyDevCatalog

.EXAMPLE
    .\Invoke-CopilotGovernanceBenchmark.ps1 -DatabaseName MyDevCatalog -SkipFixture -KeepFixture -Repeats 6

.EXAMPLE
    .\Invoke-CopilotGovernanceBenchmark.ps1 -DatabaseName MyDevCatalog -SkipFixture -KeepFixture -CandidatesPath .\shapes.sql -Only 'Signals'

.NOTES
    Never paste real names, row counts or plans into a PR or a release note. The fixture is synthetic
    and its sizes are the only counts this script reports.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,
    [int]$SyntheticUsers = 200000,
    [int]$SyntheticInteractions = 4000000,
    [int]$SyntheticDays = 180,
    [int]$FlagDays = 60,
    [int[]]$WindowDays = @(28, 90),
    [int]$Repeats = 5,
    [string]$SqlcmdPath = "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE",
    [string]$LocalDbInstance = "(localdb)\MSSQLLocalDB",
    [string]$CandidatesPath,
    [string]$Only,
    [switch]$SkipFixture,
    [switch]$KeepFixture
)

$ErrorActionPreference = "Stop"

if ($DatabaseName -notmatch '^[A-Za-z0-9_]+$') {
    throw "Use a simple development database name containing only letters, numbers and underscores."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$fixturePath = Join-Path $scriptRoot "CopilotGovernanceBenchmark.sql"
$controllerPath = Join-Path $scriptRoot "..\Web\Controllers\DlpAPIController.cs"
$artifactRoot = Join-Path $scriptRoot "artifacts"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$connectionString = "Data Source=$LocalDbInstance;Initial Catalog=$DatabaseName;Integrated Security=true;TrustServerCertificate=True;Connect Timeout=60"

# ------------------------------------------------------------------------------------------------
# The measured statements: the shipped ones, read from the controller, plus the issue's prototype.
# ------------------------------------------------------------------------------------------------

function Get-ShippedStatements {
    $source = [System.IO.File]::ReadAllText((Resolve-Path $controllerPath))
    $pattern = 'const\s+string\s+(Governance\w*Sql)\s*=\s*@"((?:[^"]|"")*)"'
    $found = [regex]::Matches($source, $pattern)
    if ($found.Count -eq 0) {
        throw "No 'const string Governance...Sql' statements found in $controllerPath."
    }

    foreach ($match in $found) {
        [pscustomobject]@{
            Key = $match.Groups[1].Value
            Sql = $match.Groups[2].Value.Replace('""', '"')
        }
    }
}

# The prototype from the issue, unchanged apart from the window parameters. It counts flagged
# interactions only - it has no denominator - so it is a reference for cost, not for the figures.
$prototype = [pscustomobject]@{
    Key = 'IssuePrototype'
    Sql = @"
SELECT
    (SELECT COUNT_BIG(*) FROM dbo.copilot_chats AS c WHERE c.time_stamp >= @from AND c.time_stamp <= @to) AS Interactions,
    (SELECT COUNT_BIG(DISTINCT m.copilot_chat_id)
       FROM dbo.copilot_event_messages AS m
       JOIN dbo.copilot_chats AS c ON c.event_id = m.copilot_chat_id
      WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND m.jailbreak_detected = 1) AS InteractionsWithJailbreakFlag,
    (SELECT COUNT_BIG(DISTINCT r.copilot_chat_id)
       FROM dbo.copilot_event_accessed_resources AS r
       JOIN dbo.copilot_chats AS c ON c.event_id = r.copilot_chat_id
      WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND r.xpia_detected = 1) AS InteractionsWithXpiaFlag
OPTION (RECOMPILE);
"@
}

function Get-CandidateStatements {
    param([string]$Path)
    if (-not $Path) { return @() }

    $text = [System.IO.File]::ReadAllText((Resolve-Path $Path))
    $parts = [regex]::Split($text, '(?m)^--\s*statement:\s*(\S+)\s*$')
    # Split keeps the captured names: [preamble, name1, sql1, name2, sql2, ...]
    for ($i = 1; $i -lt $parts.Count; $i += 2) {
        [pscustomobject]@{ Key = $parts[$i]; Sql = $parts[$i + 1].Trim() }
    }
}

function ConvertTo-FixtureSql {
    param([string]$Sql)
    # Scope markers are comments, so an unfiltered run leaves them alone, exactly as ReportScopeSql
    # does for a reader with no administrator's filter.
    return [regex]::Replace($Sql, '\bdbo\.', 'govbench.')
}

# ------------------------------------------------------------------------------------------------
# Plumbing
# ------------------------------------------------------------------------------------------------

function New-BenchConnection {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()
    return $connection
}

function Invoke-NonQuery {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql)
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 0
    [void]$command.ExecuteNonQuery()
    $command.Dispose()
}

function New-WindowCommand {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql, [datetime]$From, [datetime]$To)
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 0
    # The controller sends DateTime parameters, which SqlClient types as datetime - the column's type.
    $command.Parameters.Add("@from", [System.Data.SqlDbType]::DateTime).Value = $From
    $command.Parameters.Add("@to", [System.Data.SqlDbType]::DateTime).Value = $To
    return $command
}

<#
    One run with STATISTICS IO/TIME on: total logical and physical reads (summed across every table
    the statement touched - a statement that reads less from one table by reading far more from
    another has not improved), the reads per table, and the elapsed time.
#>
function Measure-Statement {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql, [datetime]$From, [datetime]$To)

    $messages = New-Object System.Collections.ArrayList
    $handler = [System.Data.SqlClient.SqlInfoMessageEventHandler] {
        param($sender, $e)
        foreach ($err in $e.Errors) { [void]$messages.Add($err.Message) }
    }
    $Connection.add_InfoMessage($handler)

    try {
        Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS IO ON; SET STATISTICS TIME ON;"
        $command = New-WindowCommand -Connection $Connection -Sql $Sql -From $From -To $To
        $reader = $command.ExecuteReader()
        do { while ($reader.Read()) { } } while ($reader.NextResult())
        $reader.Close()
        $command.Dispose()
        Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;"
    }
    finally {
        $Connection.remove_InfoMessage($handler)
    }

    $reads = 0
    $physical = 0
    $elapsed = 0
    $perTable = @{}
    foreach ($message in $messages) {
        foreach ($match in [regex]::Matches($message, "Table '([^']+)'\. Scan count \d+, logical reads (\d+), physical reads (\d+)")) {
            $table = $match.Groups[1].Value
            $value = [int64]$match.Groups[2].Value
            $reads += $value
            $physical += [int64]$match.Groups[3].Value
            if ($perTable.ContainsKey($table)) { $perTable[$table] += $value } else { $perTable[$table] = $value }
        }
        # MAX, not last: the SET ... OFF statement reports its own (zero) elapsed time while TIME is on.
        foreach ($match in [regex]::Matches($message, 'elapsed time = (\d+) ms')) {
            $value = [int]$match.Groups[1].Value
            if ($value -gt $elapsed) { $elapsed = $value }
        }
    }

    return [pscustomobject]@{ Reads = $reads; PhysicalReads = $physical; ElapsedMs = $elapsed; PerTable = $perTable }
}

<# The physical operators used against each table, from the actual execution plan. #>
function Get-PlanOperators {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql, [datetime]$From, [datetime]$To)

    Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS XML ON;"
    $plan = ""
    try {
        $command = New-WindowCommand -Connection $Connection -Sql $Sql -From $From -To $To
        $reader = $command.ExecuteReader()
        do {
            while ($reader.Read()) {
                $value = $reader.GetValue(0)
                if ($value -is [string] -and $value -like '*ShowPlanXML*') { $plan = $value }
            }
        } while ($reader.NextResult())
        $reader.Close()
        $command.Dispose()
    }
    finally {
        Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS XML OFF;"
    }

    if (-not $plan) { return "(plan unavailable)" }

    $xml = [xml]$plan
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace("p", "http://schemas.microsoft.com/sqlserver/2004/07/showplan")

    $operators = New-Object System.Collections.Generic.List[string]
    foreach ($relOp in $xml.SelectNodes("//p:RelOp", $ns)) {
        $object = $relOp.SelectSingleNode("./*/p:Object", $ns)
        if ($object -eq $null) { continue }
        $table = $object.GetAttribute("Table").Trim('[', ']')
        $index = $object.GetAttribute("Index").Trim('[', ']')
        $entry = "$($relOp.GetAttribute('PhysicalOp')) $table" + $(if ($index) { ".$index" } else { "" })
        if (-not $operators.Contains($entry)) { $operators.Add($entry) }
    }

    $joins = New-Object System.Collections.Generic.List[string]
    foreach ($relOp in $xml.SelectNodes("//p:RelOp[@PhysicalOp='Hash Match' or @PhysicalOp='Nested Loops' or @PhysicalOp='Merge Join' or @PhysicalOp='Adaptive Join']", $ns)) {
        $entry = "$($relOp.GetAttribute('PhysicalOp')) ($($relOp.GetAttribute('LogicalOp')))"
        if (-not $joins.Contains($entry)) { $joins.Add($entry) }
    }

    $parallel = if ($xml.SelectSingleNode("//p:QueryPlan[@DegreeOfParallelism > 1]", $ns)) { "parallel" } else { "serial" }
    return (($operators -join '; ') + " | " + ($joins -join ', ') + " | " + $parallel)
}

function Get-Median {
    param([double[]]$Values)
    $sorted = $Values | Sort-Object
    $count = $sorted.Count
    if ($count -eq 0) { return 0 }
    if ($count % 2 -eq 1) { return $sorted[[int](($count - 1) / 2)] }
    return ($sorted[$count / 2 - 1] + $sorted[$count / 2]) / 2
}

<# Median over $Repeats warm runs; run 0 is the cold run and is discarded. #>
function Measure-Case {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql, [datetime]$From, [datetime]$To)

    $samples = @()
    for ($run = 0; $run -le $Repeats; $run++) {
        $sample = Measure-Statement -Connection $Connection -Sql $Sql -From $From -To $To
        if ($run -eq 0) { continue }
        $samples += $sample
    }

    $perTable = @{}
    foreach ($table in ($samples | ForEach-Object { $_.PerTable.Keys } | Sort-Object -Unique)) {
        $perTable[$table] = Get-Median -Values ($samples | ForEach-Object { if ($_.PerTable.ContainsKey($table)) { $_.PerTable[$table] } else { 0 } })
    }

    return [pscustomobject]@{
        Reads         = Get-Median -Values ($samples | ForEach-Object { $_.Reads })
        PhysicalReads = Get-Median -Values ($samples | ForEach-Object { $_.PhysicalReads })
        ElapsedMs     = Get-Median -Values ($samples | ForEach-Object { $_.ElapsedMs })
        MinMs         = ($samples | ForEach-Object { $_.ElapsedMs } | Measure-Object -Minimum).Minimum
        MaxMs         = ($samples | ForEach-Object { $_.ElapsedMs } | Measure-Object -Maximum).Maximum
        PerTable      = $perTable
    }
}

# ------------------------------------------------------------------------------------------------
# Fixture
# ------------------------------------------------------------------------------------------------

if (-not $SkipFixture) {
    if (-not (Test-Path $SqlcmdPath)) {
        throw "sqlcmd was not found at '$SqlcmdPath'. Pass -SqlcmdPath if it is installed elsewhere."
    }

    Write-Host "Seeding the [govbench] fixture in [$DatabaseName] (users=$SyntheticUsers, interactions=$SyntheticInteractions, days=$SyntheticDays, flagDays=$FlagDays)..."
    $fixtureLog = Join-Path $artifactRoot "copilot-governance-fixture.txt"
    & $SqlcmdPath -S $LocalDbInstance -d $DatabaseName -b -i $fixturePath -W -s "|" `
        -v SyntheticUsers="$SyntheticUsers" SyntheticInteractions="$SyntheticInteractions" SyntheticDays="$SyntheticDays" FlagDays="$FlagDays" `
        -o $fixtureLog
    if ($LASTEXITCODE -ne 0) { Get-Content $fixtureLog -Tail 40; throw "Fixture seeding failed." }
    Get-Content $fixtureLog -Tail 20
}

# ------------------------------------------------------------------------------------------------
# Measure
# ------------------------------------------------------------------------------------------------

$statements = @(Get-ShippedStatements) + @($prototype) + @(Get-CandidateStatements -Path $CandidatesPath)
if ($Only) {
    $statements = @($statements | Where-Object { $_.Key -match $Only })
}
$connection = New-BenchConnection
$results = @()

try {
    # The controller's window: from midnight N days ago to now.
    $to = [DateTime]::UtcNow
    foreach ($statement in $statements) {
        $sql = ConvertTo-FixtureSql -Sql $statement.Sql
        foreach ($days in $WindowDays) {
            $from = $to.Date.AddDays(-$days)
            Write-Host "Measuring $($statement.Key) over $days days..."
            $metrics = Measure-Case -Connection $connection -Sql $sql -From $from -To $to
            $operators = Get-PlanOperators -Connection $connection -Sql $sql -From $from -To $to
            $results += [pscustomobject]@{
                Statement     = $statement.Key
                WindowDays    = $days
                LogicalReads  = [int64]$metrics.Reads
                PhysicalReads = [int64]$metrics.PhysicalReads
                MedianMs      = $metrics.ElapsedMs
                RangeMs       = "$($metrics.MinMs)-$($metrics.MaxMs)"
                ReadsByTable  = (($metrics.PerTable.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$([int64]$_.Value)" }) -join ', ')
                Plan          = $operators
            }
        }
    }
}
finally {
    $connection.Close()
}

$reportPath = Join-Path $artifactRoot "copilot-governance-benchmark.txt"
$results | Format-List | Out-String -Width 400 | Tee-Object -FilePath $reportPath
Write-Host ""
Write-Host "| Statement | Window | Logical reads | Median elapsed (ms) | Range (ms) |"
Write-Host "|---|---:|---:|---:|---:|"
foreach ($row in $results) {
    Write-Host ("| {0} | {1} days | {2:N0} | {3:N0} | {4} |" -f $row.Statement, $row.WindowDays, $row.LogicalReads, $row.MedianMs, $row.RangeMs)
}
Write-Host ""
Write-Host "Full report: $reportPath"

if (-not $KeepFixture) {
    Write-Host "Dropping the [govbench] fixture..."
    $connection = New-BenchConnection
    try {
        foreach ($table in @('copilot_event_ai_system_plugins', 'copilot_ai_system_plugins', 'copilot_event_ai_models', 'copilot_ai_models',
                             'copilot_event_accessed_resources', 'copilot_event_messages', 'copilot_chats', 'sensitivity_labels')) {
            Invoke-NonQuery -Connection $connection -Sql "IF OBJECT_ID(N'govbench.$table', N'U') IS NOT NULL DROP TABLE govbench.$table;"
        }
        Invoke-NonQuery -Connection $connection -Sql "IF SCHEMA_ID(N'govbench') IS NOT NULL DROP SCHEMA govbench;"
    }
    finally {
        $connection.Close()
    }
}
