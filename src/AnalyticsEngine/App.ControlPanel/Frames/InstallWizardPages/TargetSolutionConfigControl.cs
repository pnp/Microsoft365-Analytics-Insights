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
                // Solution workloads configured in TargetSolutionConfig.ImportTaskSettings
                return new TargetSolutionConfig()
                {
                    ImportTaskSettings = new ImportTaskSettings()
                    {
                        ActivityLog = chkAuditLog.Checked,
                        GraphTeams = chkTeams.Checked,
                        GraphUsageReports = chkUsageReports.Checked,
                        GraphUsersMetadata = chkUserMetadata.Checked,
                        Calls = chkCalls.Checked,
                        WebTraffic = chkWeb.Checked,
                        Copilot = chkCopilot.Checked,
                        SentEmails = chkSentEmails.Checked,
                        ImportPowerPlatform = chkPowerPlatform.Checked,
                        GraphCopilotUsageReports = chkCopilotUsageReports.Checked,
                        CopilotInteractionHistory = chkCopilotInteractionHistory.Checked
                    }
                };
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
            chkWeb.Checked = value.ImportTaskSettings.WebTraffic;
            chkCopilot.Checked = value.ImportTaskSettings.Copilot;
            chkSentEmails.Checked = value.ImportTaskSettings.SentEmails;
            chkPowerPlatform.Checked = value.ImportTaskSettings.ImportPowerPlatform;
            chkCopilotUsageReports.Checked = value.ImportTaskSettings.GraphCopilotUsageReports;
            chkCopilotInteractionHistory.Checked = value.ImportTaskSettings.CopilotInteractionHistory;
        }


        private void ImportJobSettingsSelection_Load(object sender, System.EventArgs e)
        {
            grpProductCfgInsights.Dock = DockStyle.Fill;
        }

        private void chkTeams_CheckedChanged(object sender, System.EventArgs e)
        {
            SolutionSelectionUIChange();
        }
        private void chkCalls_CheckedChanged(object sender, System.EventArgs e)
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
