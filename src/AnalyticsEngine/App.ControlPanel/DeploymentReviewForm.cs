using App.ControlPanel.Engine;
using App.ControlPanel.Frames.InstallWizard;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public sealed class DeploymentReviewForm : Form
    {
        public DeploymentReviewForm(SolutionInstallConfig config, string[] workloads)
        {
            Text = "Review deployment";
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(820, 700);
            MinimumSize = new Size(700, 640);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                ColumnCount = 1,
                RowCount = 6
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = "Confirm the deployment scope",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 12)
            }, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "These are the settings you entered, not a live resource diff or a dry run. " +
                "The installer can create or update Azure resources and permissions, replace deployed content, " +
                "update selected SharePoint components and apply the selected SQL schema upgrades.",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 12)
            }, 0, 1);
            var facts = new ListView
            {
                Name = "reviewFacts",
                Dock = DockStyle.Fill,
                View = View.Details,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                FullRowSelect = true,
                ShowItemToolTips = true
            };
            facts.Columns.Add("Setting", 190);
            facts.Columns.Add("Selected scope", 540);
            foreach (var fact in ConfigurationReview.Facts(config, workloads.Length))
                facts.Items.Add(new ListViewItem(new[] { fact.Item1, fact.Item2 }));
            foreach (var workload in workloads) facts.Items.Add(new ListViewItem(new[] { "Import workload", workload }));
            facts.Items.Add(new ListViewItem(new[] { "Installer tenant", config.InstallerAccount.DirectoryId }));
            facts.Items.Add(new ListViewItem(new[] { "Runtime tenant", config.ActivityAccount.DirectoryId }));
            facts.Items.Add(new ListViewItem(new[] { "App Service plan", config.AppServicePlanName }));
            facts.Items.Add(new ListViewItem(new[] { "Key Vault", config.KeyVaultName }));
            facts.Items.Add(new ListViewItem(new[] { "Application Insights", config.AppInsightsName }));
            facts.Items.Add(new ListViewItem(new[] { "Log Analytics", config.AppInsightsWorkspaceName }));
            facts.Items.Add(new ListViewItem(new[] { "Automation account", config.AutomationAccountName }));
            facts.Items.Add(new ListViewItem(new[] { "Service Bus", config.ServiceBusEnabled ? config.ServiceBusName : "Disabled" }));
            if (config.SolutionConfig.ImportTaskSettings.WebTraffic || config.SolutionConfig.ImportTaskSettings.ActivityLog)
            {
                foreach (var site in config.SharePointConfig.TargetSites)
                    facts.Items.Add(new ListViewItem(new[] { "Configured SharePoint site", site }));
            }
            if (!config.DownloadLatestStable)
            {
                foreach (var package in config.LocalSourceOverride.Components)
                    facts.Items.Add(new ListViewItem(new[] { "Local package: " + package.Key, package.Value?.FileLocation ?? "Not entered" }));
            }
            foreach (ListViewItem fact in facts.Items)
                fact.ToolTipText = fact.Text + ": " + fact.SubItems[1].Text;
            facts.Resize += (sender, args) => facts.Columns[1].Width = Math.Max(240, facts.ClientSize.Width - 214);
            layout.Controls.Add(facts, 0, 2);
            var errors = config.ValidatInputAndGetErrors();
            var checks = new TextBox
            {
                Name = "reviewValidation",
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Height = errors.Count > 0 ? 110 : 50,
                ScrollBars = ScrollBars.Vertical,
                Text = errors.Count > 0 ? "Deployment is blocked until these local issues are fixed:" + Environment.NewLine + string.Join(Environment.NewLine, errors)
                    : "Local checks passed. This does not verify permissions, network access or existing resources. Run Test Configuration first."
            };
            layout.Controls.Add(checks, 0, 3);
            var acknowledge = new CheckBox
            {
                Name = "acknowledgeDeployment",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Text = "I have reviewed this scope and understand that deployment changes resources.",
                Margin = new Padding(0, 12, 0, 12)
            };
            layout.Controls.Add(acknowledge, 0, 4);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button
            {
                Name = "cancelReview",
                Text = "&Back to configuration",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                Padding = new Padding(8, 5, 8, 5)
            };
            var deploy = new Button
            {
                Name = "confirmDeployment",
                Text = "&Deploy now",
                DialogResult = DialogResult.OK,
                Enabled = false,
                AutoSize = true,
                Padding = new Padding(8, 5, 8, 5)
            };
            acknowledge.CheckedChanged += (sender, args) => deploy.Enabled = acknowledge.Checked && errors.Count == 0;
            actions.Controls.Add(cancel);
            actions.Controls.Add(deploy);
            layout.Controls.Add(actions, 0, 5);
            Controls.Add(layout);
            CancelButton = cancel;
            // Enter must never accept this destructive confirmation.
            AcceptButton = null;
        }
    }
}
