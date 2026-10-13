using App.ControlPanel.Engine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using static App.ControlPanel.Frames.InstallWizard.InstallSolutionControl;

namespace App.ControlPanel.Frames.InstallWizard
{
    public sealed class OperationCentreControl : UserControl
    {
        private readonly ListView _log;
        private readonly List<InstallLogLVI> _messages = new List<InstallLogLVI>();
        private readonly Label _status = new Label
        {
            Name = "operationStatus",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "No operation has run. Test Configuration before deploying.",
            Visible = false
        };
        private readonly ListView _phases = new App.ControlPanel.Controls.BufferedListView
        {
            Name = "operationPhases",
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Visible = false,
            Height = 110
        };
        private readonly TextBox _search = new TextBox { Name = "logSearch", Width = 140, AccessibleName = "Search log" };
        private readonly ComboBox _severity = new ComboBox { Name = "logSeverity", DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
        private readonly CheckBox _follow = new CheckBox { Name = "followLog", Text = "Follow new messages", Checked = true, AutoSize = true };
        private readonly FlowLayoutPanel _filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Visible = false };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private readonly Stopwatch _phaseElapsed = new Stopwatch();
        private readonly Font _operationFont = new Font("Segoe UI", 11F, FontStyle.Bold);
        private readonly Timer _timer = new Timer { Interval = 1000 };
        private string _operation;
        private bool _running;
        private bool _cancelRequested;
        private int _warnings;
        private int _errors;
        private bool _phaseHasErrors;
        private readonly TableLayoutPanel _layout;

        public OperationCentreControl(ListView log)
        {
            _log = log;
            Name = "operationCentre";
            Font = new Font("Segoe UI", 9F);
            _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _status.Margin = new Padding(0, 4, 0, 12);
            _status.Font = Font;
            _phases.Columns.Add("Reported engine phase", 400);
            _phases.Columns.Add("Observed interval / result", 260);
            _severity.Items.AddRange(new object[] { "All messages", "Warnings & errors", "Errors only" });
            _severity.SelectedIndex = 0;
            _filters.Controls.Add(new Label { Text = "Search log:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            _filters.Controls.Add(_search);
            _filters.Controls.Add(_severity);
            _filters.Controls.Add(_follow);
            _layout.Controls.Add(_status, 0, 0);
            _layout.Controls.Add(_phases, 0, 1);
            _layout.Controls.Add(_filters, 0, 2);
            _layout.Controls.Add(log, 0, 3);
            Controls.Add(_layout);
            _search.TextChanged += (sender, args) => Refilter();
            _severity.SelectedIndexChanged += (sender, args) => Refilter();
            _follow.CheckedChanged += (sender, args) => Follow();
            _timer.Tick += (sender, args) => RefreshStatus();
            _log.Resize += (sender, args) => SizeLogColumn();
            _log.ShowItemToolTips = true;
            _log.AccessibleDescription = "Double-click a message or press Enter to read its full text.";
            _log.DoubleClick += (sender, args) => ShowSelectedMessage();
            _log.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                ShowSelectedMessage();
                args.Handled = true;
            };
        }

        public void BeginRun(string operation)
        {
            ClearLog();
            _operation = operation;
            _status.Font = _operationFont;
            _status.Visible = true;
            _filters.Visible = true;
            _running = true;
            _cancelRequested = false;
            _elapsed.Restart();
            _timer.Start();
            RefreshStatus();
        }

        public void RequestCancellation()
        {
            _cancelRequested = true;
            RefreshStatus();
        }

        public void CompleteRun()
        {
            _running = false;
            _elapsed.Stop();
            _timer.Stop();
            ClosePhase();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var state = _running ? _cancelRequested ? "Cancellation requested - waiting for a safe checkpoint" : "Running"
                : _cancelRequested ? "Finished after a cancellation request - review the log"
                : _errors > 0 ? "Finished with errors - review the log before retrying"
                : _warnings > 0 ? "Finished with warnings - review the log"
                : "Finished - review the log for verification results";
            _status.Text = $"{_operation}: {state}  |  {_elapsed.Elapsed:hh\\:mm\\:ss}  |  {_errors} error message(s), {_warnings} warning message(s)";
        }

        public void Append(InstallLogLVI item)
        {
            item.ToolTipText = item.Text;
            if (!_filters.Visible) _filters.Visible = true;
            _messages.Add(item);
            if (item.Level == LogLevel.Warning) _warnings++;
            if (item.Level >= LogLevel.Error)
            {
                _errors++;
                _phaseHasErrors = true;
            }
            if (item.PhaseName != null)
            {
                ClosePhase();
                _phaseHasErrors = false;
                _phases.Visible = true;
                _phases.Items.Add(new ListViewItem(new[] { item.PhaseName, "In progress - outcome not yet reported" }));
                _phaseElapsed.Restart();
            }
            if (Matches(item))
            {
                _log.Items.Add(item);
                Follow();
            }
            // Status is refreshed by the timer, not by every message (which relayouts the log).
        }

        private void ClosePhase()
        {
            if (_phases.Items.Count == 0 || !_phaseElapsed.IsRunning) return;
            _phaseElapsed.Stop();
            _phases.Items[_phases.Items.Count - 1].SubItems[1].Text =
                $"{_phaseElapsed.Elapsed.TotalSeconds:0}s observed; {(_phaseHasErrors ? "errors reported" : "see log for outcome")}";
        }

        private bool Matches(InstallLogLVI item) =>
            (_severity.SelectedIndex == 0 || item.Level >= (_severity.SelectedIndex == 1 ? LogLevel.Warning : LogLevel.Error))
            && item.Text.IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase) >= 0;

        private void Refilter()
        {
            _log.BeginUpdate();
            _log.Items.Clear();
            foreach (var item in _messages.Where(Matches)) _log.Items.Add(item);
            _log.EndUpdate();
            Follow();
        }

        private void Follow()
        {
            if (_follow.Checked && _log.Items.Count > 0) _log.Items[_log.Items.Count - 1].EnsureVisible();
        }

        private void SizeLogColumn()
        {
            var width = Math.Max(120, _log.ClientSize.Width - 24);
            if (_log.Columns[0].Width != width) _log.Columns[0].Width = width;
        }

        private void ShowSelectedMessage()
        {
            if (_log.SelectedItems.Count == 0) return;
            using (var details = new Form
            {
                Text = "Log message - review before sharing",
                ClientSize = new Size(760, 380),
                MinimumSize = new Size(500, 250),
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                ShowIcon = false
            })
            {
                details.Controls.Add(new TextBox
                {
                    Text = _log.SelectedItems[0].Text,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    Dock = DockStyle.Fill,
                    Font = Font,
                    AccessibleName = "Full log message"
                });
                details.ShowDialog(this);
            }
        }

        public string FullLogText => string.Join(Environment.NewLine, _messages.Select(item => item.Text));
        public void ShowPhases(bool visible) => _phases.Visible = visible && _phases.Items.Count > 0;

        public override Size GetPreferredSize(Size proposedSize)
        {
            var height = 100 + _log.Margin.Vertical;
            if (_status.Visible) height += _status.GetPreferredSize(proposedSize).Height + _status.Margin.Vertical;
            if (_phases.Visible) height += _phases.Height + _phases.Margin.Vertical;
            if (_filters.Visible) height += _filters.GetPreferredSize(proposedSize).Height + _filters.Margin.Vertical;
            return new Size(proposedSize.Width, height);
        }

        public void ClearLog()
        {
            _messages.Clear();
            _log.Items.Clear();
            _phases.Items.Clear();
            _phases.Visible = false;
            _phaseElapsed.Reset();
            _errors = _warnings = 0;
            _phaseHasErrors = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _operationFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
