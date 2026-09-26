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

        /// <summary>
        /// The test database every other suite uses, so the migrator and the history read below look at the same
        /// database on every machine and in CI.
        /// </summary>
        /// <remarks>
        /// net10: read through <see cref="Common.Entities.Config.AnalyticsConfig"/> (appsettings.json, user
        /// secrets, then <c>ConnectionStrings__SPOInsightsEntities</c>), as ScratchDatabase and the other LocalDB
        /// suites do. <c>ConfigurationManager.ConnectionStrings</c> has no App.config behind it on this branch, so
        /// it returned null here and every test using this property failed with a NullReferenceException.
        /// </remarks>
        private static string TestDatabase =>
            Common.Entities.Config.AnalyticsConfig.ConnectionStrings[AnalyticsEntitiesContext.ConnectionStringName].ConnectionString;

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

        /// <summary>
        /// The installer applies migrations with <c>new AnalyticsEntitiesContext(cs, true, true)</c> and
        /// <c>Database.Initialize(true)</c> (<c>DatabaseUpgrader</c>). That must still migrate an Entra-only
        /// database: skipping it would let the upgrade report success having applied nothing.
        /// </summary>
        /// <remarks>
        /// The credential throws, so the proof is that the migration initializer asked for a SQL token to connect.
        /// A version of the runtime fix that also wrapped this initializer returned silently, with no token requested.
        /// </remarks>
        [TestMethod]
        public void ExplicitUpgrade_EntraOnlyConnection_StillRunsTheMigrationInitializer()
        {
            var credential = new TokenRequestRecorder();
            AzureSqlTokenAuth.SetCredential(credential);

            using (var db = new AnalyticsEntitiesContext(EntraOnlyUnreachableServer, true, true))
            {
                Assert.AreEqual(0, credential.Requests, "Constructing the upgrade context must not connect by itself.");

                try
                {
                    db.Database.Initialize(true);
                    Assert.Fail("The migration initializer must try to connect - and so ask for a token - rather than return silently.");
                }
                catch (Exception ex) when (!(ex is AssertFailedException))
                {
                    // Expected: the throwing credential stops the migrator before any network I/O.
                }
            }

            Assert.IsTrue(credential.Requests >= 1, "An explicit schema upgrade on Entra-only SQL must run its migrations.");
        }

        [TestMethod]
        public void ContextConstructor_LocalDbConnection_StillRunsTheRegisteredInitializer()
        {
            var initializer = new RecordingInitializer();
            Database.SetInitializer(initializer);

            using (var sqlConnection = new SqlConnection(TestDatabase))
            using (var db = new AnalyticsEntitiesContext(sqlConnection))
            {
                // force: true - EF initialises each connection string once per AppDomain, and other suites have
                // usually initialised this one already.
                db.Database.Initialize(true);
                Assert.AreEqual(1, initializer.Calls, "Non-token SQL must keep EF's existing initializer behaviour.");
            }
        }

        /// <summary>
        /// Where #609 actually failed: not at construction but at a runtime context's first EF use, which runs
        /// the registered initializer. For Entra-only runtime SQL that must be skipped - CreateDatabaseIfNotExists
        /// probes master, which the contained App Service user cannot reach.
        /// </summary>
        /// <remarks>
        /// The credential throws, so running the create initializer - or anything else that connects - fails the
        /// test. <c>Initialize(true)</c> is what a first query does (EF runs the initializer once per connection
        /// string per AppDomain; forcing it makes the test independent of what ran before).
        /// </remarks>
        [TestMethod]
        public void FirstEfUse_EntraOnlyRuntimeContext_SkipsTheCreateInitializerAndRequestsNoToken()
        {
            WarmTheModelOnTheTestDatabase();
            var credential = new TokenRequestRecorder();
            AzureSqlTokenAuth.SetCredential(credential);

            using (var db = new AnalyticsEntitiesContext(EntraOnlyUnreachableServer, true, false))
            {
                db.Database.Initialize(true);
            }

            Assert.AreEqual(0, credential.Requests, "The runtime initializer must not connect to an Entra-only server at first EF use.");
        }

        /// <summary>
        /// The importer's save path builds its context over an already-open connection. That constructor has no
        /// autoUpdate of its own, so on an Entra-only server it must not run whatever create/migrate initializer
        /// another context registered for the type.
        /// </summary>
        [TestMethod]
        public void FirstEfUse_EntraOnlyDbConnectionContext_SkipsAnInheritedInitializer()
        {
            WarmTheModelOnTheTestDatabase();
            var credential = new TokenRequestRecorder();
            AzureSqlTokenAuth.SetCredential(credential);
            Database.SetInitializer(new CreateDatabaseIfNotExists<AnalyticsEntitiesContext>());

            using (var sqlConnection = new SqlConnection(EntraOnlyUnreachableServer))
            using (var db = new AnalyticsEntitiesContext(sqlConnection))
            {
                db.Database.Initialize(true);
            }

            Assert.AreEqual(0, credential.Requests, "An inherited create initializer must not run for an Entra-only DbConnection context.");
        }

        /// <summary>
        /// EF keeps one initializer per context TYPE for the whole process. A token-backed context registers the
        /// runtime wrapper; a SQL-authentication or LocalDB context built the same way then gets that wrapper, and
        /// must still have its own initializer run. The wrapper used to capture the token connection string and
        /// skip for every context while that registration stood.
        /// </summary>
        [TestMethod]
        public void TokenContext_DoesNotSuppressALaterLocalDbContextsInitializer()
        {
            WarmTheModelOnTheTestDatabase();

            using (var tokenConnection = new SqlConnection(EntraOnlyUnreachableServer))
            using (new AnalyticsEntitiesContext(tokenConnection))
            {
            }

            var counter = new TestDatabaseCommandCounter(TestDatabase);
            System.Data.Entity.Infrastructure.Interception.DbInterception.Add(counter);
            try
            {
                using (var localConnection = new SqlConnection(TestDatabase))
                using (var db = new AnalyticsEntitiesContext(localConnection))
                {
                    db.Database.Initialize(true);
                }
            }
            finally
            {
                System.Data.Entity.Infrastructure.Interception.DbInterception.Remove(counter);
            }

            Assert.IsTrue(counter.Commands > 0,
                "CreateDatabaseIfNotExists must still check the LocalDB database after a token context registered the wrapper.");
        }

        /// <summary>
        /// The other direction for the runtime wrapper itself: for a connection with credentials of its own it must
        /// hand over to CreateDatabaseIfNotExists, which on an existing database checks its tables and model.
        /// </summary>
        [TestMethod]
        public void FirstEfUse_LocalDbRuntimeContext_StillRunsCreateDatabaseIfNotExists()
        {
            WarmTheModelOnTheTestDatabase();

            var counter = new TestDatabaseCommandCounter(TestDatabase);
            System.Data.Entity.Infrastructure.Interception.DbInterception.Add(counter);
            try
            {
                using (var db = new AnalyticsEntitiesContext(TestDatabase, true, false))
                {
                    db.Database.Initialize(true);
                }
            }
            finally
            {
                System.Data.Entity.Infrastructure.Interception.DbInterception.Remove(counter);
            }

            Assert.IsTrue(counter.Commands > 0, "The runtime wrapper must run CreateDatabaseIfNotExists for a non-token connection.");
        }

        /// <summary>
        /// Builds and caches EF's model for both constructors against the test database, so a context over the
        /// unreachable Entra-only server never has to connect just to work out the provider manifest.
        /// </summary>
        private static void WarmTheModelOnTheTestDatabase()
        {
            using (var db = new AnalyticsEntitiesContext(TestDatabase, true, false))
            {
                var unused = ((System.Data.Entity.Infrastructure.IObjectContextAdapter)db).ObjectContext;
            }

            using (var sqlConnection = new SqlConnection(TestDatabase))
            using (var db = new AnalyticsEntitiesContext(sqlConnection))
            {
                var unused = ((System.Data.Entity.Infrastructure.IObjectContextAdapter)db).ObjectContext;
            }
        }

        /// <summary>Counts EF commands sent to the test database, whichever connection object carries them.</summary>
        private sealed class TestDatabaseCommandCounter : System.Data.Entity.Infrastructure.Interception.DbCommandInterceptor
        {
            private readonly string _catalog;

            public TestDatabaseCommandCounter(string connectionString)
            {
                _catalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            }

            public int Commands { get; private set; }

            public override void ReaderExecuting(System.Data.Common.DbCommand command,
                System.Data.Entity.Infrastructure.Interception.DbCommandInterceptionContext<System.Data.Common.DbDataReader> interceptionContext) => Count(command);

            public override void NonQueryExecuting(System.Data.Common.DbCommand command,
                System.Data.Entity.Infrastructure.Interception.DbCommandInterceptionContext<int> interceptionContext) => Count(command);

            public override void ScalarExecuting(System.Data.Common.DbCommand command,
                System.Data.Entity.Infrastructure.Interception.DbCommandInterceptionContext<object> interceptionContext) => Count(command);

            private void Count(System.Data.Common.DbCommand command)
            {
                var connectionString = command?.Connection?.ConnectionString;
                if (string.IsNullOrEmpty(connectionString)) return;

                if (string.Equals(new SqlConnectionStringBuilder(connectionString).InitialCatalog, _catalog, StringComparison.OrdinalIgnoreCase))
                {
                    Commands++;
                }
            }
        }

        [TestMethod]
        public async Task PendingMigrations_HistoryReadOnFullyMigratedLocalDb_ReturnsNone()
        {
            var config = new Configuration();
            new DbMigrator(config).Update();

            using (var db = new AnalyticsEntitiesContext(TestDatabase, true, true))
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
