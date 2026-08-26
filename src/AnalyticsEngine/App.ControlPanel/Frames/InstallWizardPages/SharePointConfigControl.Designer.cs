namespace App.ControlPanel.Frames.InstallWizard
{
    partial class SharePointConfigControl
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
            this.txtSharePointAppCatalog = new System.Windows.Forms.TextBox();
            this.lblGUIAppCatalog = new System.Windows.Forms.Label();
            this.lblGUISharePointTabDesc = new System.Windows.Forms.Label();
            this.lblGUISharePointTabHeader = new System.Windows.Forms.Label();
            this.lblGUISiteCollectionsExample = new System.Windows.Forms.Label();
            this.txtSharePointURLs = new System.Windows.Forms.TextBox();
            this.lblGUISiteCollections = new System.Windows.Forms.Label();
            this.lblGUIShareUninstallHeader = new System.Windows.Forms.Label();
            this.lblGUIShareUninstallText = new System.Windows.Forms.Label();
            this.lblGUIShareAuthDesc = new System.Windows.Forms.Label();
            this.lblGUIShareAuthHeader = new System.Windows.Forms.Label();
            this.lblGUIAuthClientId = new System.Windows.Forms.Label();
            this.txtAuthClientId = new System.Windows.Forms.TextBox();
            this.lblGUIAuthClientIdHelp = new System.Windows.Forms.Label();
            this.lblGUIAuthIntro = new System.Windows.Forms.Label();
            this.lblGUIAuthTenantId = new System.Windows.Forms.Label();
            this.txtAuthTenantId = new System.Windows.Forms.TextBox();
            this.btnUninstall = new System.Windows.Forms.Button();
            this.pictureBox7 = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox7)).BeginInit();
            this.SuspendLayout();
            // 
            // txtSharePointAppCatalog
            // 
            this.txtSharePointAppCatalog.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtSharePointAppCatalog.Location = new System.Drawing.Point(117, 164);
            this.txtSharePointAppCatalog.Name = "txtSharePointAppCatalog";
            this.txtSharePointAppCatalog.Size = new System.Drawing.Size(347, 20);
            this.txtSharePointAppCatalog.TabIndex = 97;
            this.txtSharePointAppCatalog.Text = "txtSharePointAppCatalog";
            // 
            // lblGUIAppCatalog
            // 
            this.lblGUIAppCatalog.AutoSize = true;
            this.lblGUIAppCatalog.Location = new System.Drawing.Point(5, 167);
            this.lblGUIAppCatalog.Name = "lblGUIAppCatalog";
            this.lblGUIAppCatalog.Size = new System.Drawing.Size(92, 13);
            this.lblGUIAppCatalog.TabIndex = 113;
            this.lblGUIAppCatalog.Text = "App catalog URL:";
            // 
            // lblGUISharePointTabDesc
            // 
            this.lblGUISharePointTabDesc.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUISharePointTabDesc.Location = new System.Drawing.Point(69, 33);
            this.lblGUISharePointTabDesc.Name = "lblGUISharePointTabDesc";
            this.lblGUISharePointTabDesc.Size = new System.Drawing.Size(395, 42);
            this.lblGUISharePointTabDesc.TabIndex = 105;
            this.lblGUISharePointTabDesc.Text = "Configure where the tracking JavaScript will be installed in SharePoint and/or si" +
    "tes where audit-logs will be saved (if enabled). You sign in to SharePoint furth" +
    "er down this page.";
            // 
            // lblGUISharePointTabHeader
            // 
            this.lblGUISharePointTabHeader.AutoSize = true;
            this.lblGUISharePointTabHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUISharePointTabHeader.Location = new System.Drawing.Point(3, 0);
            this.lblGUISharePointTabHeader.Name = "lblGUISharePointTabHeader";
            this.lblGUISharePointTabHeader.Size = new System.Drawing.Size(165, 19);
            this.lblGUISharePointTabHeader.TabIndex = 104;
            this.lblGUISharePointTabHeader.Text = "SharePoint Installation";
            // 
            // lblGUISiteCollectionsExample
            // 
            this.lblGUISiteCollectionsExample.AutoSize = true;
            this.lblGUISiteCollectionsExample.Location = new System.Drawing.Point(124, 93);
            this.lblGUISiteCollectionsExample.Name = "lblGUISiteCollectionsExample";
            this.lblGUISiteCollectionsExample.Size = new System.Drawing.Size(229, 13);
            this.lblGUISiteCollectionsExample.TabIndex = 102;
            this.lblGUISiteCollectionsExample.Text = "e.g. https://contoso.sharepoint.com/sites/corp";
            // 
            // txtSharePointURLs
            // 
            this.txtSharePointURLs.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtSharePointURLs.Location = new System.Drawing.Point(117, 109);
            this.txtSharePointURLs.Multiline = true;
            this.txtSharePointURLs.Name = "txtSharePointURLs";
            this.txtSharePointURLs.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtSharePointURLs.Size = new System.Drawing.Size(347, 49);
            this.txtSharePointURLs.TabIndex = 96;
            this.txtSharePointURLs.Text = "txtSharePointURLs";
            // 
            // lblGUISiteCollections
            // 
            this.lblGUISiteCollections.Location = new System.Drawing.Point(4, 109);
            this.lblGUISiteCollections.Name = "lblGUISiteCollections";
            this.lblGUISiteCollections.Size = new System.Drawing.Size(107, 29);
            this.lblGUISiteCollections.TabIndex = 101;
            this.lblGUISiteCollections.Text = "Site-collection root URLs (one per line):";
            // 
            // lblGUIShareAuthHeader
            // 
            this.lblGUIShareAuthHeader.AutoSize = true;
            this.lblGUIShareAuthHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIShareAuthHeader.Location = new System.Drawing.Point(3, 224);
            this.lblGUIShareAuthHeader.Name = "lblGUIShareAuthHeader";
            this.lblGUIShareAuthHeader.Size = new System.Drawing.Size(111, 19);
            this.lblGUIShareAuthHeader.TabIndex = 115;
            this.lblGUIShareAuthHeader.Text = "Authentication";
            // 
            // lblGUIShareAuthDesc
            // 
            this.lblGUIShareAuthDesc.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUIShareAuthDesc.Location = new System.Drawing.Point(4, 249);
            this.lblGUIShareAuthDesc.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblGUIShareAuthDesc.Name = "lblGUIShareAuthDesc";
            this.lblGUIShareAuthDesc.Size = new System.Drawing.Size(460, 32);
            this.lblGUIShareAuthDesc.TabIndex = 116;
            this.lblGUIShareAuthDesc.Text = "The installer opens your web browser to sign in. Use an account that is a SharePo" +
    "int administrator and a site-collection administrator on each site above.";
            // 
            // lblGUIAuthIntro
            // 
            this.lblGUIAuthIntro.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUIAuthIntro.Location = new System.Drawing.Point(4, 284);
            this.lblGUIAuthIntro.Name = "lblGUIAuthIntro";
            this.lblGUIAuthIntro.Size = new System.Drawing.Size(460, 32);
            this.lblGUIAuthIntro.TabIndex = 117;
            this.lblGUIAuthIntro.Text = "There is nothing to register: sign-in uses an app published by Microsoft. Only fi" +
    "ll these in if your tenant blocks that app and you need to use your own.";
            // 
            // lblGUIAuthClientId
            // 
            this.lblGUIAuthClientId.Location = new System.Drawing.Point(4, 325);
            this.lblGUIAuthClientId.Name = "lblGUIAuthClientId";
            this.lblGUIAuthClientId.Size = new System.Drawing.Size(105, 20);
            this.lblGUIAuthClientId.TabIndex = 118;
            this.lblGUIAuthClientId.Text = "Sign-in app ID:";
            // 
            // txtAuthClientId
            // 
            this.txtAuthClientId.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtAuthClientId.Location = new System.Drawing.Point(117, 322);
            this.txtAuthClientId.Name = "txtAuthClientId";
            this.txtAuthClientId.Size = new System.Drawing.Size(347, 20);
            this.txtAuthClientId.TabIndex = 119;
            // 
            // lblGUIAuthTenantId
            // 
            this.lblGUIAuthTenantId.Location = new System.Drawing.Point(4, 351);
            this.lblGUIAuthTenantId.Name = "lblGUIAuthTenantId";
            this.lblGUIAuthTenantId.Size = new System.Drawing.Size(105, 20);
            this.lblGUIAuthTenantId.TabIndex = 120;
            this.lblGUIAuthTenantId.Text = "Sign-in tenant:";
            // 
            // txtAuthTenantId
            // 
            this.txtAuthTenantId.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtAuthTenantId.Location = new System.Drawing.Point(117, 348);
            this.txtAuthTenantId.Name = "txtAuthTenantId";
            this.txtAuthTenantId.Size = new System.Drawing.Size(347, 20);
            this.txtAuthTenantId.TabIndex = 121;
            // 
            // lblGUIAuthClientIdHelp
            // 
            this.lblGUIAuthClientIdHelp.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUIAuthClientIdHelp.Location = new System.Drawing.Point(117, 374);
            this.lblGUIAuthClientIdHelp.Name = "lblGUIAuthClientIdHelp";
            this.lblGUIAuthClientIdHelp.Size = new System.Drawing.Size(347, 18);
            this.lblGUIAuthClientIdHelp.TabIndex = 122;
            this.lblGUIAuthClientIdHelp.Text = "If you set an app ID, the tenant is required too.";
            // 
            // lblGUIShareUninstallHeader
            // 
            this.lblGUIShareUninstallHeader.AutoSize = true;
            this.lblGUIShareUninstallHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIShareUninstallHeader.Location = new System.Drawing.Point(4, 408);
            this.lblGUIShareUninstallHeader.Name = "lblGUIShareUninstallHeader";
            this.lblGUIShareUninstallHeader.Size = new System.Drawing.Size(69, 19);
            this.lblGUIShareUninstallHeader.TabIndex = 108;
            this.lblGUIShareUninstallHeader.Text = "Uninstall";
            // 
            // lblGUIShareUninstallText
            // 
            this.lblGUIShareUninstallText.AutoSize = true;
            this.lblGUIShareUninstallText.Location = new System.Drawing.Point(5, 433);
            this.lblGUIShareUninstallText.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblGUIShareUninstallText.Name = "lblGUIShareUninstallText";
            this.lblGUIShareUninstallText.Size = new System.Drawing.Size(402, 13);
            this.lblGUIShareUninstallText.TabIndex = 109;
            this.lblGUIShareUninstallText.Text = "You can remove the tracker from the above listed sites by clicking the button bel" +
    "ow.";
            // 
            // btnUninstall
            // 
            this.btnUninstall.Image = global::App.ControlPanel.Properties.Resources.trash_can;
            this.btnUninstall.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.btnUninstall.Location = new System.Drawing.Point(9, 453);
            this.btnUninstall.Name = "btnUninstall";
            this.btnUninstall.Size = new System.Drawing.Size(159, 30);
            this.btnUninstall.TabIndex = 114;
            this.btnUninstall.Text = "Remove AITracker\r\n";
            this.btnUninstall.UseVisualStyleBackColor = true;
            this.btnUninstall.Click += new System.EventHandler(this.btnUninstall_Click);
            // 
            // pictureBox7
            // 
            this.pictureBox7.Image = global::App.ControlPanel.Properties.Resources.SharePoint;
            this.pictureBox7.Location = new System.Drawing.Point(7, 22);
            this.pictureBox7.Name = "pictureBox7";
            this.pictureBox7.Size = new System.Drawing.Size(56, 56);
            this.pictureBox7.TabIndex = 103;
            this.pictureBox7.TabStop = false;
            // 
            // SharePointConfigControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblGUIShareAuthDesc);
            this.Controls.Add(this.lblGUIShareAuthHeader);
            this.Controls.Add(this.lblGUIAuthClientId);
            this.Controls.Add(this.txtAuthClientId);
            this.Controls.Add(this.lblGUIAuthIntro);
            this.Controls.Add(this.lblGUIAuthClientIdHelp);
            this.Controls.Add(this.lblGUIAuthTenantId);
            this.Controls.Add(this.txtAuthTenantId);
            this.Controls.Add(this.btnUninstall);
            this.Controls.Add(this.txtSharePointAppCatalog);
            this.Controls.Add(this.lblGUIAppCatalog);
            this.Controls.Add(this.lblGUIShareUninstallText);
            this.Controls.Add(this.lblGUIShareUninstallHeader);
            this.Controls.Add(this.lblGUISharePointTabDesc);
            this.Controls.Add(this.lblGUISharePointTabHeader);
            this.Controls.Add(this.lblGUISiteCollectionsExample);
            this.Controls.Add(this.txtSharePointURLs);
            this.Controls.Add(this.lblGUISiteCollections);
            this.Controls.Add(this.pictureBox7);
            this.Name = "SharePointConfigControl";
            this.Size = new System.Drawing.Size(467, 454);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox7)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox txtSharePointAppCatalog;
        private System.Windows.Forms.Label lblGUIAppCatalog;
        private System.Windows.Forms.Label lblGUISharePointTabDesc;
        private System.Windows.Forms.Label lblGUISharePointTabHeader;
        private System.Windows.Forms.Label lblGUISiteCollectionsExample;
        private System.Windows.Forms.TextBox txtSharePointURLs;
        private System.Windows.Forms.Label lblGUISiteCollections;
        private System.Windows.Forms.PictureBox pictureBox7;
        private System.Windows.Forms.Label lblGUIShareUninstallHeader;
        private System.Windows.Forms.Label lblGUIShareUninstallText;
        private System.Windows.Forms.Button btnUninstall;
        private System.Windows.Forms.Label lblGUIShareAuthDesc;
        private System.Windows.Forms.Label lblGUIShareAuthHeader;
        private System.Windows.Forms.Label lblGUIAuthClientId;
        private System.Windows.Forms.TextBox txtAuthClientId;
        private System.Windows.Forms.Label lblGUIAuthClientIdHelp;
        private System.Windows.Forms.Label lblGUIAuthIntro;
        private System.Windows.Forms.Label lblGUIAuthTenantId;
        private System.Windows.Forms.TextBox txtAuthTenantId;
    }
}
