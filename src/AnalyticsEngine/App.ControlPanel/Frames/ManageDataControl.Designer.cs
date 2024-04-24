namespace App.ControlPanel.Frames
{
    partial class ManageDataControl
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
            System.Windows.Forms.ListViewItem listViewItem5 = new System.Windows.Forms.ListViewItem("user1@testing.com");
            System.Windows.Forms.ListViewItem listViewItem6 = new System.Windows.Forms.ListViewItem("user2@testing.com");
            System.Windows.Forms.ListViewItem listViewItem7 = new System.Windows.Forms.ListViewItem("2018-06-06 11:49:46.447");
            System.Windows.Forms.ListViewItem listViewItem8 = new System.Windows.Forms.ListViewItem("2018-06-06 12:49:46.447");
            this.tabs = new System.Windows.Forms.TabControl();
            this.tabConnect = new System.Windows.Forms.TabPage();
            this.lblConnectionStatus = new System.Windows.Forms.Label();
            this.btnConnect = new System.Windows.Forms.Button();
            this.txtConnectionString = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.tabUsers = new System.Windows.Forms.TabPage();
            this.lblUsersFound = new System.Windows.Forms.Label();
            this.lstUsers = new System.Windows.Forms.ListView();
            this.lblUserStatus = new System.Windows.Forms.Label();
            this.btnSearchUser = new System.Windows.Forms.Button();
            this.txtUserSearch = new System.Windows.Forms.TextBox();
            this.tabImportLog = new System.Windows.Forms.TabPage();
            this.lblImportLogsFound = new System.Windows.Forms.Label();
            this.lstImportLogs = new System.Windows.Forms.ListView();
            this.columnHeader1 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.columnHeader2 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.lblImportStatus = new System.Windows.Forms.Label();
            this.btnSearchLogs = new System.Windows.Forms.Button();
            this.txtImportSearch = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.txtDaysBack = new System.Windows.Forms.NumericUpDown();
            this.label1 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.tabs.SuspendLayout();
            this.tabConnect.SuspendLayout();
            this.tabUsers.SuspendLayout();
            this.tabImportLog.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.txtDaysBack)).BeginInit();
            this.SuspendLayout();
            // 
            // tabs
            // 
            this.tabs.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabs.Controls.Add(this.tabConnect);
            this.tabs.Controls.Add(this.tabUsers);
            this.tabs.Controls.Add(this.tabImportLog);
            this.tabs.Location = new System.Drawing.Point(0, 30);
            this.tabs.Name = "tabs";
            this.tabs.SelectedIndex = 0;
            this.tabs.Size = new System.Drawing.Size(448, 379);
            this.tabs.TabIndex = 0;
            // 
            // tabConnect
            // 
            this.tabConnect.Controls.Add(this.lblConnectionStatus);
            this.tabConnect.Controls.Add(this.btnConnect);
            this.tabConnect.Controls.Add(this.txtConnectionString);
            this.tabConnect.Controls.Add(this.label2);
            this.tabConnect.Location = new System.Drawing.Point(4, 22);
            this.tabConnect.Name = "tabConnect";
            this.tabConnect.Padding = new System.Windows.Forms.Padding(3);
            this.tabConnect.Size = new System.Drawing.Size(440, 353);
            this.tabConnect.TabIndex = 0;
            this.tabConnect.Text = "Connect";
            this.tabConnect.UseVisualStyleBackColor = true;
            // 
            // lblConnectionStatus
            // 
            this.lblConnectionStatus.AutoSize = true;
            this.lblConnectionStatus.Location = new System.Drawing.Point(9, 71);
            this.lblConnectionStatus.Name = "lblConnectionStatus";
            this.lblConnectionStatus.Size = new System.Drawing.Size(101, 13);
            this.lblConnectionStatus.TabIndex = 4;
            this.lblConnectionStatus.Text = "lblConnectionStatus";
            // 
            // btnConnect
            // 
            this.btnConnect.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnConnect.Location = new System.Drawing.Point(337, 60);
            this.btnConnect.Name = "btnConnect";
            this.btnConnect.Size = new System.Drawing.Size(88, 35);
            this.btnConnect.TabIndex = 3;
            this.btnConnect.Text = "Connect\r\n";
            this.btnConnect.UseVisualStyleBackColor = true;
            this.btnConnect.Click += new System.EventHandler(this.btnConnect_Click);
            // 
            // txtConnectionString
            // 
            this.txtConnectionString.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtConnectionString.Location = new System.Drawing.Point(12, 34);
            this.txtConnectionString.Name = "txtConnectionString";
            this.txtConnectionString.Size = new System.Drawing.Size(412, 20);
            this.txtConnectionString.TabIndex = 2;
            this.txtConnectionString.Text = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=TestingSPOInsights;Integrated " +
    "Security=true;MultipleActiveResultSets=True;";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(9, 14);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(92, 13);
            this.label2.TabIndex = 1;
            this.label2.Text = "Connection string:";
            // 
            // tabUsers
            // 
            this.tabUsers.Controls.Add(this.label4);
            this.tabUsers.Controls.Add(this.lblUsersFound);
            this.tabUsers.Controls.Add(this.lstUsers);
            this.tabUsers.Controls.Add(this.lblUserStatus);
            this.tabUsers.Controls.Add(this.btnSearchUser);
            this.tabUsers.Controls.Add(this.txtUserSearch);
            this.tabUsers.Location = new System.Drawing.Point(4, 22);
            this.tabUsers.Name = "tabUsers";
            this.tabUsers.Padding = new System.Windows.Forms.Padding(3);
            this.tabUsers.Size = new System.Drawing.Size(440, 353);
            this.tabUsers.TabIndex = 1;
            this.tabUsers.Text = "Users";
            this.tabUsers.UseVisualStyleBackColor = true;
            // 
            // lblUsersFound
            // 
            this.lblUsersFound.AutoSize = true;
            this.lblUsersFound.Location = new System.Drawing.Point(26, 100);
            this.lblUsersFound.Name = "lblUsersFound";
            this.lblUsersFound.Size = new System.Drawing.Size(67, 13);
            this.lblUsersFound.TabIndex = 5;
            this.lblUsersFound.Text = "Users found:";
            // 
            // lstUsers
            // 
            this.lstUsers.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lstUsers.Items.AddRange(new System.Windows.Forms.ListViewItem[] {
            listViewItem5,
            listViewItem6});
            this.lstUsers.Location = new System.Drawing.Point(29, 116);
            this.lstUsers.Name = "lstUsers";
            this.lstUsers.Size = new System.Drawing.Size(302, 86);
            this.lstUsers.TabIndex = 4;
            this.lstUsers.UseCompatibleStateImageBehavior = false;
            this.lstUsers.View = System.Windows.Forms.View.List;
            // 
            // lblUserStatus
            // 
            this.lblUserStatus.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.lblUserStatus.AutoSize = true;
            this.lblUserStatus.Location = new System.Drawing.Point(22, 337);
            this.lblUserStatus.Name = "lblUserStatus";
            this.lblUserStatus.Size = new System.Drawing.Size(69, 13);
            this.lblUserStatus.TabIndex = 3;
            this.lblUserStatus.Text = "lblUserStatus";
            // 
            // btnSearchUser
            // 
            this.btnSearchUser.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnSearchUser.Image = global::App.ControlPanel.Properties.Resources.Search_glyph71GrayNoHalo_16x;
            this.btnSearchUser.Location = new System.Drawing.Point(338, 48);
            this.btnSearchUser.Name = "btnSearchUser";
            this.btnSearchUser.Size = new System.Drawing.Size(78, 21);
            this.btnSearchUser.TabIndex = 2;
            this.btnSearchUser.Text = "Search";
            this.btnSearchUser.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnSearchUser.UseVisualStyleBackColor = true;
            this.btnSearchUser.Click += new System.EventHandler(this.btnSearchUser_Click);
            // 
            // txtUserSearch
            // 
            this.txtUserSearch.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtUserSearch.Location = new System.Drawing.Point(25, 48);
            this.txtUserSearch.Name = "txtUserSearch";
            this.txtUserSearch.Size = new System.Drawing.Size(307, 20);
            this.txtUserSearch.TabIndex = 1;
            // 
            // tabImportLog
            // 
            this.tabImportLog.Controls.Add(this.label1);
            this.tabImportLog.Controls.Add(this.txtDaysBack);
            this.tabImportLog.Controls.Add(this.label5);
            this.tabImportLog.Controls.Add(this.lblImportLogsFound);
            this.tabImportLog.Controls.Add(this.lstImportLogs);
            this.tabImportLog.Controls.Add(this.lblImportStatus);
            this.tabImportLog.Controls.Add(this.btnSearchLogs);
            this.tabImportLog.Controls.Add(this.txtImportSearch);
            this.tabImportLog.Location = new System.Drawing.Point(4, 22);
            this.tabImportLog.Name = "tabImportLog";
            this.tabImportLog.Padding = new System.Windows.Forms.Padding(3);
            this.tabImportLog.Size = new System.Drawing.Size(440, 353);
            this.tabImportLog.TabIndex = 2;
            this.tabImportLog.Text = "Imports";
            this.tabImportLog.UseVisualStyleBackColor = true;
            // 
            // lblImportLogsFound
            // 
            this.lblImportLogsFound.AutoSize = true;
            this.lblImportLogsFound.Location = new System.Drawing.Point(26, 149);
            this.lblImportLogsFound.Name = "lblImportLogsFound";
            this.lblImportLogsFound.Size = new System.Drawing.Size(91, 13);
            this.lblImportLogsFound.TabIndex = 12;
            this.lblImportLogsFound.Text = "Import logs found:";
            // 
            // lstImportLogs
            // 
            this.lstImportLogs.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lstImportLogs.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnHeader1,
            this.columnHeader2});
            this.lstImportLogs.Items.AddRange(new System.Windows.Forms.ListViewItem[] {
            listViewItem7,
            listViewItem8});
            this.lstImportLogs.Location = new System.Drawing.Point(29, 165);
            this.lstImportLogs.MultiSelect = false;
            this.lstImportLogs.Name = "lstImportLogs";
            this.lstImportLogs.Size = new System.Drawing.Size(302, 151);
            this.lstImportLogs.TabIndex = 11;
            this.lstImportLogs.UseCompatibleStateImageBehavior = false;
            this.lstImportLogs.View = System.Windows.Forms.View.Details;
            this.lstImportLogs.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.lstImportLogs_MouseDoubleClick);
            // 
            // columnHeader1
            // 
            this.columnHeader1.Text = "Timestamp";
            this.columnHeader1.Width = 159;
            // 
            // columnHeader2
            // 
            this.columnHeader2.Text = "Import message";
            this.columnHeader2.Width = 129;
            // 
            // lblImportStatus
            // 
            this.lblImportStatus.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.lblImportStatus.AutoSize = true;
            this.lblImportStatus.Location = new System.Drawing.Point(22, 337);
            this.lblImportStatus.Name = "lblImportStatus";
            this.lblImportStatus.Size = new System.Drawing.Size(76, 13);
            this.lblImportStatus.TabIndex = 10;
            this.lblImportStatus.Text = "lblImportStatus";
            // 
            // btnSearchLogs
            // 
            this.btnSearchLogs.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnSearchLogs.Image = global::App.ControlPanel.Properties.Resources.Search_glyph71GrayNoHalo_16x;
            this.btnSearchLogs.Location = new System.Drawing.Point(338, 47);
            this.btnSearchLogs.Name = "btnSearchLogs";
            this.btnSearchLogs.Size = new System.Drawing.Size(78, 21);
            this.btnSearchLogs.TabIndex = 9;
            this.btnSearchLogs.Text = "Search";
            this.btnSearchLogs.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnSearchLogs.UseVisualStyleBackColor = true;
            this.btnSearchLogs.Click += new System.EventHandler(this.btnImportLogSearch_Click);
            // 
            // txtImportSearch
            // 
            this.txtImportSearch.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtImportSearch.Location = new System.Drawing.Point(25, 48);
            this.txtImportSearch.Name = "txtImportSearch";
            this.txtImportSearch.Size = new System.Drawing.Size(307, 20);
            this.txtImportSearch.TabIndex = 8;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(7, 9);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(308, 13);
            this.label3.TabIndex = 1;
            this.label3.Text = "Connect to the database, then explore and maniuplate the data.";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(22, 85);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(61, 13);
            this.label5.TabIndex = 14;
            this.label5.Text = "Days back:";
            // 
            // txtDaysBack
            // 
            this.txtDaysBack.Location = new System.Drawing.Point(25, 101);
            this.txtDaysBack.Maximum = new decimal(new int[] {
            1000,
            0,
            0,
            0});
            this.txtDaysBack.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.txtDaysBack.Name = "txtDaysBack";
            this.txtDaysBack.Size = new System.Drawing.Size(77, 20);
            this.txtDaysBack.TabIndex = 15;
            this.txtDaysBack.Value = new decimal(new int[] {
            7,
            0,
            0,
            0});
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(22, 32);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(67, 13);
            this.label1.TabIndex = 16;
            this.label1.Text = "Search term:";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(22, 32);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(67, 13);
            this.label4.TabIndex = 17;
            this.label4.Text = "Search term:";
            // 
            // ManageDataControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.label3);
            this.Controls.Add(this.tabs);
            this.Name = "ManageDataControl";
            this.Size = new System.Drawing.Size(448, 409);
            this.Load += new System.EventHandler(this.ManageDataControl_Load);
            this.tabs.ResumeLayout(false);
            this.tabConnect.ResumeLayout(false);
            this.tabConnect.PerformLayout();
            this.tabUsers.ResumeLayout(false);
            this.tabUsers.PerformLayout();
            this.tabImportLog.ResumeLayout(false);
            this.tabImportLog.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.txtDaysBack)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TabControl tabs;
        private System.Windows.Forms.TabPage tabConnect;
        private System.Windows.Forms.TextBox txtConnectionString;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TabPage tabUsers;
        private System.Windows.Forms.Button btnConnect;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label lblConnectionStatus;
        private System.Windows.Forms.Button btnSearchUser;
        private System.Windows.Forms.TextBox txtUserSearch;
        private System.Windows.Forms.Label lblUsersFound;
        private System.Windows.Forms.ListView lstUsers;
        private System.Windows.Forms.Label lblUserStatus;
        private System.Windows.Forms.TabPage tabImportLog;
        private System.Windows.Forms.Label lblImportStatus;
        private System.Windows.Forms.Button btnSearchLogs;
        private System.Windows.Forms.TextBox txtImportSearch;
        private System.Windows.Forms.Label lblImportLogsFound;
        private System.Windows.Forms.ListView lstImportLogs;
        private System.Windows.Forms.ColumnHeader columnHeader1;
        private System.Windows.Forms.ColumnHeader columnHeader2;
        private System.Windows.Forms.NumericUpDown txtDaysBack;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label1;
    }
}
