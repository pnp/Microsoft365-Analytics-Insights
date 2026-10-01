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

        public AzureStorageConfigControl()
        {
            InitializeComponent();
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

            // The SQL administrator login is deprecated: grey it out when Microsoft Entra ID authentication
            // is selected, rather than removing it, because an install pointed at an existing
            // SQL-authentication server still needs it.
            var sqlLoginInUse = !chkSqlEntraAuth.Checked;
            txtSQLServerUsername.Enabled = sqlLoginInUse;
            txtSQLServerPassword.Enabled = sqlLoginInUse;
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
