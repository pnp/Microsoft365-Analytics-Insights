using Common.Entities.Installer;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace App.ControlPanel
{
    /// <summary>
    /// Edits the list of Microsoft Entra principals the installer grants their own database access to.
    /// </summary>
    /// <remarks>
    /// Exists so giving someone access to the data does not require reassigning the SQL Server's Microsoft
    /// Entra administrator. Azure permits exactly one administrator per server and it is the installer's
    /// service principal, so replacing it locks the installer out of future schema upgrades - the failure
    /// this dialog is designed to stop people walking into. See issue #117.
    /// </remarks>
    public partial class SqlDatabaseUsersForm : Form
    {
        private readonly BindingList<SqlDatabaseUser> _users;

        public SqlDatabaseUsersForm(IEnumerable<SqlDatabaseUser> users)
        {
            InitializeComponent();

            // Copied, not referenced: Cancel has to leave the caller's list untouched.
            _users = new BindingList<SqlDatabaseUser>((users ?? Enumerable.Empty<SqlDatabaseUser>())
                .Where(u => u != null)
                .Select(u => new SqlDatabaseUser
                {
                    Login = u.Login,
                    ObjectId = u.ObjectId,
                    PrincipalType = string.IsNullOrWhiteSpace(u.PrincipalType) ? SqlDatabaseUser.PrincipalTypeUser : u.PrincipalType,
                })
                .ToList());

            colPrincipalType.Items.Add(SqlDatabaseUser.PrincipalTypeUser);
            colPrincipalType.Items.Add(SqlDatabaseUser.PrincipalTypeGroup);

            gridUsers.DataSource = _users;

            // A new row starts with no principal type, which the combo column rejects with a
            // DataError dialog the moment the cell is painted. Default it instead.
            gridUsers.DefaultValuesNeeded += (s, e) =>
                e.Row.Cells[colPrincipalType.Index].Value = SqlDatabaseUser.PrincipalTypeUser;

            // Never let a malformed cell throw a modal exception dialog out of the grid.
            gridUsers.DataError += (s, e) => e.ThrowException = false;
        }

        /// <summary>The edited list. Only meaningful once the dialog returns <see cref="DialogResult.OK"/>.</summary>
        public List<SqlDatabaseUser> DatabaseUsers { get; private set; } = new List<SqlDatabaseUser>();

        private void btnOk_Click(object sender, EventArgs e)
        {
            // Commit the cell being edited, or the last thing typed is silently dropped.
            gridUsers.EndEdit();
            var currency = gridUsers.BindingContext[_users] as CurrencyManager;
            currency?.EndCurrentEdit();

            var cleaned = _users
                .Where(u => u != null && (!string.IsNullOrWhiteSpace(u.Login) || !string.IsNullOrWhiteSpace(u.ObjectId)))
                .Select(u => new SqlDatabaseUser
                {
                    Login = (u.Login ?? string.Empty).Trim(),
                    ObjectId = (u.ObjectId ?? string.Empty).Trim(),
                    PrincipalType = string.IsNullOrWhiteSpace(u.PrincipalType) ? SqlDatabaseUser.PrincipalTypeUser : u.PrincipalType.Trim(),
                })
                .ToList();

            var problems = cleaned
                .Select(u => u.GetValidationError())
                .Where(err => err != null)
                .ToList();

            if (problems.Count > 0)
            {
                // Refuse to save something the install would only reject later, when the operator has
                // stopped watching.
                MessageBox.Show(this,
                    "These database users cannot be used:" + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, problems),
                    "SQL database users", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DatabaseUsers = cleaned;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
