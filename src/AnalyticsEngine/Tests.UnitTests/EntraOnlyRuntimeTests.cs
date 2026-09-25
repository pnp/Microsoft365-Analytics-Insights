extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using Azure.Core;
using Common.Entities;
using Common.Entities.Migrations;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runtime-only coverage for Entra-authenticated Azure SQL. These tests use synthetic connection
    /// strings only; a throwing credential proves the code stops before any network I/O.
    /// </summary>
    [TestClass]
    public class EntraOnlyRuntimeTests
    {
        private const string EntraOnlyUnreachableServer =
            "data source=tcp:sql-contoso-synthetic-00000000.database.windows.net,1433;initial catalog=analytics;persist security info=False;Encrypt=True;Connect Timeout=1";

        private const string SyntheticLocalDb =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=UnitTestingSPOInsights_relfixB;Integrated Security=true;MultipleActiveResultSets=True;App=EntityFramework";

        [TestCleanup]
        public void Cleanup()
        {
            AzureSqlTokenAuth.ResetCredential();
            Database.SetInitializer(new CreateDatabaseIfNotExists<AnalyticsEntitiesContext>());
        }

        [TestMethod]
        public void ContextConstructor_EntraOnlyRuntimeConnection_SkipsEfInitializationAndRequestsNoToken()
        {
            var credential = new TokenRequestRecorder();
            AzureSqlTokenAuth.SetCredential(credential);

            // Simulate the process-wide initializer a previous non-token context registered. The fix must
            // still skip it for the contained-user runtime connection; the old code ran it, asked for a
            // token while probing master, and threw before the constructor returned.
            Database.SetInitializer(new CreateDatabaseIfNotExists<AnalyticsEntitiesContext>());

            using (var sqlConnection = new SqlConnection(EntraOnlyUnreachableServer))
            using (new AnalyticsEntitiesContext(sqlConnection))
            {
                Assert.AreEqual(0, credential.Requests, "Runtime construction must not run EF's master-dependent initializer.");
                Assert.AreEqual(System.Data.ConnectionState.Closed, sqlConnection.State, "The constructor must not open the unreachable server.");
            }
        }

        [TestMethod]
        public void ContextConstructor_LocalDbConnection_StillRunsTheRegisteredInitializer()
        {
            var initializer = new RecordingInitializer();
            Database.SetInitializer(initializer);

            using (var sqlConnection = new SqlConnection(SyntheticLocalDb))
            using (var db = new AnalyticsEntitiesContext(sqlConnection))
            {
                db.Database.Initialize(false);
                Assert.AreEqual(1, initializer.Calls, "Non-token SQL must keep EF's existing initializer behaviour.");
            }
        }

        [TestMethod]
        public async Task PendingMigrations_HistoryReadOnFullyMigratedLocalDb_ReturnsNone()
        {
            var config = new Configuration();
            new DbMigrator(config).Update();

            using (var db = new AnalyticsEntitiesContext(SyntheticLocalDb, true, true))
            {
                var pending = await SqlHealthDataSource.GetPendingMigrationsFromHistoryAsync(db, config);

                Assert.AreEqual(0, pending.Count, "The direct __MigrationHistory read must agree with EF on an up-to-date database.");
            }
        }

        [TestMethod]
        public void PendingMigrations_ComparisonReportsMissingMigrationIds()
        {
            var config = new Configuration();
            var local = new DbMigrator(config).GetLocalMigrations().ToList();
            Assert.IsTrue(local.Count > 1, "The test needs the real migration list to be non-empty.");
            var missing = local[local.Count - 1];

            var pending = SqlHealthDataSource.CompareMigrations(local, local.Take(local.Count - 1));

            CollectionAssert.AreEqual(new[] { missing }, pending.ToArray());
        }

        private sealed class RecordingInitializer : IDatabaseInitializer<AnalyticsEntitiesContext>
        {
            public int Calls { get; private set; }

            public void InitializeDatabase(AnalyticsEntitiesContext context)
            {
                Calls++;
            }
        }

        /// <summary>Counts SQL token requests and refuses every one, so a failing path cannot reach the network.</summary>
        private sealed class TokenRequestRecorder : TokenCredential
        {
            public int Requests { get; private set; }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                Requests++;
                Assert.AreEqual(AzureSqlTokenAuth.SqlTokenScope, requestContext.Scopes[0]);
                throw new InvalidOperationException("A SQL token was requested.");
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
            }
        }
    }
}
