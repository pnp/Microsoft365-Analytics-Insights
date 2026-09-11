namespace App.ControlPanel
{
    partial class SqlDatabaseUsersForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.lblIntro = new System.Windows.Forms.Label();
            this.gridUsers = new System.Windows.Forms.DataGridView();
            this.colLogin = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colObjectId = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colPrincipalType = new System.Windows.Forms.DataGridViewComboBoxColumn();
            this.btnOk = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.lblHint = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.gridUsers)).BeginInit();
            this.SuspendLayout();
            // 
            // lblIntro
            // 
            this.lblIntro.Location = new System.Drawing.Point(12, 9);
            this.lblIntro.Name = "lblIntro";
            this.lblIntro.Size = new System.Drawing.Size(660, 88);
            this.lblIntro.TabIndex = 0;
            this.lblIntro.Text = "People listed here get their own access to the analytics database, as contained da" +
                "tabase users granted db_owner. The installer creates them on every run.\r\n\r\nDo NOT" +
                " give people access by changing the SQL Server\'s Microsoft Entra administrator ins" +
                "tead. Azure allows only ONE administrator per server and it is the installer\'s ser" +
                "vice principal, so replacing it breaks the next database upgrade.";
            // 
            // gridUsers
            // 
            this.gridUsers.AllowUserToResizeRows = false;
            this.gridUsers.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.gridUsers.AutoGenerateColumns = false;
            this.gridUsers.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.gridUsers.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colLogin,
            this.colObjectId,
            this.colPrincipalType});
            this.gridUsers.Location = new System.Drawing.Point(12, 105);
            this.gridUsers.Name = "gridUsers";
            this.gridUsers.RowHeadersWidth = 25;
            this.gridUsers.Size = new System.Drawing.Size(660, 255);
            this.gridUsers.TabIndex = 1;
            // 
            // colLogin
            // 
            this.colLogin.DataPropertyName = "Login";
            this.colLogin.HeaderText = "Login (UPN or group name)";
            this.colLogin.Name = "colLogin";
            this.colLogin.Width = 240;
            // 
            // colObjectId
            // 
            this.colObjectId.DataPropertyName = "ObjectId";
            this.colObjectId.HeaderText = "Object ID (optional)";
            this.colObjectId.Name = "colObjectId";
            this.colObjectId.Width = 260;
            // 
            // colPrincipalType
            // 
            this.colPrincipalType.DataPropertyName = "PrincipalType";
            this.colPrincipalType.HeaderText = "Type";
            this.colPrincipalType.Name = "colPrincipalType";
            this.colPrincipalType.Width = 90;
            // 
            // btnOk
            // 
            this.btnOk.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOk.Location = new System.Drawing.Point(516, 425);
            this.btnOk.Name = "btnOk";
            this.btnOk.Size = new System.Drawing.Size(75, 23);
            this.btnOk.TabIndex = 2;
            this.btnOk.Text = "OK";
            this.btnOk.UseVisualStyleBackColor = true;
            this.btnOk.Click += new System.EventHandler(this.btnOk_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(597, 425);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 23);
            this.btnCancel.TabIndex = 3;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // lblHint
            // 
            this.lblHint.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblHint.Location = new System.Drawing.Point(12, 370);
            this.lblHint.Name = "lblHint";
            this.lblHint.Size = new System.Drawing.Size(660, 36);
            this.lblHint.TabIndex = 4;
            this.lblHint.Text = "Object ID is only needed if the installer cannot look the name up in Microsoft Ent" +
                "ra ID. Find it on the user\'s or group\'s overview page in the Azure portal.";
            // 
            // SqlDatabaseUsersForm
            // 
            this.AcceptButton = this.btnOk;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(684, 460);
            this.Controls.Add(this.lblHint);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOk);
            this.Controls.Add(this.gridUsers);
            this.Controls.Add(this.lblIntro);
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.MinimumSize = new System.Drawing.Size(600, 430);
            this.Name = "SqlDatabaseUsersForm";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "SQL database users";
            ((System.ComponentModel.ISupportInitialize)(this.gridUsers)).EndInit();
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.Label lblIntro;
        private System.Windows.Forms.DataGridView gridUsers;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLogin;
        private System.Windows.Forms.DataGridViewTextBoxColumn colObjectId;
        private System.Windows.Forms.DataGridViewComboBoxColumn colPrincipalType;
        private System.Windows.Forms.Button btnOk;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Label lblHint;
    }
}
