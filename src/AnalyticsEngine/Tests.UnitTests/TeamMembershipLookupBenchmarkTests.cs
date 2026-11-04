using Common.Entities;
using Common.Entities.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.Entity.Migrations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Opt-in synthetic reproduction of the Teams crawl's "is this member already logged today?" check: one query per member
    /// comparing the date's year, month and day (before) against one query per Team over a date range (after, what
    /// <c>SqlTeamsPersistenceStore.SaveMembers</c> sends now). Set <c>TEAMS_MEMBERSHIP_BENCHMARK=1</c> to run it.
    /// </summary>
    /// <remarks>
    /// Builds a throwaway database with every migration applied, so <c>team_membership_log</c> has the indexes customers have,
    /// <c>IX_team_membership_log_date</c> included, then seeds synthetic history: 2,000 users, one all-company Team and 100
    /// Teams of 40, logged daily for 30 and then 365 days. Half the small Teams have already been crawled today. Each
    /// measurement is the median of five runs after a discarded warm-up; each run clears the database's plan cache first, so a
    /// crawl's first compile is in it. Writes a markdown table to <c>teams-membership-benchmark.txt</c> in the test output folder,
    /// and deletes the database afterwards. Only synthetic data is written.
    /// </remarks>
    [TestClass]
    public class TeamMembershipLookupBenchmarkTests
    {
        private const int UserCount = 2000;
        private const int SmallTeamCount = 100;
        private const int SmallTeamSize = 40;

        [TestMethod]
        [TestCategory("Benchmark")]
        public async Task MembershipCheck_PerMemberAgainstOncePerTeam_Synthetic()
        {
            if (Environment.GetEnvironmentVariable("TEAMS_MEMBERSHIP_BENCHMARK") != "1")
            {
                Assert.Inconclusive("Set TEAMS_MEMBERSHIP_BENCHMARK=1 to run the synthetic benchmark.");
            }

            var output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "teams-membership-benchmark.txt");
            File.WriteAllText(output,
                $"{UserCount:N0} synthetic users; 1 all-company Team and {SmallTeamCount} Teams of {SmallTeamSize}; median of 5 runs after a discarded warm-up; plan cache cleared before each run" + Environment.NewLine +
                "| History | Team (members) | Path | Queries | Logical reads | Elapsed ms | Plan on team_membership_log |" + Environment.NewLine +
                "|---|---|---|---|---|---|---|" + Environment.NewLine);

            using (var database = ScratchDatabase.Create("TeamsMembershipBench"))
            {
                new DbMigrator(new Configuration
                {
                    TargetDatabase = new DbConnectionInfo(database.ConnectionString, "Microsoft.Data.SqlClient"),
                }).Update();
                Seed(database);

                var today = DateTime.UtcNow.Date;
                var seededDays = 0;
                foreach (var days in new[] { 30, 365 })
                {
                    database.Execute($@"
INSERT dbo.team_membership_log (team_id, user_id, [date])
SELECT m.team_id, m.user_id, DATEADD(day, -d.n, '{today:yyyyMMdd}')
FROM dbo.bench_members AS m
CROSS JOIN (SELECT TOP ({days - seededDays}) {seededDays} + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS d;
UPDATE STATISTICS dbo.team_membership_log WITH FULLSCAN;");
                    seededDays = days;

                    foreach (var team in new[] { (Name: "all-company", Ordinal: 1), (Name: "small", Ordinal: 2) })
                    {
                        var teamId = Convert.ToInt32(database.Scalar($"SELECT MIN(id) + {team.Ordinal - 1} FROM dbo.teams"));
                        var members = Members(database, teamId);
                        var label = $"{team.Name} ({members.Count:N0})";
                        await Measure(database, output, $"{days} days", label, "before: per member, DATEPART", db => PerMember(db, teamId, members, today));
                        await Measure(database, output, $"{days} days", label, "after: per Team, date range", db => PerTeam(db, teamId, today));

                        // The whole of SaveMembers: resolving each member's user row is unchanged and still one IX_users seek each.
                        var source = Team(database, teamId);
                        await Measure(database, output, $"{days} days", label, "SaveMembers before (users + per-member check)", db => SaveMembersBefore(db, source));
                        await Measure(database, output, $"{days} days", label, "SaveMembers after (users + one check)", db => SaveMembersAfter(db, source));
                    }
                }
            }
        }

        /// <summary>The query <c>SaveMembers</c> used to send for every member.</summary>
        private static async Task<int> PerMember(AnalyticsEntitiesContext db, int teamId, List<int> members, DateTime today)
        {
            int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;
            var found = 0;
            foreach (var userId in members)
            {
                var todaysUserLog = await db.TeamMembershipLogs.SingleOrDefaultAsync(t =>
                    t.Team.ID == teamId && t.UserID == userId &&
                    t.Date.Year == todayYear && t.Date.Month == todayMonth && t.Date.Day == todayDay);
                if (todaysUserLog != null) found++;
            }
            return found;
        }

        /// <summary>The query <c>SaveMembers</c> sends now, once per Team.</summary>
        private static async Task<int> PerTeam(AnalyticsEntitiesContext db, int teamId, DateTime today)
        {
            var tomorrow = today.AddDays(1);
            return (await db.TeamMembershipLogs
                .Where(t => t.TeamID == teamId && t.Date >= today && t.Date < tomorrow)
                .Select(t => t.UserID)
                .ToListAsync()).Count;
        }

        /// <summary><c>SaveMembers</c> as it was, verbatim, under the same tracking settings the store applies. Nothing is saved.</summary>
        private static async Task<int> SaveMembersBefore(AnalyticsEntitiesContext db, O365Team team)
        {
            var lookup = new TeamsAndCallsDBLookupManager(db);
            using (new SqlTeamsPersistenceStore(lookup))
            {
                var dbTeam = await lookup.GetOrCreateTeam(team.Id, team.DisplayName);
                var today = DateTime.UtcNow.Date;
                int todayYear = today.Year, todayMonth = today.Month, todayDay = today.Day;
                var added = 0;
                foreach (var member in team.Users)
                {
                    var user = await lookup.GetOrCreateUser(member.UserPrincipalName, true);
                    TeamMembershipLog todaysUserLog = null;
                    if (user.IsSavedToDB)
                    {
                        todaysUserLog = await db.TeamMembershipLogs.SingleOrDefaultAsync(t =>
                            t.Team.ID == dbTeam.ID && t.UserID == user.ID &&
                            t.Date.Year == todayYear && t.Date.Month == todayMonth && t.Date.Day == todayDay);
                    }
                    if (todaysUserLog == null)
                    {
                        db.TeamMembershipLogs.Add(new TeamMembershipLog { Team = dbTeam, User = user, Date = today });
                        added++;
                    }
                }
                return added;
            }
        }

        /// <summary>The production <c>SaveMembers</c>. Nothing is saved.</summary>
        private static async Task<int> SaveMembersAfter(AnalyticsEntitiesContext db, O365Team team)
        {
            using (var store = new SqlTeamsPersistenceStore(new TeamsAndCallsDBLookupManager(db)))
            {
                await store.SaveMembers(team.Users, team);
                return team.Users.Count;
            }
        }

        private static O365Team Team(ScratchDatabase database, int teamId)
        {
            var team = new O365Team
            {
                Id = (string)database.Scalar($"SELECT graph_id FROM dbo.teams WHERE id = {teamId}"),
                DisplayName = (string)database.Scalar($"SELECT name FROM dbo.teams WHERE id = {teamId}"),
            };
            using (var connection = new SqlConnection(database.ConnectionString))
            using (var command = new SqlCommand(
                "SELECT u.user_name FROM dbo.bench_members AS m JOIN dbo.users AS u ON u.id = m.user_id WHERE m.team_id = @teamId ORDER BY u.id", connection))
            {
                command.Parameters.AddWithValue("@teamId", teamId);
                connection.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) team.Users.Add(new BaseUser { UserPrincipalName = reader.GetString(0) });
            }
            return team;
        }

        private static void Seed(ScratchDatabase database)
        {
            database.Execute($@"
SET NOCOUNT ON;
WITH n AS (SELECT TOP ({UserCount}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.users (user_name) SELECT 'teamsbench' + RIGHT('000000' + CAST(i AS varchar(6)), 6) + '@contoso.com' FROM n;

WITH n AS (SELECT TOP ({SmallTeamCount + 1}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
INSERT dbo.teams (graph_id, name, discovered, has_refresh_token)
SELECT '00000000-0000-0000-0000-' + RIGHT('000000000000' + CAST(i AS varchar(12)), 12), N'Contoso ' + CAST(i AS nvarchar(10)), '20250101', 0 FROM n;

CREATE TABLE dbo.bench_members (team_id int NOT NULL, user_id int NOT NULL);
DECLARE @firstUser int = (SELECT MIN(id) FROM dbo.users), @firstTeam int = (SELECT MIN(id) FROM dbo.teams);

-- Everyone is in the all-company Team.
INSERT dbo.bench_members (team_id, user_id) SELECT @firstTeam, id FROM dbo.users;

-- Each small Team: {SmallTeamSize} distinct people (53 and {UserCount} are coprime), so each person is in about two.
WITH t AS (SELECT TOP ({SmallTeamCount}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects),
     j AS (SELECT TOP ({SmallTeamSize}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS j FROM sys.all_objects)
INSERT dbo.bench_members (team_id, user_id)
SELECT @firstTeam + t.k, @firstUser + ((t.k * 37 + j.j * 53) % {UserCount}) FROM t CROSS JOIN j;

-- Half the small Teams were crawled earlier today; the two measured Teams were not.
INSERT dbo.team_membership_log (team_id, user_id, [date])
SELECT team_id, user_id, CAST(SYSUTCDATETIME() AS date) FROM dbo.bench_members WHERE team_id > @firstTeam + {SmallTeamCount / 2};");
        }

        private static List<int> Members(ScratchDatabase database, int teamId)
        {
            using (var connection = new SqlConnection(database.ConnectionString))
            using (var command = new SqlCommand("SELECT user_id FROM dbo.bench_members WHERE team_id = @teamId ORDER BY user_id", connection))
            {
                command.Parameters.AddWithValue("@teamId", teamId);
                connection.Open();
                var ids = new List<int>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                return ids;
            }
        }

        private static async Task Measure(ScratchDatabase database, string output, string history, string team, string path,
            Func<AnalyticsEntitiesContext, Task<int>> run)
        {
            var reads = new List<long>();
            var elapsed = new List<double>();
            var queries = 0;
            var recorder = new FirstQueryRecorder();
            DbInterception.Add(recorder);
            try
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    using (var db = new AnalyticsEntitiesContext(database.ConnectionString, true, false))
                    {
                        await db.Database.Connection.OpenAsync();
                        await db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, "ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;");
                        var before = await SessionReads(db);
                        recorder.Queries = 0;
                        var watch = Stopwatch.StartNew();
                        await run(db);
                        watch.Stop();
                        queries = recorder.Queries;
                        var after = await SessionReads(db);
                        if (attempt > 0)
                        {
                            reads.Add(after - before);
                            elapsed.Add(watch.Elapsed.TotalMilliseconds);
                        }
                    }
                }
            }
            finally
            {
                DbInterception.Remove(recorder);
            }

            var plan = await PlanAsync(database.ConnectionString, recorder.Text, recorder.Parameters);
            var line = $"| {history} | {team} | {path} | {queries:N0} | {Median(reads):N0} | {Median(elapsed):N1} | {plan} |";
            Console.WriteLine(line);
            File.AppendAllText(output, line + Environment.NewLine);
        }

        private static Task<long> SessionReads(AnalyticsEntitiesContext db) => db.Database.SqlQuery<long>(
            "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID").SingleAsync();

        private static T Median<T>(List<T> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);

        /// <summary>
        /// Counts the queries a path sends, and keeps the first it sends to <c>team_membership_log</c>, to read its plan afterwards.
        /// </summary>
        private sealed class FirstQueryRecorder : DbCommandInterceptor
        {
            public int Queries { get; set; }
            public string Text { get; private set; }
            public List<SqlParameter> Parameters { get; private set; }

            public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
            {
                if (command.CommandText.IndexOf("dm_exec_sessions", StringComparison.OrdinalIgnoreCase) >= 0) return;
                Queries++;
                if (Text != null || command.CommandText.IndexOf("[team_membership_log]", StringComparison.OrdinalIgnoreCase) < 0) return;
                Text = command.CommandText;
                Parameters = command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone()).ToList();
            }
        }

        /// <summary>The actual plan's operators on <c>team_membership_log</c>, with their index and the parameters' SQL types.</summary>
        private static async Task<string> PlanAsync(string connectionString, string text, List<SqlParameter> parameters)
        {
            if (text == null) return "(no query)";
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand("SET STATISTICS XML ON", connection)) await command.ExecuteNonQueryAsync();
                using (var command = new SqlCommand(text, connection))
                {
                    command.Parameters.AddRange(parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray());
                    var operators = new List<string>();
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        do
                        {
                            var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan");
                            while (await reader.ReadAsync())
                            {
                                if (!isPlan) continue;
                                XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                                foreach (var relOp in XDocument.Parse(reader.GetString(0)).Descendants(ns + "RelOp"))
                                {
                                    var target = relOp.Elements().SelectMany(e => e.Elements(ns + "Object"))
                                        .FirstOrDefault(o => ((string)o.Attribute("Table") ?? "").Contains("team_membership_log"));
                                    if (target != null)
                                        operators.Add($"{relOp.Attribute("PhysicalOp")?.Value} {target.Attribute("Index")?.Value}");
                                }
                            }
                        } while (await reader.NextResultAsync());
                    }
                    var types = string.Join(", ", parameters.Select(p => $"{p.ParameterName} {p.SqlDbType.ToString().ToLowerInvariant()}"));
                    return string.Join(" + ", operators.Distinct()) + $" ({types})";
                }
            }
        }
    }
}
