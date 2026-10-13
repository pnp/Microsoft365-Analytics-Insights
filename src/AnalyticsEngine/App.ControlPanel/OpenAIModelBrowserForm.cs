using App.ControlPanel.Controls;
using CloudInstallEngine.Azure;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public sealed class OpenAIModelBrowserForm : Form
    {
        private readonly ComboBox _deploymentType = new ComboBox { Name = "deploymentType", DropDownStyle = ComboBoxStyle.DropDownList, Width = 185, FormattingEnabled = true };
        private readonly CheckBox _smallOnly = new CheckBox { Name = "smallOnly", Text = "Small models for categorisation", Checked = true, AutoSize = true };
        private readonly Label _residency = TextLabel("residency");
        private readonly Label _status = TextLabel("catalogueStatus");
        private readonly Label _details = TextLabel("modelDetails");
        private readonly BufferedListView _models = new BufferedListView { Name = "models", Dock = DockStyle.Fill,
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        private readonly NumericUpDown _capacity = new NumericUpDown { Name = "capacity", Minimum = 1, Maximum = 10000, Width = 80 };
        private readonly Button _refresh = new Button { Name = "refresh", Text = "Refresh catalogue", AutoSize = true };
        private readonly Button _apply = new Button { Name = "applyModel", Text = "Use selected model", AutoSize = true, Enabled = false };
        private readonly Label _context = TextLabel("catalogueContext");
        private readonly LinkLabel _errorLink = new LinkLabel { Name = "lookupErrorDetails", Text = "Error details", AutoSize = true, Visible = false, Margin = new Padding(8, 7, 0, 0) };
        private IReadOnlyList<OpenAIModelChoice> _choices = Array.Empty<OpenAIModelChoice>();
        private CancellationTokenSource _lookup;
        private readonly string _initialName;
        private readonly string _initialVersion;
        private bool _loading;
        private string _lookupError;
        private string _errorDetails;

        public Func<CancellationToken, Task<IReadOnlyList<OpenAIModelChoice>>> LoadModelsAsync { get; set; }
        public OpenAIModelChoice SelectedModel { get; private set; }
        public int SelectedCapacity { get; private set; }
        public string ContextDescription { get => _context.Text; set => _context.Text = value; }

        public OpenAIModelBrowserForm() : this("Standard", null, null) { }

        public OpenAIModelBrowserForm(string deploymentType, string initialName, string initialVersion)
        {
            _initialName = initialName;
            _initialVersion = initialVersion;
            Text = "Choose a prompt categorisation model";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(820, 570);
            MinimumSize = new Size(730, 560);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 9; i++)
                layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.AutoSize, i == 5 ? 100 : 0));
            Controls.Add(layout);
            var heading = TextLabel("heading");
            heading.Font = new Font(Font, FontStyle.Bold);
            heading.Text = "A small model is enough to classify prompts. Start with mini or nano.";
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(_context, 0, 1);
            layout.Controls.Add(_residency, 0, 3);
            layout.Controls.Add(_status, 0, 4);
            var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            filters.Controls.AddRange(new Control[] { _deploymentType, _smallOnly, _refresh, _errorLink });
            layout.Controls.Add(filters, 0, 2);
            _models.Columns.Add("Model", 190);
            _models.Columns.Add("Version", 115);
            _models.Columns.Add("Size", 70);
            _models.Columns.Add("Lifecycle / default", 190);
            layout.Controls.Add(_models, 0, 5);
            layout.Controls.Add(_details, 0, 6);
            var capacityRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            capacityRow.Controls.AddRange(new Control[] { new Label { Text = "Capacity:", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, _capacity });
            layout.Controls.Add(capacityRow, 0, 7);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.AddRange(new Control[] { cancel, _apply });
            layout.Controls.Add(actions, 0, 8);
            CancelButton = cancel;
            AcceptButton = _apply;
            _deploymentType.Items.AddRange(OpenAIModelCatalog.DeploymentTypes.Cast<object>().ToArray());
            _deploymentType.Format += (s, e) => e.Value = OpenAIModelCatalog.DeploymentTypeLabel(e.ListItem as string);
            _deploymentType.SelectedItem = deploymentType;
            _deploymentType.SelectedIndexChanged += (s, e) => DisplayChoices();
            _smallOnly.CheckedChanged += (s, e) => DisplayChoices();
            _models.SelectedIndexChanged += (s, e) => UpdateSelection();
            _capacity.ValueChanged += (s, e) => UpdateApplyState();
            _refresh.Click += async (s, e) => await RefreshModelsAsync();
            _errorLink.LinkClicked += (s, e) => MessageBox.Show(this, _errorDetails, "Model lookup error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _apply.Click += (s, e) =>
            {
                var choice = CurrentChoice;
                if (choice == null || !choice.SupportsCapacity((int)_capacity.Value))
                    return;
                SelectedModel = choice;
                SelectedCapacity = (int)_capacity.Value;
                DialogResult = DialogResult.OK;
            };
            Shown += async (s, e) => { if (LoadModelsAsync != null) await RefreshModelsAsync(); };
            FormClosing += (s, e) => _lookup?.Cancel();
            DisplayChoices();
        }

        private static Label TextLabel(string name) => new Label { Name = name, AutoSize = true,
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10), UseMnemonic = false };

        private OpenAIModelChoice CurrentChoice => _models.SelectedItems.Count == 1
            ? _models.SelectedItems[0].Tag as OpenAIModelChoice : null;

        private void DisplayChoices()
        {
            var type = _deploymentType.SelectedItem as string;
            _residency.Text = OpenAIModelCatalog.ResidencyDescription(type) +
                " Stored data remains in the resource's Azure geography. Changing this choice changes where prompts are processed.";
            _models.BeginUpdate();
            _models.Items.Clear();
            foreach (var choice in _choices.Where(x => x.IsClassifierCompatible && x.DeploymentType == type && (!_smallOnly.Checked || x.IsSmallModel)))
            {
                var item = new ListViewItem(new[] { choice.Name, choice.Version,
                    choice.IsSmallModel ? "Small" : "Larger",
                    (choice.Lifecycle == "GenerallyAvailable" ? "Available" : choice.Lifecycle) + (choice.IsDefaultVersion ? " (default)" : "") }) { Tag = choice };
                _models.Items.Add(item);
                if (choice.Name == _initialName && (string.IsNullOrWhiteSpace(_initialVersion) ? choice.IsDefaultVersion : choice.Version == _initialVersion))
                    item.Selected = true;
            }
            _models.EndUpdate();
            if (!_loading)
            {
                _status.Text = _lookupError ?? (_models.Items.Count == 0
                    ? "No compatible models shown. Refresh the catalogue, include larger models, or explicitly choose another deployment type. No automatic geography change."
                    : $"{_models.Items.Count} compatible model/version combinations listed by Azure. Availability is not a quota or live-capacity guarantee.");
                _status.ForeColor = _lookupError != null ? Color.Firebrick : (_models.Items.Count == 0 ? Color.DarkOrange : Color.DarkGreen);
            }
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            var choice = CurrentChoice;
            if (choice == null)
            {
                _details.Text = "Only models compatible with this classifier's strict JSON Chat Completions request are shown. " +
                    "Reasoning and unknown models are excluded. Mini/nano is a size guide, not a price quote; check Azure pricing and subscription quota.";
            }
            else
            {
                var capacity = choice.Capacity;
                var permitted = capacity == null ? "not reported; verify in Azure" :
                    capacity.AllowedValues.Count > 0 ? string.Join(", ", capacity.AllowedValues) :
                    $"{capacity.Minimum?.ToString() ?? "unspecified"} - {capacity.Maximum?.ToString() ?? "unspecified"}; step {capacity.Step?.ToString() ?? "unspecified"}";
                _details.Text = $"{choice.Name} / {choice.Version} / {choice.DeploymentType}. Capacity allowed: {permitted} (model-specific rate-limit units, not PTUs). " +
                    (choice.IsSmallModel ? "Small model: a sensible starting point for categorisation. " : "Larger model: usually unnecessary for simple categorisation. ") +
                    "Pay per token; capacity is a throughput limit, not a fixed monthly price. Verify Azure pricing and quota.";
                try { _capacity.Value = choice.SuggestedCapacity; }
                catch (CloudInstallEngine.Models.InstallException ex)
                {
                    _status.Text = ex.Message;
                    _status.ForeColor = Color.Firebrick;
                }
            }
            UpdateApplyState();
        }

        private void UpdateApplyState()
        {
            _capacity.Enabled = !_loading && CurrentChoice != null;
            _apply.Enabled = !_loading && CurrentChoice != null && CurrentChoice.SupportsCapacity((int)_capacity.Value);
        }

        public async Task RefreshModelsAsync()
        {
            if (_loading) return;
            _loading = true;
            _lookupError = null;
            _errorDetails = null;
            _errorLink.Visible = false;
            _choices = Array.Empty<OpenAIModelChoice>();
            DisplayChoices();
            _status.Text = "Reading model availability from Azure. No resources are created.";
            _status.ForeColor = SystemColors.ControlText;
            _refresh.Enabled = false;
            _deploymentType.Enabled = false;
            _smallOnly.Enabled = false;
            using (var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                _lookup = lookup;
                try
                {
                    if (LoadModelsAsync == null)
                        throw new InvalidOperationException("Enter the installer credentials, subscription and region before browsing models.");
                    var choices = await LoadModelsAsync(lookup.Token);
                    lookup.Token.ThrowIfCancellationRequested();
                    if (IsDisposed) return;
                    _choices = choices;
                    _loading = false;
                    DisplayChoices();
                }
                catch (OperationCanceledException)
                {
                    if (!IsDisposed)
                    {
                        _status.Text = _lookupError = "Model lookup cancelled or timed out. Refresh to retry; manual fields remain available.";
                        _status.ForeColor = Color.Firebrick;
                    }
                }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                    {
                        _errorDetails = ex.ToString();
                        _errorLink.Visible = true;
                        var message = ex.Message.Split('\r', '\n')[0];
                        if (message.Length > 160) message = message.Substring(0, 160) + "...";
                        _status.Text = _lookupError = "Model lookup failed: " + message +
                            " Check Azure read access and connection; retry or use manual fields. See Error details.";
                        _status.ForeColor = Color.Firebrick;
                    }
                }
                finally
                {
                    _lookup = null;
                    _loading = false;
                    if (!IsDisposed)
                    {
                        _refresh.Enabled = true;
                        _deploymentType.Enabled = true;
                        _smallOnly.Enabled = true;
                        UpdateApplyState();
                    }
                }
            }
        }
    }
}
