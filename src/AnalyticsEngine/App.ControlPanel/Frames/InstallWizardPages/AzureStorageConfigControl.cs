using Common.Entities.Installer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzureStorageConfigControl : UserControl
    {
        private List<SqlDatabaseUser> _sqlDatabaseUsers = new List<SqlDatabaseUser>();
        private TableLayoutPanel _sqlLoginFields;
        private Label _sqlLoginNotice;

        public AzureStorageConfigControl()
        {
            InitializeComponent();
            ConfigureServiceLayout();
            UpdateResponsiveUIControls();
        }

        private void ConfigureServiceLayout()
        {
            var layout = ServicePageLayout.Create(this, "Database & storage",
                "Stores imported analytics and supports background processing. Existing resources are reused.");
            lblGUIAzureStorageHeader.Visible = lblGUIAzureStorageHeaderDesc.Visible = false;
            var storage = ServicePageLayout.AddService(layout, "storageService", "Storage account",
                "Blobs stage import data and deployment packages. Tables retain import checkpoints, runtime settings and caches.",
                picStorage.Image);
            picStorage.Visible = false;
            lblGUIAzureHeaderBlobStorage.Visible = lblGUIAzureHeaderBlobStorageName.Visible = false;
            ServicePageLayout.AddFields(storage, "Account name:", txtStorageAccount);
            ServicePageLayout.AddRow(storage, lblStorageAccountURL);
            var bus = ServicePageLayout.AddService(layout, "busService", "Service Bus",
                "Queues Teams call-record notifications for reliable import. Disable it if Teams calls are not selected.",
                picServiceBus.Image);
            picServiceBus.Visible = false;
            lblGUIAzureHeaderServiceBus.Visible = lblGUIAzureServiceBusName.Visible = false;
            ServicePageLayout.AddFields(bus, "Namespace name:", txtServiceBusName, "", chkServiceBusEnabled);
            ServicePageLayout.AddRow(bus, lblServiceBusName);
            var sql = ServicePageLayout.AddService(layout, "sqlService", "SQL database",
                "Stores activity and reporting data for the portal. Schedule a maintenance window when upgrading its schema.",
                picSQL.Image);
            picSQL.Visible = false;
            lblGUIAzureHeaderSQL.Visible = lblGUIAzureSQLServerName.Visible = lblGUIAzureSQLDbName.Visible = false;
            ServicePageLayout.AddFields(sql, "Server name:", txtSQLServerName, "Database:", txtSQLDb);
            ServicePageLayout.AddRow(sql, lblSQLServerName);
            chkSqlEntraAuth.Text = "Use Microsoft Entra ID authentication (recommended)";
            ServicePageLayout.AddRow(sql, chkSqlEntraAuth);
            _sqlLoginNotice = ServicePageLayout.Text(
                "SQL login is deprecated; use it only for a server that still requires SQL authentication.");
            ServicePageLayout.AddRow(sql, _sqlLoginNotice);
            lblGUIAzureSQLUsername.Visible = lblGUIAzureSQLPassword.Visible = false;
            _sqlLoginFields = ServicePageLayout.AddFields(sql, "Server username:", txtSQLServerUsername, "Password:", txtSQLServerPassword);
            btnSqlDatabaseUsers.AutoSize = true;
            btnSqlDatabaseUsers.Padding = new Padding(8, 4, 8, 4);
            var users = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
            users.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            users.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            users.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            btnSqlDatabaseUsers.Dock = DockStyle.Top;
            btnSqlDatabaseUsers.MinimumSize = new System.Drawing.Size(150, 32);
            lblSqlDatabaseUsers.Dock = DockStyle.Fill;
            lblSqlDatabaseUsers.Margin = new Padding(8, 4, 0, 4);
            users.Controls.Add(btnSqlDatabaseUsers, 0, 0);
            users.Controls.Add(lblSqlDatabaseUsers, 1, 0);
            ServicePageLayout.AddRow(sql, users);
            foreach (var label in new[] { lblStorageAccountURL, lblServiceBusName, lblSQLServerName })
            {
                label.Dock = DockStyle.Fill;
                label.Margin = new Padding(0, 0, 0, 6);
            }
            layout.BringToFront();
        }

        /// <summary>
        /// Microsoft Entra users and groups the installer gives their own access to the database.
        /// </summary>
        /// <remarks>
        /// Edited in its own dialog rather than inline, because the alternative an admin would otherwise
        /// reach for - reassigning the SQL Server's Microsoft Entra administrator - silently locks the
        /// installer out of future schema upgrades, and that warning needs more room than a tab affords.
        /// See issue #117.
        /// </remarks>
        public List<SqlDatabaseUser> SqlDatabaseUsers
        {
            get { return _sqlDatabaseUsers; }
            set
            {
                _sqlDatabaseUsers = value ?? new List<SqlDatabaseUser>();
                UpdateResponsiveUIControls();
            }
        }

        public string SQLDb { get { return txtSQLDb.Text; } set { txtSQLDb.Text = value; } }
        public string SQLServerName { get { return txtSQLServerName.Text; } set { txtSQLServerName.Text = value; } }
        public string SQLServerPassword { get { return txtSQLServerPassword.Text; } set { txtSQLServerPassword.Text = value; } }
        public string SQLServerUsername { get { return txtSQLServerUsername.Text; } set { txtSQLServerUsername.Text = value; } }

        /// <summary>
        /// Whether to authenticate to Azure SQL with Microsoft Entra ID instead of the deprecated SQL
        /// administrator login. Only applied when the installer CREATES the SQL server - an existing
        /// server's authentication is detected and left alone. See issue #117.
        /// </summary>
        public SqlServerAuthMode SqlAuthMode
        {
            get { return chkSqlEntraAuth.Checked ? SqlServerAuthMode.EntraId : SqlServerAuthMode.SqlLogin; }
            set
            {
                chkSqlEntraAuth.Checked = value == SqlServerAuthMode.EntraId;
                UpdateResponsiveUIControls();
            }
        }
        public string StorageAccount { get { return txtStorageAccount.Text; } set { txtStorageAccount.Text = value; } }
        public string ServiceBusName { get { return txtServiceBusName.Text; } set { txtServiceBusName.Text = value; } }

        public bool ServiceBusEnabled
        {
            get { return chkServiceBusEnabled.Checked; }
            set
            {
                chkServiceBusEnabled.Checked = value;
                UpdateResponsiveUIControls();
            }
        }

        private void UpdateResponsiveUIControls()
        {
            lblStorageAccountURL.Text = $"https://{txtStorageAccount.Text}.blob.core.windows.net/";
            lblServiceBusName.Text = $"{txtServiceBusName.Text}.servicebus.windows.net";
            lblSQLServerName.Text = $"{txtSQLServerName.Text}.database.windows.net";

            // Disable SB name fields when Service Bus is disabled
            txtServiceBusName.Enabled = chkServiceBusEnabled.Checked;
            lblServiceBusName.Enabled = chkServiceBusEnabled.Checked;

            // Hide and disable the deprecated login in Entra mode, retaining its values for an existing
            // server that still needs SQL authentication.
            var sqlLoginInUse = !chkSqlEntraAuth.Checked;
            txtSQLServerUsername.Enabled = sqlLoginInUse;
            txtSQLServerPassword.Enabled = sqlLoginInUse;
            if (_sqlLoginFields != null) _sqlLoginFields.Visible = sqlLoginInUse;
            if (_sqlLoginNotice != null) _sqlLoginNotice.Visible = sqlLoginInUse;
            lblGUIAzureSQLUsername.Enabled = sqlLoginInUse;
            lblGUIAzureSQLPassword.Enabled = sqlLoginInUse;

            UpdateSqlDatabaseUsersLabel();
        }

        /// <summary>
        /// Summarises who will be granted database access, and says plainly what to do when that is nobody.
        /// </summary>
        /// <remarks>
        /// The "nobody" case is not cosmetic. On a server using Microsoft Entra ID authentication the only
        /// administrator is the installer's service principal, so with an empty list no person can query the
        /// database at all - and the obvious-looking fix in the Azure portal breaks the next upgrade.
        /// </remarks>
        private void UpdateSqlDatabaseUsersLabel()
        {
            var count = _sqlDatabaseUsers?.Count ?? 0;

            if (count == 0)
            {
                lblSqlDatabaseUsers.Text = chkSqlEntraAuth.Checked
                    ? "Nobody can query the database directly. Add people here - don't change the server's Entra admin."
                    : "Nobody is granted direct database access.";
                return;
            }

            var names = _sqlDatabaseUsers
                .Where(u => u != null)
                .Select(u => u.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var shown = string.Join(", ", names.Take(3));
            if (names.Count > 3) shown += $" (+{names.Count - 3} more)";

            lblSqlDatabaseUsers.Text = $"{count} database user(s): {shown}";
        }

        private void btnSqlDatabaseUsers_Click(object sender, EventArgs e)
        {
            using (var form = new SqlDatabaseUsersForm(_sqlDatabaseUsers))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;

                _sqlDatabaseUsers = form.DatabaseUsers;
                UpdateResponsiveUIControls();
            }
        }

        private void chkSqlEntraAuth_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtStorageAccount_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtServiceBusName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtSQLServerName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void chkServiceBusEnabled_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }
    }
}
