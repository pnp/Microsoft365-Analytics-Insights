using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class DatabaseUpgradeForm : Form
    {
        // Replaced per autodetect run: the messages are shown verbatim in a failure dialog, so keeping one
        // instance would report the previous attempt's errors alongside this one's.
        private InMemoryLogger _logger = new InMemoryLogger();

        /// <summary>Any long-running work: disables input for both the upgrade and autodetection.</summary>
        private bool _busy;

        /// <summary>
        /// The schema upgrade specifically. Separate from <see cref="_busy"/> because only this one may not
        /// be abandoned, so only this one blocks closing the window.
        /// </summary>
        private bool _upgradeRunning;

        /// <summary>Set once the window has gone, so a worker that finishes later stays silent.</summary>
        /// <remarks>
        /// <see cref="Form.IsDisposed"/> is not enough: a form shown with <c>ShowDialog()</c> is only
        /// HIDDEN when it closes, so without this an autodetect that completed after the operator closed
        /// the window would put an application-modal message box up over the main installer.
        /// </remarks>
        private bool _closed;

        public DatabaseUpgradeForm()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Configuration currently open in the installer. Optional: without one the form still works with a
        /// hand-typed connection string, it just cannot autodetect the target or default the Microsoft
        /// Entra ID credential.
        /// </summary>
        public SolutionInstallConfig SolutionInstallConfig { get; set; }

        /// <summary>
        /// Whether the entered connection string can only be used with Microsoft Entra ID, i.e. it names an
        /// Azure SQL server and carries no login. Same test the upgrade itself applies - see issue #117.
        /// </summary>
        bool EntraAuthRequired => DatabaseUpgradeRequest.NeedsEntraCredential(txtConnectionString.Text);

        #region Upgrade

        private void btnUpgrade_Click(object sender, EventArgs e)
        {
            // Read on the UI thread and passed as an argument: the worker runs on a background thread,
            // where touching a control is not safe.
            var request = DatabaseUpgradeRequest.Build(txtConnectionString.Text, txtEntraTenantId.Text,
                txtEntraClientId.Text, txtEntraSecret.Text);

            if (!request.IsValid)
            {
                MessageBox.Show(request.ValidationError, "Can't Upgrade", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            txtLog.Text = string.Empty;
            _upgradeRunning = true;
            SetLoadingState(true);
            backgroundWorker.RunWorkerAsync(request.UpgradeInfo);
        }

        private void backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            // Test upgrade
            try
            {
                DatabaseUpgrader.CheckDbUpgraded((DatabaseUpgradeInfo)e.Argument, msg => LogEvent(msg));
            }
            catch (Exception ex)
            {
                LogEvent(ex.ToString());
            }
        }

        private void backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Cleared first and unconditionally: BackgroundWorker always raises this event, so this is what
            // guarantees the close guard can never leave the window permanently shut.
            _upgradeRunning = false;

            if (_closed || this.IsDisposed) return;
            SetLoadingState(false);

            // DoWork logs its own exceptions, so anything here escaped that - report it rather than let
            // the form sit looking like the upgrade finished cleanly.
            if (e.Error != null) LogEvent(e.Error.ToString());
        }

        #endregion

        #region Autodetect

        private void lnkAutodetect_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (SolutionInstallConfig == null)
            {
                MessageBox.Show("No configuration is open in the installer, so there is nothing to detect your SQL details from. " +
                    "Open a configuration file from the File menu, or enter the connection string by hand.",
                    "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            if (!SolutionInstallVerifier.ConfigIsReadyForSqlAutodetection(SolutionInstallConfig))
            {
                MessageBox.Show("The configuration open in the installer doesn't have everything needed to detect your SQL details. " +
                    "You need at least: an installer account, resource-group, subscription ID, and SQL Server name.",
                    "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            _logger = new InMemoryLogger();
            SetLoadingState(true);
            backgroundWorkerAutoDetectSql.RunWorkerAsync();
        }

        private void backgroundWorkerAutoDetectSql_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                // Constructed inside the try: the base constructor builds a ClientSecretCredential, which
                // throws on a malformed directory ID. The readiness check only proves the fields are
                // non-blank, so that is reachable, and an escape here would surface as an unhandled
                // exception on the UI thread rather than the autodetect failure dialog below.
                var verifier = new SolutionInstallVerifier(SolutionInstallConfig, _logger, new TestConfiguration());

                e.Result = verifier.GetSqlDetails(SolutionInstallConfig.SQLServerAdminPassword).Result;
            }
            catch (AggregateException ex)
            {
                // Unwrap, or every failure is reported as the useless "One or more errors occurred."
                e.Result = ex.Flatten().InnerExceptions.FirstOrDefault() ?? (Exception)ex;
            }
            catch (Exception ex)
            {
                e.Result = ex;
            }
        }

        private void backgroundWorkerAutoDetectSql_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Autodetection is abandonable, so it can finish after the operator closed the window. Say
            // nothing in that case - every branch below shows a message box, which would otherwise appear
            // over the main installer with no obvious source.
            if (_closed || this.IsDisposed) return;

            SetLoadingState(false);

            // Checked before e.Result, which rethrows whatever the worker threw.
            if (e.Error != null)
            {
                MessageBox.Show($"Couldn't get some/all of your Azure resources data: {e.Error.Message}",
                    "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            var detected = e.Result as AutodetectedSqlDetails;
            if (detected?.Sql != null)
            {
                // Unlike the connectivity test, the schema upgrade has to run against the solution's own
                // database rather than the server, so the catalog is named here.
                txtConnectionString.Text = detected.Sql.GetConnectionString(SolutionInstallConfig.SQLServerDatabaseName);
                ApplyInstallerAccountAsEntraCredential(overwrite: false);
                RefreshAuthenticationUi();

                if (string.IsNullOrWhiteSpace(SolutionInstallConfig.SQLServerDatabaseName))
                {
                    // Autodetection only ever finds the server; the database name comes from the
                    // configuration. Without it the detected string would upgrade 'master'.
                    MessageBox.Show("Your SQL Server was auto-detected, but the loaded configuration has no database name, so the " +
                        "detected connection string names no database. Set the database name on the Azure Config tab and autodetect " +
                        "again, or add 'initial catalog=<your analytics database>;' by hand before upgrading.",
                        "Database Name Missing", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    return;
                }

                MessageBox.Show(EntraAuthRequired
                    ? "Your SQL resource was auto-detected successfully. It uses Microsoft Entra ID authentication, so the upgrade " +
                      "will sign in with the service principal below. Check the confirmation box and click 'Upgrade' to proceed."
                    : "Your SQL resource was auto-detected successfully, using SQL Server authentication. Check the confirmation " +
                      "box and click 'Upgrade' to proceed.",
                    "Autodetect Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (e.Result is Exception)
            {
                MessageBox.Show($"Couldn't get some/all of your Azure resources data: {((Exception)e.Result).Message}",
                    "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            MessageBox.Show($"Couldn't find SQL Server '{SolutionInstallConfig.SQLServerName}' in resource-group " +
                $"'{SolutionInstallConfig.ResourceGroupName}'. Details:{Environment.NewLine}{_logger.GetMessages()}",
                "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }

        /// <summary>
        /// Default the Microsoft Entra ID credential to the installer's own app registration, which is the
        /// principal the installer assigns as the SQL server's Entra administrator.
        /// </summary>
        void ApplyInstallerAccountAsEntraCredential(bool overwrite)
        {
            var account = SolutionInstallConfig?.InstallerAccount;
            if (account == null) return;

            if (overwrite || txtEntraTenantId.Text.Trim().Length == 0) txtEntraTenantId.Text = account.DirectoryId;
            if (overwrite || txtEntraClientId.Text.Trim().Length == 0) txtEntraClientId.Text = account.ClientId;
            if (overwrite || txtEntraSecret.Text.Trim().Length == 0) txtEntraSecret.Text = account.Secret;
        }

        #endregion

        #region UI State

        void LogEvent(string msg)
        {
            // The upgrade can run for a long time, so check before marshalling: Invoke against a form whose
            // handle has gone throws, and this runs inline inside DatabaseUpgrader, where an exception would
            // abandon the upgrade part-way through.
            if (this.IsDisposed || !this.IsHandleCreated) return;

            if (this.InvokeRequired)
            {
                try
                {
                    this.Invoke(new Action(() => LogEvent(msg)));
                }
                catch (ObjectDisposedException)
                {
                    // Closed between the check above and the marshalling. Nothing left to log to.
                }
                catch (InvalidOperationException)
                {
                    // Same race, reported differently when the window handle is destroyed rather than the
                    // control disposed.
                }
                return;
            }
            txtLog.AppendText(msg + Environment.NewLine);
        }

        /// <summary>
        /// Refuse to close while the schema upgrade is running. Closing would not stop it - the worker keeps
        /// migrating the schema - it would only hide the log and let the operator start a second upgrade
        /// against the same database from the main menu.
        /// </summary>
        /// <remarks>
        /// Deliberately keyed on the upgrade rather than on <c>_busy</c>. Autodetection also sets
        /// <c>_busy</c>, but it is a read-only Azure lookup that is safe to abandon - and because this form
        /// is modal, blocking on a slow ARM call would wedge the whole installer behind it.
        /// </remarks>
        private void DatabaseUpgradeForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_upgradeRunning) return;

            // Never argue with Windows shutting down or the app exiting - a cancelled close there just
            // blocks the operator from logging off.
            if (e.CloseReason == CloseReason.WindowsShutDown
                || e.CloseReason == CloseReason.TaskManagerClosing
                || e.CloseReason == CloseReason.ApplicationExitCall)
            {
                return;
            }

            e.Cancel = true;
            MessageBox.Show("A database upgrade is in progress and can't be interrupted - closing this window would hide the log " +
                "while the upgrade carried on in the background. Wait for it to finish.",
                "Upgrade In Progress", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }

        private void DatabaseUpgradeForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _closed = true;
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void chkConfirm_CheckedChanged(object sender, EventArgs e)
        {
            UpgradeButtonDisableCheck();
        }

        private void txtConnectionString_TextChanged(object sender, EventArgs e)
        {
            RefreshAuthenticationUi();
        }

        private void DatabaseUpgradeForm_Load(object sender, EventArgs e)
        {
            // The designer holds a sample connection string so the form reads sensibly at design time. It
            // must not survive to runtime: it names a fictional server, and - because it carries a SQL
            // login - it would wrongly grey out the Microsoft Entra ID box for an Entra-only deployment.
            txtConnectionString.Text = string.Empty;
            txtLog.Text = string.Empty;

            ApplyInstallerAccountAsEntraCredential(overwrite: true);
            SetLoadingState(false);
        }

        private void DatabaseUpgradeForm_Activated(object sender, EventArgs e)
        {
            // On load is too soon, apparently
            UpgradeButtonDisableCheck();
        }

        private void UpgradeButtonDisableCheck()
        {
            btnUpgrade.Enabled = !_busy && chkConfirm.Checked;
        }

        /// <summary>
        /// Say which authentication the entered connection string implies, and only offer the Microsoft
        /// Entra ID credential when it is the one that will actually be used.
        /// </summary>
        void RefreshAuthenticationUi()
        {
            grpEntra.Enabled = !_busy && EntraAuthRequired;
            lblAuthMode.Text = DatabaseUpgradeRequest.DescribeAuthentication(txtConnectionString.Text);
        }

        void SetLoadingState(bool loading)
        {
            if (_closed || this.IsDisposed) return;

            _busy = loading;

            this.Cursor = loading ? Cursors.WaitCursor : Cursors.Default;
            grpConnectionString.Enabled = !loading;
            chkConfirm.Enabled = !loading;

            // Closing is only blocked during the upgrade, so keep the Close button in step with that rather
            // than with _busy - otherwise the button is greyed out while the title-bar X still works.
            btnCancel.Enabled = !_upgradeRunning;
            progressBar.Visible = loading;

            RefreshAuthenticationUi();
            UpgradeButtonDisableCheck();
        }

        #endregion
    }
}
