namespace App.ControlPanel.Frames.InstallWizard
{
    partial class AzureStorageConfigControl
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AzureStorageConfigControl));
            this.lblSQLServerName = new System.Windows.Forms.Label();
            this.lblRedisName = new System.Windows.Forms.Label();
            this.lblServiceBusName = new System.Windows.Forms.Label();
            this.lblStorageAccountURL = new System.Windows.Forms.Label();
            this.lblGUIAzureStorageHeaderDesc = new System.Windows.Forms.Label();
            this.lblGUIAzureStorageHeader = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderServiceBus = new System.Windows.Forms.Label();
            this.txtServiceBusName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureServiceBusName = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderSQL = new System.Windows.Forms.Label();
            this.txtSQLServerPassword = new System.Windows.Forms.TextBox();
            this.lblGUIAzureSQLPassword = new System.Windows.Forms.Label();
            this.txtSQLServerUsername = new System.Windows.Forms.TextBox();
            this.lblGUIAzureSQLUsername = new System.Windows.Forms.Label();
            this.txtSQLDb = new System.Windows.Forms.TextBox();
            this.lblGUIAzureSQLDbName = new System.Windows.Forms.Label();
            this.txtSQLServerName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureSQLServerName = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderRedis = new System.Windows.Forms.Label();
            this.txtRedisName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureRedisName = new System.Windows.Forms.Label();
            this.txtStorageAccount = new System.Windows.Forms.TextBox();
            this.lblGUIAzureHeaderBlobStorageName = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderBlobStorage = new System.Windows.Forms.Label();
            this.picServiceBus = new System.Windows.Forms.PictureBox();
            this.picSQL = new System.Windows.Forms.PictureBox();
            this.picRedis = new System.Windows.Forms.PictureBox();
            this.picStorage = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)(this.picServiceBus)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.picSQL)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.picRedis)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.picStorage)).BeginInit();
            this.SuspendLayout();
            // 
            // lblSQLServerName
            // 
            this.lblSQLServerName.AutoSize = true;
            this.lblSQLServerName.Location = new System.Drawing.Point(153, 343);
            this.lblSQLServerName.Name = "lblSQLServerName";
            this.lblSQLServerName.Size = new System.Drawing.Size(208, 13);
            this.lblSQLServerName.TabIndex = 176;
            this.lblSQLServerName.Text = "spoinsightsdemosvr.database.windows.net";
            // 
            // lblRedisName
            // 
            this.lblRedisName.AutoSize = true;
            this.lblRedisName.Location = new System.Drawing.Point(155, 179);
            this.lblRedisName.Name = "lblRedisName";
            this.lblRedisName.Size = new System.Drawing.Size(235, 13);
            this.lblRedisName.TabIndex = 175;
            this.lblRedisName.Text = "spoinsightsdemocache.redis.cache.windows.net";
            // 
            // lblServiceBusName
            // 
            this.lblServiceBusName.AutoSize = true;
            this.lblServiceBusName.Location = new System.Drawing.Point(155, 261);
            this.lblServiceBusName.Name = "lblServiceBusName";
            this.lblServiceBusName.Size = new System.Drawing.Size(201, 13);
            this.lblServiceBusName.TabIndex = 174;
            this.lblServiceBusName.Text = "spoinsightsdemo.servicebus.windows.net";
            // 
            // lblStorageAccountURL
            // 
            this.lblStorageAccountURL.AutoSize = true;
            this.lblStorageAccountURL.Location = new System.Drawing.Point(155, 96);
            this.lblStorageAccountURL.Name = "lblStorageAccountURL";
            this.lblStorageAccountURL.Size = new System.Drawing.Size(264, 13);
            this.lblStorageAccountURL.TabIndex = 173;
            this.lblStorageAccountURL.Text = "https://spoinsightsdemoexport.blob.core.windows.net/";
            // 
            // lblGUIAzureStorageHeaderDesc
            // 
            this.lblGUIAzureStorageHeaderDesc.AutoSize = true;
            this.lblGUIAzureStorageHeaderDesc.Location = new System.Drawing.Point(-3, 19);
            this.lblGUIAzureStorageHeaderDesc.Name = "lblGUIAzureStorageHeaderDesc";
            this.lblGUIAzureStorageHeaderDesc.Size = new System.Drawing.Size(602, 13);
            this.lblGUIAzureStorageHeaderDesc.TabIndex = 172;
            this.lblGUIAzureStorageHeaderDesc.Text = "These resources need to be created for the solution to store or stage usage data." +
    " If they exist already they won\'t be recreated. ";
            // 
            // lblGUIAzureStorageHeader
            // 
            this.lblGUIAzureStorageHeader.AutoSize = true;
            this.lblGUIAzureStorageHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureStorageHeader.Location = new System.Drawing.Point(-4, 0);
            this.lblGUIAzureStorageHeader.Name = "lblGUIAzureStorageHeader";
            this.lblGUIAzureStorageHeader.Size = new System.Drawing.Size(177, 19);
            this.lblGUIAzureStorageHeader.TabIndex = 171;
            this.lblGUIAzureStorageHeader.Text = "Azure Storage Resources";
            // 
            // lblGUIAzureHeaderServiceBus
            // 
            this.lblGUIAzureHeaderServiceBus.AutoSize = true;
            this.lblGUIAzureHeaderServiceBus.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderServiceBus.Location = new System.Drawing.Point(62, 215);
            this.lblGUIAzureHeaderServiceBus.Name = "lblGUIAzureHeaderServiceBus";
            this.lblGUIAzureHeaderServiceBus.Size = new System.Drawing.Size(86, 19);
            this.lblGUIAzureHeaderServiceBus.TabIndex = 170;
            this.lblGUIAzureHeaderServiceBus.Text = "Service Bus";
            // 
            // txtServiceBusName
            // 
            this.txtServiceBusName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtServiceBusName.Location = new System.Drawing.Point(156, 238);
            this.txtServiceBusName.Name = "txtServiceBusName";
            this.txtServiceBusName.Size = new System.Drawing.Size(150, 20);
            this.txtServiceBusName.TabIndex = 150;
            this.txtServiceBusName.Text = "txtservicebusname";
            this.txtServiceBusName.TextChanged += new System.EventHandler(this.txtServiceBusName_TextChanged);
            // 
            // lblGUIAzureServiceBusName
            // 
            this.lblGUIAzureServiceBusName.AutoSize = true;
            this.lblGUIAzureServiceBusName.Location = new System.Drawing.Point(63, 241);
            this.lblGUIAzureServiceBusName.Name = "lblGUIAzureServiceBusName";
            this.lblGUIAzureServiceBusName.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureServiceBusName.TabIndex = 168;
            this.lblGUIAzureServiceBusName.Text = "Name:";
            // 
            // lblGUIAzureHeaderSQL
            // 
            this.lblGUIAzureHeaderSQL.AutoSize = true;
            this.lblGUIAzureHeaderSQL.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderSQL.Location = new System.Drawing.Point(61, 298);
            this.lblGUIAzureHeaderSQL.Name = "lblGUIAzureHeaderSQL";
            this.lblGUIAzureHeaderSQL.Size = new System.Drawing.Size(102, 19);
            this.lblGUIAzureHeaderSQL.TabIndex = 167;
            this.lblGUIAzureHeaderSQL.Text = "SQL Database";
            // 
            // txtSQLServerPassword
            // 
            this.txtSQLServerPassword.Location = new System.Drawing.Point(438, 382);
            this.txtSQLServerPassword.Name = "txtSQLServerPassword";
            this.txtSQLServerPassword.PasswordChar = '*';
            this.txtSQLServerPassword.Size = new System.Drawing.Size(150, 20);
            this.txtSQLServerPassword.TabIndex = 154;
            this.txtSQLServerPassword.Text = "omgh4x0r";
            // 
            // lblGUIAzureSQLPassword
            // 
            this.lblGUIAzureSQLPassword.AutoSize = true;
            this.lblGUIAzureSQLPassword.Location = new System.Drawing.Point(352, 385);
            this.lblGUIAzureSQLPassword.Name = "lblGUIAzureSQLPassword";
            this.lblGUIAzureSQLPassword.Size = new System.Drawing.Size(56, 13);
            this.lblGUIAzureSQLPassword.TabIndex = 165;
            this.lblGUIAzureSQLPassword.Text = "Password:";
            // 
            // txtSQLServerUsername
            // 
            this.txtSQLServerUsername.Location = new System.Drawing.Point(156, 382);
            this.txtSQLServerUsername.Name = "txtSQLServerUsername";
            this.txtSQLServerUsername.Size = new System.Drawing.Size(150, 20);
            this.txtSQLServerUsername.TabIndex = 153;
            this.txtSQLServerUsername.Text = "txtSQLServerUsername";
            // 
            // lblGUIAzureSQLUsername
            // 
            this.lblGUIAzureSQLUsername.AutoSize = true;
            this.lblGUIAzureSQLUsername.Location = new System.Drawing.Point(62, 385);
            this.lblGUIAzureSQLUsername.Name = "lblGUIAzureSQLUsername";
            this.lblGUIAzureSQLUsername.Size = new System.Drawing.Size(90, 13);
            this.lblGUIAzureSQLUsername.TabIndex = 164;
            this.lblGUIAzureSQLUsername.Text = "Server username:";
            // 
            // txtSQLDb
            // 
            this.txtSQLDb.Location = new System.Drawing.Point(438, 320);
            this.txtSQLDb.Name = "txtSQLDb";
            this.txtSQLDb.Size = new System.Drawing.Size(150, 20);
            this.txtSQLDb.TabIndex = 152;
            this.txtSQLDb.Text = "txtSQLDb";
            // 
            // lblGUIAzureSQLDbName
            // 
            this.lblGUIAzureSQLDbName.AutoSize = true;
            this.lblGUIAzureSQLDbName.Location = new System.Drawing.Point(352, 323);
            this.lblGUIAzureSQLDbName.Name = "lblGUIAzureSQLDbName";
            this.lblGUIAzureSQLDbName.Size = new System.Drawing.Size(85, 13);
            this.lblGUIAzureSQLDbName.TabIndex = 163;
            this.lblGUIAzureSQLDbName.Text = "Database name:";
            // 
            // txtSQLServerName
            // 
            this.txtSQLServerName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtSQLServerName.Location = new System.Drawing.Point(156, 320);
            this.txtSQLServerName.Name = "txtSQLServerName";
            this.txtSQLServerName.Size = new System.Drawing.Size(150, 20);
            this.txtSQLServerName.TabIndex = 151;
            this.txtSQLServerName.Text = "txtsqlservername";
            this.txtSQLServerName.TextChanged += new System.EventHandler(this.txtSQLServerName_TextChanged);
            // 
            // lblGUIAzureSQLServerName
            // 
            this.lblGUIAzureSQLServerName.AutoSize = true;
            this.lblGUIAzureSQLServerName.Location = new System.Drawing.Point(62, 323);
            this.lblGUIAzureSQLServerName.Name = "lblGUIAzureSQLServerName";
            this.lblGUIAzureSQLServerName.Size = new System.Drawing.Size(67, 13);
            this.lblGUIAzureSQLServerName.TabIndex = 162;
            this.lblGUIAzureSQLServerName.Text = "Server name";
            // 
            // lblGUIAzureHeaderRedis
            // 
            this.lblGUIAzureHeaderRedis.AutoSize = true;
            this.lblGUIAzureHeaderRedis.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderRedis.Location = new System.Drawing.Point(62, 133);
            this.lblGUIAzureHeaderRedis.Name = "lblGUIAzureHeaderRedis";
            this.lblGUIAzureHeaderRedis.Size = new System.Drawing.Size(156, 19);
            this.lblGUIAzureHeaderRedis.TabIndex = 161;
            this.lblGUIAzureHeaderRedis.Text = "Azure Cache for Redis";
            // 
            // txtRedisName
            // 
            this.txtRedisName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtRedisName.Location = new System.Drawing.Point(156, 156);
            this.txtRedisName.Name = "txtRedisName";
            this.txtRedisName.Size = new System.Drawing.Size(150, 20);
            this.txtRedisName.TabIndex = 149;
            this.txtRedisName.Text = "txtredisname";
            this.txtRedisName.TextChanged += new System.EventHandler(this.txtRedisName_TextChanged);
            // 
            // lblGUIAzureRedisName
            // 
            this.lblGUIAzureRedisName.AutoSize = true;
            this.lblGUIAzureRedisName.Location = new System.Drawing.Point(63, 159);
            this.lblGUIAzureRedisName.Name = "lblGUIAzureRedisName";
            this.lblGUIAzureRedisName.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureRedisName.TabIndex = 159;
            this.lblGUIAzureRedisName.Text = "Name:";
            // 
            // txtStorageAccount
            // 
            this.txtStorageAccount.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtStorageAccount.Location = new System.Drawing.Point(156, 73);
            this.txtStorageAccount.Name = "txtStorageAccount";
            this.txtStorageAccount.Size = new System.Drawing.Size(150, 20);
            this.txtStorageAccount.TabIndex = 147;
            this.txtStorageAccount.Text = "txtstorageaccount";
            this.txtStorageAccount.TextChanged += new System.EventHandler(this.txtStorageAccount_TextChanged);
            // 
            // lblGUIAzureHeaderBlobStorageName
            // 
            this.lblGUIAzureHeaderBlobStorageName.AutoSize = true;
            this.lblGUIAzureHeaderBlobStorageName.Location = new System.Drawing.Point(63, 76);
            this.lblGUIAzureHeaderBlobStorageName.Name = "lblGUIAzureHeaderBlobStorageName";
            this.lblGUIAzureHeaderBlobStorageName.Size = new System.Drawing.Size(79, 13);
            this.lblGUIAzureHeaderBlobStorageName.TabIndex = 155;
            this.lblGUIAzureHeaderBlobStorageName.Text = "Account name:";
            // 
            // lblGUIAzureHeaderBlobStorage
            // 
            this.lblGUIAzureHeaderBlobStorage.AutoSize = true;
            this.lblGUIAzureHeaderBlobStorage.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderBlobStorage.Location = new System.Drawing.Point(62, 51);
            this.lblGUIAzureHeaderBlobStorage.Name = "lblGUIAzureHeaderBlobStorage";
            this.lblGUIAzureHeaderBlobStorage.Size = new System.Drawing.Size(97, 19);
            this.lblGUIAzureHeaderBlobStorage.TabIndex = 158;
            this.lblGUIAzureHeaderBlobStorage.Text = "Storage Account";
            // 
            // picServiceBus
            // 
            this.picServiceBus.Image = ((System.Drawing.Image)(resources.GetObject("picServiceBus.Image")));
            this.picServiceBus.Location = new System.Drawing.Point(0, 215);
            this.picServiceBus.Name = "picServiceBus";
            this.picServiceBus.Size = new System.Drawing.Size(56, 56);
            this.picServiceBus.TabIndex = 169;
            this.picServiceBus.TabStop = false;
            // 
            // picSQL
            // 
            this.picSQL.Image = global::App.ControlPanel.Properties.Resources.SQL;
            this.picSQL.Location = new System.Drawing.Point(0, 298);
            this.picSQL.Name = "picSQL";
            this.picSQL.Size = new System.Drawing.Size(56, 56);
            this.picSQL.TabIndex = 166;
            this.picSQL.TabStop = false;
            // 
            // picRedis
            // 
            this.picRedis.Image = ((System.Drawing.Image)(resources.GetObject("picRedis.Image")));
            this.picRedis.Location = new System.Drawing.Point(0, 133);
            this.picRedis.Name = "picRedis";
            this.picRedis.Size = new System.Drawing.Size(56, 56);
            this.picRedis.TabIndex = 160;
            this.picRedis.TabStop = false;
            // 
            // picStorage
            // 
            this.picStorage.Image = global::App.ControlPanel.Properties.Resources.Storage;
            this.picStorage.Location = new System.Drawing.Point(0, 51);
            this.picStorage.Name = "picStorage";
            this.picStorage.Size = new System.Drawing.Size(56, 56);
            this.picStorage.TabIndex = 157;
            this.picStorage.TabStop = false;
            // 
            // AzureStorageConfigControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblSQLServerName);
            this.Controls.Add(this.lblRedisName);
            this.Controls.Add(this.lblServiceBusName);
            this.Controls.Add(this.lblStorageAccountURL);
            this.Controls.Add(this.lblGUIAzureStorageHeaderDesc);
            this.Controls.Add(this.lblGUIAzureStorageHeader);
            this.Controls.Add(this.lblGUIAzureHeaderServiceBus);
            this.Controls.Add(this.txtServiceBusName);
            this.Controls.Add(this.lblGUIAzureServiceBusName);
            this.Controls.Add(this.lblGUIAzureHeaderSQL);
            this.Controls.Add(this.txtSQLServerPassword);
            this.Controls.Add(this.lblGUIAzureSQLPassword);
            this.Controls.Add(this.txtSQLServerUsername);
            this.Controls.Add(this.lblGUIAzureSQLUsername);
            this.Controls.Add(this.txtSQLDb);
            this.Controls.Add(this.lblGUIAzureSQLDbName);
            this.Controls.Add(this.txtSQLServerName);
            this.Controls.Add(this.lblGUIAzureSQLServerName);
            this.Controls.Add(this.lblGUIAzureHeaderRedis);
            this.Controls.Add(this.txtRedisName);
            this.Controls.Add(this.lblGUIAzureRedisName);
            this.Controls.Add(this.txtStorageAccount);
            this.Controls.Add(this.lblGUIAzureHeaderBlobStorageName);
            this.Controls.Add(this.lblGUIAzureHeaderBlobStorage);
            this.Controls.Add(this.picServiceBus);
            this.Controls.Add(this.picSQL);
            this.Controls.Add(this.picRedis);
            this.Controls.Add(this.picStorage);
            this.Name = "AzureStorageConfigControl";
            this.Size = new System.Drawing.Size(606, 409);
            ((System.ComponentModel.ISupportInitialize)(this.picServiceBus)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.picSQL)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.picRedis)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.picStorage)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblSQLServerName;
        private System.Windows.Forms.Label lblRedisName;
        private System.Windows.Forms.Label lblServiceBusName;
        private System.Windows.Forms.Label lblStorageAccountURL;
        private System.Windows.Forms.Label lblGUIAzureStorageHeaderDesc;
        private System.Windows.Forms.Label lblGUIAzureStorageHeader;
        private System.Windows.Forms.Label lblGUIAzureHeaderServiceBus;
        private System.Windows.Forms.TextBox txtServiceBusName;
        private System.Windows.Forms.Label lblGUIAzureServiceBusName;
        private System.Windows.Forms.Label lblGUIAzureHeaderSQL;
        private System.Windows.Forms.TextBox txtSQLServerPassword;
        private System.Windows.Forms.Label lblGUIAzureSQLPassword;
        private System.Windows.Forms.TextBox txtSQLServerUsername;
        private System.Windows.Forms.Label lblGUIAzureSQLUsername;
        private System.Windows.Forms.TextBox txtSQLDb;
        private System.Windows.Forms.Label lblGUIAzureSQLDbName;
        private System.Windows.Forms.TextBox txtSQLServerName;
        private System.Windows.Forms.Label lblGUIAzureSQLServerName;
        private System.Windows.Forms.Label lblGUIAzureHeaderRedis;
        private System.Windows.Forms.TextBox txtRedisName;
        private System.Windows.Forms.Label lblGUIAzureRedisName;
        private System.Windows.Forms.TextBox txtStorageAccount;
        private System.Windows.Forms.Label lblGUIAzureHeaderBlobStorageName;
        private System.Windows.Forms.Label lblGUIAzureHeaderBlobStorage;
        private System.Windows.Forms.PictureBox picServiceBus;
        private System.Windows.Forms.PictureBox picSQL;
        private System.Windows.Forms.PictureBox picRedis;
        private System.Windows.Forms.PictureBox picStorage;
    }
}
