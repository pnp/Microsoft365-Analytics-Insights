namespace App.ControlPanel
{
    partial class ProxyConfigForm
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
            this.btnCancel = new System.Windows.Forms.Button();
            this.btnOk = new System.Windows.Forms.Button();
            this.grpProxy = new System.Windows.Forms.GroupBox();
            this.opUsernameAndPassword = new System.Windows.Forms.RadioButton();
            this.label5 = new System.Windows.Forms.Label();
            this.txtFtpProxyPassword = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.txtFtpProxyUsername = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.txtFtpProxyPort = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.txtFtpProxyHost = new System.Windows.Forms.TextBox();
            this.chkFtpProxy = new System.Windows.Forms.CheckBox();
            this.chkFtpPassiveMode = new System.Windows.Forms.CheckBox();
            this.chkUseFTPS = new System.Windows.Forms.CheckBox();
            this.lblGUIConnectionDesc = new System.Windows.Forms.Label();
            this.lblGUIConnectionHeader = new System.Windows.Forms.Label();
            this.pictureBox9 = new System.Windows.Forms.PictureBox();
            this.grpProxy.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox9)).BeginInit();
            this.SuspendLayout();
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(435, 456);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 23);
            this.btnCancel.TabIndex = 0;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // btnOk
            // 
            this.btnOk.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOk.Location = new System.Drawing.Point(339, 456);
            this.btnOk.Name = "btnOk";
            this.btnOk.Size = new System.Drawing.Size(75, 23);
            this.btnOk.TabIndex = 1;
            this.btnOk.Text = "OK";
            this.btnOk.UseVisualStyleBackColor = true;
            this.btnOk.Click += new System.EventHandler(this.btnOk_Click);
            // 
            // grpProxy
            // 
            this.grpProxy.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpProxy.Controls.Add(this.opUsernameAndPassword);
            this.grpProxy.Controls.Add(this.label5);
            this.grpProxy.Controls.Add(this.txtFtpProxyPassword);
            this.grpProxy.Controls.Add(this.label4);
            this.grpProxy.Controls.Add(this.txtFtpProxyUsername);
            this.grpProxy.Controls.Add(this.label3);
            this.grpProxy.Controls.Add(this.txtFtpProxyPort);
            this.grpProxy.Controls.Add(this.label2);
            this.grpProxy.Controls.Add(this.txtFtpProxyHost);
            this.grpProxy.Location = new System.Drawing.Point(38, 215);
            this.grpProxy.Name = "grpProxy";
            this.grpProxy.Size = new System.Drawing.Size(472, 226);
            this.grpProxy.TabIndex = 110;
            this.grpProxy.TabStop = false;
            this.grpProxy.Text = "HTTP Proxy (in preview):";
            // 
            // opUsernameAndPassword
            // 
            this.opUsernameAndPassword.AutoSize = true;
            this.opUsernameAndPassword.Location = new System.Drawing.Point(30, 120);
            this.opUsernameAndPassword.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
            this.opUsernameAndPassword.Name = "opUsernameAndPassword";
            this.opUsernameAndPassword.Size = new System.Drawing.Size(124, 17);
            this.opUsernameAndPassword.TabIndex = 112;
            this.opUsernameAndPassword.TabStop = true;
            this.opUsernameAndPassword.Text = "Basic authentication:";
            this.opUsernameAndPassword.UseVisualStyleBackColor = true;
            this.opUsernameAndPassword.CheckedChanged += new System.EventHandler(this.opAuth_CheckedChanged);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(27, 171);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(56, 13);
            this.label5.TabIndex = 110;
            this.label5.Text = "Password:";
            // 
            // txtFtpProxyPassword
            // 
            this.txtFtpProxyPassword.Location = new System.Drawing.Point(111, 168);
            this.txtFtpProxyPassword.Name = "txtFtpProxyPassword";
            this.txtFtpProxyPassword.PasswordChar = '*';
            this.txtFtpProxyPassword.Size = new System.Drawing.Size(343, 20);
            this.txtFtpProxyPassword.TabIndex = 109;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(27, 145);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(58, 13);
            this.label4.TabIndex = 108;
            this.label4.Text = "Username:";
            // 
            // txtFtpProxyUsername
            // 
            this.txtFtpProxyUsername.Location = new System.Drawing.Point(111, 142);
            this.txtFtpProxyUsername.Name = "txtFtpProxyUsername";
            this.txtFtpProxyUsername.Size = new System.Drawing.Size(343, 20);
            this.txtFtpProxyUsername.TabIndex = 107;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(27, 65);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(29, 13);
            this.label3.TabIndex = 106;
            this.label3.Text = "Port:";
            // 
            // txtFtpProxyPort
            // 
            this.txtFtpProxyPort.Location = new System.Drawing.Point(111, 62);
            this.txtFtpProxyPort.Name = "txtFtpProxyPort";
            this.txtFtpProxyPort.Size = new System.Drawing.Size(78, 20);
            this.txtFtpProxyPort.TabIndex = 105;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(27, 39);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(32, 13);
            this.label2.TabIndex = 104;
            this.label2.Text = "Host:";
            // 
            // txtFtpProxyHost
            // 
            this.txtFtpProxyHost.Location = new System.Drawing.Point(111, 36);
            this.txtFtpProxyHost.Name = "txtFtpProxyHost";
            this.txtFtpProxyHost.Size = new System.Drawing.Size(343, 20);
            this.txtFtpProxyHost.TabIndex = 103;
            // 
            // chkFtpProxy
            // 
            this.chkFtpProxy.AutoSize = true;
            this.chkFtpProxy.Checked = true;
            this.chkFtpProxy.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkFtpProxy.Location = new System.Drawing.Point(38, 178);
            this.chkFtpProxy.Name = "chkFtpProxy";
            this.chkFtpProxy.Size = new System.Drawing.Size(74, 17);
            this.chkFtpProxy.TabIndex = 109;
            this.chkFtpProxy.Text = "FTP proxy";
            this.chkFtpProxy.UseVisualStyleBackColor = true;
            this.chkFtpProxy.CheckedChanged += new System.EventHandler(this.chkFtpProxy_CheckedChanged);
            // 
            // chkFtpPassiveMode
            // 
            this.chkFtpPassiveMode.AutoSize = true;
            this.chkFtpPassiveMode.Checked = true;
            this.chkFtpPassiveMode.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkFtpPassiveMode.Location = new System.Drawing.Point(38, 141);
            this.chkFtpPassiveMode.Name = "chkFtpPassiveMode";
            this.chkFtpPassiveMode.Size = new System.Drawing.Size(114, 17);
            this.chkFtpPassiveMode.TabIndex = 108;
            this.chkFtpPassiveMode.Text = "FTP passive mode";
            this.chkFtpPassiveMode.UseVisualStyleBackColor = true;
            // 
            // chkUseFTPS
            // 
            this.chkUseFTPS.AutoSize = true;
            this.chkUseFTPS.Checked = true;
            this.chkUseFTPS.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkUseFTPS.Enabled = false;
            this.chkUseFTPS.Location = new System.Drawing.Point(38, 105);
            this.chkUseFTPS.Name = "chkUseFTPS";
            this.chkUseFTPS.Size = new System.Drawing.Size(123, 17);
            this.chkUseFTPS.TabIndex = 107;
            this.chkUseFTPS.Text = "Use FTPS (port 990)";
            this.chkUseFTPS.UseVisualStyleBackColor = true;
            // 
            // lblGUIConnectionDesc
            // 
            this.lblGUIConnectionDesc.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUIConnectionDesc.Location = new System.Drawing.Point(78, 31);
            this.lblGUIConnectionDesc.Name = "lblGUIConnectionDesc";
            this.lblGUIConnectionDesc.Size = new System.Drawing.Size(458, 42);
            this.lblGUIConnectionDesc.TabIndex = 106;
            this.lblGUIConnectionDesc.Text = "Connection configuration may need changing here, depending on your network restri" +
    "ctions.\r\n";
            // 
            // lblGUIConnectionHeader
            // 
            this.lblGUIConnectionHeader.AutoSize = true;
            this.lblGUIConnectionHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIConnectionHeader.Location = new System.Drawing.Point(12, 9);
            this.lblGUIConnectionHeader.Name = "lblGUIConnectionHeader";
            this.lblGUIConnectionHeader.Size = new System.Drawing.Size(183, 19);
            this.lblGUIConnectionHeader.TabIndex = 105;
            this.lblGUIConnectionHeader.Text = "Connection Configuration";
            // 
            // pictureBox9
            // 
            this.pictureBox9.Image = global::App.ControlPanel.Properties.Resources.AppService;
            this.pictureBox9.Location = new System.Drawing.Point(16, 31);
            this.pictureBox9.Name = "pictureBox9";
            this.pictureBox9.Size = new System.Drawing.Size(56, 56);
            this.pictureBox9.TabIndex = 104;
            this.pictureBox9.TabStop = false;
            // 
            // ProxyConfigForm
            // 
            this.AcceptButton = this.btnOk;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(548, 491);
            this.Controls.Add(this.grpProxy);
            this.Controls.Add(this.chkFtpProxy);
            this.Controls.Add(this.chkFtpPassiveMode);
            this.Controls.Add(this.chkUseFTPS);
            this.Controls.Add(this.lblGUIConnectionDesc);
            this.Controls.Add(this.lblGUIConnectionHeader);
            this.Controls.Add(this.pictureBox9);
            this.Controls.Add(this.btnOk);
            this.Controls.Add(this.btnCancel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "ProxyConfigForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Proxy Configuration";
            this.Load += new System.EventHandler(this.ProxyConfigForm_Load);
            this.grpProxy.ResumeLayout(false);
            this.grpProxy.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox9)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Button btnOk;
        private System.Windows.Forms.GroupBox grpProxy;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.TextBox txtFtpProxyPassword;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.TextBox txtFtpProxyUsername;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TextBox txtFtpProxyPort;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox txtFtpProxyHost;
        private System.Windows.Forms.CheckBox chkFtpProxy;
        private System.Windows.Forms.CheckBox chkFtpPassiveMode;
        private System.Windows.Forms.CheckBox chkUseFTPS;
        private System.Windows.Forms.Label lblGUIConnectionDesc;
        private System.Windows.Forms.Label lblGUIConnectionHeader;
        private System.Windows.Forms.PictureBox pictureBox9;
        private System.Windows.Forms.RadioButton opUsernameAndPassword;
    }
}