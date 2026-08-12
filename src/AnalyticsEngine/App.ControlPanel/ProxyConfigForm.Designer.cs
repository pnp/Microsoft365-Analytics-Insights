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
            this.chkBasicAuthentication = new System.Windows.Forms.CheckBox();
            this.label5 = new System.Windows.Forms.Label();
            this.txtProxyPassword = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.txtProxyUsername = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.txtProxyPort = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.txtProxyHost = new System.Windows.Forms.TextBox();
            this.chkProxy = new System.Windows.Forms.CheckBox();
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
            this.grpProxy.Controls.Add(this.chkBasicAuthentication);
            this.grpProxy.Controls.Add(this.label5);
            this.grpProxy.Controls.Add(this.txtProxyPassword);
            this.grpProxy.Controls.Add(this.label4);
            this.grpProxy.Controls.Add(this.txtProxyUsername);
            this.grpProxy.Controls.Add(this.label3);
            this.grpProxy.Controls.Add(this.txtProxyPort);
            this.grpProxy.Controls.Add(this.label2);
            this.grpProxy.Controls.Add(this.txtProxyHost);
            this.grpProxy.Location = new System.Drawing.Point(38, 142);
            this.grpProxy.Name = "grpProxy";
            this.grpProxy.Size = new System.Drawing.Size(472, 226);
            this.grpProxy.TabIndex = 110;
            this.grpProxy.TabStop = false;
            this.grpProxy.Text = "HTTPS deployment proxy:";
            // 
            // chkBasicAuthentication
            // 
            this.chkBasicAuthentication.AutoSize = true;
            this.chkBasicAuthentication.Location = new System.Drawing.Point(30, 120);
            this.chkBasicAuthentication.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
            this.chkBasicAuthentication.Name = "chkBasicAuthentication";
            this.chkBasicAuthentication.Size = new System.Drawing.Size(166, 17);
            this.chkBasicAuthentication.TabIndex = 112;
            this.chkBasicAuthentication.Text = "Use username and password";
            this.chkBasicAuthentication.UseVisualStyleBackColor = true;
            this.chkBasicAuthentication.CheckedChanged += new System.EventHandler(this.opAuth_CheckedChanged);
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
            // txtProxyPassword
            // 
            this.txtProxyPassword.Location = new System.Drawing.Point(111, 168);
            this.txtProxyPassword.Name = "txtProxyPassword";
            this.txtProxyPassword.PasswordChar = '*';
            this.txtProxyPassword.Size = new System.Drawing.Size(343, 20);
            this.txtProxyPassword.TabIndex = 109;
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
            // txtProxyUsername
            // 
            this.txtProxyUsername.Location = new System.Drawing.Point(111, 142);
            this.txtProxyUsername.Name = "txtProxyUsername";
            this.txtProxyUsername.Size = new System.Drawing.Size(343, 20);
            this.txtProxyUsername.TabIndex = 107;
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
            // txtProxyPort
            // 
            this.txtProxyPort.Location = new System.Drawing.Point(111, 62);
            this.txtProxyPort.Name = "txtProxyPort";
            this.txtProxyPort.Size = new System.Drawing.Size(78, 20);
            this.txtProxyPort.TabIndex = 105;
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
            // txtProxyHost
            //
            this.txtProxyHost.Location = new System.Drawing.Point(111, 36);
            this.txtProxyHost.Name = "txtProxyHost";
            this.txtProxyHost.Size = new System.Drawing.Size(343, 20);
            this.txtProxyHost.TabIndex = 103;
            //
            // chkProxy
            //
            this.chkProxy.AutoSize = true;
            this.chkProxy.Location = new System.Drawing.Point(38, 105);
            this.chkProxy.Name = "chkProxy";
            this.chkProxy.Size = new System.Drawing.Size(156, 17);
            this.chkProxy.TabIndex = 109;
            this.chkProxy.Text = "Use proxy for deployment";
            this.chkProxy.UseVisualStyleBackColor = true;
            this.chkProxy.CheckedChanged += new System.EventHandler(this.chkProxy_CheckedChanged);
            // 
            // lblGUIConnectionDesc
            // 
            this.lblGUIConnectionDesc.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUIConnectionDesc.Location = new System.Drawing.Point(78, 31);
            this.lblGUIConnectionDesc.Name = "lblGUIConnectionDesc";
            this.lblGUIConnectionDesc.Size = new System.Drawing.Size(458, 42);
            this.lblGUIConnectionDesc.TabIndex = 106;
            this.lblGUIConnectionDesc.Text = "Configure an HTTP proxy if this machine requires one to reach the App Service SCM " +
    "endpoint over HTTPS.";
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
            this.Controls.Add(this.chkProxy);
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
        private System.Windows.Forms.TextBox txtProxyPassword;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.TextBox txtProxyUsername;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TextBox txtProxyPort;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox txtProxyHost;
        private System.Windows.Forms.CheckBox chkProxy;
        private System.Windows.Forms.Label lblGUIConnectionDesc;
        private System.Windows.Forms.Label lblGUIConnectionHeader;
        private System.Windows.Forms.PictureBox pictureBox9;
        private System.Windows.Forms.CheckBox chkBasicAuthentication;
    }
}