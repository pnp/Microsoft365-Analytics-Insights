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
        private readonly ComboBox _category = new ComboBox
        {
            Name = "prerequisiteCategory", DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 310, AccessibleName = "Requirement category"
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
            AccessibleName = "Selected requirement, scope and administrator action"
        };
        private readonly LinkLabel _reference = new LinkLabel
        {
            Name = "prerequisiteReference", AutoSize = true, Text = "Open &reference",
            AccessibleDescription = "Opens the reference for the selected requirement in your browser."
        };
        private DeploymentPrerequisiteReport _report;

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
                ColumnCount = 1, RowCount = 8, MinimumSize = new Size(0, Font.Height * 38)
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 5; row++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            _requirements.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _requirements.DefaultCellStyle.Padding = new Padding(5);
            _requirements.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _requirements.Columns.Add(Column("Requirement", "Requirement", 45));
            _requirements.Columns.Add(Column("Identity", "Identity / type", 30));
            _requirements.Columns.Add(Column("Workloads", "Needed for", 25));
            _requirements.CurrentCellChanged += Requirements_CurrentCellChanged;
            _layout.Controls.Add(_requirements, 0, 5);
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
            _layout.Controls.Add(detailLayout, 0, 6);
            _status.Text = DeploymentPrerequisiteReport.PrivacyNotice;
            _status.Margin = new Padding(0, 8, 0, 0);
            _layout.Controls.Add(_status, 0, 7);
            Controls.Add(_layout);
            _category.Items.Add("All requirements");
            _category.Items.AddRange(new object[]
            {
                "Application permissions", "Delegated connections", "Azure resources", "RBAC & database access",
                "Deployment privileges", "Licensing & setup", "Optional enrichment"
            });
            _category.SelectedIndexChanged += Category_SelectedIndexChanged;
            _category.SelectedIndex = 0;
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
                _category.Width = _category.Items.Cast<string>().Max(text => TextRenderer.MeasureText(text, _category.Font).Width)
                    + SystemInformation.VerticalScrollBarWidth + 24;
            var width = Math.Max(100, ClientSize.Width - _layout.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            var headerAndFooter = _layout.Padding.Vertical;
            foreach (Control control in _layout.Controls)
            {
                var row = _layout.GetRow(control);
                if (row == 5 || row == 6) continue;
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
            _summary.Text = $"{_report.Workloads.Count} workloads  |  {permissions} application permissions  |  {resources} resource types";
            _selection.Text = _report.Workloads.Count == 0 ? "No workloads selected. Choose workloads to build an import-specific request."
                : "Selected: " + string.Join(", ", _report.Workloads);
            _status.Text = DeploymentPrerequisiteReport.PrivacyNotice;
            PopulateRequirements();
            UpdateLayoutHeight();
        }

        private void RefreshCurrentConfiguration()
        {
            if (ConfigurationProvider != null) RefreshConfiguration(ConfigurationProvider());
        }

        private void PopulateRequirements()
        {
            _requirements.Rows.Clear();
            if (_report == null) return;
            var index = _category.SelectedIndex;
            foreach (var requirement in _report.Requirements.Where(r => index <= 0 || (int)r.Category == index - 1))
            {
                var name = requirement.Category == PrerequisiteCategory.OptionalEnrichment ? "Optional: " + requirement.Name : requirement.Name;
                var row = _requirements.Rows.Add(name, requirement.Identity, string.Join(", ", requirement.Workloads));
                _requirements.Rows[row].Tag = requirement;
            }
            if (_requirements.Rows.Count > 0) _requirements.CurrentCell = _requirements.Rows[0].Cells[0];
            ShowSelectedRequirement();
        }

        private void ShowSelectedRequirement()
        {
            var requirement = _requirements.CurrentRow?.Tag as DeploymentRequirement;
            _details.Text = requirement?.Details ?? "No requirements in this category for the current selections.";
            _reference.Enabled = requirement != null;
            _reference.Tag = requirement?.Reference;
        }

        public void ExportMarkdown(string fileName)
        {
            RefreshCurrentConfiguration();
            File.WriteAllText(fileName, _report.ToMarkdown(), new UTF8Encoding(false));
        }

        private void Refresh_Click(object sender, EventArgs e) => RefreshCurrentConfiguration();
        private void Category_SelectedIndexChanged(object sender, EventArgs e) => PopulateRequirements();
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
