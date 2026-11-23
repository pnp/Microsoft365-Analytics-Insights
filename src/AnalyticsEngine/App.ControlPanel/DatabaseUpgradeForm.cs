using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using System;
using System.ComponentModel;
using System.Drawing;
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
            if (LicenseManager.UsageMode != LicenseUsageMode.Designtime)
                ConfigureLayout();
        }

        private void ConfigureLayout()
        {
            SuspendLayout();
            var content = CreateLayout("upgradeContent");
            foreach (var label in new[] { label1, label2, label3 })
                AddLayoutRow(content, label);

            var connection = CreateLayout("upgradeConnection");
            connection.Padding = new Padding(8);
            foreach (var control in new Control[] { label4, txtConnectionString, lnkAutodetect, lblAuthMode })
                AddLayoutRow(connection, control);
            ConfigureGroup(grpConnectionString, connection);
            AddLayoutRow(content, grpConnectionString);

            var credentials = CreateLayout("upgradeCredentials");
            credentials.Padding = new Padding(8);
            credentials.ColumnCount = 2;
            credentials.ColumnStyles.Clear();
            credentials.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddLayoutRow(credentials, lblEntraInfo);
            credentials.SetColumnSpan(lblEntraInfo, 2);
            var labels = new[] { lblEntraTenantId, lblEntraClientId, lblEntraSecret };
            var fields = new[] { txtEntraTenantId, txtEntraClientId, txtEntraSecret };
            for (var i = 0; i < fields.Length; i++)
            {
                AddLayoutRow(credentials, labels[i]);
                fields[i].Dock = DockStyle.Fill;
                fields[i].Margin = new Padding(8, 0, 0, 6);
                credentials.Controls.Add(fields[i], 1, credentials.RowCount - 1);
            }
            ConfigureGroup(grpEntra, credentials);
            AddLayoutRow(content, grpEntra);

            grpLog.Dock = DockStyle.Fill;
            grpLog.MinimumSize = new Size(0, 120);
            grpLog.Margin = new Padding(0, 0, 0, 6);
            grpLog.Padding = new Padding(8);
            txtLog.Dock = DockStyle.Fill;
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
            content.Controls.Add(grpLog, 0, content.RowCount++);

            var viewport = new Panel
            {
                Name = "upgradeViewport",
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12)
            };
            viewport.Controls.Add(content);
            // A percent row in an autosized table can feed the log's current height back into its preferred size.
            content.Layout += (sender, args) =>
            {
                var settingsHeight = content.GetRowHeights().Take(content.RowCount - 1).Sum();
                content.RowStyles[content.RowCount - 1].Height = Math.Max(grpLog.MinimumSize.Height + grpLog.Margin.Vertical,
                    viewport.ClientSize.Height - viewport.Padding.Vertical - settingsHeight);
            };
            viewport.ClientSizeChanged += (sender, args) => content.PerformLayout();

            var footer = CreateLayout("upgradeFooter");
            footer.Dock = DockStyle.Bottom;
            footer.Padding = new Padding(12, 6, 12, 12);
            AddLayoutRow(footer, chkConfirm);
            var actions = CreateLayout("upgradeActions");
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 3;
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.RowCount = 1;
            actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            progressBar.Anchor = AnchorStyles.Left;
            progressBar.Margin = new Padding(0, 0, 8, 0);
            actions.Controls.Add(progressBar, 0, 0);
            var buttons = new[] { btnCancel, btnUpgrade };
            for (var i = 0; i < buttons.Length; i++)
            {
                buttons[i].AutoSize = true;
                buttons[i].MinimumSize = new Size(90, 32);
                buttons[i].Size = buttons[i].MinimumSize;
                buttons[i].Anchor = AnchorStyles.Right;
                buttons[i].Margin = new Padding(8, 0, 0, 0);
                actions.Controls.Add(buttons[i], i + 1, 0);
            }
            AddLayoutRow(footer, actions);
            viewport.TabIndex = 0;
            footer.TabIndex = 1;
            chkConfirm.TabIndex = 0;
            actions.TabIndex = 1;
            btnCancel.TabIndex = 0;
            btnUpgrade.TabIndex = 1;
            Controls.Add(viewport);
            Controls.Add(footer);
            MinimumSize = new Size(480, 360);
            ClientSize = new Size(640, 640);
            StartPosition = FormStartPosition.CenterParent;
            ResumeLayout(true);
        }

        private static TableLayoutPanel CreateLayout(string name)
        {
            var layout = new TableLayoutPanel
            {
                Name = name,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return layout;
        }

        private static void AddLayoutRow(TableLayoutPanel layout, Control control)
        {
            control.AutoSize = true;
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, 0, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, layout.RowCount++);
        }

        private static void ConfigureGroup(GroupBox group, TableLayoutPanel content)
        {
            group.Controls.Clear();
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Controls.Add(content);
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
            var workingArea = Screen.FromControl(Owner ?? this).WorkingArea;
            Size = new Size(Math.Min(Width, workingArea.Width), Math.Min(Height, workingArea.Height));

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
