using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Common.Entities.CopilotCosts;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotCostCollectorTests
    {
        [TestMethod]
        public async Task DisabledAndMissingStorageConstructNothing()
        {
            var calls = 0;
            Func<CancellationToken, Task<string>> factory = _ => { calls++; throw new InvalidOperationException(); };
            Assert.AreEqual("disabled", await CostCollectorRuntime.RunIfEnabledAsync(false, "configured", factory, CancellationToken.None));
            Assert.AreEqual("storageRequired", await CostCollectorRuntime.RunIfEnabledAsync(true, null, factory, CancellationToken.None));
            Assert.AreEqual(0, calls);
            Assert.AreEqual("sourceFailure", await CostCollectorRuntime.RunIfEnabledAsync(true, "configured", factory, CancellationToken.None));
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void FreshnessIsNotAnAccountingMonthAndFilteredInventoryIsRejected()
        {
            var json = User("00000000-0000-0000-0000-000000000001", "workIQ", 0.125);
            var parsed = CostGraphSource.ParseUser(json, (string)json["id"], new DateTimeOffset(2001, 2, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.AreEqual(0.125, parsed.Services.Single().ConsumedQuantity);
            Assert.AreEqual(new DateTimeOffset(2001, 1, 31, 23, 0, 0, TimeSpan.Zero), parsed.Services.Single().Freshness.LastModifiedDateTime);
            Assert.IsNull(parsed.SourceMonth);
            Assert.AreEqual("unverified", parsed.PeriodStatus);
            Assert.ThrowsException<CostConnectionException>(() =>
                CostGraphSource.ValidateDirectoryPage("https://graph.microsoft.com/v1.0/users?$select=id&$filter=accountEnabled%20eq%20true"));
            json["serviceBalances"][0]["dataStatus"]["lastModifiedDateTime"] = "yesterday";
            Assert.ThrowsException<CostConnectionException>(() => CostGraphSource.ParseUser(json, (string)json["id"], DateTimeOffset.UtcNow));
        }

        [TestMethod]
        public async Task TypedPolicyChildrenTenantBalancesAndUnknownValuesArePreserved()
        {
            using (var http = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("getTenantCreditBalance"))
                    return JObject.Parse(@"{'billingMethodBalances':[{'billingMethod':'futureMethod','consumedQuantity':0.25,'remainingQuantity':null,'unit':'copilotCredit','dataStatus':{'lastModifiedDateTime':'2001-01-01T00:00:00Z'}}]}");
                return JObject.Parse(@"{'value':[{'id':'00000000-0000-0000-0000-000000000002','@odata.type':'#microsoft.graph.selectedGroupsSpendingPolicy','displayName':'Contoso Preview','status':'futureState','periodLimit':{'@odata.type':'#microsoft.graph.unlimitedCostManagementQuantity'},'perUserLimit':{'@odata.type':'#microsoft.graph.futureQuantity','value':0.125}}]}");
            })))
            {
                var source = new CostGraphSource(http, "synthetic-token");
                var policy = await source.ReadContextPageAsync(new CostContextTask
                {
                    Kind = "policies", Url = "https://graph.microsoft.com/beta/copilot/costManagement/spendingPolicies"
                }, CancellationToken.None);
                Assert.AreEqual(2, policy.Following.Count);
                StringAssert.Contains(policy.Document, "futureState");
                StringAssert.Contains(policy.Document, "futureQuantity");
                var tenant = await source.ReadContextPageAsync(new CostContextTask
                {
                    Kind = "tenant", Url = "https://graph.microsoft.com/beta/copilot/costManagement/getTenantCreditBalance"
                }, CancellationToken.None);
                StringAssert.Contains(tenant.Document, "0.25");
                StringAssert.Contains(tenant.Document, "\"RemainingQuantity\":null");
            }
        }

        internal static JObject User(string id, string category, double amount) => new JObject
        {
            ["id"] = id, ["selectionDataStatus"] = new JObject { ["lastModifiedDateTime"] = "2001-01-31T23:00:00Z" },
            ["unevaluatedServices"] = new JArray(),
            ["serviceBalances"] = new JArray(new JObject
            {
                ["serviceId"] = category, ["spendingPolicyId"] = "Contoso-policy", ["consumedQuantity"] = amount,
                ["unit"] = "copilotCredit", ["isConsumptionAllowed"] = true,
                ["dataStatus"] = new JObject { ["lastModifiedDateTime"] = "2001-01-31T23:00:00Z" },
                ["remainingQuantity"] = new JObject { ["@odata.type"] = "#microsoft.graph.unlimitedCostManagementQuantity" }
            })
        };

        internal sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, JObject> _read;
            public Handler(Func<HttpRequestMessage, JObject> read) { _read = read; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Assert.AreEqual(HttpMethod.Get, request.Method);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_read(request).ToString()) });
            }
        }
    }

    [TestClass]
    [DoNotParallelize]
    public class CopilotCostCollectorSqlTests
    {
        private static readonly CancellationToken Ct = CancellationToken.None;
        private static readonly Guid User1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        private static readonly Guid User2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        private static string _instance, _connection, _folder;
        private static bool _instanceCreated;
        private SqlCostCollectorStore _store;
        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static void Start(TestContext context)
        {
            _instance = "CopilotCost769_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _instance);
            Directory.CreateDirectory(_folder);
            LocalDb("create " + _instance);
            _instanceCreated = true;
            LocalDb("start " + _instance);
            var master = $@"Server=(localdb)\{_instance};Database=master;Integrated Security=true;TrustServerCertificate=true";
            using (var db = new SqlConnection(master))
            {
                db.Open();
                var mdf = Path.Combine(_folder, "CopilotCostPreview.mdf").Replace("'", "''");
                var ldf = Path.Combine(_folder, "CopilotCostPreview_log.ldf").Replace("'", "''");
                using (var cmd = new SqlCommand(
                    $"CREATE DATABASE CopilotCostPreview ON (NAME=CopilotCostPreview,FILENAME=N'{mdf}') " +
                    $"LOG ON (NAME=CopilotCostPreview_log,FILENAME=N'{ldf}');", db))
                    cmd.ExecuteNonQuery();
            }
            _connection = $@"Server=(localdb)\{_instance};Database=CopilotCostPreview;Integrated Security=true;TrustServerCertificate=true";
        }

        [ClassCleanup]
        public static void Stop()
        {
            SqlConnection.ClearAllPools();
            if (_instanceCreated)
            {
                LocalDb("stop " + _instance);
                LocalDb("delete " + _instance);
                _instanceCreated = false;
            }
            if (_folder != null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        private static void LocalDb(string arguments)
        {
            var path = FindLocalDbExecutable(Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Assert.IsNotNull(path, "SQL LocalDB is required: no SqlLocalDB.exe was found on PATH or in installed SQL Server tools.");
            using (var process = Process.Start(new ProcessStartInfo(path, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode, output + error);
            }
        }

        internal static string FindLocalDbExecutable(string path, string programFiles)
        {
            var candidates = (path ?? string.Empty).Split(Path.PathSeparator)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.Combine(p.Trim().Trim('"'), "SqlLocalDB.exe"));
            var serverRoot = Path.Combine(programFiles, "Microsoft SQL Server");
            if (Directory.Exists(serverRoot))
            {
                candidates = candidates.Concat(Directory.GetDirectories(serverRoot)
                    .Select(p => new { Path = p, Version = int.TryParse(Path.GetFileName(p), out var v) ? v : -1 })
                    .Where(p => p.Version >= 0)
                    .OrderByDescending(p => p.Version)
                    .Select(p => Path.Combine(p.Path, "Tools", "Binn", "SqlLocalDB.exe")));
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        [TestInitialize]
        public void Reset()
        {
            Sql(AddCopilotCostPreview.Up_Sql);
            Sql(AddCopilotCostPreview.Purge_Sql);
            Sql(@"DELETE dbo.copilot_cost_services;DELETE dbo.copilot_cost_unevaluated;DELETE dbo.copilot_cost_observations;
DELETE dbo.copilot_cost_history;DELETE dbo.copilot_cost_context;DELETE dbo.copilot_cost_pages;
DELETE dbo.copilot_cost_work;DELETE dbo.copilot_cost_runs;
DELETE dbo.copilot_cost_control;
IF OBJECT_ID(N'dbo.users',N'U') IS NULL CREATE TABLE dbo.users(id int IDENTITY PRIMARY KEY,user_name varchar(250),azure_ad_id nvarchar(max));
DELETE dbo.users;");
            _store = new SqlCostCollectorStore(() => new SqlConnection(_connection));
        }

        private static int Sql(string sql)
        {
            using (var db = new SqlConnection(_connection))
            {
                db.Open();
                using (var cmd = new SqlCommand(sql, db) { CommandTimeout = 300 })
                {
                    var value = cmd.ExecuteScalar();
                    return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
                }
            }
        }

        private async Task<CostCollectorRun> Run(params Guid[] users)
        {
            var run = await _store.AcquireRunAsync(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, Ct);
            await _store.SaveInventoryPageAsync(run, users, null, Ct);
            await _store.LinkInventoryPageAsync(run, users, Ct);
            Sql($"UPDATE dbo.copilot_cost_runs SET context_phase=2 WHERE run_id='{run.Id}';");
            return run;
        }

        private static CostUserObservation Observation(Guid user, string category = "workIQ", double amount = 0.125)
        {
            return CostGraphSource.ParseUser(CopilotCostCollectorTests.User(user.ToString(), category, amount),
                user.ToString(), new DateTimeOffset(2001, 2, 1, 0, 0, 0, TimeSpan.Zero));
        }

        [TestMethod]
        public async Task AtomicPublicationRollsBackAllFactsHistoryAndWorkOnChildFailure()
        {
            var run = await Run(User1);
            Sql("CREATE OR ALTER TRIGGER dbo.cost_test_fail ON dbo.copilot_cost_services AFTER INSERT AS THROW 51000,'Synthetic child failure',1;");
            try
            {
                var ex = await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                    _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct));
                Assert.AreEqual("databaseUnavailable", ex.Status);
                Assert.AreEqual(0, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
                Assert.AreEqual(0, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations;"));
                Assert.AreEqual(0, Sql("SELECT SUM(attempts) FROM dbo.copilot_cost_work;"));
            }
            finally { Sql("DROP TRIGGER dbo.cost_test_fail;"); }
            await _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
        }

        [TestMethod]
        public async Task ExactUnicodeCategoryFractionNullableLinksAndSameDayCorrectionsSurvive()
        {
            Sql($"INSERT dbo.users(user_name,azure_ad_id) VALUES('synthetic@contoso.example','{User1}');");
            var run = await Run(User1, User2);
            var first = Observation(User1, "Καλημέρα");
            first.Services.Add(Observation(User1, "καλημέρα", 0).Services[0]);
            await _store.PublishAsync(run, first, _ => Task.CompletedTask, Ct);
            await _store.PublishAsync(run, first, _ => Task.CompletedTask, Ct);
            await _store.PublishAsync(run, Observation(User2), _ => Task.CompletedTask, Ct);
            Assert.AreEqual(3, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_services;"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NULL;"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NOT NULL;"));
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE source_month IS NULL AND period_status='unverified';"));
            var correction = Observation(User1, "Καλημέρα", 1.375);
            await _store.PublishAsync(run, correction, _ => Task.CompletedTask, Ct);
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_services;"));
            Assert.AreEqual(3, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_services WHERE consumed_quantity=1.375 AND category=N'Καλημέρα' COLLATE Latin1_General_100_BIN2;"));
        }

        [TestMethod]
        public async Task LaterInventoryRelinksRetainedFactsWithoutCreatingDirectoryUsers()
        {
            var run = await Run(User1, User2);
            await _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct);
            await _store.PublishAsync(run, Observation(User2), _ => Task.CompletedTask, Ct);
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NULL;"));
            Assert.AreEqual(0, Sql("SELECT COUNT(*) FROM dbo.users;"));
            Sql($"INSERT dbo.users(user_name,azure_ad_id) VALUES('synthetic@contoso.example','{User1}');");
            await _store.LinkInventoryPageAsync(run, new[] { User1, User2 }, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NOT NULL;"));
            Sql($@";WITH n AS(
 SELECT TOP(1005) ROW_NUMBER() OVER(ORDER BY(SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.copilot_cost_observations(observation_date,object_id,observed_at,quality,selection_status)
 SELECT DATEADD(day,n,CONVERT(date,'19900101')),'{User1}','2001-01-01T00:00:00+00:00',2,N'{{}}' FROM n;");
            await _store.LinkInventoryPageAsync(run, new[] { User1 }, Ct);
            Assert.AreEqual(5, Sql($"SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NULL AND object_id='{User1}';"));
            await _store.LinkInventoryPageAsync(run, new[] { User1 }, Ct);
            Assert.AreEqual(0, Sql($"SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NULL AND object_id='{User1}';"));
            Assert.AreEqual(1, Sql($"SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NULL AND object_id='{User2}';"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.users;"));
            await _store.LinkInventoryPageAsync(run, new[] { User1, User2 }, Ct);
            Assert.AreEqual(1006, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE user_id IS NOT NULL;"));
        }

        [TestMethod]
        public async Task OlderCompleteObservationCannotReplaceNewerSameDayFacts()
        {
            var run = await Run(User1);
            var newer = Observation(User1, amount: 2.5);
            newer.ObservedAt = newer.ObservedAt.AddHours(2);
            await _store.PublishAsync(run, newer, _ => Task.CompletedTask, Ct);
            var older = Observation(User1, amount: 0.125);
            await _store.PublishAsync(run, older, _ => Task.CompletedTask, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_services WHERE consumed_quantity=2.5;"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE DATEPART(hour,observed_at)=2;"));
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
            var correction = Observation(User1, amount: 3.75);
            correction.ObservedAt = newer.ObservedAt.AddHours(1);
            await _store.PublishAsync(run, correction, _ => Task.CompletedTask, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_services WHERE consumed_quantity=3.75;"));
        }

        [TestMethod]
        public async Task PartialCannotEraseCompleteAndPartialProgressIsRetryable()
        {
            var run = await Run(User1, User2);
            await _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct);
            var partial = Observation(User1);
            partial.Services.Clear();
            partial.UnevaluatedServices.Add(JObject.Parse("{'serviceId':'workIQ','reason':'futureReason'}"));
            await _store.PublishAsync(run, partial, _ => Task.CompletedTask, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_services;"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE object_id='00000000-0000-0000-0000-000000000001' AND quality=2;"));
            Assert.AreEqual(1, Sql("SELECT CONVERT(int,completed) FROM dbo.copilot_cost_work WHERE object_id='00000000-0000-0000-0000-000000000001';"));
            var partial2 = Observation(User2);
            partial2.UnevaluatedServices.Add(JObject.Parse("{'serviceId':'future','reason':'sourceUnavailable'}"));
            await _store.PublishAsync(run, partial2, _ => Task.CompletedTask, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_work WHERE completed=0 AND attempts>0;"));
        }

        [TestMethod]
        public async Task ExpiredOwnerCannotPublishAfterReplacementAndGenerationGuardIsAtomic()
        {
            var run = await Run(User1);
            Sql($"UPDATE dbo.copilot_cost_runs SET owned_until=DATEADD(second,-1,SYSDATETIMEOFFSET()) WHERE run_id='{run.Id}';");
            var next = await _store.AcquireRunAsync(run.Generation, DateTimeOffset.UtcNow, Ct);
            Assert.IsTrue(next.Fence > run.Fence);
            await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct));
            await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.PublishAsync(next, Observation(User1), _ => throw new CostConnectionException("busy"), Ct));
            Assert.AreEqual(0, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
            await _store.PublishAsync(next, Observation(User1), _ => Task.CompletedTask, Ct);
        }

        [TestMethod]
        public async Task PublicationAndDisconnectShareAnActualCrossConnectionSqlLock()
        {
            var run = await Run(User1);
            var entered = new TaskCompletionSource<bool>();
            var proceed = new TaskCompletionSource<bool>();
            var disconnected = false;
            var publication = _store.PublishAsync(run, Observation(User1), async _ =>
            {
                entered.TrySetResult(true);
                await proceed.Task;
                Assert.IsFalse(disconnected);
            }, Ct);
            await entered.Task;
            var disconnect = _store.ExecuteExclusiveAsync(_ => { disconnected = true; return Task.CompletedTask; }, Ct);
            await Task.Delay(100);
            Assert.IsFalse(disconnected);
            proceed.SetResult(true);
            await Task.WhenAll(publication, disconnect);
            Assert.IsTrue(disconnected);
        }

        [TestMethod]
        public async Task FailedTableDisconnectLeavesSqlFailClosedUntilANewValidatedGeneration()
        {
            var run = await Run(User1);
            await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.InvalidateAndExecuteExclusiveAsync(_ => Task.FromResult(run.Generation),
                    _ => throw new CostConnectionException("storageUnavailable"), Ct));
            var error = await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.AcquireRunAsync(run.Generation, DateTimeOffset.UtcNow, Ct));
            Assert.AreEqual("disconnected", error.Status);
            await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct));
            var generation = Guid.NewGuid().ToString("N");
            var replacement = await _store.AcquireRunAsync(generation, DateTimeOffset.UtcNow, Ct, _ => Task.CompletedTask);
            Assert.IsNotNull(replacement);
            await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.AcquireRunAsync(run.Generation, DateTimeOffset.UtcNow, Ct, _ => throw new CostConnectionException("busy")));
            Assert.AreEqual(1, Sql($"SELECT COUNT(*) FROM dbo.copilot_cost_control WHERE generation='{generation}' AND blocked=0;"));
            await Assert.ThrowsExceptionAsync<CostConnectionException>(() =>
                _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct));
            Assert.AreEqual(0, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
        }

        [TestMethod]
        public async Task CanceledSqlStatusHasOnlyAStableSanitizedClassification()
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var error = await Assert.ThrowsExceptionAsync<CostConnectionException>(() => _store.ReadStatusAsync(canceled.Token));
                Assert.AreEqual("databaseUnavailable", error.Status);
                Assert.AreEqual("Copilot cost connection: databaseUnavailable", error.Message);
            }
        }

        [TestMethod]
        public async Task MissingUserTerminatesOnlyItsRunWithoutInventingAnEmptyObservation()
        {
            var run = await Run(User1, User2);
            await _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct);
            var work = (await _store.ReadWorkAsync(run, DateTimeOffset.UtcNow, Ct)).Single();
            await _store.RecordFailureAsync(run, work, "notFound", DateTimeOffset.UtcNow, Ct);
            var status = await _store.ReadStatusAsync(Ct);
            Assert.AreEqual("complete", status.Phase);
            Assert.IsNotNull(status.LastCompleted);
            Assert.AreEqual(1L, status.Denied);
            Assert.AreEqual(1L, status.Observed);
            Assert.AreEqual(0L, status.Empty);
            Assert.AreEqual(0L, status.Retrying);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations;"));
            Assert.AreEqual(0, (await _store.ReadWorkAsync(run, DateTimeOffset.UtcNow.AddDays(2), Ct)).Count);
            await _store.ReleaseAsync(run, Ct);
            Assert.IsNull(await _store.AcquireRunAsync(run.Generation, DateTimeOffset.UtcNow, Ct));
            var tomorrow = await _store.AcquireRunAsync(run.Generation, DateTimeOffset.UtcNow.AddDays(1), Ct);
            Assert.IsNotNull(tomorrow);
            Assert.AreNotEqual(run.Id, tomorrow.Id);
            await _store.SaveInventoryPageAsync(tomorrow, new[] { User2 }, null, Ct);
            Assert.AreEqual(User2, (await _store.ReadWorkAsync(tomorrow, DateTimeOffset.UtcNow.AddDays(1), Ct)).Single().UserId);
        }

        [TestMethod]
        public async Task RetriedUsersYieldToFreshWorkAndObservationDateIsNotRunDate()
        {
            var run = await Run(User1, User2);
            var work = await _store.ReadWorkAsync(run, DateTimeOffset.UtcNow, Ct);
            await _store.RecordFailureAsync(run, work.Single(w => w.UserId == User1), "throttled", DateTimeOffset.UtcNow, Ct, TimeSpan.FromHours(2));
            Assert.AreEqual(User2, (await _store.ReadWorkAsync(run, DateTimeOffset.UtcNow, Ct)).Single().UserId);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_work WHERE next_attempt>DATEADD(minute,119,SYSDATETIMEOFFSET());"));
            var observation = Observation(User2);
            observation.ObservedAt = new DateTimeOffset(2001, 2, 1, 0, 30, 0, TimeSpan.FromHours(2));
            await _store.PublishAsync(run, observation, _ => Task.CompletedTask, Ct);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE observation_date='20010131';"));
            Assert.IsNull((await _store.ReadStatusAsync(Ct)).LastCompleted);
            var tomorrow = await _store.AcquireRunAsync(run.Generation, DateTimeOffset.UtcNow.AddDays(1), Ct);
            Assert.IsNotNull(tomorrow);
            // Fair selection may finish the old backlog first; tomorrow's independent inventory
            // still exists and no observation is falsely backdated to either run's inventory date.
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_runs;"));
        }

        [TestMethod]
        public async Task ManualUpgradeCopiesSnapshotRerunsAndPurgeRequiresScopedConfirmation()
        {
            var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, @"Common\Entities\Migrations\202610230900001_AddCopilotCostPreview.manual.sql")))
                root = root.Parent;
            Assert.IsNotNull(root);
            var migrationFolder = Path.Combine(root.FullName, @"Common\Entities\Migrations");
            Assert.IsTrue(File.ReadAllBytes(Path.Combine(migrationFolder, "202610221500003_UniqueOnlineMeetingIds.resx"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(migrationFolder, "202610230900001_AddCopilotCostPreview.resx"))));
            Sql(@"IF OBJECT_ID(N'dbo.__MigrationHistory',N'U') IS NULL
CREATE TABLE dbo.__MigrationHistory(MigrationId nvarchar(150),ContextKey nvarchar(300),Model varbinary(max),ProductVersion nvarchar(32));
DELETE dbo.__MigrationHistory;
INSERT dbo.__MigrationHistory VALUES('202610221500003_UniqueOnlineMeetingIds','SyntheticContext',0x010203,'6.5.2');");
            var script = File.ReadAllText(Path.Combine(migrationFolder, "202610230900001_AddCopilotCostPreview.manual.sql"));
            StringAssert.Contains(script, AddCopilotCostPreview.Up_Sql);
            StringAssert.Contains(script, AddCopilotCostPreview.Purge_Sql);
            var batches = System.Text.RegularExpressions.Regex.Split(script, @"(?im)^\s*GO\s*$");
            Sql("DROP INDEX IX_copilot_cost_history_scope ON dbo.copilot_cost_history;");
            foreach (var batch in batches) if (!string.IsNullOrWhiteSpace(batch)) Sql(batch);
            foreach (var batch in batches) if (!string.IsNullOrWhiteSpace(batch)) Sql(batch);
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId='202610230900001_AddCopilotCostPreview' AND Model=0x010203;"));
            var run = await Run(User1, User2);
            await _store.PublishAsync(run, Observation(User1), _ => Task.CompletedTask, Ct);
            await _store.PublishAsync(run, Observation(User2), _ => Task.CompletedTask, Ct);
            Sql("EXEC dbo.copilot_cost_purge_preview @BeforeDate='20020101';");
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
            // A NULL @DryRun - passed explicitly, or from a script variable never set - is not a confirmed purge: it was
            // neither "= 0" (so the confirmation was never asked for) nor "= 1" (so the dry run did not return).
            Sql("EXEC dbo.copilot_cost_purge_preview @BeforeDate='20020101',@DryRun=NULL;");
            Sql("DECLARE @dryRun bit; EXEC dbo.copilot_cost_purge_preview @BeforeDate='20020101',@DryRun=@dryRun;");
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"), "A NULL @DryRun deleted history without the confirmation.");
            Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations;"), "A NULL @DryRun deleted observations without the confirmation.");
            Assert.ThrowsException<SqlException>(() => Sql("EXEC dbo.copilot_cost_purge_preview @BeforeDate='20020101',@DryRun=0;"));
            Sql("EXEC dbo.copilot_cost_purge_preview @BeforeDate='20020101',@ObjectId='00000000-0000-0000-0000-000000000001',@BatchSize=1,@DryRun=0,@Confirm=N'PURGE COPILOT COST HISTORY';");
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
            Assert.AreEqual(1, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_observations;"));
        }

        /// <summary>
        /// The manual script's last batch is all that stops a DBA stamping a half-applied preview schema, so it must
        /// require every index the migration creates. In SSMS a CREATE INDEX that fails (a lock timeout, no space) does
        /// not stop the batch, and once stamped the migration is never run again. The indexes are read from
        /// <see cref="AddCopilotCostPreview.Up_Sql"/>, so one added there without a check here fails this test.
        /// </summary>
        [TestMethod]
        public void ManualPostFlightRefusesToStampWithoutAnyPreviewIndex_AndStampsOnceTheSchemaIsWhole()
        {
            var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, @"Common\Entities\Migrations\202610230900001_AddCopilotCostPreview.manual.sql")))
                root = root.Parent;
            Assert.IsNotNull(root);
            var script = File.ReadAllText(Path.Combine(root.FullName, @"Common\Entities\Migrations\202610230900001_AddCopilotCostPreview.manual.sql"));
            var postFlight = System.Text.RegularExpressions.Regex.Split(script, @"(?im)^\s*GO\s*$").Last(b => !string.IsNullOrWhiteSpace(b));
            StringAssert.Contains(postFlight, "Preview schema is incomplete");
            var indexes = System.Text.RegularExpressions.Regex.Matches(AddCopilotCostPreview.Up_Sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)\s+ON\s+(dbo\.\w+)")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => new { Name = m.Groups[1].Value, Table = m.Groups[2].Value })
                .ToList();
            Assert.AreEqual(8, indexes.Count, "Every index the preview creates.");
            Sql(@"IF OBJECT_ID(N'dbo.__MigrationHistory',N'U') IS NULL
CREATE TABLE dbo.__MigrationHistory(MigrationId nvarchar(150),ContextKey nvarchar(300),Model varbinary(max),ProductVersion nvarchar(32));
DELETE dbo.__MigrationHistory;
INSERT dbo.__MigrationHistory VALUES('202610221500003_UniqueOnlineMeetingIds','SyntheticContext',0x010203,'6.5.2');");
            Func<int> stamps = () => Sql("SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId='202610230900001_AddCopilotCostPreview';");

            foreach (var index in indexes)
            {
                Sql($"DROP INDEX {index.Name} ON {index.Table};");
                var refused = Assert.ThrowsException<SqlException>(() => Sql(postFlight), $"Stamped without {index.Name}.");
                StringAssert.Contains(refused.Message, "Preview schema is incomplete");
                Assert.AreEqual(0, stamps(), $"Stamped without {index.Name}.");
                Sql(AddCopilotCostPreview.Up_Sql);
            }

            Sql(postFlight);
            Sql(postFlight);
            Assert.AreEqual(1, stamps(), "A whole schema is stamped, once.");
        }

        [TestMethod]
        public async Task InterruptedDirectoryInventoryResumesBeforeAnyUserReadAndPublishesSeparateContext()
        {
            const string tenant = "00000000-0000-0000-0000-000000000001";
            const string client = "00000000-0000-0000-0000-000000000002";
            const string continuation = "https://graph.microsoft.com/v1.0/users?$select=id&$skiptoken=synthetic";
            var documents = new Documents();
            await documents.WriteAsync("connection", CostConnectionEncoding.Serialize(new CostConnectionState
            { AccountId = "synthetic-reader", Cache = Convert.ToBase64String(new byte[] { 1 }), Status = "connected" }), null, Ct);
            var connection = new CostConnectionService(documents, new Identity(), tenant, client,
                "https://contoso.example/copilot-cost/callback", publicationFence: _store);
            var failSecond = true;
            var userReads = 0;
            using (var http = new HttpClient(new CopilotCostCollectorTests.Handler(request =>
            {
                if (request.RequestUri.AbsolutePath == "/v1.0/users")
                {
                    Assert.AreEqual("synthetic-directory-token", request.Headers.Authorization.Parameter);
                    if (request.RequestUri.Query.Contains("skiptoken"))
                    {
                        if (failSecond) { failSecond = false; throw new OperationCanceledException("Synthetic timeout"); }
                        return new JObject { ["value"] = new JArray(new JObject { ["id"] = User2 }) };
                    }
                    return new JObject { ["value"] = new JArray(new JObject { ["id"] = User1 }), ["@odata.nextLink"] = continuation };
                }
                Assert.AreEqual("synthetic-delegated-token", request.Headers.Authorization.Parameter);
                if (request.RequestUri.AbsolutePath.EndsWith("/spendingPolicies")) return JObject.Parse("{'value':[]}");
                if (request.RequestUri.AbsolutePath.EndsWith("/getTenantCreditBalance")) return JObject.Parse("{'billingMethodBalances':[]}");
                userReads++;
                return CopilotCostCollectorTests.User(request.RequestUri.Segments.Last(), "workIQ", 0.5);
            })))
            {
                var collector = new CostCollector(_store, connection, http, _ => Task.FromResult("synthetic-directory-token"));
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => collector.RunSliceAsync(Ct));
                var incomplete = await _store.ReadStatusAsync(Ct);
                Assert.AreEqual(1L, incomplete.DirectoryUsers);
                Assert.IsFalse(incomplete.InventoryComplete);
                Assert.AreEqual("sliceTimedOut", incomplete.LastOutcome);
                Assert.AreEqual(0, userReads);
                Sql("UPDATE dbo.copilot_cost_runs SET not_before=NULL;");
                await collector.RunSliceAsync(Ct);
                Assert.AreEqual(0, userReads);
                Assert.IsTrue((await _store.ReadStatusAsync(Ct)).InventoryComplete);
                await collector.RunSliceAsync(Ct);
                await collector.RunSliceAsync(Ct);
                Assert.AreEqual(0, userReads);
                await collector.RunSliceAsync(Ct);
                var status = await _store.ReadStatusAsync(Ct);
                Assert.AreEqual(2, userReads);
                Assert.AreEqual(2L, status.DirectoryUsers);
                Assert.AreEqual(2L, status.Observed);
                Assert.AreEqual(2L, status.Measured);
                Assert.AreEqual(0L, status.Pending + status.Retrying);
                Assert.AreEqual("complete", status.Phase);
                Assert.IsNotNull(status.LastCompleted);
                Assert.AreEqual(2, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_context;"));
            }
        }

        private sealed class Identity : ICostIdentity
        {
            public Task<CostToken> RedeemAsync(string code, string verifier, string nonce, CancellationToken ct) => throw new NotSupportedException();
            public Task<CostToken> RefreshAsync(string accountId, byte[] cache, CancellationToken ct) =>
                Task.FromResult(new CostToken { AccountId = accountId, Cache = cache,
                    TenantId = "00000000-0000-0000-0000-000000000001", AccessToken = "synthetic-delegated-token" });
        }

        [TestMethod]
        [TestCategory("SyntheticScale")]
        public void Synthetic200kDirectoryAndMillionBundleQueriesRemainBounded()
        {
            var timer = Stopwatch.StartNew();
            Sql(@"
DECLARE @run uniqueidentifier='00000000-0000-0000-0000-000000000010';
INSERT dbo.copilot_cost_runs(run_id,generation,inventory_date,last_served,inventory_complete,context_phase)
 VALUES(@run,'synthetic-scale','20000101',SYSDATETIMEOFFSET(),1,2);
;WITH n AS(SELECT TOP(200000) ROW_NUMBER() OVER(ORDER BY(SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.copilot_cost_work(run_id,object_id,next_attempt)
 SELECT @run,CONVERT(uniqueidentifier,CONVERT(binary(16),n)),'20000101' FROM n;
INSERT dbo.users(user_name,azure_ad_id)
 SELECT 'synthetic@contoso.example',CONVERT(nvarchar(36),object_id) FROM dbo.copilot_cost_work;
INSERT dbo.copilot_cost_observations(observation_date,object_id,observed_at,quality,selection_status)
 SELECT DATEADD(day,ROW_NUMBER() OVER(ORDER BY object_id)%400,CONVERT(date,'20000101')),object_id,
 '2001-01-01T00:00:00+00:00',2,N'{}' FROM dbo.copilot_cost_work;
;WITH n AS(SELECT TOP(1000000) ROW_NUMBER() OVER(ORDER BY(SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.copilot_cost_history(run_id,publication_id,observation_date,object_id,observed_at,quality,bundle)
 SELECT @run,NEWID(),DATEADD(day,n%400,CONVERT(date,'20000101')),
 CONVERT(uniqueidentifier,CONVERT(binary(16),1+n%200000)),
 '2001-01-01T00:00:00+00:00',2,N'{""synthetic"":""'+REPLICATE(N'x',480)+N'""}' FROM n;
UPDATE STATISTICS dbo.copilot_cost_work WITH FULLSCAN;
UPDATE STATISTICS dbo.copilot_cost_history WITH FULLSCAN;
UPDATE STATISTICS dbo.users WITH FULLSCAN;");
            timer.Stop();
            TestContext.WriteLine("Synthetic setup: 200000 directory/work rows, 1000000 ~1KiB history bundles; elapsed {0} ms.", timer.ElapsedMilliseconds);
            const string ready = @"SELECT TOP(100) object_id,attempts FROM dbo.copilot_cost_work
WHERE run_id='00000000-0000-0000-0000-000000000010' AND completed=0 AND next_attempt<='20010101'
ORDER BY next_attempt,attempts,object_id OPTION(RECOMPILE);";
            Measure("ready-dense", ready);
            Sql(@"UPDATE dbo.copilot_cost_work SET completed=1;
UPDATE dbo.copilot_cost_work SET completed=0 WHERE object_id IN(SELECT TOP(500) object_id FROM dbo.copilot_cost_work ORDER BY object_id);");
            Measure("ready-sparse", ready);
            Measure("history-1-day", "SELECT COUNT_BIG(*) FROM dbo.copilot_cost_history WHERE observation_date>='20000101' AND observation_date<'20000102' OPTION(RECOMPILE);");
            Measure("history-90-days", "SELECT COUNT_BIG(*) FROM dbo.copilot_cost_history WHERE observation_date>='20000101' AND observation_date<'20000331' OPTION(RECOMPILE);");
            Measure("coverage-200k", SqlCostCollectorStore.StatusSql.TrimEnd(';') + " OPTION(RECOMPILE);");
            Measure("purge-tenant-dry-run", "EXEC dbo.copilot_cost_purge_preview @BeforeDate='20010101',@BatchSize=1000;");
            Measure("purge-user-dry-run", "EXEC dbo.copilot_cost_purge_preview @BeforeDate='20010101',@ObjectId='01000000-0000-0000-0000-000000000000',@BatchSize=1000;");
            Measure("nullable-link-999", @"
DECLARE @run uniqueidentifier='00000000-0000-0000-0000-000000000010';
DECLARE @ids nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'""'+CONVERT(nvarchar(36),object_id)+N'""'),N',')+N']'
 FROM (SELECT TOP(999) object_id FROM dbo.copilot_cost_work) ids);
" + SqlCostCollectorStore.LinkSql.TrimEnd(';') + " OPTION(RECOMPILE);");
            Sql(@"SELECT 0;");
            using (var db = new SqlConnection(_connection))
            {
                db.Open();
                using (var cmd = new SqlCommand(@"SELECT SUM(reserved_page_count)*8 FROM sys.dm_db_partition_stats
WHERE object_id IN(OBJECT_ID('dbo.copilot_cost_work'),OBJECT_ID('dbo.copilot_cost_history'),OBJECT_ID('dbo.copilot_cost_observations'));", db))
                    TestContext.WriteLine("Synthetic retained stores reserved KiB: {0}. Includes indexes; no Graph throughput claim.", cmd.ExecuteScalar());
            }
            Assert.AreEqual(200000, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_work;"));
            Assert.AreEqual(1000000, Sql("SELECT COUNT(*) FROM dbo.copilot_cost_history;"));
        }

        private void Measure(string name, string query)
        {
            var times = new List<long>();
            var reads = new List<int>();
            var operators = new HashSet<string>();
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var messages = new List<string>();
                using (var db = new SqlConnection(_connection))
                {
                    db.InfoMessage += (_, e) => messages.Add(e.Message);
                    db.Open();
                    var timer = Stopwatch.StartNew();
                    using (var cmd = new SqlCommand("SET STATISTICS IO ON;SET STATISTICS XML " + (attempt == 0 ? "ON;" : "OFF;") + query, db) { CommandTimeout = 60 })
                    using (var reader = cmd.ExecuteReader())
                    {
                        do
                        {
                            while (reader.Read())
                                if (attempt == 0 && reader.FieldCount == 1 && reader.GetName(0).IndexOf("Showplan", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var plan = System.Xml.Linq.XDocument.Parse(reader.GetValue(0).ToString());
                                    foreach (var op in plan.Descendants().Attributes("PhysicalOp")) operators.Add(op.Value);
                                }
                        } while (reader.NextResult());
                    }
                    timer.Stop();
                    if (attempt > 0)
                    {
                        times.Add(timer.ElapsedMilliseconds);
                        reads.Add(messages.SelectMany(m => System.Text.RegularExpressions.Regex.Matches(m, @"logical reads (\d+)")
                            .Cast<System.Text.RegularExpressions.Match>()).Sum(m => int.Parse(m.Groups[1].Value)));
                    }
                }
            }
            TestContext.WriteLine("{0}: median {1} ms / {2} logical reads; plan {3}. Cold run discarded; five warm runs.",
                name, times.OrderBy(t => t).ElementAt(2), reads.OrderBy(r => r).ElementAt(2), string.Join(", ", operators.OrderBy(x => x)));
        }

        private sealed class Documents : ICostDocumentStore
        {
            private readonly Dictionary<string, CostDocument> _documents = new Dictionary<string, CostDocument>();
            public Task<CostDocument> ReadAsync(string key, CancellationToken ct)
            {
                _documents.TryGetValue(key, out var value);
                return Task.FromResult(value);
            }
            public Task<string> WriteAsync(string key, byte[] content, string expectedVersion, CancellationToken ct)
            {
                _documents.TryGetValue(key, out var old);
                if (old?.Version != expectedVersion) throw new CostConnectionException("busy");
                var version = Guid.NewGuid().ToString("N");
                _documents[key] = new CostDocument { Content = content, Version = version };
                return Task.FromResult(version);
            }
        }
    }
}
