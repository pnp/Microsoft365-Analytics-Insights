<#
.SYNOPSIS
    Measures the Teams Explorer's queries before and after each candidate index.

.DESCRIPTION
    The repository rule for schema changes is explicit: no performance-motivated index ships without
    before/after LOGICAL READS and ELAPSED TIME, the plan operator either side, and more than one
    selectivity. This script produces exactly that, automatically, so the numbers in a PR can be
    regenerated rather than retyped.

    For every (query, window) pair it:
      * drops the candidate index, warms the cache, then measures N runs and takes the median;
      * creates the candidate index and measures the same way;
      * captures the plan operator used against the table the index is for;
      * records the index's build time and size.

    Each run clears the plan cache and runs with OPTION (RECOMPILE), so the measured plan is the one
    the real window produces rather than a cached plan for a different one. The first (cold) run of
    each set is discarded.

    A NEGATIVE RESULT IS A GOOD RESULT. An index that does not improve both reads and elapsed time at
    both windows should not be shipped: it costs every customer an offline build on a large table for
    nothing, and index builds are not free to undo.

    -Compare MembershipHorizon measures a query change instead of an index: the three statements that
    find each team's latest membership snapshot (TeamsExplorerSql.LatestTeamMembers, in the channel
    leaderboard and the sentiment by team and by channel), reading the whole of team_membership_log as
    they did and only the last seven days as they do now, with the production indexes in place. It
    reports the reads on team_membership_log beside each statement's total.

.PARAMETER SyntheticUsers
    Distinct synthetic users. Use a large value for a 200k-user-tenant shape.

.PARAMETER SyntheticCalls
    Call records spread across the history.

.PARAMETER SyntheticDays
    Days of history, so the 28-day and 365-day windows are genuinely different selectivities.

.PARAMETER SyntheticMembersPerTeam
    Members of each of the fixture's 500 Teams, logged every day as the importer logs them. With
    2,000 users, 40 gives ten Team memberships per user: the 200,000-user design point at 1:100.

.PARAMETER Compare
    Indexes (the default) measures each candidate index; MembershipHorizon measures the
    LatestTeamMembers query change.

.EXAMPLE
    .\Invoke-TeamsExplorerIndexBenchmark.ps1 -SyntheticUsers 20000 -SyntheticCalls 300000 -SyntheticDays 400

.EXAMPLE
    .\Invoke-TeamsExplorerIndexBenchmark.ps1 -Compare MembershipHorizon -SyntheticUsers 2000 -SyntheticCalls 20000 -SyntheticDays 400 -SyntheticMembersPerTeam 40

.NOTES
    Creates and drops its own throwaway LocalDB database. Never point it at a customer database, and
    never paste real names, row counts or plans into a PR or a release note.
#>
param(
    [string]$DatabaseName = "TeamsExplorerIndexBench",
    [int]$SyntheticUsers = 20000,
    [int]$SyntheticCalls = 300000,
    [int]$SyntheticDays = 400,
    [int]$SyntheticMembersPerTeam = 5,
    [ValidateSet('Indexes', 'MembershipHorizon')]
    [string]$Compare = 'Indexes',
    [int[]]$WindowDays = @(28, 365),
    [int]$Repeats = 4,
    [string]$SqlcmdPath = "C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE",
    [string]$LocalDbInstance = "(localdb)\MSSQLLocalDB",
    [switch]$SkipFixture
)

$ErrorActionPreference = "Stop"

if ($DatabaseName -notmatch '^[A-Za-z0-9_]+$') {
    throw "Use a simple throwaway LocalDB database name containing only letters, numbers and underscores."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$fixturePath = Join-Path $scriptRoot "TeamsExplorerIndexBenchmark.sql"
$artifactRoot = Join-Path $scriptRoot "artifacts"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$connectionString = "Data Source=$LocalDbInstance;Initial Catalog=$DatabaseName;Integrated Security=true;TrustServerCertificate=True;Connect Timeout=60"

# ------------------------------------------------------------------------------------------------
# The measured queries.
#
# These MIRROR Common/Entities/TeamsExplorer/TeamsExplorerSql.cs. They are duplicated here rather
# than read from the C# because the benchmark has to run standalone against a synthetic fixture with
# no application present - the same trade the existing usage-report benchmark makes. If a query in
# TeamsExplorerSql changes shape, change it here too or the measurement stops describing the product.
# ------------------------------------------------------------------------------------------------

$dayOfWeek = "(DATEDIFF(DAY, 0, c.[start]) % 7)"
$outOfHours = "(CASE WHEN DATEPART(HOUR, c.[start]) < 8 OR DATEPART(HOUR, c.[start]) >= 18 OR $dayOfWeek >= 5 THEN 1 ELSE 0 END)"

$queries = @(
    [pscustomobject]@{
        Key      = 'calls-kpis'
        Table    = 'call_sessions'
        Candidate= 'call_sessions_covering'
        Sql      = @"
WITH Calls AS (
    SELECT c.id, c.call_type_id, c.[start],
           CAST(DATEDIFF(SECOND, c.[start], c.[end]) AS bigint) AS DurationSeconds,
           $outOfHours AS OutOfHours
    FROM dbo.call_records AS c
    WHERE c.[start] >= @from AND c.[start] < @to
),
PerCall AS (
    SELECT k.id, k.call_type_id, k.DurationSeconds, k.OutOfHours,
           ISNULL(s.Attendees, 0) AS Attendees, ISNULL(s.AttendeeSeconds, 0) AS AttendeeSeconds
    FROM Calls AS k
    LEFT JOIN (
        SELECT s.call_record_id, COUNT_BIG(*) AS Attendees,
               SUM(CAST(DATEDIFF(SECOND, s.[start], s.[end]) AS bigint)) AS AttendeeSeconds
        FROM dbo.call_sessions AS s
        INNER JOIN Calls AS k2 ON k2.id = s.call_record_id
        GROUP BY s.call_record_id
    ) AS s ON s.call_record_id = k.id
)
SELECT COUNT_BIG(*) AS Calls, ISNULL(SUM(p.DurationSeconds), 0) AS CallSeconds,
       ISNULL(SUM(p.AttendeeSeconds), 0) AS AttendeeSeconds,
       ISNULL(AVG(CAST(p.Attendees AS float)), 0) AS MeanAttendees,
       ISNULL(SUM(p.OutOfHours), 0) AS OutOfHoursCalls
FROM PerCall AS p
LEFT JOIN dbo.call_types AS ct ON ct.id = p.call_type_id
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key      = 'calls-sizes'
        Table    = 'call_sessions'
        Candidate= 'call_sessions_covering'
        Sql      = @"
WITH PerCall AS (
    SELECT c.id, (SELECT COUNT_BIG(*) FROM dbo.call_sessions AS s WHERE s.call_record_id = c.id) AS Attendees
    FROM dbo.call_records AS c
    WHERE c.[start] >= @from AND c.[start] < @to
)
SELECT p.Attendees, COUNT_BIG(*) AS Calls
FROM PerCall AS p
GROUP BY p.Attendees
ORDER BY p.Attendees
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key      = 'calls-modalities'
        Table    = 'call_session_call_modalities'
        Candidate= 'modalities_by_session'
        Sql      = @"
SELECT m.[name] AS Name, COUNT_BIG(DISTINCT l.call_session_id) AS [Count]
FROM dbo.call_session_call_modalities AS l
INNER JOIN dbo.call_modalities AS m ON m.id = l.call_modality_id
INNER JOIN dbo.call_sessions AS s ON s.id = l.call_session_id
INNER JOIN dbo.call_records AS c ON c.id = s.call_record_id
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY m.[name]
ORDER BY [Count] DESC
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key      = 'adoption-devices'
        Table    = 'teams_user_device_usage_log'
        Candidate= 'device_usage_covering'
        Sql      = @"
WITH PerUser AS (
    SELECT d.user_id,
        MAX(CASE WHEN d.used_windows   = 1 THEN 1 ELSE 0 END) AS UsedWindows,
        MAX(CASE WHEN d.used_mac       = 1 THEN 1 ELSE 0 END) AS UsedMac,
        MAX(CASE WHEN d.used_web       = 1 THEN 1 ELSE 0 END) AS UsedWeb,
        MAX(CASE WHEN d.used_ios       = 1 THEN 1 ELSE 0 END) AS UsedIos,
        MAX(CASE WHEN d.used_android   = 1 THEN 1 ELSE 0 END) AS UsedAndroid,
        MAX(CASE WHEN d.used_linux     = 1 THEN 1 ELSE 0 END) AS UsedLinux,
        MAX(CASE WHEN d.used_chrome_os = 1 THEN 1 ELSE 0 END) AS UsedChromeOs
    FROM dbo.teams_user_device_usage_log AS d
    WHERE d.[date] >= @from AND d.[date] < @to
    GROUP BY d.user_id
)
SELECT COUNT(*) AS MeasuredUsers, ISNULL(SUM(UsedWindows), 0) AS UsedWindows,
       ISNULL(SUM(UsedMac), 0) AS UsedMac, ISNULL(SUM(UsedWeb), 0) AS UsedWeb,
       ISNULL(SUM(UsedIos), 0) AS UsedIos, ISNULL(SUM(UsedAndroid), 0) AS UsedAndroid,
       ISNULL(SUM(UsedLinux), 0) AS UsedLinux, ISNULL(SUM(UsedChromeOs), 0) AS UsedChromeOs
FROM PerUser
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key      = 'collab-channel-stats'
        Table    = 'teams_channel_stats_log'
        Candidate= 'channel_stats_by_date'
        Sql      = @"
SELECT
    (SELECT COUNT(DISTINCT ch.team_id)
     FROM dbo.teams_channel_stats_log AS s
     INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
     WHERE s.[date] >= @from AND s.[date] < @to AND ISNULL(s.chats_count, 0) > 0) AS ActiveTeams,
    (SELECT COUNT(DISTINCT s.channel_id)
     FROM dbo.teams_channel_stats_log AS s
     WHERE s.[date] >= @from AND s.[date] < @to AND ISNULL(s.chats_count, 0) > 0) AS ActiveChannels,
    (SELECT ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0)
     FROM dbo.teams_channel_stats_log AS s
     WHERE s.[date] >= @from AND s.[date] < @to) AS ChannelMessages
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key      = 'collab-reactions'
        Table    = 'teams_user_channel_reactions'
        Candidate= 'reactions_by_date'
        Sql      = @"
SELECT ISNULL(r.reaction_id, 0) AS Name, COUNT_BIG(*) AS [Count]
FROM dbo.teams_user_channel_reactions AS r
WHERE r.[date] >= @from AND r.[date] < @to
GROUP BY ISNULL(r.reaction_id, 0)
ORDER BY [Count] DESC
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key      = 'collab-membership'
        Table    = 'team_membership_log'
        Candidate= 'membership_by_date'
        Sql      = @"
SELECT DATEADD(DAY, -(DATEDIFF(DAY, 0, m.[date]) % 7), CAST(m.[date] AS date)) AS WeekStart,
       COUNT(DISTINCT m.user_id) AS ActiveUsers
FROM dbo.team_membership_log AS m
WHERE m.[date] >= @from AND m.[date] < @to
GROUP BY DATEADD(DAY, -(DATEDIFF(DAY, 0, m.[date]) % 7), CAST(m.[date] AS date))
ORDER BY WeekStart
OPTION (RECOMPILE);
"@
    }
)

# Candidate indexes. Each is created, measured and dropped; nothing here touches production DDL.
$candidates = @{
    'call_sessions_covering' = [pscustomobject]@{
        Table  = 'call_sessions'
        Name   = 'IX_call_sessions_call_record_id_covering'
        Create = 'CREATE NONCLUSTERED INDEX IX_call_sessions_call_record_id_covering ON dbo.call_sessions(call_record_id) INCLUDE(attendee_user_id, [start], [end]);'
    }
    'modalities_by_session' = [pscustomobject]@{
        Table  = 'call_session_call_modalities'
        Name   = 'IX_call_session_call_modalities_session'
        Create = 'CREATE NONCLUSTERED INDEX IX_call_session_call_modalities_session ON dbo.call_session_call_modalities(call_session_id) INCLUDE(call_modality_id);'
    }
    'device_usage_covering' = [pscustomobject]@{
        Table  = 'teams_user_device_usage_log'
        Name   = 'IX_teams_user_device_usage_log_date_covering'
        Create = 'CREATE NONCLUSTERED INDEX IX_teams_user_device_usage_log_date_covering ON dbo.teams_user_device_usage_log([date]) INCLUDE(user_id, used_web, used_win_phone, used_linux, used_chrome_os, used_ios, used_android, used_mac, used_windows);'
    }
    'channel_stats_by_date' = [pscustomobject]@{
        Table  = 'teams_channel_stats_log'
        Name   = 'IX_teams_channel_stats_log_date'
        Create = 'CREATE NONCLUSTERED INDEX IX_teams_channel_stats_log_date ON dbo.teams_channel_stats_log([date]) INCLUDE(channel_id, chats_count, sentiment_score);'
    }
    'reactions_by_date' = [pscustomobject]@{
        Table  = 'teams_user_channel_reactions'
        Name   = 'IX_teams_user_channel_reactions_date'
        Create = 'CREATE NONCLUSTERED INDEX IX_teams_user_channel_reactions_date ON dbo.teams_user_channel_reactions([date]) INCLUDE(channel_id, user_id, reaction_id);'
    }
    'membership_by_date' = [pscustomobject]@{
        Table  = 'team_membership_log'
        Name   = 'IX_team_membership_log_date'
        Create = 'CREATE NONCLUSTERED INDEX IX_team_membership_log_date ON dbo.team_membership_log([date]) INCLUDE(team_id, user_id);'
    }
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
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Sql, [int]$Timeout = 0)
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = $Timeout
    [void]$command.ExecuteNonQuery()
    $command.Dispose()
}

<#
    Runs one statement with STATISTICS IO/TIME on and returns its logical reads and elapsed ms.

    The figures come from the messages SQL Server emits, which is the only way to get a per-statement
    read count without a trace. Reads are summed across every table the statement touched - a query
    that reads less from one table by reading far more from another has not improved.
#>
function Measure-Query {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [datetime]$From,
        [datetime]$To
    )

    $messages = New-Object System.Collections.ArrayList
    $handler = [System.Data.SqlClient.SqlInfoMessageEventHandler] {
        param($sender, $e)
        foreach ($error in $e.Errors) { [void]$messages.Add($error.Message) }
    }
    $Connection.add_InfoMessage($handler)
    $Connection.FireInfoMessageEventOnUserErrors = $false

    try {
        Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS IO ON; SET STATISTICS TIME ON;"

        $command = $Connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = 0
        $command.Parameters.Add("@from", [System.Data.SqlDbType]::Date).Value = $From.Date
        $command.Parameters.Add("@to", [System.Data.SqlDbType]::Date).Value = $To.Date
        # TeamsExplorerQuery.DefaultTop. SQL Server accepts a parameter a statement does not use, as the product relies on.
        $command.Parameters.Add("@top", [System.Data.SqlDbType]::Int).Value = 20

        $reader = $command.ExecuteReader()
        while ($reader.Read()) { }
        while ($reader.NextResult()) { while ($reader.Read()) { } }
        $reader.Close()
        $command.Dispose()

        Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;"
    }
    finally {
        $Connection.remove_InfoMessage($handler)
    }

    $reads = 0
    $elapsed = 0
    $tableReads = @{}
    foreach ($message in $messages) {
        foreach ($match in [regex]::Matches($message, 'logical reads (\d+)')) {
            $reads += [int]$match.Groups[1].Value
        }
        foreach ($match in [regex]::Matches($message, "Table '([^']+)'\. Scan count \d+, logical reads (\d+)")) {
            $tableReads[$match.Groups[1].Value] = [int]$tableReads[$match.Groups[1].Value] + [int]$match.Groups[2].Value
        }
        # MAX, not last. SQL Server emits an execution-times block for parse/compile, one for the
        # statement, and another for the `SET STATISTICS ... OFF` statement itself - which runs while
        # TIME is still on and always reports 0 ms. Taking the last match therefore recorded 0 for
        # every measurement; the statement being measured is always the longest of the three.
        foreach ($match in [regex]::Matches($message, 'elapsed time = (\d+) ms')) {
            $value = [int]$match.Groups[1].Value
            if ($value -gt $elapsed) { $elapsed = $value }
        }
    }

    return [pscustomobject]@{ Reads = $reads; ElapsedMs = $elapsed; TableReads = $tableReads }
}

<# The physical operator used against a named table, taken from the actual execution plan. With -WithRows, each access
   to the table with the rows it read across its executions: a seek repeated once per team is not one seek. #>
function Get-PlanOperator {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [datetime]$From,
        [datetime]$To,
        [string]$Table,
        [switch]$WithRows
    )

    Invoke-NonQuery -Connection $Connection -Sql "SET STATISTICS XML ON;"
    try {
        $command = $Connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = 0
        $command.Parameters.Add("@from", [System.Data.SqlDbType]::Date).Value = $From.Date
        $command.Parameters.Add("@to", [System.Data.SqlDbType]::Date).Value = $To.Date
        $command.Parameters.Add("@top", [System.Data.SqlDbType]::Int).Value = 20

        $plan = ""
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

    if ($WithRows) {
        [xml]$xml = $plan
        $ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
        $ns.AddNamespace('s', 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
        $accesses = foreach ($op in $xml.SelectNodes("//s:RelOp[s:IndexScan/s:Object[@Table='[$Table]']]", $ns)) {
            $rows = 0; $executions = 0
            foreach ($thread in $op.SelectNodes('s:RunTimeInformation/s:RunTimeCountersPerThread', $ns)) {
                $rows += [long]$thread.ActualRowsRead
                $executions += [long]$thread.ActualExecutions
            }
            "{0} ({1:N0} rows read, {2:N0} executions)" -f $op.PhysicalOp, $rows, $executions
        }
        if (-not $accesses) { return "(table not referenced)" }
        return ($accesses -join ' + ')
    }

    $operators = New-Object System.Collections.Generic.HashSet[string]
    foreach ($match in [regex]::Matches($plan, '<RelOp[^>]*PhysicalOp="([^"]+)"[^>]*>(.*?)</RelOp>', 'Singleline')) {
        $body = $match.Groups[2].Value
        if ($body -match [regex]::Escape("Table=`"[$Table]`"")) {
            [void]$operators.Add($match.Groups[1].Value)
        }
    }

    if ($operators.Count -eq 0) { return "(table not referenced)" }
    return (($operators | Sort-Object) -join ' + ')
}

function Get-Median {
    param([int[]]$Values)
    $sorted = $Values | Sort-Object
    $count = $sorted.Count
    if ($count -eq 0) { return 0 }
    if ($count % 2 -eq 1) { return $sorted[[int](($count - 1) / 2)] }
    return [int](($sorted[$count / 2 - 1] + $sorted[$count / 2]) / 2)
}

<#
    Median reads and elapsed over $Repeats runs, discarding the first (cold) run and clearing the
    plan cache before each one so a cached plan for a different window cannot leak in. With -Table,
    also the median reads on that one table.
#>
function Measure-Case {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [datetime]$From,
        [datetime]$To,
        [string]$Table
    )

    $reads = @()
    $elapsed = @()
    $tableReads = @()

    for ($run = 0; $run -le $Repeats; $run++) {
        Invoke-NonQuery -Connection $Connection -Sql "DBCC FREEPROCCACHE WITH NO_INFOMSGS;"
        $sample = Measure-Query -Connection $Connection -Sql $Sql -From $From -To $To
        if ($run -eq 0) { continue }   # cold run, discarded
        $reads += $sample.Reads
        $elapsed += $sample.ElapsedMs
        if ($Table) { $tableReads += [int]$sample.TableReads[$Table] }
    }

    return [pscustomobject]@{
        Reads      = Get-Median -Values $reads
        ElapsedMs  = Get-Median -Values $elapsed
        TableReads = if ($Table) { Get-Median -Values $tableReads } else { $null }
    }
}

# ------------------------------------------------------------------------------------------------
# -Compare MembershipHorizon: the LatestTeamMembers query change, with the production indexes.
#
# The three statements MIRROR ChannelLeaderboard, SentimentByTeam and SentimentByChannel in
# Common/Entities/TeamsExplorer/TeamsExplorerSql.cs, with {MEMBERS} standing for LatestTeamMembers:
# before, each team's latest snapshot found in the whole of team_membership_log; after, in the seven
# days that end with the window.
# ------------------------------------------------------------------------------------------------

$membersWholeHistory = @"
LatestMembership AS (
    SELECT m.team_id, MAX(m.[date]) AS LatestDate
    FROM dbo.team_membership_log AS m
    GROUP BY m.team_id
),
Members AS (
    SELECT m.team_id, COUNT(DISTINCT m.user_id) AS Members
    FROM dbo.team_membership_log AS m
    INNER JOIN LatestMembership AS l ON l.team_id = m.team_id AND l.LatestDate = m.[date]
    GROUP BY m.team_id
)
"@

$membersLastSevenDays = @"
LatestMembership AS (
    SELECT m.team_id, MAX(m.[date]) AS LatestDate
    FROM dbo.team_membership_log AS m
    WHERE m.[date] >= DATEADD(DAY, -7, @to) AND m.[date] < @to
    GROUP BY m.team_id
),
Members AS (
    SELECT m.team_id, COUNT(DISTINCT m.user_id) AS Members
    FROM dbo.team_membership_log AS m
    INNER JOIN LatestMembership AS l ON l.team_id = m.team_id AND l.LatestDate = m.[date]
    WHERE m.[date] >= DATEADD(DAY, -7, @to) AND m.[date] < @to
    GROUP BY m.team_id
)
"@

$membershipStatements = @(
    [pscustomobject]@{
        Key = 'collab-channels'
        Sql = @"
WITH {MEMBERS},
Stats AS (
    SELECT s.channel_id,
           SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)) AS Messages,
           SUM(CASE WHEN ISNULL(s.chats_count, 0) > 0 THEN 1 ELSE 0 END) AS MessageDays,
           SUM(CASE WHEN s.sentiment_score IS NULL THEN 0
                    ELSE s.sentiment_score * ISNULL(s.chats_count, 0) END) AS SentimentWeight,
           SUM(CASE WHEN s.sentiment_score IS NULL THEN 0
                    ELSE CAST(ISNULL(s.chats_count, 0) AS bigint) END)     AS ScoredMessages
    FROM dbo.teams_channel_stats_log AS s
    WHERE s.[date] >= @from AND s.[date] < @to
    GROUP BY s.channel_id
),
Reactions AS (
    SELECT r.channel_id,
           COUNT_BIG(*) AS Reactions,
           COUNT(DISTINCT r.user_id) AS ReactingUsers
    FROM dbo.teams_user_channel_reactions AS r
    WHERE r.[date] >= @from AND r.[date] < @to
    GROUP BY r.channel_id
),
Tabs AS (
    SELECT tl.channel_id, COUNT(DISTINCT tl.tab_id) AS Tabs
    FROM dbo.teams_channel_tabs_log AS tl
    WHERE tl.[date] >= @from AND tl.[date] < @to
    GROUP BY tl.channel_id
)
SELECT TOP (@top)
    ch.id                       AS Id,
    ch.[name]                   AS Name,
    t.[name]                    AS TeamName,
    ISNULL(tab.Tabs, 0)         AS Tabs,
    ISNULL(st.Messages, 0)      AS Messages,
    ISNULL(re.Reactions, 0)     AS Reactions,
    ISNULL(re.ReactingUsers, 0) AS ReactingUsers,
    CASE WHEN ISNULL(st.ScoredMessages, 0) > 0
         THEN st.SentimentWeight / st.ScoredMessages END AS Sentiment,
    ISNULL(st.MessageDays, 0)   AS ActiveDays,
    ISNULL(mem.Members, 0)      AS TeamMembers
FROM dbo.teams_channels AS ch
INNER JOIN dbo.teams AS t ON t.id = ch.team_id
LEFT JOIN Members   AS mem ON mem.team_id = ch.team_id
LEFT JOIN Stats     AS st  ON st.channel_id = ch.id
LEFT JOIN Reactions AS re  ON re.channel_id = ch.id
LEFT JOIN Tabs      AS tab ON tab.channel_id = ch.id
ORDER BY ISNULL(st.Messages, 0) DESC, ISNULL(re.Reactions, 0) DESC, ch.[name]
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key = 'conv-sentiment-team'
        Sql = @"
WITH {MEMBERS}
SELECT TOP (@top)
    t.[name] AS Name,
    SUM(s.sentiment_score * ISNULL(s.chats_count, 0))
        / NULLIF(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0) AS Sentiment,
    ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0)       AS Messages,
    ISNULL(MAX(mem.Members), 0)                                    AS Members
FROM dbo.teams_channel_stats_log AS s
INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
INNER JOIN dbo.teams AS t ON t.id = ch.team_id
LEFT JOIN Members AS mem ON mem.team_id = t.id
WHERE s.[date] >= @from AND s.[date] < @to
  AND s.sentiment_score IS NOT NULL
  AND ISNULL(s.chats_count, 0) > 0
GROUP BY t.[name]
ORDER BY Messages DESC
OPTION (RECOMPILE);
"@
    },
    [pscustomobject]@{
        Key = 'conv-sentiment-channel'
        Sql = @"
WITH {MEMBERS}
SELECT TOP (@top)
    t.[name] + N' / ' + ch.[name] AS Name,
    SUM(s.sentiment_score * ISNULL(s.chats_count, 0))
        / NULLIF(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0) AS Sentiment,
    ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0)       AS Messages,
    ISNULL(MAX(mem.Members), 0)                                    AS Members
FROM dbo.teams_channel_stats_log AS s
INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
INNER JOIN dbo.teams AS t ON t.id = ch.team_id
LEFT JOIN Members AS mem ON mem.team_id = t.id
WHERE s.[date] >= @from AND s.[date] < @to
  AND s.sentiment_score IS NOT NULL
  AND ISNULL(s.chats_count, 0) > 0
GROUP BY t.[name] + N' / ' + ch.[name]
ORDER BY Messages DESC
OPTION (RECOMPILE);
"@
    }
)

function Invoke-MembershipHorizonComparison {
    $connection = New-BenchConnection
    $results = @()

    try {
        # The date indexes production has (IndexTeamsExplorerQueries) on the tables these statements read.
        foreach ($key in @('channel_stats_by_date', 'reactions_by_date', 'membership_by_date')) {
            $candidate = $candidates[$key]
            Invoke-NonQuery -Connection $connection -Sql `
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.$($candidate.Table)') AND name = N'$($candidate.Name)') $($candidate.Create)"
        }

        $sizeCommand = $connection.CreateCommand()
        $sizeCommand.CommandText = @"
SELECT SUM(ps.row_count), SUM(ps.used_page_count), (SELECT COUNT(DISTINCT [date]) FROM dbo.team_membership_log)
FROM sys.dm_db_partition_stats AS ps
INNER JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID(N'dbo.team_membership_log') AND i.name = N'IX_team_membership_log_date';
"@
        $reader = $sizeCommand.ExecuteReader()
        [void]$reader.Read()
        $indexRows = [long]$reader.GetValue(0); $indexPages = [long]$reader.GetValue(1); $historyDays = [int]$reader.GetValue(2)
        $reader.Close()
        $sizeCommand.Dispose()

        $to = (Get-Date).Date.AddDays(1)
        foreach ($statement in $membershipStatements) {
            foreach ($days in $WindowDays) {
                $from = $to.AddDays(-$days)
                foreach ($variant in @(
                        [pscustomobject]@{ Name = 'whole history'; Members = $membersWholeHistory },
                        [pscustomobject]@{ Name = 'last 7 days'; Members = $membersLastSevenDays })) {
                    $sql = $statement.Sql.Replace('{MEMBERS}', $variant.Members.Trim())
                    $metrics = Measure-Case -Connection $connection -Sql $sql -From $from -To $to -Table 'team_membership_log'
                    $operator = Get-PlanOperator -Connection $connection -Sql $sql -From $from -To $to -Table 'team_membership_log' -WithRows

                    $results += [pscustomobject]@{
                        Statement       = $statement.Key
                        WindowDays      = $days
                        Membership      = $variant.Name
                        Reads           = $metrics.Reads
                        MembershipReads = $metrics.TableReads
                        ElapsedMs       = $metrics.ElapsedMs
                        Plan            = $operator
                    }

                    Write-Host ("  {0,-24} {1,4}d  {2,-14} reads {3,9} (membership {4,9})  {5,6} ms  {6}" -f `
                        $statement.Key, $days, $variant.Name, $metrics.Reads, $metrics.TableReads, $metrics.ElapsedMs, $operator)
                }
            }
        }
    }
    finally {
        $connection.Close()
        $connection.Dispose()
    }

    $results | Export-Csv -NoTypeInformation -Path (Join-Path $artifactRoot "teams-explorer-membership-horizon.csv")

    Write-Host ""
    Write-Host ("IX_team_membership_log_date: {0:N0} rows on {1:N0} pages over {2} days of history - {3:N0} rows and {4:N0} pages a day, {5:N0} rows a page." -f `
        $indexRows, $indexPages, $historyDays, ($indexRows / $historyDays), ($indexPages / $historyDays), ($indexRows / $indexPages))
    Write-Host ""
    Write-Host "| Statement | Window | Membership read | Logical reads (team_membership_log) | Elapsed | Accesses to team_membership_log |"
    Write-Host "|---|---|---|---|---|---|"
    foreach ($row in $results) {
        Write-Host ("| {0} | {1}d | {2} | {3:N0} ({4:N0}) | {5} ms | {6} |" -f `
            $row.Statement, $row.WindowDays, $row.Membership, $row.Reads, $row.MembershipReads, $row.ElapsedMs, $row.Plan)
    }
}

# ------------------------------------------------------------------------------------------------
# Fixture
# ------------------------------------------------------------------------------------------------

if (-not $SkipFixture) {
    if (-not (Test-Path $SqlcmdPath)) {
        throw "sqlcmd was not found at '$SqlcmdPath'. Pass -SqlcmdPath if it is installed elsewhere."
    }

    Write-Host "Creating throwaway database [$DatabaseName]..."
    & $SqlcmdPath -S $LocalDbInstance -d master -b -Q "IF DB_ID(N'$DatabaseName') IS NOT NULL BEGIN ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DatabaseName]; END; CREATE DATABASE [$DatabaseName] COLLATE Latin1_General_CI_AS;"
    if ($LASTEXITCODE -ne 0) { throw "Could not create the benchmark database." }

    Write-Host "Seeding the fixture (users=$SyntheticUsers, calls=$SyntheticCalls, days=$SyntheticDays, membersPerTeam=$SyntheticMembersPerTeam)..."
    $fixtureLog = Join-Path $artifactRoot "fixture.txt"
    & $SqlcmdPath -S $LocalDbInstance -d $DatabaseName -b -i $fixturePath `
        -v SyntheticUsers="$SyntheticUsers" SyntheticCalls="$SyntheticCalls" SyntheticDays="$SyntheticDays" SyntheticMembersPerTeam="$SyntheticMembersPerTeam" `
        -o $fixtureLog
    if ($LASTEXITCODE -ne 0) { Get-Content $fixtureLog -Tail 40; throw "Fixture seeding failed." }
    Get-Content $fixtureLog -Tail 20
}

if ($Compare -eq 'MembershipHorizon') {
    Invoke-MembershipHorizonComparison
    Write-Host ""
    Write-Host "Artifacts written under $artifactRoot"
    return
}

# ------------------------------------------------------------------------------------------------
# Measure
# ------------------------------------------------------------------------------------------------

$connection = New-BenchConnection
$results = @()
$indexFacts = @()

try {
    $to = (Get-Date).Date.AddDays(1)

    foreach ($candidateKey in ($queries | Select-Object -ExpandProperty Candidate -Unique)) {
        $candidate = $candidates[$candidateKey]
        $affected = $queries | Where-Object { $_.Candidate -eq $candidateKey }

        Write-Host ""
        Write-Host "=== Candidate: $($candidate.Name) on dbo.$($candidate.Table) ==="

        Invoke-NonQuery -Connection $connection -Sql `
            "IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.$($candidate.Table)') AND name = N'$($candidate.Name)') DROP INDEX [$($candidate.Name)] ON [dbo].[$($candidate.Table)];"

        $before = @{}
        foreach ($query in $affected) {
            foreach ($days in $WindowDays) {
                $from = $to.AddDays(-$days)
                $before["$($query.Key)|$days"] = [pscustomobject]@{
                    Metrics  = Measure-Case -Connection $connection -Sql $query.Sql -From $from -To $to
                    Operator = Get-PlanOperator -Connection $connection -Sql $query.Sql -From $from -To $to -Table $query.Table
                }
            }
        }

        $buildWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-NonQuery -Connection $connection -Sql $candidate.Create
        $buildWatch.Stop()

        $sizeCommand = $connection.CreateCommand()
        $sizeCommand.CommandText = @"
SELECT CAST(SUM(ps.used_page_count) * 8.0 / 1024.0 AS decimal(10,1))
FROM sys.dm_db_partition_stats AS ps
INNER JOIN sys.indexes AS i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID(N'dbo.$($candidate.Table)') AND i.name = N'$($candidate.Name)';
"@
        $sizeMb = $sizeCommand.ExecuteScalar()
        $sizeCommand.Dispose()

        $indexFacts += [pscustomobject]@{
            Index       = $candidate.Name
            Table       = $candidate.Table
            BuildSecs   = [math]::Round($buildWatch.Elapsed.TotalSeconds, 1)
            SizeMb      = $sizeMb
        }

        foreach ($query in $affected) {
            foreach ($days in $WindowDays) {
                $from = $to.AddDays(-$days)
                $after = Measure-Case -Connection $connection -Sql $query.Sql -From $from -To $to
                $afterOperator = Get-PlanOperator -Connection $connection -Sql $query.Sql -From $from -To $to -Table $query.Table
                $baseline = $before["$($query.Key)|$days"]

                $results += [pscustomobject]@{
                    Query          = $query.Key
                    Index          = $candidate.Name
                    WindowDays     = $days
                    ReadsBefore    = $baseline.Metrics.Reads
                    ReadsAfter     = $after.Reads
                    ReadsDeltaPct  = if ($baseline.Metrics.Reads -gt 0) { [math]::Round((($after.Reads - $baseline.Metrics.Reads) / [double]$baseline.Metrics.Reads) * 100, 1) } else { 0 }
                    MsBefore       = $baseline.Metrics.ElapsedMs
                    MsAfter        = $after.ElapsedMs
                    MsDeltaPct     = if ($baseline.Metrics.ElapsedMs -gt 0) { [math]::Round((($after.ElapsedMs - $baseline.Metrics.ElapsedMs) / [double]$baseline.Metrics.ElapsedMs) * 100, 1) } else { 0 }
                    PlanBefore     = $baseline.Operator
                    PlanAfter      = $afterOperator
                }

                Write-Host ("  {0,-22} {1,4}d  reads {2,9} -> {3,-9}  ms {4,6} -> {5,-6}  {6} -> {7}" -f `
                    $query.Key, $days, $baseline.Metrics.Reads, $after.Reads,
                    $baseline.Metrics.ElapsedMs, $after.ElapsedMs, $baseline.Operator, $afterOperator)
            }
        }
    }
}
finally {
    $connection.Close()
    $connection.Dispose()
}

$resultPath = Join-Path $artifactRoot "teams-explorer-index-benchmark.csv"
$results | Export-Csv -NoTypeInformation -Path $resultPath
$indexFacts | Export-Csv -NoTypeInformation -Path (Join-Path $artifactRoot "teams-explorer-index-costs.csv")

Write-Host ""
Write-Host "| Query | Index | Window | Reads before -> after | Elapsed before -> after | Plan before -> after |"
Write-Host "|---|---|---|---|---|---|"
foreach ($row in $results) {
    Write-Host ("| {0} | {1} | {2}d | {3} -> {4} ({5}%) | {6} ms -> {7} ms ({8}%) | {9} -> {10} |" -f `
        $row.Query, $row.Index, $row.WindowDays, $row.ReadsBefore, $row.ReadsAfter, $row.ReadsDeltaPct,
        $row.MsBefore, $row.MsAfter, $row.MsDeltaPct, $row.PlanBefore, $row.PlanAfter)
}

Write-Host ""
Write-Host "Index build cost:"
$indexFacts | Format-Table -AutoSize

Write-Host ""
Write-Host "A candidate is only approved when BOTH reads and elapsed time improve at BOTH windows."
Write-Host "Artifacts written under $artifactRoot"
