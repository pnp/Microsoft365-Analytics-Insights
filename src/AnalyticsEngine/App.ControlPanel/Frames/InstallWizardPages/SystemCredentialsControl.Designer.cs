namespace App.ControlPanel.Frames.InstallWizard
{
    partial class SystemCredentialsControl
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
            this.lblGUICredsRuntimeAccOfficeDesc = new System.Windows.Forms.Label();
            this.lblGUICredsInstallAccDesc = new System.Windows.Forms.Label();
            this.pictureBox6 = new System.Windows.Forms.PictureBox();
            this.lblGUICredsDescr = new System.Windows.Forms.Label();
            this.lblGUICredsHeader = new System.Windows.Forms.Label();
            this.runtimeO365AccountDetails = new App.ControlPanel.Controls.AppLoginDetailsControl();
            this.lblGUICredsRuntimeAccountOffice = new System.Windows.Forms.Label();
            this.installAccountControl = new App.ControlPanel.Controls.AppLoginDetailsControl();
            this.lblGUICredsInstallAccHeader = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox6)).BeginInit();
            this.SuspendLayout();
            // 
            // lblGUICredsRuntimeAccOfficeDesc
            // 
            this.lblGUICredsRuntimeAccOfficeDesc.AutoSize = true;
            this.lblGUICredsRuntimeAccOfficeDesc.Location = new System.Drawing.Point(-3, 258);
            this.lblGUICredsRuntimeAccOfficeDesc.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblGUICredsRuntimeAccOfficeDesc.Name = "lblGUICredsRuntimeAccOfficeDesc";
            this.lblGUICredsRuntimeAccOfficeDesc.Size = new System.Drawing.Size(248, 13);
            this.lblGUICredsRuntimeAccOfficeDesc.TabIndex = 92;
            this.lblGUICredsRuntimeAccOfficeDesc.Text = "Identity used by web-jobs to import Office 365 data.";
            // 
            // lblGUICredsInstallAccDesc
            // 
            this.lblGUICredsInstallAccDesc.AutoSize = true;
            this.lblGUICredsInstallAccDesc.Location = new System.Drawing.Point(-3, 113);
            this.lblGUICredsInstallAccDesc.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblGUICredsInstallAccDesc.Name = "lblGUICredsInstallAccDesc";
            this.lblGUICredsInstallAccDesc.Size = new System.Drawing.Size(457, 13);
            this.lblGUICredsInstallAccDesc.TabIndex = 91;
            this.lblGUICredsInstallAccDesc.Text = "Identity used to create Azure PaaS recources. Only needed during the setup or upd" +
    "ate process.\r\n";
            // 
            // pictureBox6
            // 
            this.pictureBox6.Image = global::App.ControlPanel.Properties.Resources.Azure_Active_Directory;
            this.pictureBox6.Location = new System.Drawing.Point(0, 22);
            this.pictureBox6.Name = "pictureBox6";
            this.pictureBox6.Size = new System.Drawing.Size(56, 56);
            this.pictureBox6.TabIndex = 90;
            this.pictureBox6.TabStop = false;
            // 
            // lblGUICredsDescr
            // 
            this.lblGUICredsDescr.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUICredsDescr.Location = new System.Drawing.Point(62, 33);
            this.lblGUICredsDescr.Name = "lblGUICredsDescr";
            this.lblGUICredsDescr.Size = new System.Drawing.Size(456, 42);
            this.lblGUICredsDescr.TabIndex = 89;
            this.lblGUICredsDescr.Text = "We need three application registrations in Azure AD: one to setup Azure resources" +
    " with and two others to allow web-jobs to read activity data from Office 365 and" +
    " Azure. ";
            // 
            // lblGUICredsHeader
            // 
            this.lblGUICredsHeader.AutoSize = true;
            this.lblGUICredsHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUICredsHeader.Location = new System.Drawing.Point(-4, 0);
            this.lblGUICredsHeader.Name = "lblGUICredsHeader";
            this.lblGUICredsHeader.Size = new System.Drawing.Size(137, 19);
            this.lblGUICredsHeader.TabIndex = 88;
            this.lblGUICredsHeader.Text = "System Credentials";
            // 
            // runtimeO365AccountDetails
            // 
            this.runtimeO365AccountDetails.ContextName = "Activity API registration";
            this.runtimeO365AccountDetails.Location = new System.Drawing.Point(-4, 276);
            this.runtimeO365AccountDetails.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.runtimeO365AccountDetails.Name = "runtimeO365AccountDetails";
            this.runtimeO365AccountDetails.Size = new System.Drawing.Size(523, 75);
            this.runtimeO365AccountDetails.TabIndex = 85;
            // 
            // lblGUICredsRuntimeAccountOffice
            // 
            this.lblGUICredsRuntimeAccountOffice.AutoSize = true;
            this.lblGUICredsRuntimeAccountOffice.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUICredsRuntimeAccountOffice.Location = new System.Drawing.Point(-4, 231);
            this.lblGUICredsRuntimeAccountOffice.Name = "lblGUICredsRuntimeAccountOffice";
            this.lblGUICredsRuntimeAccountOffice.Size = new System.Drawing.Size(254, 19);
            this.lblGUICredsRuntimeAccountOffice.TabIndex = 87;
            this.lblGUICredsRuntimeAccountOffice.Text = "Office 365 Runtime Service Principal";
            // 
            // installAccountControl
            // 
            this.installAccountControl.ContextName = "Installation Service Principal";
            this.installAccountControl.Location = new System.Drawing.Point(-4, 133);
            this.installAccountControl.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.installAccountControl.Name = "installAccountControl";
            this.installAccountControl.Size = new System.Drawing.Size(523, 77);
            this.installAccountControl.TabIndex = 84;
            // 
            // lblGUICredsInstallAccHeader
            // 
            this.lblGUICredsInstallAccHeader.AutoSize = true;
            this.lblGUICredsInstallAccHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUICredsInstallAccHeader.Location = new System.Drawing.Point(-4, 89);
            this.lblGUICredsInstallAccHeader.Name = "lblGUICredsInstallAccHeader";
            this.lblGUICredsInstallAccHeader.Size = new System.Drawing.Size(202, 19);
            this.lblGUICredsInstallAccHeader.TabIndex = 86;
            this.lblGUICredsInstallAccHeader.Text = "Installation Service Principal";
            // 
            // SystemCredentialsControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblGUICredsRuntimeAccOfficeDesc);
            this.Controls.Add(this.lblGUICredsInstallAccDesc);
            this.Controls.Add(this.pictureBox6);
            this.Controls.Add(this.lblGUICredsDescr);
            this.Controls.Add(this.lblGUICredsHeader);
            this.Controls.Add(this.runtimeO365AccountDetails);
            this.Controls.Add(this.lblGUICredsRuntimeAccountOffice);
            this.Controls.Add(this.installAccountControl);
            this.Controls.Add(this.lblGUICredsInstallAccHeader);
            this.Name = "SystemCredentialsControl";
            this.Size = new System.Drawing.Size(524, 386);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox6)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblGUICredsRuntimeAccOfficeDesc;
        private System.Windows.Forms.Label lblGUICredsInstallAccDesc;
        private System.Windows.Forms.PictureBox pictureBox6;
        private System.Windows.Forms.Label lblGUICredsDescr;
        private System.Windows.Forms.Label lblGUICredsHeader;
        private Controls.AppLoginDetailsControl runtimeO365AccountDetails;
        private System.Windows.Forms.Label lblGUICredsRuntimeAccountOffice;
        private Controls.AppLoginDetailsControl installAccountControl;
        private System.Windows.Forms.Label lblGUICredsInstallAccHeader;
    }
}
