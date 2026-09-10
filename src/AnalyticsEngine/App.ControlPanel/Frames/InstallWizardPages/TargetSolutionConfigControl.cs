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
            grpProductCfgInsights.SizeChanged += (s, args) => LayoutCopilotColumn();
            lblCopilotDesc.SizeChanged += (s, args) => LayoutCopilotColumn();
            FontChanged += (s, args) => LayoutCopilotColumn();
            LayoutCopilotColumn();
        }

        /// <summary>
        /// Sizes the grey interaction-history note to the text it actually holds, then slides the
        /// toggles underneath it to sit below.
        /// </summary>
        /// <remarks>
        /// The note wraps, so how tall it needs to be depends on the column width and the font - neither
        /// of which is known at design time. A fixed height either wastes space or, if the wording is
        /// ever lengthened, silently paints over the DLP row (which is the defect this column already
        /// had once). Measuring instead means the wording can change without anyone re-checking the
        /// geometry. Moving the checkboxes drags their "needs setup" links along, because those are
        /// bound to their owner's LocationChanged.
        /// </remarks>
        private void LayoutCopilotColumn()
        {
            if (_layingOutCopilotColumn || lblCopilotDesc.Width <= 0) return;

            _layingOutCopilotColumn = true;
            try
            {
                var needed = TextRenderer.MeasureText(lblCopilotDesc.Text, lblCopilotDesc.Font,
                    new Size(lblCopilotDesc.Width, int.MaxValue), TextFormatFlags.WordBreak);
                lblCopilotDesc.Height = needed.Height;

                // Half a row of clear air, so the gap scales with the font like everything else.
                var gap = Math.Max(6, chkDlp.Height / 2);
                var delta = (lblCopilotDesc.Bottom + gap) - chkDlp.Top;
                if (delta == 0) return;

                foreach (var below in new Control[] { chkDlp, chkCopilotStudioCredits, chkAzureCostManagement })
                {
                    below.Top += delta;
                }
            }
            finally
            {
                _layingOutCopilotColumn = false;
            }
        }

        private bool _layingOutCopilotColumn;

        /// <summary>
        /// Keeps each "needs setup" link parked just after the caption of the checkbox it belongs to.
        /// </summary>
        /// <remarks>
        /// The checkboxes are AutoSize, so their realised width depends on the font and DPI in force -
        /// and that can change well after load, when the installer is dragged to a monitor with different
        /// scaling and WinForms re-runs its scaling pass. Positioning the links once would leave them
        /// detached from, or overlapping, their captions, so they follow the checkbox instead.
        /// </remarks>
        private void BindHelpLinks()
        {
            BindHelpLink(lnkInteractionHistoryHelp, chkCopilotInteractionHistory);
            BindHelpLink(lnkDlpHelp, chkDlp);
            BindHelpLink(lnkStudioCreditsHelp, chkCopilotStudioCredits);
            BindHelpLink(lnkAzureCostsHelp, chkAzureCostManagement);
        }

        private static void BindHelpLink(LinkLabel link, CheckBox owner)
        {
            EventHandler place = (s, e) => link.Location = new Point(owner.Right + 4, owner.Top + 1);
            owner.SizeChanged += place;
            owner.LocationChanged += place;
            place(owner, EventArgs.Empty);
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
