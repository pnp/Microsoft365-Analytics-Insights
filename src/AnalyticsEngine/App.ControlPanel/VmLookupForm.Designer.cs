namespace App.ControlPanel
{
    partial class VmLookupForm
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

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.grpLookup = new System.Windows.Forms.GroupBox();
            this.lblResourceGroup = new System.Windows.Forms.Label();
            this.txtResourceGroup = new System.Windows.Forms.TextBox();
            this.btnLookup = new System.Windows.Forms.Button();
            this.lblVmName = new System.Windows.Forms.Label();
            this.cmbVmName = new System.Windows.Forms.ComboBox();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnOkLookup = new System.Windows.Forms.Button();

            this.btnCancel = new System.Windows.Forms.Button();
            this.lblDescription = new System.Windows.Forms.Label();
            this.grpLookup.SuspendLayout();

            this.SuspendLayout();
            // 
            // lblDescription
            // 
            this.lblDescription.Location = new System.Drawing.Point(12, 9);
            this.lblDescription.Name = "lblDescription";
            this.lblDescription.Size = new System.Drawing.Size(520, 30);
            this.lblDescription.TabIndex = 0;
            this.lblDescription.Text = "Select a VM for the Hybrid Runbook Worker. You can look up VMs using the configured Azure credentials.";
            // 
            // grpLookup
            // 
            this.grpLookup.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpLookup.Controls.Add(this.lblResourceGroup);
            this.grpLookup.Controls.Add(this.txtResourceGroup);
            this.grpLookup.Controls.Add(this.btnLookup);
            this.grpLookup.Controls.Add(this.lblVmName);
            this.grpLookup.Controls.Add(this.cmbVmName);
            this.grpLookup.Controls.Add(this.lblStatus);
            this.grpLookup.Controls.Add(this.btnOkLookup);
            this.grpLookup.Location = new System.Drawing.Point(12, 42);
            this.grpLookup.Name = "grpLookup";
            this.grpLookup.Size = new System.Drawing.Size(520, 145);
            this.grpLookup.TabIndex = 1;
            this.grpLookup.TabStop = false;
            this.grpLookup.Text = "Look Up VM";
            // 
            // lblResourceGroup
            // 
            this.lblResourceGroup.AutoSize = true;
            this.lblResourceGroup.Location = new System.Drawing.Point(12, 25);
            this.lblResourceGroup.Name = "lblResourceGroup";
            this.lblResourceGroup.Size = new System.Drawing.Size(90, 13);
            this.lblResourceGroup.TabIndex = 0;
            this.lblResourceGroup.Text = "Resource Group:";
            // 
            // txtResourceGroup
            // 
            this.txtResourceGroup.Location = new System.Drawing.Point(115, 22);
            this.txtResourceGroup.Name = "txtResourceGroup";
            this.txtResourceGroup.Size = new System.Drawing.Size(250, 20);
            this.txtResourceGroup.TabIndex = 1;
            // 
            // btnLookup
            // 
            this.btnLookup.Location = new System.Drawing.Point(375, 20);
            this.btnLookup.Name = "btnLookup";
            this.btnLookup.Size = new System.Drawing.Size(130, 23);
            this.btnLookup.TabIndex = 2;
            this.btnLookup.Text = "Find VMs";
            this.btnLookup.UseVisualStyleBackColor = true;
            this.btnLookup.Click += new System.EventHandler(this.btnLookup_Click);
            // 
            // lblVmName
            // 
            this.lblVmName.AutoSize = true;
            this.lblVmName.Location = new System.Drawing.Point(12, 55);
            this.lblVmName.Name = "lblVmName";
            this.lblVmName.Size = new System.Drawing.Size(55, 13);
            this.lblVmName.TabIndex = 3;
            this.lblVmName.Text = "Select VM:";
            // 
            // cmbVmName
            // 
            this.cmbVmName.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbVmName.FormattingEnabled = true;
            this.cmbVmName.Location = new System.Drawing.Point(115, 52);
            this.cmbVmName.Name = "cmbVmName";
            this.cmbVmName.Size = new System.Drawing.Size(250, 21);
            this.cmbVmName.TabIndex = 4;
            this.cmbVmName.SelectedIndexChanged += new System.EventHandler(this.cmbVmName_SelectedIndexChanged);
            // 
            // lblStatus
            // 
            this.lblStatus.Location = new System.Drawing.Point(12, 82);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(365, 30);
            this.lblStatus.TabIndex = 5;
            this.lblStatus.Text = "Enter a resource group and click 'Find VMs' to search.";
            this.lblStatus.ForeColor = System.Drawing.SystemColors.GrayText;
            // 
            // btnOkLookup
            // 
            this.btnOkLookup.Enabled = false;
            this.btnOkLookup.Location = new System.Drawing.Point(375, 50);
            this.btnOkLookup.Name = "btnOkLookup";
            this.btnOkLookup.Size = new System.Drawing.Size(130, 23);
            this.btnOkLookup.TabIndex = 6;
            this.btnOkLookup.Text = "Use Selected VM";
            this.btnOkLookup.UseVisualStyleBackColor = true;
            this.btnOkLookup.Click += new System.EventHandler(this.btnOkLookup_Click);

            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(457, 195);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 23);
            this.btnCancel.TabIndex = 3;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // VmLookupForm
            // 
            this.AcceptButton = this.btnOkLookup;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(544, 226);
            this.Controls.Add(this.lblDescription);
            this.Controls.Add(this.grpLookup);

            this.Controls.Add(this.btnCancel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "VmLookupForm";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Select Hybrid Worker VM";
            this.Load += new System.EventHandler(this.VmLookupForm_Load);
            this.grpLookup.ResumeLayout(false);
            this.grpLookup.PerformLayout();

            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.Label lblDescription;
        private System.Windows.Forms.GroupBox grpLookup;
        private System.Windows.Forms.Label lblResourceGroup;
        private System.Windows.Forms.TextBox txtResourceGroup;
        private System.Windows.Forms.Button btnLookup;
        private System.Windows.Forms.Label lblVmName;
        private System.Windows.Forms.ComboBox cmbVmName;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Button btnOkLookup;

        private System.Windows.Forms.Button btnCancel;
    }
}
