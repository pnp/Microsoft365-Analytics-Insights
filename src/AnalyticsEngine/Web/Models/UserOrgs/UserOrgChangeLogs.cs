using Common.Entities.Config;
using Common.Entities.UserOrgs;
using System;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// Chooses where user organisation change logs are written and read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Azure Table Storage when the <c>Storage</c> connection string is set; <see cref="InMemoryUserOrgChangeLog.Shared"/>
    /// only when it is not. A configured account that cannot be reached is never swapped for memory: the
    /// log waits, pending, in the SQL outbox until the account is back, because quietly making volatile
    /// a log the deployment set out to keep durably would lose it at the next restart. The account is
    /// tried again after <see cref="RetryAfter"/>, not on every request.
    /// </para>
    /// <para>
    /// Reading always goes back to the store the job row says the log was written to - never "whichever
    /// is current" - so a log written to memory is not looked for in Table Storage, or the reverse.
    /// </para>
    /// </remarks>
    internal static class UserOrgChangeLogs
    {
        public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

        private static readonly object Gate = new object();
        private static TableStorageUserOrgChangeLog _table;
        private static DateTime _retryAfterUtc = DateTime.MinValue;
        private static DateTime _writesPausedUntilUtc = DateTime.MinValue;
        private static bool _notConfigured;

        /// <summary>
        /// Where a new change log goes, or <c>null</c> when the configured storage account cannot be
        /// reached right now and the log must wait.
        /// </summary>
        public static IUserOrgChangeLog ForWriting()
        {
            lock (Gate)
            {
                // A table that refused a write is given a rest rather than a write per sweep: every
                // pending log would otherwise fail against it, loudly, each time the admin page loads.
                if (DateTime.UtcNow < _writesPausedUntilUtc)
                {
                    return null;
                }
            }

            var table = TryGetTable();
            if (table != null)
            {
                return table;
            }

            lock (Gate)
            {
                return _notConfigured ? InMemoryUserOrgChangeLog.Shared : null;
            }
        }

        /// <summary>
        /// The store a change log was written to, or <c>null</c> when that store cannot be reached right
        /// now. A log still <see cref="UserOrgChangeLogStatus.Pending"/> has no store yet: for it, this is
        /// where it would be written now (<see cref="ForWriting"/>).
        /// </summary>
        public static IUserOrgChangeLog ForReading(UserOrgChangeLogStatus writtenTo)
        {
            switch (writtenTo)
            {
                case UserOrgChangeLogStatus.TableStorage: return TryGetTable();
                case UserOrgChangeLogStatus.Memory: return InMemoryUserOrgChangeLog.Shared;
                case UserOrgChangeLogStatus.Pending: return ForWriting();
                default: return null;
            }
        }

        private static TableStorageUserOrgChangeLog TryGetTable()
        {
            lock (Gate)
            {
                if (_table != null)
                {
                    return _table;
                }

                if (_notConfigured || DateTime.UtcNow < _retryAfterUtc)
                {
                    return null;
                }

                try
                {
                    var config = new AppConfig();
                    var connectionString = config.ConnectionStrings?.StorageConnectionString;
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        // Not configured, as opposed to unreachable: nothing will change until the app
                        // restarts with new settings, so do not keep asking.
                        _notConfigured = true;
                        return null;
                    }

                    var client = UserOrgChangeLogTableFactory.CreateAndEnsureTable(
                        connectionString,
                        TableStorageUserOrgChangeLog.TableName,
                        config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString(),
                        config.ClientID,
                        config.ClientSecret);

                    TableStorageUserOrgChangeLog created = null;
                    created = new TableStorageUserOrgChangeLog(client, ex => WriteFailed(created, ex));
                    _table = created;
                    return _table;
                }
                catch (Exception ex)
                {
                    _retryAfterUtc = DateTime.UtcNow.Add(RetryAfter);
                    Record(ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// A write the table refused: no more writes until <see cref="RetryAfter"/> has passed. The logs
        /// wait, pending, in the outbox; reads carry on. A table deleted since it was opened is created
        /// again by the next attempt.
        /// </summary>
        private static void WriteFailed(TableStorageUserOrgChangeLog table, Exception ex)
        {
            lock (Gate)
            {
                _writesPausedUntilUtc = DateTime.UtcNow.Add(RetryAfter);
                if (Status(ex) == 404 && ReferenceEquals(_table, table))
                {
                    _table = null;
                }
            }

            Record(ex);
        }

        private static void Record(Exception ex)
        {
            UserOrgImportAppInsights.Default.Record(new UserOrgImportTelemetryEvent
            {
                Stage = UserOrgImportStages.ChangeLogStorageUnavailable,
                Code = Classify(ex),
                ExceptionType = ex.GetBaseException().GetType().Name,
            });
        }

        private static int? Status(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                var failed = current as Azure.RequestFailedException;
                if (failed != null)
                {
                    return failed.Status;
                }
            }

            return null;
        }

        /// <summary>A stable reason, for telemetry - never the exception's message.</summary>
        private static string Classify(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                var failed = current as Azure.RequestFailedException;
                if (failed != null && !string.IsNullOrEmpty(failed.ErrorCode))
                {
                    return failed.ErrorCode;
                }
            }

            return ex is InvalidOperationException ? "noUsableCredential" : "transport";
        }
    }
}
