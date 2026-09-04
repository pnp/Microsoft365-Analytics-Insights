using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using CloudInstallEngine.Azure;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Installer base class that does something long-running with a config file
    /// </summary>
    public abstract class BaseInstallProcess
    {
        private bool _sqlTestDoneAlready = false;
        public BaseInstallProcess(SolutionInstallConfig config, ILogger logger)
        {
            this.Config = config;
            // Tee everything logged during the install into _installLogEvents so the full log can be
            // registered into sys_configs.messages, while still forwarding to the on-screen logger.
            _logger = new InstallLogCapturingLogger(logger, _installLogEvents);
        }

        protected List<InstallLogEventArgs> _installLogEvents = new List<InstallLogEventArgs>();
        protected readonly ILogger _logger;


        public SolutionInstallConfig Config { get; set; }

        public async Task<bool> VerifySQL(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }
            if (_sqlTestDoneAlready) return true;

            var (ok, error) = await TrySqlConnection(connectionString);
            if (!ok)
            {
                ReportSqlConnectionFailure(connectionString, error);
                return false;
            }

            _sqlTestDoneAlready = true;
            return true;
        }

        /// <summary>
        /// How long to wait between retries after repairing the firewall rule. Azure's own message says a rule
        /// change can take up to five minutes to take effect; in practice it is seconds, so this ramps up
        /// rather than waiting the worst case every time. Total budget ~3 minutes.
        /// </summary>
        static readonly TimeSpan[] _firewallRepairRetryBackoff =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(45),
            TimeSpan.FromSeconds(60),
        };

        /// <summary>
        /// Verify SQL connectivity, repairing the installer's own firewall rule and retrying if Azure rejects
        /// us for coming from an address the firewall does not allow.
        /// </summary>
        /// <param name="repairFirewallForIp">
        /// Creates/updates the installer's firewall rule for the given IP, returning whether it succeeded.
        /// Null disables self-healing (e.g. a private-only deployment, where Azure rejects firewall edits with
        /// <c>DenyPublicEndpointEnabled</c> and the right answer is the VNet guidance instead).
        /// </param>
        /// <remarks>
        /// The client IP comes from Azure's own <c>40615</c> rejection message, which is authoritative: it is
        /// the address this SQL server actually saw, so it beats asking a third-party echo service and is
        /// correct behind NAT, proxies and split-tunnel VPNs. Retries are bounded so a permanently broken
        /// environment cannot loop. See issue #326.
        /// </remarks>
        public async Task<bool> VerifySqlWithFirewallSelfHeal(string connectionString, Func<string, Task<bool>> repairFirewallForIp)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }
            if (_sqlTestDoneAlready) return true;

            string repairedForIp = null;

            for (var attempt = 0; ; attempt++)
            {
                var (ok, error) = await TrySqlConnection(connectionString);
                if (ok)
                {
                    if (repairedForIp != null)
                    {
                        _logger.LogInformation(
                            $"SQL connectivity restored after updating the firewall rule for {repairedForIp}. Continuing with the install.");
                    }
                    _sqlTestDoneAlready = true;
                    return true;
                }

                // Only a firewall rejection is self-healable, and only while retries remain.
                var canRetry = attempt < _firewallRepairRetryBackoff.Length;
                if (repairFirewallForIp == null
                    || !canRetry
                    || error == null
                    || error.Number != SqlFirewallRules.ClientIpNotAllowedErrorNumber
                    || !SqlFirewallRules.TryGetBlockedClientIp(error.Message, out var blockedIp))
                {
                    ReportSqlConnectionFailure(connectionString, error);

                    if (repairedForIp != null)
                    {
                        var stillFirewallBlocked = error != null
                            && error.Number == SqlFirewallRules.ClientIpNotAllowedErrorNumber;

                        if (stillFirewallBlocked)
                        {
                            // Azure is STILL reporting a firewall rejection, so do not claim the firewall is
                            // ruled out - the rule was accepted by ARM but has not taken effect yet. Azure
                            // documents propagation as taking up to five minutes; we waited less than that.
                            _logger.LogError(
                                $"The SQL firewall rule was updated to allow {repairedForIp} and Azure accepted the change, but SQL Server " +
                                "is still rejecting this host. Firewall changes can take up to five minutes to take effect - wait a few " +
                                "minutes and re-run the installer. If it persists, confirm the rule exists on the server in the Azure portal.");
                        }
                        else
                        {
                            // A different failure now, so the firewall genuinely is dealt with. Say so, or the
                            // admin will keep chasing the IP.
                            _logger.LogError(
                                $"NOTE: the SQL firewall rule was updated and verified to allow {repairedForIp} during this run, and SQL Server " +
                                "is no longer reporting a firewall rejection - so the firewall is not the remaining cause. Look at the " +
                                "login/password, any database-level firewall, Private Link configuration, or outbound filtering on this network.");
                        }
                    }
                    return false;
                }

                if (blockedIp == repairedForIp)
                {
                    // The rule already names this exact address; waiting longer for propagation is the only
                    // thing left, and the backoff below does that.
                    _logger.LogInformation(
                        $"SQL Server still reports {blockedIp} as blocked after the rule was updated - waiting for the firewall change to take effect...");
                }
                else
                {
                    _logger.LogWarning(
                        $"Azure SQL rejected this host: it reports our address as {blockedIp}, which the firewall does not allow. " +
                        "Updating the installer's firewall rule to that address and retrying - this normally means the public IP " +
                        "changed since the last install (DHCP renewal, a different network, or VPN on/off).");

                    bool repaired;
                    try
                    {
                        repaired = await repairFirewallForIp(blockedIp);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Could not update the SQL Server firewall rule for {blockedIp}: {ex.Message}");
                        ReportSqlConnectionFailure(connectionString, error);
                        return false;
                    }

                    if (!repaired)
                    {
                        ReportSqlConnectionFailure(connectionString, error);
                        return false;
                    }

                    repairedForIp = blockedIp;
                }

                var wait = _firewallRepairRetryBackoff[attempt];
                _logger.LogInformation($"Waiting {wait.TotalSeconds:0}s for the SQL Server firewall change to propagate, then retrying...");
                await Task.Delay(wait);
            }
        }

        /// <summary>Opens a connection and runs a trivial query. Returns the SqlException rather than logging it.</summary>
        async Task<(bool ok, SqlException error)> TrySqlConnection(string connectionString)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                try
                {
                    var sqlConnectionInfo = new SqlConnectionStringBuilder(connectionString);
                    _logger.LogInformation($"Testing connection to SQL Server '{sqlConnectionInfo.DataSource}'");

                    conn.Open(); // throws if invalid
                    var cmd = conn.CreateCommand();
                    cmd.CommandText = "Select @@version";
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (SqlException ex)
                {
                    return (false, ex);
                }
            }
            _logger.LogInformation($"Connection test to SQL Server successful.");
            return (true, null);
        }

        void ReportSqlConnectionFailure(string connectionString, SqlException error)
        {
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource;

            _logger.LogError($"Error testing SQL connection to '{dataSource}': '{error?.Message}'. " +
                $"Verify network connectivity to server.");

            if (PrivateNetworkGuidance.IsPrivateNetworkOnly(Config))
            {
                _logger.LogError(PrivateNetworkGuidance.BuildVmOnVNetGuidance("the SQL connectivity test and database schema initialisation", Config.NetworkConfig?.VNetName));
            }
        }

        #region ExecuteAndReportFailure

        protected async Task<bool> ExecuteAndReportFailure(string taskName, Func<Task> taskFunctionDelegate)
        {
            if (string.IsNullOrEmpty(taskName)) throw new ArgumentNullException(nameof(taskName));

            try
            {
                await taskFunctionDelegate();
                return true;
            }
            catch (Exception ex)
            {
                bool addDefaultLogging = true;
                if (addDefaultLogging) ReportError(taskName, ex);

                throw;
            }
        }
        public async Task<T> ExecuteAndReportFailure<T>(string taskName, Func<Task<T>> taskFunctionDelegate)
        {
            return await ExecuteReportFailureAndThrowExceptionIfCritical(taskName, taskFunctionDelegate, null);
        }
        public async Task<T> ExecuteReportFailureAndThrowExceptionIfCritical<T>(string taskName, Func<Task<T>> taskFunctionDelegate, Func<Exception, bool> onError)
        {
            if (string.IsNullOrEmpty(taskName)) throw new ArgumentNullException(nameof(taskName));

            try
            {
                return await taskFunctionDelegate();
            }
            catch (Exception ex)
            {
                bool addDefaultLogging = true;
                if (onError != null) addDefaultLogging = onError(ex);
                if (addDefaultLogging) ReportError(taskName, ex);


                throw;
            }
        }
        public async Task ExecuteReportFailureAndThrowExceptionIfCritical(string taskName, Func<Task> taskActionDelegate)
        {
            if (string.IsNullOrEmpty(taskName)) throw new ArgumentNullException(nameof(taskName));

            try
            {
                await taskActionDelegate();
            }
            catch (Exception ex)
            {
                ReportError(taskName, ex);

                throw;
            }
        }

        void ReportError(string taskName, Exception ex)
        {
            Console.WriteLine(ex.Message);
            _logger.LogError($"Unexpected error on installer task '{taskName}': Exception message below:");
            _logger.LogError($"{ex.Message}");
        }

        #endregion
    }

    public abstract class BaseInstallProcessWithProxy : BaseInstallProcess
    {
        protected BaseInstallProcessWithProxy(SolutionInstallConfig config, ILogger logger, InstallerProxyConfig proxyConfig) : base(config, logger)
        {
            _proxyConfig = proxyConfig;
        }
        protected readonly InstallerProxyConfig _proxyConfig;
    }
}
