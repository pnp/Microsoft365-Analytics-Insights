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
                        GraphUserApps = chkUserApps.Checked,
                        GraphUsersMetadata = chkUserMetadata.Checked,
                        Calls = chkCalls.Checked,
                        WebTraffic = chkWeb.Checked
                    },
                    SolutionTargeted = rdbAdoptify.Checked ? SolutionImportType.Adoptify : SolutionImportType.CustomOrInsights
                };

                solConfig.Adoptify.ExistingSiteUrl = txtAdoptifySiteUrl.Text;
                solConfig.Adoptify.CreateDefaultData = chkInstallAdoptifyDefaultContent.Checked;
                solConfig.Adoptify.ProvisionSchema = chkInstallAdoptifySchema.Checked;
                if (cmbLanguage.SelectedItem is SolutionLingo)
                {
                    solConfig.SolutionLanguageCode = ((SolutionLingo)cmbLanguage.SelectedItem).Code;
                }
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
            chkUserApps.Checked = value.ImportTaskSettings.GraphUserApps;
            chkUserMetadata.Checked = value.ImportTaskSettings.GraphUsersMetadata;
            chkCalls.Checked = value.ImportTaskSettings.Calls;
            chkWeb.Checked = value.ImportTaskSettings.WebTraffic;
            rdbAdoptify.Checked = value.SolutionTargeted == SolutionImportType.Adoptify;
            rdbInsights.Checked = value.SolutionTargeted == SolutionImportType.CustomOrInsights;

            grpProductCfgAdoptify.Visible = value.SolutionTargeted == SolutionImportType.Adoptify;
            grpProductCfgInsights.Visible = value.SolutionTargeted == SolutionImportType.CustomOrInsights;

            foreach (var item in cmbLanguage.Items)
            {
                if (item is SolutionLingo && ((SolutionLingo)item).Code == value.SolutionLanguageCode)
                {
                    cmbLanguage.SelectedItem = item;
                    break;
                }
            }
            txtAdoptifySiteUrl.Text = value.Adoptify.ExistingSiteUrl;
            chkInstallAdoptifyDefaultContent.Checked = value.Adoptify.CreateDefaultData;
            chkInstallAdoptifySchema.Checked = value.Adoptify.ProvisionSchema;
        }


        private void rdbSolutionOps_CheckedChanged(object sender, System.EventArgs e)
        {
            SetGui(Config);     // New prop will reflect GUI change
        }

        private void ImportJobSettingsSelection_Load(object sender, System.EventArgs e)
        {
            grpProductCfgAdoptify.Dock = DockStyle.Fill;
            grpProductCfgInsights.Dock = DockStyle.Fill;

            cmbLanguage.Items.Add(new SolutionLingo { Name = "English", Code = TargetSolutionConfig.LANG_ENGLISH });
            cmbLanguage.Items.Add(new SolutionLingo { Name = "Español", Code = TargetSolutionConfig.LANG_ESPAÑOL });
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
