namespace App.ControlPanel.Frames.InstallWizard
{
    partial class NetworkingConfigControl
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
            this.lblHeader = new System.Windows.Forms.Label();
            this.lblDescription = new System.Windows.Forms.Label();
            this.chkEnableVNet = new System.Windows.Forms.CheckBox();
            this.grpVNetSettings = new System.Windows.Forms.GroupBox();
            this.lblVNetName = new System.Windows.Forms.Label();
            this.txtVNetName = new System.Windows.Forms.TextBox();
            this.lblSubnetName = new System.Windows.Forms.Label();
            this.txtSubnetName = new System.Windows.Forms.TextBox();
            this.lblAddressPrefix = new System.Windows.Forms.Label();
            this.txtAddressPrefix = new System.Windows.Forms.TextBox();
            this.lblSubnetAddressPrefix = new System.Windows.Forms.Label();
            this.txtSubnetAddressPrefix = new System.Windows.Forms.TextBox();
            this.lblAppSubnetName = new System.Windows.Forms.Label();
            this.txtAppSubnetName = new System.Windows.Forms.TextBox();
            this.lblAppSubnetAddressPrefix = new System.Windows.Forms.Label();
            this.txtAppSubnetAddressPrefix = new System.Windows.Forms.TextBox();
            this.chkDeployDnsZones = new System.Windows.Forms.CheckBox();
            this.lblDnsZonesHelp = new System.Windows.Forms.Label();
            this.chkAllowPublicAccess = new System.Windows.Forms.CheckBox();
            this.lblAllowPublicAccessHelp = new System.Windows.Forms.Label();
            this.grpEndpointNames = new System.Windows.Forms.GroupBox();
            this.lblPeSql = new System.Windows.Forms.Label();
            this.txtPeSql = new System.Windows.Forms.TextBox();
            this.lblPeApp = new System.Windows.Forms.Label();
            this.txtPeApp = new System.Windows.Forms.TextBox();
            this.lblPeRedis = new System.Windows.Forms.Label();
            this.txtPeRedis = new System.Windows.Forms.TextBox();
            this.lblPeStorage = new System.Windows.Forms.Label();
            this.txtPeStorage = new System.Windows.Forms.TextBox();
            this.lblPeKeyVault = new System.Windows.Forms.Label();
            this.txtPeKeyVault = new System.Windows.Forms.TextBox();
            this.lblPeServiceBus = new System.Windows.Forms.Label();
            this.txtPeServiceBus = new System.Windows.Forms.TextBox();
            this.lblPeCognitive = new System.Windows.Forms.Label();
            this.txtPeCognitive = new System.Windows.Forms.TextBox();
            this.lblPeAutomation = new System.Windows.Forms.Label();
            this.txtPeAutomation = new System.Windows.Forms.TextBox();
            this.lblPeHelp = new System.Windows.Forms.Label();
            this.lblHybridWorkerVm = new System.Windows.Forms.Label();
            this.txtHybridWorkerVm = new System.Windows.Forms.TextBox();
            this.btnBrowseVm = new System.Windows.Forms.Button();
            this.lblHybridWorkerVmHelp = new System.Windows.Forms.Label();
            this.lblSkuWarning = new System.Windows.Forms.Label();
            this.grpVNetSettings.SuspendLayout();
            this.grpEndpointNames.SuspendLayout();
            this.SuspendLayout();
            // 
            // lblHeader
            // 
            this.lblHeader.AutoSize = true;
            this.lblHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblHeader.Location = new System.Drawing.Point(10, 10);
            this.lblHeader.Name = "lblHeader";
            this.lblHeader.Size = new System.Drawing.Size(200, 19);
            this.lblHeader.TabIndex = 0;
            this.lblHeader.Text = "Private VNet Configuration";
            // 
            // lblDescription
            // 
            this.lblDescription.Location = new System.Drawing.Point(12, 32);
            this.lblDescription.Name = "lblDescription";
            this.lblDescription.Size = new System.Drawing.Size(600, 17);
            this.lblDescription.TabIndex = 1;
            this.lblDescription.Text = "Enable private VNet integration for all Azure PaaS resources with private endpoints.";
            // 
            // chkEnableVNet
            // 
            this.chkEnableVNet.AutoSize = true;
            this.chkEnableVNet.Location = new System.Drawing.Point(15, 55);
            this.chkEnableVNet.Name = "chkEnableVNet";
            this.chkEnableVNet.Size = new System.Drawing.Size(230, 17);
            this.chkEnableVNet.TabIndex = 2;
            this.chkEnableVNet.Text = "Enable private VNet for all Azure resources";
            this.chkEnableVNet.UseVisualStyleBackColor = true;
            this.chkEnableVNet.CheckedChanged += new System.EventHandler(this.chkEnableVNet_CheckedChanged);
            // 
            // grpVNetSettings
            // 
            this.grpVNetSettings.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpVNetSettings.Controls.Add(this.lblVNetName);
            this.grpVNetSettings.Controls.Add(this.txtVNetName);
            this.grpVNetSettings.Controls.Add(this.lblSubnetName);
            this.grpVNetSettings.Controls.Add(this.txtSubnetName);
            this.grpVNetSettings.Controls.Add(this.lblAddressPrefix);
            this.grpVNetSettings.Controls.Add(this.txtAddressPrefix);
            this.grpVNetSettings.Controls.Add(this.lblSubnetAddressPrefix);
            this.grpVNetSettings.Controls.Add(this.txtSubnetAddressPrefix);
            this.grpVNetSettings.Controls.Add(this.lblAppSubnetName);
            this.grpVNetSettings.Controls.Add(this.txtAppSubnetName);
            this.grpVNetSettings.Controls.Add(this.lblAppSubnetAddressPrefix);
            this.grpVNetSettings.Controls.Add(this.txtAppSubnetAddressPrefix);
            this.grpVNetSettings.Controls.Add(this.chkDeployDnsZones);
            this.grpVNetSettings.Controls.Add(this.lblDnsZonesHelp);
            this.grpVNetSettings.Controls.Add(this.chkAllowPublicAccess);
            this.grpVNetSettings.Controls.Add(this.lblAllowPublicAccessHelp);
            this.grpVNetSettings.Controls.Add(this.grpEndpointNames);
            this.grpVNetSettings.Controls.Add(this.lblHybridWorkerVm);
            this.grpVNetSettings.Controls.Add(this.txtHybridWorkerVm);
            this.grpVNetSettings.Controls.Add(this.btnBrowseVm);
            this.grpVNetSettings.Controls.Add(this.lblHybridWorkerVmHelp);
            this.grpVNetSettings.Controls.Add(this.lblSkuWarning);
            this.grpVNetSettings.Enabled = false;
            this.grpVNetSettings.Location = new System.Drawing.Point(15, 78);
            this.grpVNetSettings.Name = "grpVNetSettings";
            this.grpVNetSettings.Size = new System.Drawing.Size(600, 540);
            this.grpVNetSettings.TabIndex = 3;
            this.grpVNetSettings.TabStop = false;
            this.grpVNetSettings.Text = "VNet Settings";
            // 
            // lblVNetName
            // 
            this.lblVNetName.AutoSize = true;
            this.lblVNetName.Location = new System.Drawing.Point(15, 25);
            this.lblVNetName.Name = "lblVNetName";
            this.lblVNetName.Size = new System.Drawing.Size(65, 13);
            this.lblVNetName.TabIndex = 0;
            this.lblVNetName.Text = "VNet Name:";
            // 
            // txtVNetName
            // 
            this.txtVNetName.Location = new System.Drawing.Point(120, 22);
            this.txtVNetName.Name = "txtVNetName";
            this.txtVNetName.Size = new System.Drawing.Size(180, 20);
            this.txtVNetName.TabIndex = 1;
            // 
            // lblSubnetName
            // 
            this.lblSubnetName.AutoSize = true;
            this.lblSubnetName.Location = new System.Drawing.Point(15, 52);
            this.lblSubnetName.Name = "lblSubnetName";
            this.lblSubnetName.Size = new System.Drawing.Size(75, 13);
            this.lblSubnetName.TabIndex = 2;
            this.lblSubnetName.Text = "Subnet Name:";
            // 
            // txtSubnetName
            // 
            this.txtSubnetName.Location = new System.Drawing.Point(120, 49);
            this.txtSubnetName.Name = "txtSubnetName";
            this.txtSubnetName.Size = new System.Drawing.Size(180, 20);
            this.txtSubnetName.TabIndex = 3;
            this.txtSubnetName.Text = "default";
            // 
            // lblAddressPrefix
            // 
            this.lblAddressPrefix.AutoSize = true;
            this.lblAddressPrefix.Location = new System.Drawing.Point(320, 25);
            this.lblAddressPrefix.Name = "lblAddressPrefix";
            this.lblAddressPrefix.Size = new System.Drawing.Size(110, 13);
            this.lblAddressPrefix.TabIndex = 4;
            this.lblAddressPrefix.Text = "VNet Address Prefix:";
            // 
            // txtAddressPrefix
            // 
            this.txtAddressPrefix.Location = new System.Drawing.Point(450, 22);
            this.txtAddressPrefix.Name = "txtAddressPrefix";
            this.txtAddressPrefix.Size = new System.Drawing.Size(130, 20);
            this.txtAddressPrefix.TabIndex = 5;
            this.txtAddressPrefix.Text = "10.0.0.0/16";
            // 
            // lblSubnetAddressPrefix
            // 
            this.lblSubnetAddressPrefix.AutoSize = true;
            this.lblSubnetAddressPrefix.Location = new System.Drawing.Point(320, 52);
            this.lblSubnetAddressPrefix.Name = "lblSubnetAddressPrefix";
            this.lblSubnetAddressPrefix.Size = new System.Drawing.Size(120, 13);
            this.lblSubnetAddressPrefix.TabIndex = 6;
            this.lblSubnetAddressPrefix.Text = "Subnet Prefix:";
            // 
            // txtSubnetAddressPrefix
            // 
            this.txtSubnetAddressPrefix.Location = new System.Drawing.Point(450, 49);
            this.txtSubnetAddressPrefix.Name = "txtSubnetAddressPrefix";
            this.txtSubnetAddressPrefix.Size = new System.Drawing.Size(130, 20);
            this.txtSubnetAddressPrefix.TabIndex = 7;
            this.txtSubnetAddressPrefix.Text = "10.0.0.0/24";
            // 
            // lblAppSubnetName
            // 
            this.lblAppSubnetName.AutoSize = true;
            this.lblAppSubnetName.Location = new System.Drawing.Point(15, 79);
            this.lblAppSubnetName.Name = "lblAppSubnetName";
            this.lblAppSubnetName.Size = new System.Drawing.Size(120, 13);
            this.lblAppSubnetName.TabIndex = 30;
            this.lblAppSubnetName.Text = "App Subnet Name:";
            // 
            // txtAppSubnetName
            // 
            this.txtAppSubnetName.Location = new System.Drawing.Point(120, 76);
            this.txtAppSubnetName.Name = "txtAppSubnetName";
            this.txtAppSubnetName.Size = new System.Drawing.Size(180, 20);
            this.txtAppSubnetName.TabIndex = 31;
            this.txtAppSubnetName.Text = "app-integration";
            // 
            // lblAppSubnetAddressPrefix
            // 
            this.lblAppSubnetAddressPrefix.AutoSize = true;
            this.lblAppSubnetAddressPrefix.Location = new System.Drawing.Point(320, 79);
            this.lblAppSubnetAddressPrefix.Name = "lblAppSubnetAddressPrefix";
            this.lblAppSubnetAddressPrefix.Size = new System.Drawing.Size(120, 13);
            this.lblAppSubnetAddressPrefix.TabIndex = 32;
            this.lblAppSubnetAddressPrefix.Text = "App Subnet Prefix:";
            // 
            // txtAppSubnetAddressPrefix
            // 
            this.txtAppSubnetAddressPrefix.Location = new System.Drawing.Point(450, 76);
            this.txtAppSubnetAddressPrefix.Name = "txtAppSubnetAddressPrefix";
            this.txtAppSubnetAddressPrefix.Size = new System.Drawing.Size(130, 20);
            this.txtAppSubnetAddressPrefix.TabIndex = 33;
            this.txtAppSubnetAddressPrefix.Text = "10.0.2.0/24";
            // 
            // chkDeployDnsZones
            // 
            this.chkDeployDnsZones.AutoSize = true;
            this.chkDeployDnsZones.Checked = true;
            this.chkDeployDnsZones.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkDeployDnsZones.Location = new System.Drawing.Point(18, 107);
            this.chkDeployDnsZones.Name = "chkDeployDnsZones";
            this.chkDeployDnsZones.Size = new System.Drawing.Size(280, 17);
            this.chkDeployDnsZones.TabIndex = 34;
            this.chkDeployDnsZones.Text = "Deploy Azure Private DNS zones for each resource";
            this.chkDeployDnsZones.UseVisualStyleBackColor = true;
            // 
            // lblDnsZonesHelp
            // 
            this.lblDnsZonesHelp.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblDnsZonesHelp.Location = new System.Drawing.Point(35, 125);
            this.lblDnsZonesHelp.Name = "lblDnsZonesHelp";
            this.lblDnsZonesHelp.Size = new System.Drawing.Size(550, 14);
            this.lblDnsZonesHelp.TabIndex = 9;
            this.lblDnsZonesHelp.Text = "Uncheck if you manage DNS externally (e.g. on-premises DNS, Azure DNS Private Resolver, or custom forwarding).";
            // 
            // chkAllowPublicAccess
            // 
            this.chkAllowPublicAccess.AutoSize = true;
            this.chkAllowPublicAccess.Checked = true;
            this.chkAllowPublicAccess.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAllowPublicAccess.Location = new System.Drawing.Point(18, 144);
            this.chkAllowPublicAccess.Name = "chkAllowPublicAccess";
            this.chkAllowPublicAccess.Size = new System.Drawing.Size(280, 17);
            this.chkAllowPublicAccess.TabIndex = 35;
            this.chkAllowPublicAccess.Text = "Allow public network access on Azure PaaS resources";
            this.chkAllowPublicAccess.UseVisualStyleBackColor = true;
            // 
            // lblAllowPublicAccessHelp
            // 
            this.lblAllowPublicAccessHelp.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblAllowPublicAccessHelp.Location = new System.Drawing.Point(35, 162);
            this.lblAllowPublicAccessHelp.Name = "lblAllowPublicAccessHelp";
            this.lblAllowPublicAccessHelp.Size = new System.Drawing.Size(550, 42);
            this.lblAllowPublicAccessHelp.TabIndex = 36;
            this.lblAllowPublicAccessHelp.Text = "Uncheck to disable public access on SQL, Storage, Key Vault, Redis, Service Bus, App Service,\r\nAutomation, and Cognitive Services. Log Analytics and Application Insights always stay public —\r\nconfigure Azure Monitor Private Link Scope (AMPLS) manually if private access is required.";
            // 
            // grpEndpointNames
            // 
            this.grpEndpointNames.Controls.Add(this.lblPeHelp);
            this.grpEndpointNames.Controls.Add(this.lblPeSql);
            this.grpEndpointNames.Controls.Add(this.txtPeSql);
            this.grpEndpointNames.Controls.Add(this.lblPeApp);
            this.grpEndpointNames.Controls.Add(this.txtPeApp);
            this.grpEndpointNames.Controls.Add(this.lblPeRedis);
            this.grpEndpointNames.Controls.Add(this.txtPeRedis);
            this.grpEndpointNames.Controls.Add(this.lblPeStorage);
            this.grpEndpointNames.Controls.Add(this.txtPeStorage);
            this.grpEndpointNames.Controls.Add(this.lblPeKeyVault);
            this.grpEndpointNames.Controls.Add(this.txtPeKeyVault);
            this.grpEndpointNames.Controls.Add(this.lblPeServiceBus);
            this.grpEndpointNames.Controls.Add(this.txtPeServiceBus);
            this.grpEndpointNames.Controls.Add(this.lblPeCognitive);
            this.grpEndpointNames.Controls.Add(this.txtPeCognitive);
            this.grpEndpointNames.Controls.Add(this.lblPeAutomation);
            this.grpEndpointNames.Controls.Add(this.txtPeAutomation);
            this.grpEndpointNames.Location = new System.Drawing.Point(15, 210);
            this.grpEndpointNames.Name = "grpEndpointNames";
            this.grpEndpointNames.Size = new System.Drawing.Size(570, 145);
            this.grpEndpointNames.TabIndex = 10;
            this.grpEndpointNames.TabStop = false;
            this.grpEndpointNames.Text = "Private Endpoint Names (optional — leave blank for defaults)";
            // 
            // lblPeHelp
            // 
            this.lblPeHelp.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblPeHelp.Location = new System.Drawing.Point(10, 18);
            this.lblPeHelp.Name = "lblPeHelp";
            this.lblPeHelp.Size = new System.Drawing.Size(545, 14);
            this.lblPeHelp.TabIndex = 0;
            this.lblPeHelp.Text = "Defaults are auto-generated as pe-{resourceName}-{type}. Override only if your naming conventions require it.";
            // 
            // lblPeSql
            // 
            this.lblPeSql.AutoSize = true;
            this.lblPeSql.Location = new System.Drawing.Point(10, 40);
            this.lblPeSql.Name = "lblPeSql";
            this.lblPeSql.Size = new System.Drawing.Size(60, 13);
            this.lblPeSql.TabIndex = 1;
            this.lblPeSql.Text = "SQL Server:";
            // 
            // txtPeSql
            // 
            this.txtPeSql.Location = new System.Drawing.Point(85, 37);
            this.txtPeSql.Name = "txtPeSql";
            this.txtPeSql.Size = new System.Drawing.Size(185, 20);
            this.txtPeSql.TabIndex = 2;
            // 
            // lblPeApp
            // 
            this.lblPeApp.AutoSize = true;
            this.lblPeApp.Location = new System.Drawing.Point(285, 40);
            this.lblPeApp.Name = "lblPeApp";
            this.lblPeApp.Size = new System.Drawing.Size(68, 13);
            this.lblPeApp.TabIndex = 3;
            this.lblPeApp.Text = "App Service:";
            // 
            // txtPeApp
            // 
            this.txtPeApp.Location = new System.Drawing.Point(370, 37);
            this.txtPeApp.Name = "txtPeApp";
            this.txtPeApp.Size = new System.Drawing.Size(185, 20);
            this.txtPeApp.TabIndex = 4;
            // 
            // lblPeRedis
            // 
            this.lblPeRedis.AutoSize = true;
            this.lblPeRedis.Location = new System.Drawing.Point(10, 67);
            this.lblPeRedis.Name = "lblPeRedis";
            this.lblPeRedis.Size = new System.Drawing.Size(38, 13);
            this.lblPeRedis.TabIndex = 5;
            this.lblPeRedis.Text = "Redis:";
            // 
            // txtPeRedis
            // 
            this.txtPeRedis.Location = new System.Drawing.Point(85, 64);
            this.txtPeRedis.Name = "txtPeRedis";
            this.txtPeRedis.Size = new System.Drawing.Size(185, 20);
            this.txtPeRedis.TabIndex = 6;
            // 
            // lblPeStorage
            // 
            this.lblPeStorage.AutoSize = true;
            this.lblPeStorage.Location = new System.Drawing.Point(285, 67);
            this.lblPeStorage.Name = "lblPeStorage";
            this.lblPeStorage.Size = new System.Drawing.Size(70, 13);
            this.lblPeStorage.TabIndex = 7;
            this.lblPeStorage.Text = "Storage:";
            // 
            // txtPeStorage
            // 
            this.txtPeStorage.Location = new System.Drawing.Point(370, 64);
            this.txtPeStorage.Name = "txtPeStorage";
            this.txtPeStorage.Size = new System.Drawing.Size(185, 20);
            this.txtPeStorage.TabIndex = 8;
            // 
            // lblPeKeyVault
            // 
            this.lblPeKeyVault.AutoSize = true;
            this.lblPeKeyVault.Location = new System.Drawing.Point(10, 94);
            this.lblPeKeyVault.Name = "lblPeKeyVault";
            this.lblPeKeyVault.Size = new System.Drawing.Size(56, 13);
            this.lblPeKeyVault.TabIndex = 9;
            this.lblPeKeyVault.Text = "Key Vault:";
            // 
            // txtPeKeyVault
            // 
            this.txtPeKeyVault.Location = new System.Drawing.Point(85, 91);
            this.txtPeKeyVault.Name = "txtPeKeyVault";
            this.txtPeKeyVault.Size = new System.Drawing.Size(185, 20);
            this.txtPeKeyVault.TabIndex = 10;
            // 
            // lblPeServiceBus
            // 
            this.lblPeServiceBus.AutoSize = true;
            this.lblPeServiceBus.Location = new System.Drawing.Point(285, 94);
            this.lblPeServiceBus.Name = "lblPeServiceBus";
            this.lblPeServiceBus.Size = new System.Drawing.Size(68, 13);
            this.lblPeServiceBus.TabIndex = 11;
            this.lblPeServiceBus.Text = "Service Bus:";
            // 
            // txtPeServiceBus
            // 
            this.txtPeServiceBus.Location = new System.Drawing.Point(370, 91);
            this.txtPeServiceBus.Name = "txtPeServiceBus";
            this.txtPeServiceBus.Size = new System.Drawing.Size(185, 20);
            this.txtPeServiceBus.TabIndex = 12;
            // 
            // lblPeCognitive
            // 
            this.lblPeCognitive.AutoSize = true;
            this.lblPeCognitive.Location = new System.Drawing.Point(10, 121);
            this.lblPeCognitive.Name = "lblPeCognitive";
            this.lblPeCognitive.Size = new System.Drawing.Size(104, 13);
            this.lblPeCognitive.TabIndex = 13;
            this.lblPeCognitive.Text = "Cognitive:";
            // 
            // txtPeCognitive
            // 
            this.txtPeCognitive.Location = new System.Drawing.Point(85, 118);
            this.txtPeCognitive.Name = "txtPeCognitive";
            this.txtPeCognitive.Size = new System.Drawing.Size(185, 20);
            this.txtPeCognitive.TabIndex = 14;
            // 
            // lblPeAutomation
            // 
            this.lblPeAutomation.AutoSize = true;
            this.lblPeAutomation.Location = new System.Drawing.Point(285, 121);
            this.lblPeAutomation.Name = "lblPeAutomation";
            this.lblPeAutomation.Size = new System.Drawing.Size(68, 13);
            this.lblPeAutomation.TabIndex = 15;
            this.lblPeAutomation.Text = "Automation:";
            // 
            // txtPeAutomation
            // 
            this.txtPeAutomation.Location = new System.Drawing.Point(370, 118);
            this.txtPeAutomation.Name = "txtPeAutomation";
            this.txtPeAutomation.Size = new System.Drawing.Size(185, 20);
            this.txtPeAutomation.TabIndex = 16;
            // 
            // lblHybridWorkerVm
            // 
            this.lblHybridWorkerVm.AutoSize = true;
            this.lblHybridWorkerVm.Location = new System.Drawing.Point(15, 417);
            this.lblHybridWorkerVm.Name = "lblHybridWorkerVm";
            this.lblHybridWorkerVm.Size = new System.Drawing.Size(140, 13);
            this.lblHybridWorkerVm.TabIndex = 17;
            this.lblHybridWorkerVm.Text = "Hybrid Worker VM (optional):";
            // 
            // txtHybridWorkerVm
            // 
            this.txtHybridWorkerVm.Location = new System.Drawing.Point(160, 364);
            this.txtHybridWorkerVm.Name = "txtHybridWorkerVm";
            this.txtHybridWorkerVm.Size = new System.Drawing.Size(340, 20);
            this.txtHybridWorkerVm.TabIndex = 18;
            // 
            // btnBrowseVm
            // 
            this.btnBrowseVm.Location = new System.Drawing.Point(506, 362);
            this.btnBrowseVm.Name = "btnBrowseVm";
            this.btnBrowseVm.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseVm.TabIndex = 19;
            this.btnBrowseVm.Text = "Browse...";
            this.btnBrowseVm.UseVisualStyleBackColor = true;
            this.btnBrowseVm.Click += new System.EventHandler(this.btnBrowseVm_Click);
            // 
            // lblHybridWorkerVmHelp
            // 
            this.lblHybridWorkerVmHelp.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblHybridWorkerVmHelp.Location = new System.Drawing.Point(157, 387);
            this.lblHybridWorkerVmHelp.Name = "lblHybridWorkerVmHelp";
            this.lblHybridWorkerVmHelp.Size = new System.Drawing.Size(430, 42);
            this.lblHybridWorkerVmHelp.TabIndex = 20;
            this.lblHybridWorkerVmHelp.Text = "Azure VM Resource ID. The VM must be connected to the VNet specified above.\r\nThe installer will automatically deploy the Hybrid Worker extension to the VM\r\nand register it so automation runbooks can access private-endpoint resources.";
            // 
            // lblSkuWarning
            // 
            this.lblSkuWarning.ForeColor = System.Drawing.Color.DarkOrange;
            this.lblSkuWarning.Location = new System.Drawing.Point(15, 437);
            this.lblSkuWarning.Name = "lblSkuWarning";
            this.lblSkuWarning.Size = new System.Drawing.Size(570, 90);
            this.lblSkuWarning.TabIndex = 21;
            this.lblSkuWarning.Text = "Note: Enabling private VNet will automatically upgrade certain resource SKUs to support private endpoints:\r\n" +
                "  • Redis Cache: Basic → Standard\r\n" +
                "  • Service Bus: Basic → Premium (private endpoints require Premium)\r\n" +
                "  • SQL Database: Basic → S2\r\n" +
                "These upgrades may increase Azure costs.";
            // 
            // NetworkingConfigControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblHeader);
            this.Controls.Add(this.lblDescription);
            this.Controls.Add(this.chkEnableVNet);
            this.Controls.Add(this.grpVNetSettings);
            this.Name = "NetworkingConfigControl";
            this.Size = new System.Drawing.Size(632, 640);
            this.grpVNetSettings.ResumeLayout(false);
            this.grpVNetSettings.PerformLayout();
            this.grpEndpointNames.ResumeLayout(false);
            this.grpEndpointNames.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblHeader;
        private System.Windows.Forms.Label lblDescription;
        private System.Windows.Forms.CheckBox chkEnableVNet;
        private System.Windows.Forms.GroupBox grpVNetSettings;
        private System.Windows.Forms.Label lblVNetName;
        private System.Windows.Forms.TextBox txtVNetName;
        private System.Windows.Forms.Label lblSubnetName;
        private System.Windows.Forms.TextBox txtSubnetName;
        private System.Windows.Forms.Label lblAddressPrefix;
        private System.Windows.Forms.TextBox txtAddressPrefix;
        private System.Windows.Forms.Label lblSubnetAddressPrefix;
        private System.Windows.Forms.TextBox txtSubnetAddressPrefix;
        private System.Windows.Forms.CheckBox chkDeployDnsZones;
        private System.Windows.Forms.Label lblDnsZonesHelp;
        private System.Windows.Forms.CheckBox chkAllowPublicAccess;
        private System.Windows.Forms.Label lblAllowPublicAccessHelp;
        private System.Windows.Forms.GroupBox grpEndpointNames;
        private System.Windows.Forms.Label lblPeHelp;
        private System.Windows.Forms.Label lblPeSql;
        private System.Windows.Forms.TextBox txtPeSql;
        private System.Windows.Forms.Label lblPeApp;
        private System.Windows.Forms.TextBox txtPeApp;
        private System.Windows.Forms.Label lblPeRedis;
        private System.Windows.Forms.TextBox txtPeRedis;
        private System.Windows.Forms.Label lblPeStorage;
        private System.Windows.Forms.TextBox txtPeStorage;
        private System.Windows.Forms.Label lblPeKeyVault;
        private System.Windows.Forms.TextBox txtPeKeyVault;
        private System.Windows.Forms.Label lblPeServiceBus;
        private System.Windows.Forms.TextBox txtPeServiceBus;
        private System.Windows.Forms.Label lblPeCognitive;
        private System.Windows.Forms.TextBox txtPeCognitive;
        private System.Windows.Forms.Label lblPeAutomation;
        private System.Windows.Forms.TextBox txtPeAutomation;
        private System.Windows.Forms.Label lblHybridWorkerVm;
        private System.Windows.Forms.TextBox txtHybridWorkerVm;
        private System.Windows.Forms.Button btnBrowseVm;
        private System.Windows.Forms.Label lblHybridWorkerVmHelp;
        private System.Windows.Forms.Label lblSkuWarning;
        private System.Windows.Forms.Label lblAppSubnetName;
        private System.Windows.Forms.TextBox txtAppSubnetName;
        private System.Windows.Forms.Label lblAppSubnetAddressPrefix;
        private System.Windows.Forms.TextBox txtAppSubnetAddressPrefix;
    }
}
