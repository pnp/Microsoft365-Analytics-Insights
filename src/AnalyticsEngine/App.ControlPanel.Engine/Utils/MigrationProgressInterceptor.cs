using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity.Infrastructure.Interception;

namespace App.ControlPanel.Engine
{
    /// <summary>Forwards SQL progress from migration connections EF creates internally, scoped to one upgrade.</summary>
    internal sealed class MigrationProgressInterceptor : DbCommandInterceptor, IDisposable
    {
        private readonly Action<string> _log;
        private readonly string _server;
        private readonly string _database;
        private readonly HashSet<SqlConnection> _connections = new HashSet<SqlConnection>();

        internal MigrationProgressInterceptor(string connectionString, Action<string> log)
        {
            var target = new SqlConnectionStringBuilder(connectionString);
            _server = target.DataSource;
            _database = target.InitialCatalog;
            _log = log;
            DbInterception.Add(this);
        }

        public override void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
            // Attach before execution, not to the caller's context connection: EF may use a different one.
            if (command.Connection is SqlConnection connection
                && string.Equals(connection.DataSource, _server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(connection.Database, _database, StringComparison.OrdinalIgnoreCase))
            {
                lock (_connections)
                {
                    if (_connections.Add(connection)) connection.InfoMessage += OnInfoMessage;
                }
                if (command.CommandText.IndexOf("NOWAIT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // ExecuteNonQuery buffers InfoMessage until the batch finishes. A drained reader streams NOWAIT
                    // without FireInfoMessageEventOnUserErrors, which would turn real SQL failures into success.
                    try
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.NextResult()) { }
                            interceptionContext.Result = reader.RecordsAffected;
                        }
                    }
                    catch (SqlException exception)
                    {
                        LogInformationalErrors(exception);
                        throw;
                    }
                }
            }
            base.NonQueryExecuting(command, interceptionContext);
        }

        private void OnInfoMessage(object sender, SqlInfoMessageEventArgs args)
        {
            foreach (SqlError error in args.Errors) LogMessage(error);
        }

        public override void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
            // SqlClient can bundle informational messages into the exception instead of raising InfoMessage.
            if (interceptionContext.Exception is SqlException exception && command.Connection is SqlConnection connection)
            {
                lock (_connections)
                {
                    if (_connections.Contains(connection))
                        LogInformationalErrors(exception);
                }
            }
            base.NonQueryExecuted(command, interceptionContext);
        }

        private void LogInformationalErrors(SqlException exception)
        {
            foreach (SqlError error in exception.Errors)
                if (error.Class <= 10) LogMessage(error);
        }

        private void LogMessage(SqlError error)
        {
            var severity = error.Message.IndexOf("outcome=Failed", StringComparison.OrdinalIgnoreCase) >= 0 ? "Error"
                : error.Message.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0
                  || error.Message.IndexOf("retry", StringComparison.OrdinalIgnoreCase) >= 0 ? "Warning" : "Information";
            _log?.Invoke($"[{DateTime.UtcNow:O}] MigrationSqlProgress: severity={severity} sqlError={error.Number}; {error.Message}");
        }

        public void Dispose()
        {
            DbInterception.Remove(this);
            lock (_connections)
            {
                foreach (var connection in _connections) connection.InfoMessage -= OnInfoMessage;
                _connections.Clear();
            }
        }
    }
}
