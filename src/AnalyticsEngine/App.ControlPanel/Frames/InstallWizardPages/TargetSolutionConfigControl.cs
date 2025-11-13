using Common.Entities;
using Common.Entities.Installer;
using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace App.ControlPanel.Controls
{
    public partial class TargetSolutionConfigControl : UserControl
    {
        public TargetSolutionConfigControl()
        {
            InitializeComponent();
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public TargetSolutionConfig Config
        {
            get
            {
                // Solution specific workloads configured in TargetSolutionConfig.ImportTaskSettings
                var solConfig = new TargetSolutionConfig()
                {
                    ImportTaskSettings = new ImportTaskSettings()
                    {
                        ActivityLog = chkAuditLog.Checked,
                        GraphTeams = chkTeams.Checked,
                        GraphUsageReports = chkUsageReports.Checked,
                        GraphUserApps = false,  // Deprecated
                        GraphUsersMetadata = chkUserMetadata.Checked,
                        Calls = chkCalls.Checked,
                        WebTraffic = true // Allow web traffic always as Insights needs it
                    },
                    SolutionTargeted = SolutionImportType.CustomOrInsights
                };

                return solConfig;
            }
            set
            {
                SetGui(value);
            }
        }

        public event EventHandler SolutionSelectionChange;

        private void SetGui(TargetSolutionConfig value)
        {
            chkAuditLog.Checked = value.ImportTaskSettings.ActivityLog;
            chkTeams.Checked = value.ImportTaskSettings.GraphTeams;
            chkUsageReports.Checked = value.ImportTaskSettings.GraphUsageReports;
            chkUserMetadata.Checked = value.ImportTaskSettings.GraphUsersMetadata;
            chkCalls.Checked = value.ImportTaskSettings.Calls;
            rdbInsights.Checked = value.SolutionTargeted == SolutionImportType.CustomOrInsights;

            grpProductCfgInsights.Visible = value.SolutionTargeted == SolutionImportType.CustomOrInsights;

        }


        private void rdbSolutionOps_CheckedChanged(object sender, System.EventArgs e)
        {
            SetGui(Config);     // New prop will reflect GUI change
        }

        private void ImportJobSettingsSelection_Load(object sender, System.EventArgs e)
        {
            grpProductCfgInsights.Dock = DockStyle.Fill;
        }


        class SolutionLingo
        {
            public string Code { get; set; }
            public string Name { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }

        private void chkTeams_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }
        private void chkCalls_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }

        private void chkUserApps_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }

        private void chkUsageReports_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }

        private void chkUserMetadata_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }

        private void chkWeb_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }

        private void chkAuditLog_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }

        private void SolutionSelectionUIChange()
        {
            SolutionSelectionChange?.Invoke(this, EventArgs.Empty);
        }

    }
}
