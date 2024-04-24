using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Data.Common;
using System.Data.Entity.Infrastructure;
using System.Drawing;
using App.ControlPanel.Engine;

namespace App.ControlPanel.Frames
{
    public partial class ManageDataControl : UserControl, ISolutionConfigurableComponent
    {
        public ManageDataControl()
        {
            InitializeComponent();
        }

        private void ManageDataControl_Load(object sender, EventArgs e)
        {
            SetConnected(false);
            lblUserStatus.Text = "Waiting to search.";
            lblImportStatus.Text = lblUserStatus.Text;

            lstUsers.Items.Clear();
            lstImportLogs.Items.Clear();
        }

        void SetConnected(bool connected)
        {
            if (connected)
            {
                tabs.TabPages.Add(tabUsers);
                tabs.TabPages.Add(tabImportLog);

                btnConnect.Enabled = false;

                string sqlServer = SPOInsights.Common.DataUtils.StringUtils.FindValueForProp(txtConnectionString.Text, "data source");
                string sqlDB = SPOInsights.Common.DataUtils.StringUtils.FindValueForProp(txtConnectionString.Text, "initial catalog");

                lblConnectionStatus.Text = $"Connected to '{sqlDB}' on '{sqlServer}'. You can now use the other tabs to find things.";
                lblConnectionStatus.ForeColor = Color.DarkGreen;
                txtConnectionString.ReadOnly = true;
            }
            else
            {
                btnConnect.Enabled = true;
                tabs.TabPages.Remove(tabUsers);
                tabs.TabPages.Remove(tabImportLog);
                lblConnectionStatus.Text = "Waiting to connect.";
            }
        }


        private async void btnConnect_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            btnConnect.Enabled = false;

            List<string> errs = await Connect(txtConnectionString.Text);

            this.Cursor = Cursors.Default;
            if (errs.Count > 0)
            {
                string msg = string.Empty;
                foreach (var err in errs)
                {
                    msg += Environment.NewLine + "-" + err;
                }
                MessageBox.Show("Error connecting to database: " + msg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetConnected(false);
            }
            else
            {
                SetConnected(true);
            }

        }

        SPOInsightsEntitiesContext GetDB()
        {
            var sqlConnFact = new SqlConnectionFactory(txtConnectionString.Text);
            DbConnection con = sqlConnFact.CreateConnection(txtConnectionString.Text);
            return new SPOInsightsEntitiesContext(con);
        }

        async Task<List<string>> Connect(string sqlConn)
        {
            List<string> errs = new List<string>();

            await Task.Run(() =>
            {
                try
                {
                    using (SPOInsightsEntitiesContext db = GetDB())
                    {
                        int logsCount = db.import_log.Count();
                    }
                }
                catch (InvalidOperationException ex)
                {
                    errs.Add(ex.Message);
                }
                catch (System.Data.SqlClient.SqlException ex)
                {
                    errs.Add(ex.Message);
                }
            });

            return errs;
        }

        private async void btnSearchUser_Click(object sender, EventArgs e)
        {
            if (txtUserSearch.Text.Trim().Length == 0)
            {
                MessageBox.Show("Enter search value for user", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            lstUsers.Items.Clear();
            SetUserSearchingStatus(true);

            // Search users table
            Task userTask = Task.Run(() =>
            {
                using (SPOInsightsEntitiesContext db = GetDB())
                {
                    return db.users.Where(u => u.user_name.Contains(txtUserSearch.Text)).ToList();
                }
            }).ContinueWith(t =>
            {

                // Did loader error?
                if (t.IsFaulted) { return; }

                // Display results
                lstUsers.Invoke((Action)(() =>
                {
                    foreach (var item in t.Result)
                    {
                        lstUsers.Items.Add(item.user_name);
                    }

                    lblUsersFound.Text = $"Users found: {t.Result.Count}";
                }));
            });

            // Wait for all to finish
            await Task.WhenAll(userTask);
            SetUserSearchingStatus(false);
        }

        private void SetUserSearchingStatus(bool searching)
        {
            lstUsers.Enabled = !searching;
            btnSearchUser.Enabled = !searching;
            txtUserSearch.ReadOnly = searching;
            if (!searching)
            {
                this.Cursor = Cursors.Default;
                lblUserStatus.Text = $"Searching complete.";
            }
            else
            {
                lblUserStatus.Text = $"Searching for all users containing '{txtUserSearch.Text}'";
                lblUsersFound.Text = $"Users found: searching...";
                this.Cursor = Cursors.WaitCursor;
            }
        }

        private void SetLogSearchingStatus(bool searching)
        {
            lstImportLogs.Enabled = !searching;
            btnSearchLogs.Enabled = !searching;
            txtImportSearch.ReadOnly = searching;
            txtDaysBack.ReadOnly = searching;
            if (!searching)
            {
                this.Cursor = Cursors.Default;
                lblImportStatus.Text = $"Searching complete.";
            }
            else
            {
                lblImportStatus.Text = $"Searching for all import-logs containing '{txtImportSearch.Text}'";
                lblImportLogsFound.Text = $"Import logs found: searching...";
                this.Cursor = Cursors.WaitCursor;
            }
        }

        class ImportLogLVI : ListViewItem
        {
            public ImportLogLVI(import_log log)
            {
                this.Log = log;
                this.Text = log.time_stamp.ToString();
                this.SubItems.Add(log.import_message);
            }
            public import_log Log { get; set; }
        }

        private void lstImportLogs_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (lstImportLogs.SelectedItems.Count == 1 && lstImportLogs.SelectedItems[0] is ImportLogLVI)
            {
                ImportLogLVI item = lstImportLogs.SelectedItems[0] as ImportLogLVI;
                ImportLogForm f = new ImportLogForm();

                f.SetDisplay(item.Log, txtImportSearch.Text);
                f.Show();
            }
        }

        private async void btnImportLogSearch_Click(object sender, EventArgs e)
        {
            if (txtImportSearch.Text.Trim().Length == 0)
            {
                MessageBox.Show("Enter search value for log search", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            lstImportLogs.Items.Clear();
            SetLogSearchingStatus(true);

            // Search import-log
            await Task.Run(() =>
            {
                using (SPOInsightsEntitiesContext db = GetDB())
                {
                    double daysBackMinused = Convert.ToDouble(txtDaysBack.Value * -1);
                    DateTime xDaysSinceNow = DateTime.Now.AddDays(daysBackMinused);

                    return db.import_log.Where(l => l.time_stamp > xDaysSinceNow && l.contents.Contains(txtImportSearch.Text)).ToList();
                }
            }).ContinueWith(t => {
                if (t.IsFaulted) { return; }

                lstImportLogs.Invoke((Action)(() =>
                {
                    foreach (var item in t.Result)
                    {
                        lstImportLogs.Items.Add(new ImportLogLVI(item));
                    }

                    lblImportLogsFound.Text = $"Import logs found: {t.Result.Count}. Double-click to open an import-log.";
                }));
            });

            SetLogSearchingStatus(false);
        }

        public void ConfigureUI(SolutionInstallConfig config)
        {
            throw new NotImplementedException();
        }

        public SolutionInstallConfig GetConfigurationState()
        {
            throw new NotImplementedException();
        }
    }

}
