using Common.Entities.Migrations;
using Common.Entities.PromptCategories;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    [TestClass]
    public class PromptCategoryReportTests
    {
        [TestMethod]
        public async Task AggregateQueries_RequireTenDistinctPeople_AfterScope_AndNeverMixVersions()
        {
            using (var fixture = await Fixture.CreateAsync())
            {
                await fixture.ExecuteAsync(@"
                    DECLARE @n int=1;
                    WHILE @n<=20
                    BEGIN
                        INSERT dbo.copilot_interactions VALUES(@n,@n,DATEADD(day,-1,CAST(SYSUTCDATETIME() AS date)));
                        INSERT dbo.copilot_prompt_classifications VALUES(@n,CASE WHEN @n<=10 THEN N'meeting-summary' ELSE N'analysis' END,REPLICATE(N'a',64),NULL);
                        SET @n+=1;
                    END;
                    DELETE dbo.copilot_prompt_classifications WHERE interaction_id=20;
                    DELETE dbo.copilot_interactions WHERE id=20;
                    SET @n=201;
                    WHILE @n<=300
                    BEGIN
                        INSERT dbo.copilot_interactions VALUES(@n,20,DATEADD(day,-1,CAST(SYSUTCDATETIME() AS date)));
                        INSERT dbo.copilot_prompt_classifications VALUES(@n,N'content-drafting',REPLICATE(N'a',64),NULL);
                        SET @n+=1;
                    END;
                    SET @n=1001;
                    WHILE @n<=1020
                    BEGIN
                        INSERT dbo.copilot_interactions VALUES(@n,@n-1000,DATEADD(day,-1,CAST(SYSUTCDATETIME() AS date)));
                        INSERT dbo.copilot_prompt_classifications VALUES(@n,N'analysis',REPLICATE(N'b',64),NULL);
                        SET @n+=1;
                    END;");
                var mix = await fixture.ReadAsync(PromptCategoryReportSql.Mix, ReportUserScope.Everyone);
                Assert.AreEqual(1, mix.Count);
                Assert.AreEqual("meeting-summary", mix[0][0]);
                Assert.AreEqual(10L, mix[0][1], "Other versions must not inflate a selected version.");
                var trend = await fixture.ReadAsync(PromptCategoryReportSql.Trend, ReportUserScope.Everyone);
                Assert.AreEqual(1, trend.Count);
                Assert.AreEqual(DayOfWeek.Monday, ((DateTime)trend[0][1]).DayOfWeek);
                Assert.AreEqual(10L, trend[0][2]);
                var scope = ReportUserScope.ForUsers(Enumerable.Range(1, 9));
                foreach (var sql in new[] { PromptCategoryReportSql.Mix, PromptCategoryReportSql.Trend, PromptCategoryReportSql.Versions })
                    Assert.AreEqual(0, (await fixture.ReadAsync(sql, scope)).Count,
                        "The minimum group applies after scope, and to people rather than repeated prompts.");
                Assert.AreEqual(2, (await fixture.ReadAsync(PromptCategoryReportSql.Versions, ReportUserScope.Everyone)).Count);
            }
        }

        [TestMethod]
        [TestCategory("SyntheticPerformance")]
        public async Task SyntheticScale_200kPeople_1mFacts_BoundedSqlAggregates()
        {
            if (Environment.GetEnvironmentVariable("RUN_PROMPT_CATEGORY_SQL_SCALE") != "true")
                Assert.Inconclusive("Set RUN_PROMPT_CATEGORY_SQL_SCALE=true to run the explicit synthetic SQL scale fixture.");
            using (var fixture = await Fixture.CreateAsync())
            {
                await fixture.ExecuteAsync(@"
                    WITH d AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
                    numbers AS (SELECT 1+a.n+10*b.n+100*c.n+1000*d1.n+10000*e.n+100000*f.n AS n
                                FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d d1 CROSS JOIN d e CROSS JOIN d f)
                    INSERT dbo.copilot_interactions
                    SELECT n,1+(n%200000),DATEADD(day,-(n%180),CAST(SYSUTCDATETIME() AS date)) FROM numbers;
                    INSERT dbo.copilot_prompt_classifications
                    SELECT id,CASE id%6 WHEN 0 THEN N'meeting-summary' WHEN 1 THEN N'document-editing'
                        WHEN 2 THEN N'information-lookup' WHEN 3 THEN N'content-drafting'
                        WHEN 4 THEN N'analysis' ELSE N'other' END,REPLICATE(N'a',64),NULL
                    FROM dbo.copilot_interactions;");
                using (var storage = fixture.Connection.CreateCommand())
                {
                    storage.CommandText = @"SELECT SUM(used_page_count)*8.0/1024 FROM sys.dm_db_partition_stats p
                        JOIN sys.tables t ON t.object_id=p.object_id
                        WHERE t.name IN ('copilot_prompt_taxonomies','copilot_prompt_classifications','copilot_prompt_classification_runs')";
                    Console.WriteLine($"Synthetic categorisation tables at 1m facts: {await storage.ExecuteScalarAsync()} MiB used.");
                }
                foreach (var days in new[] { 30, 180 })
                    foreach (var query in new[] { PromptCategoryReportSql.Mix, PromptCategoryReportSql.Trend })
                    {
                        var elapsed = new List<long>();
                        var logicalReads = new List<long>();
                        var operators = new HashSet<string>(StringComparer.Ordinal);
                        for (var run = 0; run < 4; run++)
                        {
                            long reads = 0;
                            SqlInfoMessageEventHandler handler = (_, args) =>
                            {
                                foreach (System.Text.RegularExpressions.Match match in
                                    System.Text.RegularExpressions.Regex.Matches(args.Message, @"logical reads (\d+)"))
                                    reads += long.Parse(match.Groups[1].Value);
                            };
                            fixture.Connection.InfoMessage += handler;
                            try
                            {
                                using (var command = fixture.Command(query + " OPTION (RECOMPILE)", ReportUserScope.Everyone, days))
                                {
                                    command.CommandText = "SET STATISTICS IO ON; SET STATISTICS XML ON;" + command.CommandText;
                                    var sw = Stopwatch.StartNew();
                                    using (var reader = await command.ExecuteReaderAsync())
                                        do
                                        {
                                            while (await reader.ReadAsync())
                                                if (reader.FieldCount == 1 && reader.GetValue(0) is string xml && xml.StartsWith("<"))
                                                    foreach (var op in XDocument.Parse(xml).Descendants()
                                                        .Where(e => e.Name.LocalName == "RelOp").Select(e => (string)e.Attribute("PhysicalOp")))
                                                        operators.Add(op);
                                        } while (await reader.NextResultAsync());
                                    sw.Stop();
                                    if (run > 0) { elapsed.Add(sw.ElapsedMilliseconds); logicalReads.Add(reads); }
                                }
                            }
                            finally { fixture.Connection.InfoMessage -= handler; }
                        }
                        Console.WriteLine($"Synthetic 200k people/1m facts; {days} days; {(query == PromptCategoryReportSql.Mix ? "mix" : "trend")}; " +
                            $"median {elapsed.OrderBy(x => x).ElementAt(1)}ms / {logicalReads.OrderBy(x => x).ElementAt(1)} logical reads; " +
                            $"operators {string.Join(", ", operators.OrderBy(x => x))}.");
                        Assert.IsTrue(operators.Count > 0, "Capture the actual plan operator, not only elapsed time.");
                    }
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _master;
            private readonly string _name;
            public SqlConnection Connection { get; }
            private Fixture(string master, string name, SqlConnection connection)
            {
                _master = master; _name = name; Connection = connection;
            }
            public static async Task<Fixture> CreateAsync()
            {
                var connection = System.Configuration.ConfigurationManager.ConnectionStrings["SPOInsightsEntities"]?.ConnectionString;
                if (connection == null) Assert.Inconclusive("A synthetic LocalDB test configuration is required.");
                var builder = new SqlConnectionStringBuilder(connection);
                if (builder.DataSource.IndexOf("(localdb)", StringComparison.OrdinalIgnoreCase) < 0)
                    Assert.Inconclusive("The isolated SQL fixture only runs on LocalDB.");
                var name = "PromptCategoryReportFixture_" + Guid.NewGuid().ToString("N");
                var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
                using (var db = new SqlConnection(master))
                {
                    await db.OpenAsync();
                    using (var command = db.CreateCommand())
                    {
                        command.CommandText = "CREATE DATABASE [" + name + "]";
                        await command.ExecuteNonQueryAsync();
                    }
                }
                builder.InitialCatalog = name;
                var sql = new SqlConnection(builder.ConnectionString);
                var fixture = new Fixture(master, name, sql);
                try
                {
                    await sql.OpenAsync();
                    await fixture.ExecuteAsync(@"CREATE TABLE dbo.copilot_interactions(id int PRIMARY KEY,user_id int NOT NULL,created_utc datetime NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_created_utc ON dbo.copilot_interactions(created_utc);
                    CREATE NONCLUSTERED INDEX IX_user_id_created_utc ON dbo.copilot_interactions(user_id,created_utc);
                    CREATE TABLE dbo.copilot_interaction_import_log(id int PRIMARY KEY);");
                    var ddl = Stopwatch.StartNew();
                    await fixture.ExecuteAsync(AddPromptCategories.Up_Sql);
                    Console.WriteLine($"Synthetic empty-table migration creation: {ddl.ElapsedMilliseconds}ms; no existing index rebuild.");
                    await fixture.ExecuteAsync(@"INSERT dbo.copilot_prompt_taxonomies VALUES(REPLICATE(N'a',64),N'[]'),(REPLICATE(N'b',64),N'[]');");
                    return fixture;
                }
                catch
                {
                    fixture.Dispose();
                    throw;
                }
            }
            public async Task ExecuteAsync(string sql)
            {
                using (var command = Connection.CreateCommand())
                {
                    command.CommandTimeout = 180;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
            }
            public SqlCommand Command(string sql, ReportUserScope scope, int days = 30)
            {
                var command = Connection.CreateCommand();
                command.CommandTimeout = 60;
                command.CommandText = ReportScopeSql.Apply(sql, scope);
                command.Parameters.AddWithValue("@from", DateTime.UtcNow.Date.AddDays(-days));
                command.Parameters.AddWithValue("@version", new string('a', 64));
                if (scope.IsRestricted)
                    command.Parameters.Add(new SqlParameter("@scopeUsers", SqlDbType.NVarChar, -1) { Value = scope.ToJson() });
                return command;
            }
            public async Task<List<object[]>> ReadAsync(string sql, ReportUserScope scope)
            {
                using (var command = Command(sql, scope))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    var rows = new List<object[]>();
                    while (await reader.ReadAsync())
                    {
                        var row = new object[reader.FieldCount];
                        reader.GetValues(row);
                        rows.Add(row);
                    }
                    return rows;
                }
            }
            public void Dispose()
            {
                Connection.Dispose();
                SqlConnection.ClearAllPools();
                using (var master = new SqlConnection(_master))
                {
                    master.Open();
                    using (var command = master.CreateCommand())
                    {
                        command.CommandText = "DROP DATABASE [" + _name + "]";
                        command.ExecuteNonQuery();
                    }
                }
            }
        }
    }
}
