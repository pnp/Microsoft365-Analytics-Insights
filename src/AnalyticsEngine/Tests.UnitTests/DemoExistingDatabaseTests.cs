using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("DemoGenerator")]
    [TestCategory("Integration")]
    [DoNotParallelize]
    public class DemoExistingDatabaseTests
    {
        [TestMethod]
        public void ExistingSchema_TextCompatibilityAllowsOnlyLosslessWidening()
        {
            var ascii = new DemoColumn("country_name", SqlDbType.VarChar, 250);
            var unicode = new DemoColumn("name", SqlDbType.NVarChar, 250);
            Assert.IsTrue(SqlExistingDemoDatabase.Compatible(ascii, "nvarchar", 500));
            Assert.IsTrue(SqlExistingDemoDatabase.Compatible(ascii, "nvarchar", -1));
            Assert.IsFalse(SqlExistingDemoDatabase.Compatible(ascii, "nvarchar", 498));
            Assert.IsTrue(SqlExistingDemoDatabase.Compatible(ascii, "varchar", 250));
            Assert.IsFalse(SqlExistingDemoDatabase.Compatible(unicode, "varchar", 1000));
            Assert.IsFalse(SqlExistingDemoDatabase.Compatible(unicode, "varchar", -1));
            Assert.IsTrue(SqlExistingDemoDatabase.Compatible(unicode, "nvarchar", 500));
        }

        [TestMethod]
        public void ExistingFullDemo_AppendsTwicePreservingRowsAndRemappingThePopulation()
        {
            WithDatabase((name, options) =>
            {
                using (var database = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    database.Open(null);
                    var summary = DemoCommand.NewSummary(options);
                    using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                        new DemoGenerator(options).Generate(sink, summary, null);
                    database.ValidateAndComplete(summary, null);
                }
                using (var connection = Connect(name))
                {
                    int sentinel;
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"INSERT dbo.users (user_name,mail,azure_ad_id,account_enabled,last_updated)
OUTPUT INSERTED.id VALUES ('sentinel@contoso.example',N'sentinel@contoso.example',
N'00000000-0000-0000-0000-000000000000',0,'20020101');";
                        sentinel = Convert.ToInt32(command.ExecuteScalar());
                    }
                    Execute(connection, @"INSERT dbo.audit_events (id,user_id,operation_id,time_stamp)
SELECT '00000000-0000-0000-0000-000000000000',id,
(SELECT TOP (1) id FROM dbo.event_operations ORDER BY id),'20020101'
FROM dbo.users WHERE user_name='sentinel@contoso.example';");
                    Execute(connection, @"UPDATE dbo.file_metadata_property_values
SET field_value=N'Contoso sentinel metadata',tag_guid=NULL,updated='20020101'
WHERE url_id=1 AND field_id=1;");
                    long originalEvents = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.audit_events;");
                    long originalSessions = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.sessions;");
                    long originalInteractionSessions = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.copilot_interaction_sessions;");
                    long originalHits = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.hits;");
                    long originalCounts = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.copilot_user_count_log;");
                    long originalDepartments = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_departments;");
                    long originalLicences = Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.license_types;");
                    const string unicodeUrlsSql = "SELECT COUNT_BIG(*) FROM dbo.urls WHERE full_url LIKE N'%\u039a\u03b1\u03bb\u03b7\u03bc\u03ad\u03c1\u03b1%';";
                    long originalUnicodeUrls = Scalar(connection, unicodeUrlsSql);
                    var originalRows = Counts(connection);
                    var namespacedObjects = new[]
                    {
                        "power_app_environments", "power_apps", "power_automate_flows", "power_bi_workspaces",
                        "power_bi_reports", "power_bi_dashboards", "copilot_studio_bots", "yammer_groups",
                        "sites", "webs", "urls", "copilot_ai_system_plugins", "online_meetings"
                    };
                    Assert.IsTrue(originalEvents > 1 && originalSessions > 0 && originalInteractionSessions > 0);

                    for (int run = 1; run <= 2; run++)
                    {
                        var before = Counts(connection);
                        var messages = new List<string>();
                        var appendOptions = Options(null, profiles: true);
                        Assert.IsNull(appendOptions.Database, "The connection string, not --database, supplies an existing target.");
                        var summary = DemoCommand.NewSummary(appendOptions);
                        using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, appendOptions, CancellationToken.None))
                        {
                            database.Open(messages.Add);
                            using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                                new DemoGenerator(appendOptions).Generate(sink, summary, null);
                            database.Complete(summary, messages.Add);
                            Assert.ThrowsException<InvalidOperationException>(() => database.CreateSink());
                            Assert.ThrowsException<InvalidOperationException>(() => database.Complete(summary, null));
                        }
                        var after = Counts(connection);
                        Assert.AreEqual(after.Sum(p => p.Value - before[p.Key]), summary.TotalRows,
                            "Summary rows must count committed inserts, not reused dimensions or skipped tenant snapshots.");
                        Assert.AreEqual(0, summary.CompletedProfileWeeks);
                        Assert.AreEqual(0L, summary.Rows[DemoTables.CopilotCounts.Name]);
                        Assert.IsTrue(messages.Any(m => m.Contains("Skipped") && m.Contains("tenant-wide")));
                        Assert.IsTrue(messages.Any(m => m.Contains("reused dimension")));
                        Assert.IsTrue(messages.Any(m => m.Contains("Global weekly profiling is skipped")));
                        Assert.AreEqual(options.Users * (run + 1) + 1L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                        Assert.AreEqual(originalCounts, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.copilot_user_count_log;"));
                        Assert.AreEqual(originalDepartments, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_departments;"));
                        Assert.AreEqual(originalLicences, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.license_types;"));
                        foreach (string table in namespacedObjects)
                        {
                            Assert.IsTrue(originalRows[table] > 0, "The full demo must exercise " + table);
                            Assert.AreEqual(originalRows[table] * (run + 1), after[table],
                                "Each append needs fresh namespaced objects in " + table);
                        }
                        foreach (string table in new[] { "power_platform_connectors", "power_platform_client_types",
                            "copilot_ai_models", "file_field_definitions" })
                            Assert.AreEqual(originalRows[table], after[table], "Stable lookup names must be reused in " + table);
                        Assert.AreEqual(originalRows["file_metadata_property_values"] * (run + 1),
                            after["file_metadata_property_values"], "Each namespaced page must receive its own immutable metadata.");
                        Assert.AreEqual(originalUnicodeUrls * (run + 1), Scalar(connection, unicodeUrlsSql),
                            "Resource namespacing must retain the original Unicode paths, not inflate them with percent encoding.");
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM (
SELECT url_id,field_id FROM dbo.file_metadata_property_values
GROUP BY url_id,field_id HAVING COUNT_BIG(*)>1) duplicates;"));
                        Assert.AreEqual(1L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.file_metadata_property_values
WHERE url_id=1 AND field_id=1 AND field_value=N'Contoso sentinel metadata'
AND tag_guid IS NULL AND updated='20020101';"), "Existing page metadata must not be reshaped.");
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM (
SELECT yammer_group_id,[date] FROM dbo.yammer_group_activity_log
GROUP BY yammer_group_id,[date] HAVING COUNT_BIG(*)>1) duplicates;"),
                            "A new sample population must not masquerade as another snapshot of an existing group.");
                        Assert.AreEqual((originalEvents - 1) * (run + 1) + 1,
                            Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.audit_events;"));
                        Assert.AreEqual(originalSessions * run, Scalar(connection,
                            "SELECT COUNT_BIG(*) FROM dbo.sessions WHERE user_id>" + sentinel + ";"));
                        Assert.AreEqual(originalInteractionSessions * run, Scalar(connection,
                            "SELECT COUNT_BIG(*) FROM dbo.copilot_interaction_sessions WHERE user_id>" + sentinel + ";"));
                        Assert.AreEqual(originalHits * run, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.hits h
JOIN dbo.sessions s ON s.id=h.session_id WHERE s.user_id>" + sentinel + ";"));
                        Assert.AreEqual(originalRows["hits_clicked_elements"] * run, Scalar(connection,
                            @"SELECT COUNT_BIG(*) FROM dbo.hits_clicked_elements c
JOIN dbo.hits h ON h.id=c.hit_id JOIN dbo.sessions s ON s.id=h.session_id WHERE s.user_id>" + sentinel + ";"),
                            "Click references must remap the hit identity even when its projected column is last.");
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.users
WHERE id>" + sentinel + " AND manager_id IS NOT NULL AND manager_id<=" + sentinel + ";"));
                        Assert.IsTrue(Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.users
WHERE id>" + sentinel + " AND manager_id IS NOT NULL;") > 0, "Exercise manager remapping, not just NULL managers.");
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.users u
JOIN dbo.users manager ON manager.id=u.manager_id
WHERE u.id>" + sentinel + " AND (u.department_id<>manager.department_id OR u.company_name_id<>manager.company_name_id);"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.copilot_interactions i
JOIN dbo.copilot_interaction_sessions s ON s.id=i.session_id WHERE s.user_id<>i.user_id;"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.copilot_chats c
JOIN dbo.audit_events a ON a.id=c.event_id WHERE a.user_id<>c.user_id OR a.time_stamp<>c.time_stamp;"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.event_meta_power_app e
JOIN dbo.audit_events a ON a.id=e.event_id WHERE a.user_id>" + sentinel
                            + " AND e.power_app_id<=" + originalRows["power_apps"] + ";"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.event_meta_power_app_share e
JOIN dbo.audit_events a ON a.id=e.event_id WHERE a.user_id>" + sentinel
                            + " AND e.shared_with_user_id<=" + sentinel + ";"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.copilot_interactions i
WHERE LEFT(i.graph_interaction_id,32)<>i.request_id
OR NOT EXISTS (SELECT 1 FROM dbo.audit_events a WHERE REPLACE(CONVERT(varchar(36),a.id),'-','')=i.request_id);"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.copilot_interaction_sessions s
WHERE NOT EXISTS (SELECT 1 FROM dbo.copilot_chats c WHERE c.thread_id=s.session_ref AND c.user_id=s.user_id);"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.users WHERE id>" + sentinel
                            + " AND (user_name NOT LIKE 'demo-%@contoso.example' OR mail<>user_name OR LEN(azure_ad_id)<>36);"));
                        Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM (
SELECT azure_ad_id FROM dbo.users GROUP BY azure_ad_id HAVING COUNT_BIG(*)>1) duplicates;"));
                        Assert.AreEqual(1L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.users
WHERE user_name='sentinel@contoso.example' AND mail=N'sentinel@contoso.example'
AND azure_ad_id=N'00000000-0000-0000-0000-000000000000' AND account_enabled=0 AND last_updated='20020101';"));
                        Assert.AreEqual(1L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.audit_events
WHERE id='00000000-0000-0000-0000-000000000000' AND time_stamp='20020101' AND user_id=" + sentinel + ";"));
                        Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM profiling.ActivitiesWeeklyColumns;"));
                        Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM profiling.UsageWeekly;"));

                        if (run == 1)
                        {
                            Assert.AreEqual(1L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM sys.extended_properties
WHERE class=0 AND name=N'M365AnalyticsSyntheticDemoState' AND CONVERT(nvarchar(4000),value)=N'modified-by-append';"));
                            using (var rerun = new SqlDemoDatabase(options, CancellationToken.None))
                                Assert.ThrowsException<InvalidOperationException>(() => rerun.Open(null));
                            // An ordinary current-schema application DB has no new-demo markers.
                            // The second identical append must also support that state, without claiming it.
                            Execute(connection, @"EXEC sys.sp_dropextendedproperty @name=N'M365AnalyticsSyntheticDemo';
EXEC sys.sp_dropextendedproperty @name=N'M365AnalyticsSyntheticDemoState';
EXEC sys.sp_dropextendedproperty @name=N'M365AnalyticsSyntheticDemoFingerprint';");
                        }
                        else
                            Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM sys.extended_properties
WHERE class=0 AND name LIKE N'M365AnalyticsSyntheticDemo%';"));
                    }
                }
            });
        }

        [TestMethod]
        public void ExistingAppend_NamespacesGuidFormatsAndMaximumLengthStringReferencesConsistently()
        {
            WithDatabase((name, schemaOptions) =>
            {
                CreateSchema(schemaOptions);
                var options = DemoOptions.Parse(new[] { "--users", "1",
                    "--days", "31", "--as-of", "2026-09-01", "--no-profiles" }, new DateTime(2026, 9, 1), existingTarget: true);
                var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
                string session = id.ToString("N") + new string('x', 18);
                string thread = id.ToString("D") + "-" + new string('x', 413);
                string graph = id.ToString("N") + "-" + new string('x', 167);
                string upn = new string('a', 200) + "@contoso.example";
                const string resourceBase = "https://contoso.example/";
                string resource = resourceBase + new string('x', 850 - resourceBase.Length - "/contoso-demo-".Length - 32);
                using (var connection = Connect(name))
                {
                    for (int run = 0; run < 2; run++)
                    {
                        var summary = DemoCommand.NewSummary(options);
                        using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, CancellationToken.None))
                        {
                            database.Open(null);
                            using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                            {
                                sink.Write(DemoTables.Operations, 1, "Contoso boundary operation");
                                sink.Write(DemoTables.InteractionTypes, 1, "userPrompt");
                                sink.Write(DemoTables.InteractionApps, 1, "Contoso boundary app");
                                sink.Write(DemoTables.ConversationTypes, 1, "bizchat");
                                sink.Write(DemoTables.Urls, 1, resource);
                                sink.Write(DemoTables.Users, 1, upn, upn, id.ToString(), true, options.AsOf,
                                    "00000", null, null, null, null, null, null, null, null);
                                sink.Write(DemoTables.Sessions, 1, session, 1);
                                sink.Write(DemoTables.InteractionSessions, 1, thread, 1);
                                sink.Write(DemoTables.Audit, id, 1, 1, options.AsOf);
                                sink.Write(DemoTables.Chats, id, "Contoso", null, thread, "US", "1", 1, options.AsOf);
                                sink.Write(DemoTables.Interactions, graph, 1, 1, id.ToString("N"), 1, 1, 1,
                                    options.AsOf, 10, 2, 0, 0, 0, 0, null, null, null);
                                sink.Flush();
                            }
                            database.Complete(summary, null);
                        }
                    }
                    Assert.AreEqual(2L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                    Assert.AreEqual(2L, Scalar(connection, "SELECT COUNT_BIG(DISTINCT user_name) FROM dbo.users;"));
                    Assert.AreEqual(2L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.urls
WHERE LEN(full_url)=850 AND full_url LIKE N'https://contoso.example/contoso-demo-%';"),
                        "Exactly fitting namespaced URLs must persist without truncation.");
                    Assert.AreEqual(2L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.users
WHERE user_name LIKE 'demo-%@contoso.example' AND mail=user_name AND LEN(user_name)<=250;"));
                    Assert.AreEqual(2L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM dbo.audit_events a
JOIN dbo.users u ON u.id=a.user_id AND u.azure_ad_id=CONVERT(nvarchar(36),a.id)
JOIN dbo.copilot_chats c ON c.event_id=a.id
JOIN dbo.copilot_interaction_sessions s ON s.user_id=u.id AND s.session_ref=c.thread_id
JOIN dbo.copilot_interactions i ON i.session_id=s.id AND i.user_id=u.id
JOIN dbo.sessions web ON web.user_id=u.id
WHERE i.request_id=REPLACE(CONVERT(varchar(36),a.id),'-','')
AND LEFT(i.graph_interaction_id,32)=i.request_id AND LEN(i.graph_interaction_id)=200
AND LEFT(s.session_ref,36)=CONVERT(varchar(36),a.id) AND LEN(s.session_ref)=450
AND LEFT(web.ai_session_id,32)=i.request_id AND LEN(web.ai_session_id)=50;"));
                }
            });
        }

        [TestMethod]
        public void ExistingSchema_PreflightRejectsRequiredColumnsMissingTablesAndUnsupportedForeignKeysBeforeWrites()
        {
            WithDatabase((name, options) =>
            {
                CreateSchema(options);
                using (var connection = Connect(name))
                {
                    Execute(connection, "ALTER TABLE dbo.users ADD contoso_required int NOT NULL;");
                    AssertPreflightRefused(connection, options, "contoso_required");
                    Execute(connection, "ALTER TABLE dbo.users DROP COLUMN contoso_required;");

                    Execute(connection, @"CREATE TABLE dbo.ContosoUnsupported (id int NOT NULL PRIMARY KEY);
ALTER TABLE dbo.user_departments ADD CONSTRAINT FK_ContosoUnsupported
FOREIGN KEY (id) REFERENCES dbo.ContosoUnsupported(id);");
                    AssertPreflightRefused(connection, options, "foreign-key");
                    Execute(connection, "ALTER TABLE dbo.user_departments DROP CONSTRAINT FK_ContosoUnsupported;");

                    Execute(connection, @"DECLARE @name sysname = (
SELECT f.name FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
JOIN sys.columns c ON c.object_id=fc.parent_object_id AND c.column_id=fc.parent_column_id
WHERE f.parent_object_id=OBJECT_ID('dbo.sessions') AND c.name='user_id');
DECLARE @sql nvarchar(max)=N'ALTER TABLE dbo.sessions DROP CONSTRAINT ' + QUOTENAME(@name);
EXEC sys.sp_executesql @sql;");
                    AssertPreflightRefused(connection, options, "missing foreign-key metadata for user_id");
                    Execute(connection, @"ALTER TABLE dbo.sessions ADD CONSTRAINT FK_ContosoSessionUser
FOREIGN KEY (user_id) REFERENCES dbo.users(id);");

                    Execute(connection, "ALTER TABLE dbo.copilot_user_count_log DROP COLUMN average_prompts_submitted;");
                    AssertPreflightRefused(connection, options, "average_prompts_submitted");
                    Execute(connection, "DROP TABLE dbo.copilot_user_count_log;");
                    AssertPreflightRefused(connection, options, "missing table");
                    Assert.AreEqual(1L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM sys.extended_properties
WHERE class=0 AND name=N'M365AnalyticsSyntheticDemoState' AND CONVERT(nvarchar(4000),value)=N'building';"));
                }
            });
        }

        [TestMethod]
        public void ExistingSchema_OmittedNullableForeignKeysStayNullAndRequiredDefaultsRemainSupported()
        {
            WithDatabase((name, options) =>
            {
                CreateSchema(options);
                using (var connection = Connect(name))
                {
                    Execute(connection, @"CREATE TABLE dbo.ContosoUnusedLookup (id int NOT NULL PRIMARY KEY);
INSERT dbo.ContosoUnusedLookup (id) VALUES (1);
ALTER TABLE dbo.users ADD contoso_unused_id int NULL CONSTRAINT DF_ContosoUnused DEFAULT (1),
contoso_required_default_id int NOT NULL CONSTRAINT DF_ContosoRequiredDefault DEFAULT (1);
ALTER TABLE dbo.users ADD CONSTRAINT FK_ContosoUnused FOREIGN KEY (contoso_unused_id)
REFERENCES dbo.ContosoUnusedLookup(id),
CONSTRAINT FK_ContosoRequiredDefault FOREIGN KEY (contoso_required_default_id)
REFERENCES dbo.ContosoUnusedLookup(id);");
                    var summary = DemoCommand.NewSummary(options);
                    using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, CancellationToken.None))
                    {
                        database.Open(null);
                        using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                            new DemoGenerator(options).Generate(sink, summary, null);
                        database.Complete(summary, null);
                    }
                    Assert.AreEqual((long)options.Users, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users WHERE contoso_unused_id IS NOT NULL;"));
                    Assert.AreEqual((long)options.Users, Scalar(connection,
                        "SELECT COUNT_BIG(*) FROM dbo.users WHERE contoso_required_default_id=1;"));
                }
            });
        }

        [TestMethod]
        public void ExistingTarget_RejectsSystemImplicitAttachedAndAbsentCatalogsWithoutCreatingAnything()
        {
            string name = "ContosoDemo_Absent_" + Guid.NewGuid().ToString("N");
            var options = Options(name);
            foreach (var catalog in new[] { "master", "model", "msdb", "tempdb", "mssqlsystemresource", "" })
                Assert.ThrowsException<ArgumentException>(() =>
                    new SqlExistingDemoDatabase(SqlDemoDatabase.LocalConnection(catalog), options, CancellationToken.None));
            var attached = new SqlConnectionStringBuilder(SqlDemoDatabase.LocalConnection(name)) { AttachDBFilename = "Contoso.mdf" };
            Assert.ThrowsException<ArgumentException>(() =>
                new SqlExistingDemoDatabase(attached.ConnectionString, options, CancellationToken.None));
            using (var absent = new SqlExistingDemoDatabase(SqlDemoDatabase.LocalConnection(name), options, CancellationToken.None))
            {
                Assert.ThrowsException<SqlException>(() => absent.Open(null));
                Assert.ThrowsException<InvalidOperationException>(() => absent.CreateSink());
                Assert.ThrowsException<InvalidOperationException>(() => absent.Complete(DemoCommand.NewSummary(options), null));
            }
            using (var master = Connect("master"))
            using (var command = master.CreateCommand())
            {
                command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END;";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
                Assert.AreEqual(0, Convert.ToInt32(command.ExecuteScalar()));
            }
        }

        [TestMethod]
        public void ExistingAppend_ErrorCancellationAndAbandonedBuffersNeverFlushOrReportSuccess()
        {
            WithDatabase((name, options) =>
            {
                CreateSchema(options);
                using (var connection = Connect(name))
                {
                    using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, CancellationToken.None))
                    {
                        database.Open(null);
                        using (var sink = database.CreateSink())
                        {
                            sink.Write(DemoTables.Departments, 1, "Contoso committed department");
                            sink.Flush();
                            sink.Write(DemoTables.Jobs, 1, "Contoso rolled-back job");
                            sink.Write(DemoTables.Users, 1, "failure@contoso.example", "failure@contoso.example",
                                Guid.Empty.ToString(), true, options.AsOf, "00000", null, null, null, null, null, null, null, 999);
                            var failure = Assert.ThrowsException<InvalidOperationException>(() => sink.Flush());
                            StringAssert.Contains(failure.Message, "batch-committed");
                            Assert.ThrowsException<InvalidOperationException>(() => sink.Flush());
                        }
                        Assert.ThrowsException<InvalidOperationException>(() => database.Complete(DemoCommand.NewSummary(options), null));
                    }
                    Assert.AreEqual(1L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_departments;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_job_titles;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));

                    using (var cancellation = new CancellationTokenSource())
                    using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, cancellation.Token))
                    {
                        database.Open(null);
                        using (var sink = database.CreateSink())
                        {
                            sink.Write(DemoTables.Departments, 1, "Contoso cancellation department");
                            sink.Flush();
                            sink.Write(DemoTables.Jobs, 1, "Contoso abandoned job");
                            cancellation.Cancel();
                            Assert.ThrowsException<OperationCanceledException>(() => sink.Flush());
                        }
                        Assert.ThrowsException<InvalidOperationException>(() => database.Complete(DemoCommand.NewSummary(options), null));
                    }
                    using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, CancellationToken.None))
                    {
                        database.Open(null);
                        using (var sink = database.CreateSink())
                            sink.Write(DemoTables.Jobs, 1, "Contoso unflushed job");
                        Assert.ThrowsException<InvalidOperationException>(() => database.Complete(DemoCommand.NewSummary(options), null));
                    }
                    using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, CancellationToken.None))
                    {
                        database.Open(null);
                        using (var sink = database.CreateSink())
                        {
                            const string resourceBase = "https://contoso.example/";
                            var tooWideAfterNamespacing = resourceBase + new string('x', 850 - resourceBase.Length);
                            Assert.ThrowsException<InvalidOperationException>(() =>
                                sink.Write(DemoTables.Urls, 1, tooWideAfterNamespacing));
                        }
                        Assert.ThrowsException<InvalidOperationException>(() => database.Complete(DemoCommand.NewSummary(options), null));
                    }
                    using (var cancellation = new CancellationTokenSource())
                    {
                        cancellation.Cancel();
                        using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, cancellation.Token))
                        {
                            Assert.ThrowsException<OperationCanceledException>(() => database.Open(null));
                            Assert.ThrowsException<InvalidOperationException>(() => database.CreateSink());
                        }
                    }
                    Assert.AreEqual(2L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_departments;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_job_titles;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.urls;"),
                        "An over-width namespaced resource must be refused, never silently truncated.");
                    Assert.AreEqual(0L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM sys.extended_properties
WHERE class=0 AND name=N'M365AnalyticsSyntheticDemoState' AND CONVERT(nvarchar(4000),value)=N'complete';"));
                }
            });
        }

        private static void AssertPreflightRefused(SqlConnection connection, DemoOptions options, string expected)
        {
            using (var database = new SqlExistingDemoDatabase(connection.ConnectionString, options, CancellationToken.None))
            {
                var exception = Assert.ThrowsException<InvalidOperationException>(() => database.Open(null));
                StringAssert.Contains(exception.Message, expected);
                Assert.ThrowsException<InvalidOperationException>(() => database.CreateSink());
            }
            Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
            Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.user_departments;"));
        }

        private static Dictionary<string, long> Counts(SqlConnection connection) =>
            DemoTables.All.ToDictionary(t => t.Name, t => Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.[" + t.Name + "];"));

        private static DemoOptions Options(string name, bool profiles = false) => DemoOptions.Parse(new[]
        {
            "--users", "30", "--days", "31", "--as-of", "2026-09-01",
            "--skus", "10", "--batch-size", "40"
        }.Concat(name == null ? new string[0] : new[] { "--database", name })
            .Concat(profiles ? new string[0] : new[] { "--no-profiles" }).ToArray(),
            new DateTime(2026, 9, 1), existingTarget: name == null);

        private static void CreateSchema(DemoOptions options)
        {
            using (var database = new SqlDemoDatabase(options, CancellationToken.None)) database.Open(null);
        }

        private static SqlConnection Connect(string name)
        {
            var connection = new SqlConnection(SqlDemoDatabase.LocalConnection(name));
            connection.Open();
            return connection;
        }

        private static long Scalar(SqlConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static void Execute(SqlConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static void WithDatabase(Action<string, DemoOptions> test)
        {
            string name = "ContosoDemo_AppendTest_" + Guid.NewGuid().ToString("N");
            try { test(name, Options(name)); }
            finally
            {
                using (var master = Connect("master"))
                using (var command = master.CreateCommand())
                {
                    command.CommandText = "IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [" + name
                        + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + name + "]; END;";
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
