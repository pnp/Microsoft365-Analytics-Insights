using App.ControlPanel.Engine;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public sealed class ConfigurationOverviewControl : UserControl
    {
        private readonly ListView _facts = new ListView
        {
            Name = "configurationFacts",
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            HideSelection = false
        };
        private readonly ListBox _issues = new ListBox
        {
            Name = "configurationIssues",
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            AccessibleName = "Local configuration validation issues"
        };
        private readonly Label _validation = new Label { Name = "configurationValidation", AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label _validationIcon = new Label
        {
            Name = "configurationValidationIcon",
            Text = "\u2713",
            AutoSize = true,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.Green,
            AccessibleName = "Local configuration checks passed",
            Visible = false
        };

        public event EventHandler OpenRequested;
        public event EventHandler NewRequested;
        public event EventHandler ContinueRequested;

        public ConfigurationOverviewControl()
        {
            Font = new Font("Segoe UI", 9F);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = "Configuration overview",
                AutoSize = true,
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8)
            }, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "A snapshot of your entered settings, not a live Azure deployment plan. " +
                "Use Test Configuration before deploying.",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 16)
            }, 0, 1);
            _facts.Columns.Add("Setting", 200);
            _facts.Columns.Add("Entered value", 480);
            _facts.Resize += (sender, args) => _facts.Columns[1].Width = Math.Max(180, _facts.ClientSize.Width - _facts.Columns[0].Width - 24);
            layout.Controls.Add(_facts, 0, 2);
            var validationRow = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 12, 0, 8)
            };
            validationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            validationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _validation.Margin = new Padding(0, 6, 0, 0);
            validationRow.Controls.Add(_validationIcon, 0, 0);
            validationRow.Controls.Add(_validation, 1, 0);
            layout.Controls.Add(validationRow, 0, 3);
            layout.Controls.Add(_issues, 0, 4);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            AddAction(actions, "Open &configuration...", () => OpenRequested?.Invoke(this, EventArgs.Empty));
            AddAction(actions, "&New configuration", () => NewRequested?.Invoke(this, EventArgs.Empty));
            AddAction(actions, "&Edit workloads", () => ContinueRequested?.Invoke(this, EventArgs.Empty));
            layout.Controls.Add(actions, 0, 5);
            Controls.Add(layout);
        }

        private static void AddAction(FlowLayoutPanel actions, string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
            button.Click += (sender, args) => action();
            actions.Controls.Add(button);
        }

        public void RefreshConfiguration(SolutionInstallConfig config, int selectedWorkloads)
        {
            _facts.Items.Clear();
            foreach (var fact in ConfigurationReview.Facts(config, selectedWorkloads))
                _facts.Items.Add(new ListViewItem(new[] { fact.Item1, fact.Item2 }));
            var issues = config.ValidatInputAndGetErrors();
            _issues.Items.Clear();
            _issues.Items.AddRange(issues.ToArray());
            _validation.Text = issues.Count == 0 ? "Local checks passed - resources and permissions have not been verified."
                : $"{issues.Count} local validation message(s) - review before deployment";
            _validationIcon.Visible = issues.Count == 0;
        }
    }

    internal static class ConfigurationReview
    {
        internal static Tuple<string, string>[] Facts(SolutionInstallConfig config, int workloads)
        {
            string Value(string value) => string.IsNullOrWhiteSpace(value) ? "Not entered" : value;
            string Selected(bool value) => value ? "Selected" : "Not selected";
            return new[]
            {
                Tuple.Create("Environment", config.EnvironmentType.ToString()),
                Tuple.Create("Subscription", Value(config.Subscription?.DisplayName)),
                Tuple.Create("Subscription ID", Value(config.Subscription?.SubId)),
                Tuple.Create("Resource group", Value(config.ResourceGroupName)),
                Tuple.Create("Region", Value(config.AzureLocationName)),
                Tuple.Create("Workloads", $"{workloads} selected - edit Workloads to review the individual imports"),
                Tuple.Create("Web application", Value(config.AppServiceWebAppName)),
                Tuple.Create("Database", Value(config.SQLServerDatabaseName)),
                Tuple.Create("SQL server", Value(config.SQLServerName)),
                Tuple.Create("Storage account", Value(config.StorageAccountName)),
                Tuple.Create("Language service", config.CognitiveServicesEnabled ? Value(config.CognitiveServiceName) : "Disabled"),
                Tuple.Create("Prompt categorisation", config.FoundryPromptEnabled ? Value(config.FoundryPromptResourceName) : "Disabled"),
                Tuple.Create("Network", config.NetworkConfig?.Enabled == true
                    ? config.NetworkConfig.AllowPublicAccess ? "VNet enabled; public access allowed" : "Private network only - installer needs line-of-sight"
                    : "VNet integration not selected"),
                Tuple.Create("Packages", config.DownloadLatestStable ? "Latest stable release on GitHub" : "Local package files"),
                Tuple.Create("Deploy content", Selected(config.TasksConfig.InstallLatestSolutionContent)),
                Tuple.Create("SQL schema upgrade", Selected(config.TasksConfig.UpgradeSchema)),
                Tuple.Create("SharePoint sites", $"{config.SharePointConfig.TargetSites.Count} configured"),
                Tuple.Create("Usage reporting", config.AllowTelemetry ? "Allowed" : "Disabled")
            };
        }

    }
}
