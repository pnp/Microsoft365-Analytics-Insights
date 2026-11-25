using Azure.Core;
using Common.Entities;
using Common.Entities.Sql;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace Tests.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class AppInsightsSqlTokenShutdownTests
    {
        // Selects the legacy Azure host-suffix path, but even an accidental SQL open can only reach a local pipe.
        private const string EntraSql = @"Data Source=np:\\.\pipe\contoso-test.database.windows.net;Initial Catalog=Contoso;Connect Timeout=1";

        [TestCleanup]
        public void ResetCredential() => AzureSqlTokenAuth.ResetCredential();

        [DataTestMethod]
        [DataRow("search")]
        [DataRow("batch")]
        [DataRow("chunk")]
        public async Task ShutdownInterruptsStagingDuringTokenAcquisition(string path)
        {
            using (var stop = new CancellationTokenSource())
            using (var gate = new CredentialGate())
            using (var db = new ContextFactory().Create())
            {
                AzureSqlTokenAuth.SetCredential(gate);
                var staging = Task.Run(() =>
                {
                    if (path == "search")
                    {
                        return (Task)Search().SaveSearchBatchAsync(NullLogger.Instance, db, cancellationToken: stop.Token);
                    }
                    var batch = new EFInsertBatch<Row>(db, NullLogger.Instance, stop.Token);
                    batch.Rows.Add(new Row { Number = 1 });
                    if (path == "batch") return batch.SaveToStagingTable(null);
                    // The chunk normally follows a successful global SQL open; enter that worker without a live server.
                    return batch.ProcessChunkAsync("##contoso", NullLogger.Instance, batch.Rows, 0);
                });
                await gate.WaitForEntryAsync();
                stop.Cancel();
                await AssertStopped(staging, stop.Token, gate);
            }
        }

        [TestMethod]
        public async Task StagingCredentialTimeout_IsNotReportedAsHostCancellation()
        {
            using (var running = new CancellationTokenSource())
            using (var db = new ContextFactory().Create())
            {
                AzureSqlTokenAuth.SetCredential(new Credential(token =>
                {
                    Assert.AreEqual(running.Token, token);
                    throw new TimeoutException("Synthetic credential timeout");
                }));
                await Assert.ThrowsAsync<TimeoutException>(() => Search().SaveSearchBatchAsync(NullLogger.Instance, db, cancellationToken: running.Token));
                Assert.IsFalse(running.IsCancellationRequested);
            }
        }

        [TestMethod]
        public void EfOpens_RestoreTheEnclosingToken_AndLeaveLoginAndLocalDbAlone()
        {
            using (var outer = new CancellationTokenSource())
            using (var inner = new CancellationTokenSource())
            using (var entra = new SqlConnection(EntraSql))
            using (var login = new SqlConnection(EntraSql + ";User ID=synthetic;Password=synthetic"))
            using (var local = new SqlConnection(@"Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True"))
            {
                var interceptor = new AzureSqlAccessTokenInterceptor();
                CancellationToken observed = default;
                AzureSqlTokenAuth.SetCredential(new Credential(token =>
                {
                    observed = token;
                    return new AccessToken("synthetic", DateTimeOffset.UtcNow.AddMinutes(10));
                }));
                using (AzureSqlAccessTokenInterceptor.UseCancellationToken(outer.Token))
                {
                    inner.Cancel();
                    Assert.Throws<OperationCanceledException>(() =>
                    {
                        using (AzureSqlAccessTokenInterceptor.UseCancellationToken(inner.Token))
                        {
                            interceptor.Opening(login, null);
                            interceptor.Opening(local, null);
                            interceptor.Opening(entra, null);
                        }
                    });
                    Assert.IsTrue(string.IsNullOrEmpty(login.AccessToken));
                    Assert.IsTrue(string.IsNullOrEmpty(local.AccessToken));
                    interceptor.Opening(entra, null);
                    Assert.AreEqual(outer.Token, observed);
                }
                interceptor.Opening(entra, null);
                Assert.AreEqual(CancellationToken.None, observed);
            }
        }

        private static CustomEventsResultCollection Search()
        {
            var events = new CustomEventsResultCollection();
            events.Rows.Add(new SearchEventAppInsightsQueryResult
            {
                Username = "reader@contoso.example",
                CustomProperties = new SearchCustomProps { SearchText = "Example", SessionId = "session" }
            });
            return events;
        }

        [TestMethod]
        public async Task ShutdownInterruptsAnImportersActualEfOpen_ButNotAParallelUnscopedOpen()
        {
            using (var stop = new CancellationTokenSource())
            using (var gate = new CredentialGate())
            using (var finishMigration = new ManualResetEventSlim())
            {
                AzureSqlTokenAuth.SetCredential(gate);
                var cursor = new InMemoryIngestionCursorStore();
                var importer = new AppInsightsImporter(null, AnalyticsLogger.ConsoleOnlyTracer(),
                    contextFactory: new ContextFactory(), cursorStore: cursor);
                var import = Task.Run(() => importer.ImportAndSave(false, null, stop.Token));
                await gate.WaitForEntryAsync();

                var migrationEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                var migration = Task.Run(() =>
                {
                    using (var connection = new SqlConnection(EntraSql))
                    {
                        gate.ProbeUnscoped(token =>
                        {
                            migrationEntered.TrySetResult(token);
                            finishMigration.Wait(token);
                            return new AccessToken("synthetic", DateTimeOffset.UtcNow.AddMinutes(10));
                        }, () => new AzureSqlAccessTokenInterceptor().Opening(connection, null));
                        return connection.AccessToken;
                    }
                });
                try
                {
                    Assert.AreSame(migrationEntered.Task, await Task.WhenAny(migrationEntered.Task, migration, Task.Delay(TimeSpan.FromSeconds(30))));
                    stop.Cancel();
                    await AssertStopped(import, stop.Token, gate);
                    Assert.IsFalse((await migrationEntered.Task).CanBeCanceled, "An unrelated EF connection inherited the import's token.");
                    Assert.IsFalse(migration.IsCompleted, "Stopping the importer must not interrupt the migration's own credential request.");
                }
                finally { finishMigration.Set(); }
                Assert.AreEqual("synthetic", await migration);
                Assert.AreEqual(0, cursor.Writes);
            }
        }

        private static async Task AssertStopped(Task work, CancellationToken expected, CredentialGate gate)
        {
            Assert.AreSame(gate.Interrupted, await Task.WhenAny(gate.Interrupted, Task.Delay(TimeSpan.FromSeconds(5))),
                "Shutdown must interrupt the credential itself, without releasing it or opening SQL.");
            Assert.AreEqual(expected, await gate.Interrupted);
            // A cold EF provider can still finish building its in-memory model after a cancelled manifest lookup.
            Assert.AreSame(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30))));
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => work);
            Assert.AreEqual(expected, ex.CancellationToken);
        }

        [TempTableName("##contoso")]
        private sealed class Row
        {
            [Column("number")]
            public int Number { get; set; }
        }

        private sealed class ContextFactory : IAnalyticsDbContextFactory
        {
            public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext(new SqlConnection(EntraSql));
        }

        private sealed class Credential : TokenCredential
        {
            private readonly Func<CancellationToken, AccessToken> _get;
            public Credential(Func<CancellationToken, AccessToken> get) => _get = get;
            public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => _get(token);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) => throw new NotSupportedException();
        }

        private sealed class CredentialGate : TokenCredential, IDisposable
        {
            private readonly TaskCompletionSource<bool> _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<CancellationToken> _interrupted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _cleanup = new ManualResetEventSlim();
            private readonly AsyncLocal<Func<CancellationToken, AccessToken>> _parallelCredential = new AsyncLocal<Func<CancellationToken, AccessToken>>();

            public Task<CancellationToken> Interrupted => _interrupted.Task;

            public async Task WaitForEntryAsync() =>
                Assert.AreSame(_entered.Task, await Task.WhenAny(_entered.Task, Task.Delay(TimeSpan.FromSeconds(30))),
                    "The importer must reach the synchronous SQL credential before its caller cancels.");

            public void ProbeUnscoped(Func<CancellationToken, AccessToken> acquire, Action open)
            {
                _parallelCredential.Value = acquire;
                try { open(); }
                finally { _parallelCredential.Value = null; }
            }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                if (_parallelCredential.Value != null) return _parallelCredential.Value(cancellationToken);
                _entered.TrySetResult(true);
                try { _cleanup.Wait(cancellationToken); }
                catch (OperationCanceledException)
                {
                    _interrupted.TrySetResult(cancellationToken);
                    throw;
                }
                throw new InvalidOperationException("Test cleanup: the fake never permits the importer to reach SQL.");
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public void Dispose() => _cleanup.Set();
        }
    }
}
