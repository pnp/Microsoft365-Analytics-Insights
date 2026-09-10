using DataUtils.Sql;
using System.Data;
using System.Data.Common;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.SqlClient;

namespace Common.Entities.Sql
{
    /// <summary>
    /// Attaches a Microsoft Entra ID access token to every Entity Framework connection that needs one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An Azure SQL server can be created with SQL authentication disabled, in which case the connection
    /// string has no <c>user id</c> / <c>password</c> and the caller must set
    /// <see cref="SqlConnection.AccessToken"/> instead. EF6 has no built-in support for that, but it does
    /// route connection opens through <see cref="DbInterception"/> - including the ones the migration
    /// pipeline makes for itself, which is the case that matters here, because a schema upgrade opens
    /// connections EF creates internally rather than the context's own.
    /// </para>
    /// <para>
    /// Deliberately inert unless the connection string genuinely needs a token
    /// (<see cref="AzureSqlTokenAuth.NeedsAccessToken"/>), so existing SQL-authentication installs and
    /// LocalDB test databases are completely unaffected. See issue #117.
    /// </para>
    /// </remarks>
    public class AzureSqlAccessTokenInterceptor : IDbConnectionInterceptor
    {
        public void Opening(DbConnection connection, DbConnectionInterceptionContext interceptionContext)
        {
            var sqlConnection = connection as SqlConnection;
            if (sqlConnection == null) return;

            AzureSqlTokenAuth.ApplyAccessTokenIfNeeded(sqlConnection);
        }

        #region Interface members with nothing to do

        // EF6 ships no base class for IDbConnectionInterceptor (unlike DbCommandInterceptor), so every
        // member has to be implemented even though only Opening does anything.

        public void Opened(DbConnection connection, DbConnectionInterceptionContext interceptionContext) { }
        public void BeganTransaction(DbConnection connection, BeginTransactionInterceptionContext interceptionContext) { }
        public void BeginningTransaction(DbConnection connection, BeginTransactionInterceptionContext interceptionContext) { }
        public void Closed(DbConnection connection, DbConnectionInterceptionContext interceptionContext) { }
        public void Closing(DbConnection connection, DbConnectionInterceptionContext interceptionContext) { }
        public void ConnectionStringGetting(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void ConnectionStringGot(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void ConnectionStringSetting(DbConnection connection, DbConnectionPropertyInterceptionContext<string> interceptionContext) { }
        public void ConnectionStringSet(DbConnection connection, DbConnectionPropertyInterceptionContext<string> interceptionContext) { }
        public void ConnectionTimeoutGetting(DbConnection connection, DbConnectionInterceptionContext<int> interceptionContext) { }
        public void ConnectionTimeoutGot(DbConnection connection, DbConnectionInterceptionContext<int> interceptionContext) { }
        public void DatabaseGetting(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void DatabaseGot(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void DataSourceGetting(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void DataSourceGot(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void Disposing(DbConnection connection, DbConnectionInterceptionContext interceptionContext) { }
        public void Disposed(DbConnection connection, DbConnectionInterceptionContext interceptionContext) { }
        public void EnlistingTransaction(DbConnection connection, EnlistTransactionInterceptionContext interceptionContext) { }
        public void EnlistedTransaction(DbConnection connection, EnlistTransactionInterceptionContext interceptionContext) { }
        public void ServerVersionGetting(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void ServerVersionGot(DbConnection connection, DbConnectionInterceptionContext<string> interceptionContext) { }
        public void StateGetting(DbConnection connection, DbConnectionInterceptionContext<ConnectionState> interceptionContext) { }
        public void StateGot(DbConnection connection, DbConnectionInterceptionContext<ConnectionState> interceptionContext) { }

        #endregion
    }
}
