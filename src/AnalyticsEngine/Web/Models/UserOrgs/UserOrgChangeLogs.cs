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
    /// Azure Table Storage when the <c>Storage</c> connection string is set and the table can be reached;
    /// otherwise <see cref="InMemoryUserOrgChangeLog.Shared"/>. A table that could not be reached is tried
    /// again after <see cref="RetryAfter"/>, so a storage account that was briefly unreachable does not
    /// leave this process logging to memory until it next restarts.
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

        /// <summary>Where a new change log goes.</summary>
        public static IUserOrgChangeLog ForWriting()
        {
            return (IUserOrgChangeLog)TryGetTable() ?? InMemoryUserOrgChangeLog.Shared;
        }

        /// <summary>
        /// The store a change log was written to, or <c>null</c> when that store cannot be reached right now.
        /// </summary>
        public static IUserOrgChangeLog ForReading(UserOrgChangeLogStatus writtenTo)
        {
            switch (writtenTo)
            {
                case UserOrgChangeLogStatus.TableStorage: return TryGetTable();
                case UserOrgChangeLogStatus.Memory: return InMemoryUserOrgChangeLog.Shared;
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

                if (DateTime.UtcNow < _retryAfterUtc)
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
                        _retryAfterUtc = DateTime.MaxValue;
                        return null;
                    }

                    var client = UserOrgChangeLogTableFactory.CreateAndEnsureTable(
                        connectionString,
                        TableStorageUserOrgChangeLog.TableName,
                        config.TenantGUID == Guid.Empty ? null : config.TenantGUID.ToString(),
                        config.ClientID,
                        config.ClientSecret);

                    _table = new TableStorageUserOrgChangeLog(client);
                    return _table;
                }
                catch (Exception ex)
                {
                    _retryAfterUtc = DateTime.UtcNow.Add(RetryAfter);
                    UserOrgImportAppInsights.Default.Record(new UserOrgImportTelemetryEvent
                    {
                        Stage = UserOrgImportStages.ChangeLogStorageUnavailable,
                        Code = Classify(ex),
                        ExceptionType = ex.GetBaseException().GetType().Name,
                    });
                    return null;
                }
            }
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
