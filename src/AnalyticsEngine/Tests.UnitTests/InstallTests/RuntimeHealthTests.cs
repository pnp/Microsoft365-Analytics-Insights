using Azure.Core;
using Common.Entities.Health;
using DataUtils;
using DataUtils.Health;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.SqlClient;
using System.Data.Entity.Migrations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class RuntimeHealthTests
    {
        [DataTestMethod]
        [DataRow(-1, HealthStatus.Unhealthy)]
        [DataRow(2, HealthStatus.Unhealthy)]
        [DataRow(3, HealthStatus.Degraded)]
        [DataRow(13, HealthStatus.Degraded)]
        [DataRow(14, HealthStatus.Healthy)]
        public async Task Credential_FreshTokenAndCertificateExpiry_AreIndependentFacts(int days, HealthStatus expected)
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var credential = new SyntheticCredential(new AccessToken("synthetic-token", now.AddHours(1)));
            var result = await RuntimeCredentialHealth.CheckTokenAsync(credential, now.AddDays(days), now, CancellationToken.None);
            Assert.AreEqual(expected, result.Status);
            Assert.AreEqual(days, result.DaysToExpiry);
            CollectionAssert.AreEqual(new[] { "https://graph.microsoft.com/.default" }, credential.Scopes);
        }

        [TestMethod]
        public async Task Credential_SecretExpiryIsUnknown_NotInventedFromAccessTokenLifetime()
        {
            var now = DateTime.UtcNow;
            var credential = new SyntheticCredential(new AccessToken("synthetic-token", now.AddHours(1)));
            var result = await RuntimeCredentialHealth.CheckTokenAsync(credential, null, now, CancellationToken.None);
            Assert.AreEqual(HealthStatus.Degraded, result.Status);
            Assert.IsNull(result.DaysToExpiry);
            Assert.AreEqual("defaultHealth.secretExpiryUnknown", result.ReasonKey);
            var expired = await RuntimeCredentialHealth.CheckTokenAsync(
                new SyntheticCredential(new AccessToken("synthetic-token", now.AddSeconds(-1))), now.AddYears(1), now, CancellationToken.None);
            Assert.AreEqual(HealthStatus.Unhealthy, expired.Status);
        }

        [TestMethod]
        public async Task Monitor_IndependentChecksTimeoutWithoutOverlap_RecoverAndStopWithoutLeakingExceptions()
        {
            var pending = new TaskCompletionSource<RuntimeHealthResult>();
            var events = new ConcurrentQueue<Tuple<HealthComponent, RuntimeHealthResult>>();
            var sqlCalls = 0;
            var credentialsCalls = 0;
            var monitor = new RuntimeHealthMonitor(
                new Dictionary<HealthComponent, Func<CancellationToken, Task<RuntimeHealthResult>>>
                {
                    [HealthComponent.Sql] = _ => { Interlocked.Increment(ref sqlCalls); return pending.Task; },
                    [HealthComponent.Credential] = _ =>
                    {
                        if (Interlocked.Increment(ref credentialsCalls) == 1)
                            throw new InvalidOperationException("synthetic-private-detail-not-for-telemetry");
                        return Task.FromResult(new RuntimeHealthResult(HealthStatus.Healthy, "synthetic"));
                    }
                }, (c, r) => events.Enqueue(Tuple.Create(c, r)), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30));
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => events.Count(e => e.Item1 == HealthComponent.Sql) >= 3, 5000));
                Assert.AreEqual(1, sqlCalls, "A timed-out check must not accumulate background tasks.");
                Assert.IsTrue(SpinWait.SpinUntil(() => credentialsCalls >= 3, 5000), "The stuck SQL check must not suppress credential checks.");
                Assert.IsTrue(events.Any(e => e.Item2.Status == HealthStatus.Unhealthy));
                Assert.IsFalse(events.Any(e => e.Item2.Detail.Contains("synthetic-private-detail")));
                pending.SetResult(new RuntimeHealthResult(HealthStatus.Healthy, "synthetic"));
                Assert.IsTrue(SpinWait.SpinUntil(() => events.Any(e => e.Item1 == HealthComponent.Sql && e.Item2.Status == HealthStatus.Healthy), 5000));
            }
            finally { monitor.Dispose(); }
            var count = events.Count;
            await Task.Delay(100);
            Assert.AreEqual(count, events.Count);
            monitor.Dispose();
        }

        [TestMethod]
        public void SqlSchema_MissingNewerAndWrongContextHistory_AreNotHealthy()
        {
            Assert.AreEqual(HealthStatus.Healthy, DefaultRuntimeHealth.CompareSchema(new[] { "a", "b" }, new[] { "b", "a" }).Status);
            Assert.AreEqual(HealthStatus.Healthy, DefaultRuntimeHealth.CompareSchema(new[] { "A", "b" }, new[] { "B", "a" }).Status);
            Assert.AreEqual(HealthStatus.Unhealthy, DefaultRuntimeHealth.CompareSchema(new[] { "a", "b" }, new[] { "a" }).Status);
            Assert.AreEqual(HealthStatus.Unhealthy, DefaultRuntimeHealth.CompareSchema(new[] { "a" }, new[] { "a", "b" }).Status);
            Assert.AreEqual(HealthStatus.Unhealthy, DefaultRuntimeHealth.CompareSchema(new[] { "a" }, Array.Empty<string>()).Status);
            Assert.AreEqual(HealthStatus.Unhealthy, DefaultRuntimeHealth.CompareSchema(Array.Empty<string>(), Array.Empty<string>()).Status);
        }

        [TestMethod]
        public async Task SqlProbe_ReadsOnlyItsDatabase_RecognisesHistoryDriftAndReadOnly_WithoutApplyingMigrations()
        {
            var catalog = "ContosoHealth144_" + Guid.NewGuid().ToString("N");
            var masterString = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Pooling=false";
            var builder = new SqlConnectionStringBuilder(masterString) { InitialCatalog = catalog };
            var created = false;
            using (var master = new SqlConnection(masterString))
            {
                await master.OpenAsync();
                try
                {
                    using (var command = master.CreateCommand())
                    {
                        command.CommandText = "CREATE DATABASE [" + catalog + "];";
                        await command.ExecuteNonQueryAsync();
                        created = true;
                    }
                    var migrations = new Common.Entities.Migrations.Configuration();
                    var expected = new DbMigrator(migrations).GetLocalMigrations().ToArray();
                    Assert.IsTrue(expected.Length > 0);
                    using (var connection = new SqlConnection(builder.ConnectionString))
                    {
                        await connection.OpenAsync();
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "CREATE TABLE dbo.__MigrationHistory (MigrationId nvarchar(150) NOT NULL, " +
                                "ContextKey nvarchar(300) NOT NULL, Model varbinary(max) NOT NULL, ProductVersion nvarchar(32) NOT NULL, " +
                                "PRIMARY KEY(MigrationId, ContextKey));";
                            await command.ExecuteNonQueryAsync();
                            command.CommandText = "INSERT dbo.__MigrationHistory VALUES (@id, @context, 0x00, N'6.5.2');";
                            command.Parameters.AddWithValue("@id", "");
                            command.Parameters.AddWithValue("@context", "Contoso.WrongContext");
                            foreach (var id in expected)
                            {
                                command.Parameters["@id"].Value = id;
                                await command.ExecuteNonQueryAsync();
                            }
                            Assert.AreEqual(HealthStatus.Unhealthy,
                                (await DefaultRuntimeHealth.CheckSqlAsync(builder.ConnectionString, CancellationToken.None)).Status);
                            command.Parameters.Clear();
                            command.CommandText = "UPDATE dbo.__MigrationHistory SET ContextKey = @context;";
                            command.Parameters.AddWithValue("@context", migrations.ContextKey);
                            await command.ExecuteNonQueryAsync();
                            Assert.AreEqual(HealthStatus.Healthy,
                                (await DefaultRuntimeHealth.CheckSqlAsync(builder.ConnectionString, CancellationToken.None)).Status);
                            command.Parameters.Clear();
                            command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;";
                            Assert.AreEqual(1, (int)await command.ExecuteScalarAsync(), "The health check must never initialize the entity schema.");
                            command.CommandText = "DELETE FROM dbo.__MigrationHistory WHERE MigrationId = @id;";
                            command.Parameters.AddWithValue("@id", expected.Last());
                            await command.ExecuteNonQueryAsync();
                            Assert.AreEqual(HealthStatus.Unhealthy,
                                (await DefaultRuntimeHealth.CheckSqlAsync(builder.ConnectionString, CancellationToken.None)).Status);
                        }
                    }
                    using (var command = master.CreateCommand())
                    {
                        command.CommandText = "ALTER DATABASE [" + catalog + "] SET READ_ONLY;";
                        await command.ExecuteNonQueryAsync();
                        command.CommandText = "SELECT is_read_only FROM sys.databases WHERE name = @catalog;";
                        command.Parameters.AddWithValue("@catalog", catalog);
                        Assert.IsTrue((bool)await command.ExecuteScalarAsync(), "The isolated fixture must really be read-only.");
                    }
                    var readOnly = await DefaultRuntimeHealth.CheckSqlAsync(builder.ConnectionString, CancellationToken.None);
                    Assert.AreEqual("defaultHealth.sqlReadOnly", readOnly.ReasonKey);
                }
                finally
                {
                    if (created)
                    {
                        using (var command = master.CreateCommand())
                        {
                            command.CommandText = "ALTER DATABASE [" + catalog + "] SET READ_WRITE; DROP DATABASE [" + catalog + "];";
                            await command.ExecuteNonQueryAsync();
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Telemetry_AttributesCheckToExpectedJob_WithoutClaimingExpiryWhenUnknown()
        {
            var channel = new RecordingChannel();
            using (var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
            })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), ImporterHeartbeat.ActivityJob);
                logger.TrackHealthCheck(HealthComponent.Credential, HealthStatus.Degraded, "synthetic",
                    reasonKey: "defaultHealth.secretExpiryUnknown", jobName: ImporterHeartbeat.ActivityJob);
                var telemetry = channel.Events.Single();
                Assert.AreEqual("HealthCheck", telemetry.Name);
                Assert.AreEqual("DefaultHealth", telemetry.Properties["Monitor"]);
                Assert.AreEqual(ImporterHeartbeat.ActivityJob, telemetry.Properties["JobName"]);
                Assert.IsFalse(telemetry.Properties.ContainsKey("DaysToExpiry"));
            }
        }

        private sealed class SyntheticCredential : TokenCredential
        {
            private readonly AccessToken _token;
            public SyntheticCredential(AccessToken token) { _token = token; }
            public string[] Scopes { get; private set; }
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                Scopes = requestContext.Scopes;
                return _token;
            }
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class RecordingChannel : ITelemetryChannel
        {
            public ConcurrentQueue<EventTelemetry> Events { get; } = new ConcurrentQueue<EventTelemetry>();
            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }
            public void Send(ITelemetry item) { if (item is EventTelemetry e) Events.Enqueue(e); }
            public void Flush() { }
            public void Dispose() { }
        }
    }
}
