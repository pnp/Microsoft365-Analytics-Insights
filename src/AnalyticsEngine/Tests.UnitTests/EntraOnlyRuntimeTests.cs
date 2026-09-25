using Azure.Core;
using Common.Entities;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
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
