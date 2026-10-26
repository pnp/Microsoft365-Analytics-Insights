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
            BindHelpLinks();
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
                        Agent365PackageCatalog = chkAgent365Catalog.Checked,
                        CopilotCostManagement = chkCopilotCostManagement.Checked,
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
            _settingGui = true;
            try
            {
                SetCheckBoxes(value);
            }
            finally
            {
                _settingGui = false;
            }
        }

        // True while a loaded config is being applied, so the preview confirmation only interrupts a person
        // ticking the box - not opening a config file that already has it on.
        private bool _settingGui;

        private void SetCheckBoxes(TargetSolutionConfig value)
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
            chkAgent365Catalog.Checked = value.ImportTaskSettings.Agent365PackageCatalog;
            chkCopilotCostManagement.Checked = value.ImportTaskSettings.CopilotCostManagement;
        }


        private void ImportJobSettingsSelection_Load(object sender, System.EventArgs e)
        {
            grpProductCfgInsights.Dock = DockStyle.Top;
        }

        private void BindHelpLinks()
        {
            BindHelpLink(lnkInteractionHistoryHelp, chkCopilotInteractionHistory);
            BindHelpLink(lnkDlpHelp, chkDlp);
            BindHelpLink(lnkStudioCreditsHelp, chkCopilotStudioCredits);
            BindHelpLink(lnkAzureCostsHelp, chkAzureCostManagement);
            BindHelpLink(lnkAgent365CatalogHelp, chkAgent365Catalog);
            BindHelpLink(lnkCopilotCostHelp, chkCopilotCostManagement);
        }

        private static void BindHelpLink(LinkLabel link, CheckBox owner)
        {
            link.AccessibleName = owner.Text + " setup documentation";
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

        internal const string CopilotCostPreviewConfirmation =
            "Copilot credit history uses a separately connected delegated reader account.\r\n\r\n"
            + "- A portal administrator with 'Portal.Administration' connects the reader from "
            + "Administration > Service configuration in the web app.\r\n"
            + "- The portal explains the Graph delegated permissions, admin consent and reader role needed.\r\n"
            + "- It needs the solution's Azure storage account (Table storage) and will not run without it.\r\n\r\n"
            + "Turn on Copilot credit history?";

        private void chkCopilotCostManagement_CheckedChanged(object sender, EventArgs e)
        {
            if (_settingGui || !chkCopilotCostManagement.Checked) return;

            var answer = MessageBox.Show(this, CopilotCostPreviewConfirmation, "Copilot credit history (preview)",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                chkCopilotCostManagement.Checked = false;
            }
        }

        private void SolutionSelectionUIChange()
        {
            SolutionSelectionChange?.Invoke(this, EventArgs.Empty);
        }

    }
}
