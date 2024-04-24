namespace App.ControlPanel.Controls
{
    partial class TagsEditor
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.Windows.Forms.ListViewItem listViewItem1 = new System.Windows.Forms.ListViewItem(new string[] {
            "tag1",
            "asdfasdf"}, "tag-icon.png");
            System.Windows.Forms.ListViewItem listViewItem2 = new System.Windows.Forms.ListViewItem(new string[] {
            "tag2",
            ""}, "tag-icon.png");
            System.Windows.Forms.ListViewItem listViewItem3 = new System.Windows.Forms.ListViewItem("Another tag: Value", 0);
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(TagsEditor));
            this.pnlAddTag = new System.Windows.Forms.Panel();
            this.label2 = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.btnSaveNew = new System.Windows.Forms.Button();
            this.txtTagVal = new System.Windows.Forms.TextBox();
            this.txtTagName = new System.Windows.Forms.TextBox();
            this.pnlList = new System.Windows.Forms.Panel();
            this.lstTags = new System.Windows.Forms.ListView();
            this.columnHeader1 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.columnHeader2 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.imageListTags = new System.Windows.Forms.ImageList(this.components);
            this.btnRemove = new System.Windows.Forms.Button();
            this.btnAdd = new System.Windows.Forms.Button();
            this.pnlAddTag.SuspendLayout();
            this.pnlList.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnlAddTag
            // 
            this.pnlAddTag.Controls.Add(this.label2);
            this.pnlAddTag.Controls.Add(this.label1);
            this.pnlAddTag.Controls.Add(this.btnSaveNew);
            this.pnlAddTag.Controls.Add(this.txtTagVal);
            this.pnlAddTag.Controls.Add(this.txtTagName);
            this.pnlAddTag.Location = new System.Drawing.Point(36, 168);
            this.pnlAddTag.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.pnlAddTag.Name = "pnlAddTag";
            this.pnlAddTag.Size = new System.Drawing.Size(411, 75);
            this.pnlAddTag.TabIndex = 3;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(180, 17);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(54, 20);
            this.label2.TabIndex = 7;
            this.label2.Text = "Value:";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(4, 15);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(115, 20);
            this.label1.TabIndex = 6;
            this.label1.Text = "New tag name:";
            // 
            // btnSaveNew
            // 
            this.btnSaveNew.Image = global::App.ControlPanel.Properties.Resources.plus;
            this.btnSaveNew.Location = new System.Drawing.Point(364, 37);
            this.btnSaveNew.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.btnSaveNew.Name = "btnSaveNew";
            this.btnSaveNew.Size = new System.Drawing.Size(42, 38);
            this.btnSaveNew.TabIndex = 5;
            this.btnSaveNew.UseVisualStyleBackColor = true;
            this.btnSaveNew.Click += new System.EventHandler(this.btnSaveNew_Click);
            // 
            // txtTagVal
            // 
            this.txtTagVal.Location = new System.Drawing.Point(184, 42);
            this.txtTagVal.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.txtTagVal.Name = "txtTagVal";
            this.txtTagVal.Size = new System.Drawing.Size(169, 26);
            this.txtTagVal.TabIndex = 1;
            // 
            // txtTagName
            // 
            this.txtTagName.Location = new System.Drawing.Point(4, 42);
            this.txtTagName.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.txtTagName.Name = "txtTagName";
            this.txtTagName.Size = new System.Drawing.Size(169, 26);
            this.txtTagName.TabIndex = 0;
            // 
            // pnlList
            // 
            this.pnlList.Controls.Add(this.lstTags);
            this.pnlList.Controls.Add(this.btnRemove);
            this.pnlList.Controls.Add(this.btnAdd);
            this.pnlList.Location = new System.Drawing.Point(21, 32);
            this.pnlList.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.pnlList.Name = "pnlList";
            this.pnlList.Size = new System.Drawing.Size(428, 92);
            this.pnlList.TabIndex = 2;
            // 
            // lstTags
            // 
            this.lstTags.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lstTags.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnHeader1,
            this.columnHeader2});
            this.lstTags.FullRowSelect = true;
            this.lstTags.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.None;
            this.lstTags.HideSelection = false;
            this.lstTags.Items.AddRange(new System.Windows.Forms.ListViewItem[] {
            listViewItem1,
            listViewItem2,
            listViewItem3});
            this.lstTags.Location = new System.Drawing.Point(0, 0);
            this.lstTags.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.lstTags.Name = "lstTags";
            this.lstTags.Size = new System.Drawing.Size(374, 90);
            this.lstTags.SmallImageList = this.imageListTags;
            this.lstTags.TabIndex = 5;
            this.lstTags.UseCompatibleStateImageBehavior = false;
            this.lstTags.View = System.Windows.Forms.View.SmallIcon;
            this.lstTags.SelectedIndexChanged += new System.EventHandler(this.lstTags_SelectedIndexChanged);
            // 
            // columnHeader1
            // 
            this.columnHeader1.Text = "Tag";
            this.columnHeader1.Width = 130;
            // 
            // columnHeader2
            // 
            this.columnHeader2.Text = "Value";
            this.columnHeader2.Width = 96;
            // 
            // imageListTags
            // 
            this.imageListTags.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageListTags.ImageStream")));
            this.imageListTags.TransparentColor = System.Drawing.Color.Transparent;
            this.imageListTags.Images.SetKeyName(0, "tag-icon.png");
            // 
            // btnRemove
            // 
            this.btnRemove.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnRemove.Image = global::App.ControlPanel.Properties.Resources.trash_can;
            this.btnRemove.Location = new System.Drawing.Point(386, 43);
            this.btnRemove.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.btnRemove.Name = "btnRemove";
            this.btnRemove.Size = new System.Drawing.Size(42, 38);
            this.btnRemove.TabIndex = 4;
            this.btnRemove.UseVisualStyleBackColor = true;
            this.btnRemove.Click += new System.EventHandler(this.btnRemove_Click);
            // 
            // btnAdd
            // 
            this.btnAdd.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnAdd.Image = global::App.ControlPanel.Properties.Resources.plus;
            this.btnAdd.Location = new System.Drawing.Point(386, 0);
            this.btnAdd.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.btnAdd.Name = "btnAdd";
            this.btnAdd.Size = new System.Drawing.Size(42, 38);
            this.btnAdd.TabIndex = 3;
            this.btnAdd.UseVisualStyleBackColor = true;
            this.btnAdd.Click += new System.EventHandler(this.btnAdd_Click);
            // 
            // TagsEditor
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.pnlAddTag);
            this.Controls.Add(this.pnlList);
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.Name = "TagsEditor";
            this.Size = new System.Drawing.Size(480, 251);
            this.Load += new System.EventHandler(this.TagsEditor_Load);
            this.pnlAddTag.ResumeLayout(false);
            this.pnlAddTag.PerformLayout();
            this.pnlList.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Panel pnlAddTag;
        private System.Windows.Forms.Button btnAdd;
        private System.Windows.Forms.Button btnRemove;
        private System.Windows.Forms.Panel pnlList;
        private System.Windows.Forms.ListView lstTags;
        private System.Windows.Forms.ImageList imageListTags;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button btnSaveNew;
        private System.Windows.Forms.TextBox txtTagVal;
        private System.Windows.Forms.TextBox txtTagName;
        private System.Windows.Forms.ColumnHeader columnHeader1;
        private System.Windows.Forms.ColumnHeader columnHeader2;
    }
}
