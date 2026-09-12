using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity.Core.Common;
using System.Data.Entity.Infrastructure.DependencyResolution;
using System.Data.Entity.Migrations.Sql;
using System.Data.Entity.SqlServer;
using System.Linq;

namespace Common.Entities.Sql
{
    /// <summary>
    /// Answers Entity Framework's lookups for the legacy <c>System.Data.SqlClient</c> invariant name with
    /// the modern <c>Microsoft.Data.SqlClient</c> services, without claiming that name as the provider's
    /// identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Azure App Service hands a connection string saved with type <c>SQLAzure</c> to a .NET Framework app
    /// with <c>providerName="System.Data.SqlClient"</c>. The installer saves <c>SPOInsightsEntities</c>
    /// that way and every existing deployment already has it, so EF is asked for the legacy name at
    /// runtime regardless of which provider this build ships.
    /// </para>
    /// <para>
    /// The obvious implementation - <c>SetProviderFactory</c> and <c>SetProviderServices</c> under the
    /// legacy name - is wrong, and subtly so. Both also register a <b>reverse</b>
    /// <see cref="IProviderInvariantName"/> lookup keyed on the factory instance. Since
    /// <see cref="MicrosoftSqlDbConfiguration"/> has already registered the modern name, the later legacy
    /// registration wins, and EF then reports every <c>Microsoft.Data.SqlClient</c> connection as
    /// "System.Data.SqlClient". Provider-specific services - above all
    /// <see cref="MicrosoftSqlServerMigrationSqlGenerator"/> - are keyed on the modern name only, so they
    /// stop resolving and <c>DatabaseUpgrader</c> fails with "No MigrationSqlGenerator found for provider".
    /// Ordinary queries keep working, which is what makes it dangerous: it would surface only when a
    /// customer upgrades a database.
    /// </para>
    /// <para>
    /// This resolver is therefore deliberately <b>forward-only</b>. It answers "what services does the
    /// legacy name need?" and never "what is this factory called?", leaving the modern name as the
    /// provider's single identity. See issue #511.
    /// </para>
    /// </remarks>
    internal sealed class LegacyProviderNameAliasResolver : IDbDependencyResolver
    {
        public object GetService(Type type, object key)
        {
            if (type == null) return null;

            // Only ever answer for the legacy invariant name; everything else falls through to EF's
            // normal resolution, including every lookup keyed on the factory instance.
            if (!string.Equals(key as string, SPOInsightsDBConfiguration.LegacyProviderInvariantName, StringComparison.Ordinal))
                return null;

            if (type == typeof(DbProviderServices))
                return MicrosoftSqlProviderServices.Instance;

            if (type == typeof(DbProviderFactory))
                return Microsoft.Data.SqlClient.SqlClientFactory.Instance;

            // Migrations are resolved by provider name, so the alias has to cover the SQL generator or
            // DatabaseUpgrader breaks on any deployment whose connection string carries the legacy name -
            // which is all of them.
            if (type == typeof(Func<MigrationSqlGenerator>))
                return (Func<MigrationSqlGenerator>)(() => new MicrosoftSqlServerMigrationSqlGenerator());

            return null;
        }

        public IEnumerable<object> GetServices(Type type, object key)
        {
            var service = GetService(type, key);
            return service == null ? Enumerable.Empty<object>() : new[] { service };
        }
    }
}
