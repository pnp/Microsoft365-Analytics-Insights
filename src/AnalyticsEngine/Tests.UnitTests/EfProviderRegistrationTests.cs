using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Core.Common;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Infrastructure.DependencyResolution;
using System.Data.Entity.SqlServer;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards the EF6 provider registration after the move to Microsoft.Data.SqlClient (issue #511).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installer saves the <c>SPOInsightsEntities</c> connection string on the App Service with type
    /// <c>SQLAzure</c>. Azure surfaces that to a .NET Framework app as <c>SQLAZURECONNSTR_*</c> and hands
    /// it to <c>ConfigurationManager</c> with <c>providerName="System.Data.SqlClient"</c> - the OLD
    /// invariant name - regardless of which provider this build prefers. Every existing deployment
    /// already has that setting, and re-running the installer does not change it.
    /// </para>
    /// <para>
    /// So if EF only knows <c>Microsoft.Data.SqlClient</c>, the first query after an upgrade fails with
    /// <c>No Entity Framework provider found for the ADO.NET provider with invariant name
    /// 'System.Data.SqlClient'</c>.
    /// </para>
    /// <para>
    /// The subtler failure matters more: if the machine-wide factory resolved instead, connections would
    /// be <c>System.Data.SqlClient.SqlConnection</c>, and <see cref="Common.Entities.Sql.AzureSqlAccessTokenInterceptor"/>
    /// - which casts to the <c>Microsoft.Data.SqlClient</c> type - would silently stop attaching Entra
    /// tokens. Token-authenticated installs would break with nothing wrong at the provider layer.
    /// </para>
    /// <para>
    /// These tests fail against a build that registers only the new invariant name.
    /// </para>
    /// </remarks>
    [TestClass]
    public class EfProviderRegistrationTests
    {
        const string LegacyInvariantName = "System.Data.SqlClient";
        const string ModernInvariantName = "Microsoft.Data.SqlClient";

        /// <summary>
        /// Touch the context type so EF applies <see cref="SPOInsightsDBConfiguration"/> before the
        /// resolver is queried. EF resolves its configuration lazily on first use.
        /// </summary>
        [ClassInitialize]
        public static void EnsureDbConfigurationLoaded(TestContext context)
        {
            DbConfiguration.LoadConfiguration(typeof(AnalyticsEntitiesContext));
        }

        [DataTestMethod]
        [DataRow(ModernInvariantName, DisplayName = "Modern invariant name")]
        [DataRow(LegacyInvariantName, DisplayName = "Legacy invariant name injected by App Service")]
        public void DbProviderServices_ResolvesToTheMicrosoftSqlProvider(string invariantName)
        {
            var services = DbConfiguration.DependencyResolver.GetService<DbProviderServices>(invariantName);

            Assert.IsNotNull(services,
                $"EF has no provider registered for '{invariantName}'. A connection string arriving with " +
                "that invariant name would fail with 'No Entity Framework provider found'.");
            Assert.IsInstanceOfType(services, typeof(MicrosoftSqlProviderServices),
                $"'{invariantName}' must map to the Microsoft.Data.SqlClient provider services.");
        }

        [DataTestMethod]
        [DataRow(ModernInvariantName, DisplayName = "Modern invariant name")]
        [DataRow(LegacyInvariantName, DisplayName = "Legacy invariant name injected by App Service")]
        public void DbProviderFactory_ResolvesToTheMicrosoftSqlClientFactory(string invariantName)
        {
            var factory = DbConfiguration.DependencyResolver.GetService<DbProviderFactory>(invariantName);

            Assert.IsNotNull(factory, $"EF has no DbProviderFactory registered for '{invariantName}'.");
            Assert.IsInstanceOfType(factory, typeof(Microsoft.Data.SqlClient.SqlClientFactory),
                $"'{invariantName}' must produce Microsoft.Data.SqlClient connections. If it produces the " +
                "legacy System.Data.SqlClient type, AzureSqlAccessTokenInterceptor stops attaching Entra " +
                "tokens and token-authenticated installs break silently.");
        }

        /// <summary>
        /// The consequence that actually matters: a connection created for the legacy invariant name must
        /// be the type the Entra token interceptor can act on.
        /// </summary>
        [TestMethod]
        public void ConnectionCreatedForLegacyInvariantName_IsAMicrosoftDataSqlConnection()
        {
            var factory = DbConfiguration.DependencyResolver.GetService<DbProviderFactory>(LegacyInvariantName);

            using (var connection = factory.CreateConnection())
            {
                Assert.IsInstanceOfType(connection, typeof(Microsoft.Data.SqlClient.SqlConnection),
                    "A connection built from the App Service-injected invariant name must be a " +
                    "Microsoft.Data.SqlClient.SqlConnection, otherwise AzureSqlAccessTokenInterceptor " +
                    "cannot attach an access token to it.");
            }
        }

        /// <summary>
        /// Both names must resolve to the retry strategy, or Azure SQL transient faults stop being retried
        /// on whichever name the deployment actually uses.
        /// </summary>
        [DataTestMethod]
        [DataRow(ModernInvariantName, DisplayName = "Modern invariant name")]
        [DataRow(LegacyInvariantName, DisplayName = "Legacy invariant name injected by App Service")]
        public void ExecutionStrategy_IsRegisteredForBothInvariantNames(string invariantName)
        {
            var strategy = DbConfiguration.DependencyResolver.GetService<Func<IDbExecutionStrategy>>(
                new ExecutionStrategyKey(invariantName, "tcp:contoso.database.windows.net,1433"));

            Assert.IsNotNull(strategy, $"No execution strategy registered for '{invariantName}'.");
            Assert.IsInstanceOfType(strategy(), typeof(MicrosoftSqlAzureExecutionStrategy),
                $"'{invariantName}' must use the Azure SQL retry strategy.");
        }
    }
}
