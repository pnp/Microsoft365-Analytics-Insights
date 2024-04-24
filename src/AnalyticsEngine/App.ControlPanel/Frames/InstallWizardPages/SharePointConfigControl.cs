using App.ControlPanel.Engine.Entities;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class SharePointConfigControl : UserControl
    {
        public SharePointConfigControl()
        {
            InitializeComponent();
        }

        public SharePointInstallConfig SharePointInstallConfig
        {
            get
            {
                var sites = new List<string>(txtSharePointURLs.Text.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries));

                var spConfig = new SharePointInstallConfig(
                    "SPOInsights",
                    "AITracker.js",
                    sites,
                    txtSharePointAppCatalog.Text
                );

                return spConfig;
            }
            set
            {
                if (value != null)
                {
                    txtSharePointAppCatalog.Text = value.AppCatalogueURL;

                    var targetSitesText = string.Empty;
                    foreach (var site in value.TargetSites)
                    {
                        targetSitesText += site + Environment.NewLine;
                    }
                    targetSitesText = targetSitesText.TrimEnd(Environment.NewLine.ToCharArray());
                    txtSharePointURLs.Text = targetSitesText;
                }
                else
                {
                    txtSharePointURLs.Text = String.Empty;
                }
            }
        }

        public event EventHandler UninstallClicked;
        private void btnUninstall_Click(object sender, EventArgs e)
        {
            UninstallClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
