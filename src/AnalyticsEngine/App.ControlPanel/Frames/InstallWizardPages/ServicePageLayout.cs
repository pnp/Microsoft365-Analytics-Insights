using System;
using System.Drawing;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    internal static class ServicePageLayout
    {
        internal static TableLayoutPanel Create(UserControl page, string title, string description)
        {
            page.Font = new Font("Segoe UI", 9F);
            page.AutoScroll = true;
            var layout = new TableLayoutPanel
            {
                Name = "servicePageLayout",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Padding = new Padding(10),
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(layout, Text(title, 16, true));
            AddRow(layout, Text(description));
            page.Controls.Add(layout);
            page.ClientSizeChanged += (sender, args) =>
                layout.MinimumSize = new Size(0, Math.Max(0, page.ClientSize.Height));
            return layout;
        }

        internal static TableLayoutPanel AddService(TableLayoutPanel page, string name, string title,
            string description, Image image, string iconName = null, string descriptionName = null)
        {
            // Only the gaps share spare height; each section keeps its own natural height.
            if (page.Controls.Count > 2)
            {
                page.RowCount++;
                page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            }
            var service = new TableLayoutPanel
            {
                Name = name,
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 8, 0, 4)
            };
            service.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            service.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            service.Controls.Add(new PictureBox
            {
                Name = iconName ?? name + "Icon",
                Image = image,
                Size = new Size(56, 56),
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false,
                Margin = new Padding(0, 0, 12, 0)
            }, 0, 0);
            var content = new TableLayoutPanel
            {
                Name = name + "Content",
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Margin = Padding.Empty
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(content, Text(title, 11, true));
            var explanation = Text(description);
            explanation.Name = descriptionName ?? name + "Description";
            AddRow(content, explanation);
            service.Controls.Add(content, 1, 0);
            AddRow(page, service);
            return content;
        }

        internal static Label Text(string text, float size = 9, bool bold = false) => new Label
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            UseMnemonic = false,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 4)
        };

        internal static void AddRow(TableLayoutPanel layout, Control control)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
        }

        internal static TableLayoutPanel AddFields(TableLayoutPanel content, string caption, Control field,
            string secondCaption = null, Control secondField = null)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = secondField == null ? 2 : 4,
                Margin = new Padding(0, 2, 0, 2)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            if (secondField != null)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            }
            AddField(row, caption, field, 0);
            if (secondField != null) AddField(row, secondCaption, secondField, 2);
            AddRow(content, row);
            return row;
        }

        internal static TableLayoutPanel AddFieldGrid(TableLayoutPanel content)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 4,
                Margin = new Padding(0, 2, 0, 2)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            AddRow(content, grid);
            return grid;
        }

        internal static void AddFieldGridRow(TableLayoutPanel grid, string caption, Control field,
            string secondCaption, Control secondField)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AddField(grid, caption, field, 0, row);
            AddField(grid, secondCaption, secondField, 2, row);
        }

        private static void AddField(TableLayoutPanel row, string caption, Control field, int column, int rowIndex = 0)
        {
            var label = Text(caption);
            label.Margin = new Padding(column == 0 ? 0 : 16, 5, 8, 0);
            row.Controls.Add(label, column, rowIndex);
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(0, 2, 0, 2);
            row.Controls.Add(field, column + 1, rowIndex);
        }
    }
}
