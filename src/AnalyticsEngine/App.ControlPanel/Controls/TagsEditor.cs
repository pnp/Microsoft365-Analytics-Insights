using Common.Entities.Installer;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace App.ControlPanel.Controls
{
    public partial class TagsEditor : UserControl
    {
        public TagsEditor()
        {
            InitializeComponent();
            if (!this.DesignMode)
            {
                lstTags.Items.Clear();
            }
        }

        public List<AzTag> Tags
        {
            get
            {
                var tags = new List<AzTag>();
                foreach (var lvi in lstTags.Items)
                {
                    if (!(lvi is TagLVI tagLVI))
                    {
                        continue;
                    }
                    tags.Add(tagLVI.AzTag);
                }
                return tags;
            }
            set
            {
                lstTags.Items.Clear();
                foreach (var tag in value)
                {
                    lstTags.Items.Add(new TagLVI(tag));
                }
            }
        }

        private void TagsEditor_Load(object sender, EventArgs e)
        {
            UpdateDisplayControls(View.List);
            pnlAddTag.Dock = DockStyle.Fill;
            pnlList.Dock = DockStyle.Fill;
            DeleteButtonUI();
        }

        void UpdateDisplayControls(View v)
        {
            pnlList.Visible = v == View.List;
            pnlAddTag.Visible = v == View.Add;
        }
        void DeleteButtonUI()
        {
            btnRemove.Enabled = lstTags.SelectedItems.Count > 0;
        }

        enum View
        {
            List,
            Add
        }

        private void btnAdd_Click(object sender, EventArgs e)
        {
            UpdateDisplayControls(View.Add);
        }

        private void btnSaveNew_Click(object sender, EventArgs e)
        {
            var name = txtTagName.Text.Trim();
            var val = txtTagVal.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a tag name", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            foreach (var lvi in lstTags.Items)
            {
                if (!(lvi is TagLVI tagLVI))
                {
                    continue;
                }
                if (tagLVI.AzTag.Name == name)
                {
                    MessageBox.Show($"Invalid tag name. The tag name '{name}' is already used. Tag names are case-insensitive.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            lstTags.Items.Add(new TagLVI(new AzTag(name, val)));
            txtTagName.Text = string.Empty;
            txtTagVal.Text = string.Empty;

            UpdateDisplayControls(View.List);
        }

        private void lstTags_SelectedIndexChanged(object sender, EventArgs e)
        {
            DeleteButtonUI();
        }

        class TagLVI : ListViewItem
        {
            public TagLVI(AzTag tag)
            {
                if (string.IsNullOrEmpty(tag.Value))
                {
                    this.Text = $"{tag.Name}";
                }
                else
                {
                    this.Text = $"{tag.Name}: {tag.Value}";
                }
                this.ImageIndex = 0;
                AzTag = tag;
            }

            public AzTag AzTag { get; }
        }

        private void btnRemove_Click(object sender, EventArgs e)
        {
            foreach (var lvi in lstTags.SelectedItems)
            {
                lstTags.Items.Remove(lvi as ListViewItem);
            }
        }
    }
}
