using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Prerequisites;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public sealed class DeploymentPrerequisitesControl : UserControl
    {
        private readonly TableLayoutPanel _layout;
        private readonly Label _title = new Label
        {
            Name = "prerequisiteTitle", Text = "Deployment prerequisites", AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        private readonly Label _summary = new Label { Name = "prerequisiteSummary", AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label _selection = new Label { Name = "prerequisiteWorkloads", AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label _status = new Label { Name = "prerequisiteStatus", AutoSize = true, Dock = DockStyle.Fill };
        private readonly Label _categoryHelp = new Label
        {
            Name = "prerequisiteCategoryHelp", AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        private readonly ComboBox _category = new ComboBox
        {
            Name = "prerequisiteCategory", DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 310, AccessibleName = "Prerequisite view or category"
        };
        private readonly DataGridView _requirements = new DataGridView
        {
            Name = "prerequisiteRequirements", Dock = DockStyle.Fill, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Requirements for the selected workloads", AutoGenerateColumns = false
        };
        private readonly TextBox _details = new TextBox
        {
            Name = "prerequisiteDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window,
            AccessibleName = "Selected requirement, scope, setup responsibility and checks"
        };
        private readonly LinkLabel _reference = new LinkLabel
        {
            Name = "prerequisiteReference", AutoSize = true, Text = "Open &reference",
            AccessibleDescription = "Opens the reference for the selected requirement in your browser."
        };
        private DeploymentPrerequisiteReport _report;
        private bool _refreshingCategories;
        private bool _viewChosen;

        private sealed class CategoryChoice
        {
            public string Key { get; }
            public PrerequisiteCategory? Value { get; }
            private readonly string _label;
            public CategoryChoice(string key, string label, PrerequisiteCategory? value = null)
            { Key = key; Value = value; _label = label; }
            public override string ToString() => _label;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<SolutionInstallConfig> ConfigurationProvider { get; set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DeploymentPrerequisiteReport Report => _report;

        public DeploymentPrerequisitesControl()
        {
            Name = "deploymentPrerequisites";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            AutoScroll = true;
            _layout = new TableLayoutPanel
            {
                Name = "prerequisiteLayout", Dock = DockStyle.Top, Padding = new Padding(16),
                ColumnCount = 1, RowCount = 9, MinimumSize = new Size(0, Font.Height * 38)
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 6; row++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _title.Font = new Font(Font.FontFamily, Font.Size * 2, FontStyle.Bold);
            _layout.Controls.Add(_title, 0, 0);
            _summary.Font = new Font(Font, FontStyle.Bold);
            _summary.Margin = new Padding(0, 0, 0, 6);
            _layout.Controls.Add(_summary, 0, 1);
            _selection.Margin = new Padding(0, 0, 0, 8);
            _layout.Controls.Add(_selection, 0, 2);
            _layout.Controls.Add(new Label
            {
                Name = "prerequisiteNotice", Text = DeploymentPrerequisiteReport.ScopeNotice,
                AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10)
            }, 0, 3);
            var toolbar = new FlowLayoutPanel
            {
                Name = "prerequisiteActions", Dock = DockStyle.Fill, AutoSize = true,
                WrapContents = true, Margin = new Padding(0, 0, 0, 8)
            };
            toolbar.Controls.Add(_category);
            var refresh = new Button { Name = "refreshPrerequisites", Text = "&Refresh", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
            refresh.Click += Refresh_Click;
            toolbar.Controls.Add(refresh);
            var export = new Button
            {
                Name = "exportPrerequisites", Text = "&Export Markdown...", AutoSize = true,
                Padding = new Padding(6, 3, 6, 3), AccessibleDescription = "Save the full prerequisites report, regardless of the category filter. No credentials or entered resource values are included."
            };
            export.Click += Export_Click;
            toolbar.Controls.Add(export);
            _layout.Controls.Add(toolbar, 0, 4);
            _layout.Controls.Add(_categoryHelp, 0, 5);
            _requirements.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _requirements.DefaultCellStyle.Padding = new Padding(5);
            _requirements.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _requirements.Columns.Add(Column("Requirement", "Requirement", 45));
            _requirements.Columns.Add(Column("Identity", "Identity / type", 30));
            _requirements.Columns.Add(Column("Workloads", "Needed for", 25));
            _requirements.Columns.Add(Column("Why", "Why needed", 35));
            _requirements.CurrentCellChanged += Requirements_CurrentCellChanged;
            _layout.Controls.Add(_requirements, 0, 6);
            var detailLayout = new TableLayoutPanel
            {
                Name = "prerequisiteDetailLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = new Padding(0, 8, 0, 0)
            };
            detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailLayout.Controls.Add(_details, 0, 0);
            detailLayout.Controls.Add(_reference, 0, 1);
            _reference.LinkClicked += Reference_LinkClicked;
            _layout.Controls.Add(detailLayout, 0, 7);
            _status.Text = DeploymentPrerequisiteReport.PrivacyNotice;
            _status.Margin = new Padding(0, 8, 0, 0);
            _layout.Controls.Add(_status, 0, 8);
            Controls.Add(_layout);
            _category.SelectedIndexChanged += Category_SelectedIndexChanged;
            RefreshConfiguration(new SolutionInstallConfig());
        }

        private static DataGridViewTextBoxColumn Column(string name, string title, int weight) =>
            new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = 60,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_layout != null)
            {
                _title.Font = new Font(Font.FontFamily, Font.Size * 2, FontStyle.Bold);
                _summary.Font = new Font(Font, FontStyle.Bold);
                _layout.MinimumSize = new Size(0, Font.Height * 38);
                UpdateLayoutHeight();
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateLayoutHeight();
        }

        private void UpdateLayoutHeight()
        {
            if (_layout == null) return;
            if (_category.Items.Count > 0)
                _category.Width = _category.Items.Cast<object>().Max(item => TextRenderer.MeasureText(item.ToString(), _category.Font).Width)
                    + SystemInformation.VerticalScrollBarWidth + 24;
            var width = Math.Max(100, ClientSize.Width - _layout.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            var headerAndFooter = _layout.Padding.Vertical;
            foreach (Control control in _layout.Controls)
            {
                var row = _layout.GetRow(control);
                if (row == 6 || row == 7) continue;
                headerAndFooter += control.GetPreferredSize(new Size(Math.Max(100, width - control.Margin.Horizontal), 0)).Height
                    + control.Margin.Vertical;
            }
            // Grow the page instead of letting wrapped selections squeeze either reading viewport away.
            _layout.Height = Math.Max(ClientSize.Height, headerAndFooter + Font.Height * 22);
        }

        public void RefreshConfiguration(SolutionInstallConfig config)
        {
            _report = DeploymentPrerequisites.Build(config);
            var permissions = _report.Requirements.Count(r => r.Category == PrerequisiteCategory.ApplicationPermissions);
            var resources = _report.Requirements.Count(r => r.Category == PrerequisiteCategory.AzureResources);
            _summary.Text = $"Selected imports: {_report.Workloads.Count}  |  Application permissions: {permissions}  |  Azure resource types: {resources}";
            _selection.Text = _report.Workloads.Count == 0 ? "No workloads selected. Choose workloads to build an import-specific request."
                : "Selected: " + string.Join(", ", _report.Workloads);
            _selection.Text += _report.OptionalComponents.Count == 0 ? "\r\nNo Text Analytics, Service Bus or Azure OpenAI add-on is selected."
                : "\r\nSelected add-on services: " + string.Join("; ", _report.OptionalComponents.Select(c =>
                    c.Name + (c.CanRemoveWithoutChangingImports ? "" : " (required for Calls)")))
                + ". Review Selected optional resources to see what could be left out.";
            if (config.SolutionConfig?.ImportTaskSettings?.Calls == true && !config.ServiceBusEnabled)
                _selection.Text += "\r\nTeams calls import requires Service Bus, but Service Bus provisioning is currently off.";
            _status.Text = DeploymentPrerequisiteReport.PrivacyNotice;
            RefreshCategories();
            PopulateRequirements();
            UpdateLayoutHeight();
        }

        private void RefreshCategories()
        {
            var previous = _viewChosen ? (_category.SelectedItem as CategoryChoice)?.Key : null;
            _refreshingCategories = true;
            _category.BeginUpdate();
            try
            {
                _category.Items.Clear();
                if (_report.WorkloadPermissions.Count > 0)
                    _category.Items.Add(new CategoryChoice("workloads", "Runtime API by workload"));
                if (_report.OptionalComponents.Count > 0)
                    _category.Items.Add(new CategoryChoice("optional-resources", "Selected optional resources"));
                _category.Items.Add(new CategoryChoice("all", "All requirements"));
                foreach (var category in _report.Requirements.Select(r => r.Category).Distinct().OrderBy(c => c))
                    _category.Items.Add(new CategoryChoice("category-" + category, FilterTitle(category), category));
                _category.SelectedItem = _category.Items.Cast<CategoryChoice>().FirstOrDefault(c => c.Key == previous)
                    ?? _category.Items[0];
            }
            finally
            {
                _category.EndUpdate();
                _refreshingCategories = false;
            }
        }

        private static string FilterTitle(PrerequisiteCategory category)
        {
            switch (category)
            {
                case PrerequisiteCategory.ApplicationPermissions: return "Application permissions";
                case PrerequisiteCategory.DelegatedConnections: return "Delegated connections";
                case PrerequisiteCategory.AzureResources: return "Azure resources";
                case PrerequisiteCategory.RuntimeAccess: return "RBAC & database access";
                case PrerequisiteCategory.DeploymentAccess: return "Deployment privileges";
                case PrerequisiteCategory.OperationalChecks: return "Licensing & setup";
                default: return "Copilot extras (optional)";
            }
        }

        private void RefreshCurrentConfiguration()
        {
            if (ConfigurationProvider != null) RefreshConfiguration(ConfigurationProvider());
        }

        private void PopulateRequirements()
        {
            _requirements.Rows.Clear();
            if (_report == null) return;
            var choice = _category.SelectedItem as CategoryChoice;
            var permissionsView = choice?.Key == "workloads";
            var optionalView = choice?.Key == "optional-resources";
            ConfigureColumns(permissionsView, optionalView);
            if (permissionsView)
            {
                _categoryHelp.Text = "For each selected import, these are the runtime app's API permissions/connections and the reason it needs them. Graph and Office 365 Management APIs are different audiences. Optional extras are labelled; a 'None' row means no Graph/Activity API application grant, not that the workload needs no setup.";
                foreach (var permission in _report.WorkloadPermissions)
                {
                    var name = permission.Requirement.Category == PrerequisiteCategory.OptionalEnrichment && !permission.NoApplicationGrant
                        ? "Optional: " + permission.Permission : permission.Permission;
                    var row = _requirements.Rows.Add(name, permission.ApiAndType, permission.Workload, permission.Why);
                    _requirements.Rows[row].Tag = permission;
                }
            }
            else if (optionalView)
            {
                _categoryHelp.Text = "These add-on services are currently selected. Review what functionality would be lost and where to turn provisioning off. Service Bus is required while Teams calls import is selected. " + DeploymentPrerequisiteReport.RemovalNotice;
                foreach (var component in _report.OptionalComponents)
                {
                    var row = _requirements.Rows.Add(component.Name, component.Status, component.FunctionalityLost, component.HowToRemove);
                    _requirements.Rows[row].Tag = component;
                }
            }
            else
            {
                var category = choice?.Value;
                _categoryHelp.Text = category.HasValue ? DeploymentPrerequisiteReport.CategoryDescription(category.Value)
                    : "Only applicable categories are shown. Each item's 'Who sets this up' separates automatic installer work from approvals and checks. Optional Copilot file, meeting and history features are listed only for the Copilot audit workload.";
                foreach (var requirement in _report.Requirements.Where(r => !category.HasValue || r.Category == category.Value))
                {
                    var name = requirement.Category == PrerequisiteCategory.OptionalEnrichment ? "Optional: " + requirement.Name : requirement.Name;
                    var row = _requirements.Rows.Add(name, requirement.Identity, string.Join(", ", requirement.Workloads));
                    _requirements.Rows[row].Tag = requirement;
                }
            }
            if (_requirements.Rows.Count > 0) _requirements.CurrentCell = _requirements.Rows[0].Cells[0];
            ShowSelectedRequirement();
        }

        private void ConfigureColumns(bool permissions, bool optional)
        {
            _requirements.Columns["Requirement"].HeaderText = optional ? "Selected component" : permissions ? "Runtime permission" : "Requirement";
            _requirements.Columns["Identity"].HeaderText = optional ? "Can it be left out?" : permissions ? "API / type" : "Identity / type";
            _requirements.Columns["Workloads"].HeaderText = optional ? "Functionality lost" : permissions ? "Workload" : "Needed for";
            _requirements.Columns["Why"].HeaderText = optional ? "What to change" : "Why needed";
            _requirements.Columns["Why"].Visible = permissions || optional;
            _requirements.Columns["Requirement"].FillWeight = permissions || optional ? 28 : 45;
            _requirements.Columns["Identity"].FillWeight = permissions || optional ? 22 : 30;
            _requirements.Columns["Workloads"].FillWeight = optional ? 30 : permissions ? 22 : 25;
            _requirements.Columns["Why"].FillWeight = optional ? 30 : 35;
        }

        private void ShowSelectedRequirement()
        {
            var item = _requirements.CurrentRow?.Tag;
            var requirement = item as DeploymentRequirement;
            var permission = item as WorkloadPermissionRow;
            var component = item as OptionalAzureComponent;
            _details.Text = permission?.Details ?? component?.Details ?? requirement?.Details ?? "No requirements in this category for the current selections.";
            _reference.Tag = permission?.Requirement.Reference ?? requirement?.Reference
                ?? (component == null ? null : "https://github.com/pnp/Microsoft365-Analytics-Insights/wiki");
            _reference.Enabled = _reference.Tag != null;
        }

        public void ExportMarkdown(string fileName)
        {
            RefreshCurrentConfiguration();
            File.WriteAllText(fileName, _report.ToMarkdown(), new UTF8Encoding(false));
        }

        private void Refresh_Click(object sender, EventArgs e) => RefreshCurrentConfiguration();
        private void Category_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_refreshingCategories) return;
            _viewChosen = true;
            PopulateRequirements();
            UpdateLayoutHeight();
        }
        private void Requirements_CurrentCellChanged(object sender, EventArgs e) => ShowSelectedRequirement();

        private void Export_Click(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog
            {
                Title = "Export deployment prerequisites", Filter = "Markdown document (*.md)|*.md",
                DefaultExt = "md", AddExtension = true, OverwritePrompt = true, FileName = "deployment-prerequisites.md"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ExportMarkdown(dialog.FileName);
                    _status.Text = "Markdown exported. " + DeploymentPrerequisiteReport.PrivacyNotice;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    MessageBox.Show(this, "The prerequisites document could not be saved. Choose a writable location and try again.",
                        "Export prerequisites", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void Reference_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (!(_reference.Tag is string url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                MessageBox.Show(this, "The reference could not be opened. References are also included in the exported Markdown.",
                    "Prerequisites reference", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
