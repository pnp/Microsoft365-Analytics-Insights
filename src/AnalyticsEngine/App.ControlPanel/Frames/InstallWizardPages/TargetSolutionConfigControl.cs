using Common.Entities;
using Common.Entities.Installer;
using System;
using System.ComponentModel;
using System.Drawing;
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
                        CopilotInteractionHistory = chkCopilotInteractionHistory.Checked,
                        CopilotStudioCredits = chkCopilotStudioCredits.Checked,
                        AzureCostManagement = chkAzureCostManagement.Checked,
                        ImportDlp = chkDlp.Checked
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
            chkDlp.Checked = value.ImportTaskSettings.ImportDlp;
            chkCopilotStudioCredits.Checked = value.ImportTaskSettings.CopilotStudioCredits;
            chkAzureCostManagement.Checked = value.ImportTaskSettings.AzureCostManagement;
        }


        private void ImportJobSettingsSelection_Load(object sender, System.EventArgs e)
        {
            grpProductCfgInsights.Dock = DockStyle.Fill;
            PositionHelpLinks();
        }

        /// <summary>
        /// Parks each "needs setup" link just after the caption of the checkbox it belongs to.
        /// The checkboxes are AutoSize, so their realised width depends on the font and DPI the
        /// installer happens to be running at - a designer-time X would only be right on one machine.
        /// </summary>
        private void PositionHelpLinks()
        {
            PlaceHelpLink(lnkInteractionHistoryHelp, chkCopilotInteractionHistory);
            PlaceHelpLink(lnkDlpHelp, chkDlp);
            PlaceHelpLink(lnkStudioCreditsHelp, chkCopilotStudioCredits);
            PlaceHelpLink(lnkAzureCostsHelp, chkAzureCostManagement);
        }

        private static void PlaceHelpLink(LinkLabel link, CheckBox owner)
        {
            link.Location = new Point(owner.Right + 4, owner.Top + 1);
        }

        private void HelpLink_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var url = (sender as Control)?.Tag as string;
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                // Opening a browser is a convenience; failing to must never take the installer down.
                MessageBox.Show(this, $"Couldn't open {url}\r\n\r\n{ex.Message}", "Open documentation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
