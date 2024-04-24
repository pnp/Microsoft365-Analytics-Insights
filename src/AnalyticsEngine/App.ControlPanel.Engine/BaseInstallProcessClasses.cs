using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
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
            _logger = logger;
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

            var sqlConnectionInfo = new SqlConnectionStringBuilder(connectionString);
            _logger.LogInformation($"Testing connection to SQL Server '{sqlConnectionInfo.DataSource}'");
            using (var conn = new SqlConnection(connectionString))
            {
                try
                {
                    conn.Open(); // throws if invalid
                    var cmd = conn.CreateCommand();
                    cmd.CommandText = "Select @@version";
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (SqlException ex)
                {
                    _logger.LogError($"Error testing SQL connection: '{ex.Message}. " +
                        $"Verify network connectivity to server");

                    return false;
                }
            }
            _logger.LogInformation($"Connection test to SQL Server successful.");
            _sqlTestDoneAlready = true;
            return true;
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

    public abstract class BaseInstallProcessWithFtp : BaseInstallProcess
    {
        protected BaseInstallProcessWithFtp(SolutionInstallConfig config, ILogger logger, InstallerFtpConfig ftpConfig) : base(config, logger)
        {
            _ftpConfig = ftpConfig;
        }
        protected readonly InstallerFtpConfig _ftpConfig;
    }
}

