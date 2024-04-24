namespace App.ControlPanel.Frames.InstallWizardPages
{
    partial class AzureBaseConfigControl
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
            this.grpSubDetails = new System.Windows.Forms.GroupBox();
            this.lblGUIAzureConfigManualSubID = new System.Windows.Forms.Label();
            this.lblGUIAzureConfigManualSubName = new System.Windows.Forms.Label();
            this.txtSubscriptionId = new System.Windows.Forms.TextBox();
            this.txtSubscriptionName = new System.Windows.Forms.TextBox();
            this.btnSwitchSubscriptionInput = new System.Windows.Forms.LinkLabel();
            this.lblGUIAzureConfigSubsAvailable = new System.Windows.Forms.Label();
            this.cmbSubscriptions = new System.Windows.Forms.ComboBox();
            this.lblGUIAzureConfigSubscrHeader = new System.Windows.Forms.Label();
            this.lblGUIAzureConfigLocationDataCentre = new System.Windows.Forms.Label();
            this.cmbLocation = new System.Windows.Forms.ComboBox();
            this.lblGUIAzureConfigLocationHeader = new System.Windows.Forms.Label();
            this.lblGUIAzureConfigDesc = new System.Windows.Forms.Label();
            this.lblGUIAzureConfigHeader = new System.Windows.Forms.Label();
            this.lblGUIAzureConfigRGHeader = new System.Windows.Forms.Label();
            this.txtResourceGroup = new System.Windows.Forms.TextBox();
            this.lblGUIAzureConfigRGName = new System.Windows.Forms.Label();
            this.refreshSubbackgroundWorker = new System.ComponentModel.BackgroundWorker();
            this.label1 = new System.Windows.Forms.Label();
            this.cmbEnvType = new System.Windows.Forms.ComboBox();
            this.label2 = new System.Windows.Forms.Label();
            this.flowLayoutPanel1 = new System.Windows.Forms.FlowLayoutPanel();
            this.grpTestingTierDetails = new System.Windows.Forms.Panel();
            this.lnkSQLLevelBasic = new System.Windows.Forms.LinkLabel();
            this.label4 = new System.Windows.Forms.Label();
            this.lnkPlanLevelB1 = new System.Windows.Forms.LinkLabel();
            this.label3 = new System.Windows.Forms.Label();
            this.grpProdTierDetails = new System.Windows.Forms.Panel();
            this.lnkSQLLevelStandard = new System.Windows.Forms.LinkLabel();
            this.label5 = new System.Windows.Forms.Label();
            this.lnkPlanLevelB2 = new System.Windows.Forms.LinkLabel();
            this.label6 = new System.Windows.Forms.Label();
            this.label7 = new System.Windows.Forms.Label();
            this.label8 = new System.Windows.Forms.Label();
            this.pictureBox3 = new System.Windows.Forms.PictureBox();
            this.pictureBox2 = new System.Windows.Forms.PictureBox();
            this.btnRefreshSubs = new System.Windows.Forms.Button();
            this.pictureBox11 = new System.Windows.Forms.PictureBox();
            this.pictureBox10 = new System.Windows.Forms.PictureBox();
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.tagsEditor = new App.ControlPanel.Controls.TagsEditor();
            this.grpSubDetails.SuspendLayout();
            this.flowLayoutPanel1.SuspendLayout();
            this.grpTestingTierDetails.SuspendLayout();
            this.grpProdTierDetails.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox11)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox10)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            this.SuspendLayout();
            // 
            // grpSubDetails
            // 
            this.grpSubDetails.Controls.Add(this.lblGUIAzureConfigManualSubID);
            this.grpSubDetails.Controls.Add(this.lblGUIAzureConfigManualSubName);
            this.grpSubDetails.Controls.Add(this.txtSubscriptionId);
            this.grpSubDetails.Controls.Add(this.txtSubscriptionName);
            this.grpSubDetails.Location = new System.Drawing.Point(410, 148);
            this.grpSubDetails.Name = "grpSubDetails";
            this.grpSubDetails.Size = new System.Drawing.Size(295, 75);
            this.grpSubDetails.TabIndex = 133;
            this.grpSubDetails.TabStop = false;
            this.grpSubDetails.Text = "Manual subscription details:";
            // 
            // lblGUIAzureConfigManualSubID
            // 
            this.lblGUIAzureConfigManualSubID.AutoSize = true;
            this.lblGUIAzureConfigManualSubID.Location = new System.Drawing.Point(15, 47);
            this.lblGUIAzureConfigManualSubID.Name = "lblGUIAzureConfigManualSubID";
            this.lblGUIAzureConfigManualSubID.Size = new System.Drawing.Size(21, 13);
            this.lblGUIAzureConfigManualSubID.TabIndex = 118;
            this.lblGUIAzureConfigManualSubID.Text = "ID:";
            // 
            // lblGUIAzureConfigManualSubName
            // 
            this.lblGUIAzureConfigManualSubName.AutoSize = true;
            this.lblGUIAzureConfigManualSubName.Location = new System.Drawing.Point(15, 21);
            this.lblGUIAzureConfigManualSubName.Name = "lblGUIAzureConfigManualSubName";
            this.lblGUIAzureConfigManualSubName.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureConfigManualSubName.TabIndex = 117;
            this.lblGUIAzureConfigManualSubName.Text = "Name:";
            // 
            // txtSubscriptionId
            // 
            this.txtSubscriptionId.Location = new System.Drawing.Point(61, 44);
            this.txtSubscriptionId.Name = "txtSubscriptionId";
            this.txtSubscriptionId.Size = new System.Drawing.Size(215, 20);
            this.txtSubscriptionId.TabIndex = 8;
            // 
            // txtSubscriptionName
            // 
            this.txtSubscriptionName.Location = new System.Drawing.Point(61, 18);
            this.txtSubscriptionName.Name = "txtSubscriptionName";
            this.txtSubscriptionName.Size = new System.Drawing.Size(215, 20);
            this.txtSubscriptionName.TabIndex = 7;
            // 
            // btnSwitchSubscriptionInput
            // 
            this.btnSwitchSubscriptionInput.AutoSize = true;
            this.btnSwitchSubscriptionInput.Location = new System.Drawing.Point(62, 267);
            this.btnSwitchSubscriptionInput.Name = "btnSwitchSubscriptionInput";
            this.btnSwitchSubscriptionInput.Size = new System.Drawing.Size(136, 13);
            this.btnSwitchSubscriptionInput.TabIndex = 132;
            this.btnSwitchSubscriptionInput.TabStop = true;
            this.btnSwitchSubscriptionInput.Text = "btnSwitchSubscriptionInput";
            this.btnSwitchSubscriptionInput.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.btnSwitchSubscriptionInput_LinkClicked);
            // 
            // lblGUIAzureConfigSubsAvailable
            // 
            this.lblGUIAzureConfigSubsAvailable.AutoSize = true;
            this.lblGUIAzureConfigSubsAvailable.Location = new System.Drawing.Point(63, 246);
            this.lblGUIAzureConfigSubsAvailable.Name = "lblGUIAzureConfigSubsAvailable";
            this.lblGUIAzureConfigSubsAvailable.Size = new System.Drawing.Size(118, 13);
            this.lblGUIAzureConfigSubsAvailable.TabIndex = 131;
            this.lblGUIAzureConfigSubsAvailable.Text = "Subscriptions available:";
            // 
            // cmbSubscriptions
            // 
            this.cmbSubscriptions.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbSubscriptions.FormattingEnabled = true;
            this.cmbSubscriptions.Location = new System.Drawing.Point(187, 246);
            this.cmbSubscriptions.Name = "cmbSubscriptions";
            this.cmbSubscriptions.Size = new System.Drawing.Size(258, 21);
            this.cmbSubscriptions.TabIndex = 119;
            this.cmbSubscriptions.SelectedValueChanged += new System.EventHandler(this.cmbSubscriptions_SelectedValueChanged);
            // 
            // lblGUIAzureConfigSubscrHeader
            // 
            this.lblGUIAzureConfigSubscrHeader.AutoSize = true;
            this.lblGUIAzureConfigSubscrHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureConfigSubscrHeader.Location = new System.Drawing.Point(62, 224);
            this.lblGUIAzureConfigSubscrHeader.Name = "lblGUIAzureConfigSubscrHeader";
            this.lblGUIAzureConfigSubscrHeader.Size = new System.Drawing.Size(233, 19);
            this.lblGUIAzureConfigSubscrHeader.TabIndex = 130;
            this.lblGUIAzureConfigSubscrHeader.Text = "Subscription for Azure Resources";
            // 
            // lblGUIAzureConfigLocationDataCentre
            // 
            this.lblGUIAzureConfigLocationDataCentre.AutoSize = true;
            this.lblGUIAzureConfigLocationDataCentre.Location = new System.Drawing.Point(63, 161);
            this.lblGUIAzureConfigLocationDataCentre.Name = "lblGUIAzureConfigLocationDataCentre";
            this.lblGUIAzureConfigLocationDataCentre.Size = new System.Drawing.Size(66, 13);
            this.lblGUIAzureConfigLocationDataCentre.TabIndex = 128;
            this.lblGUIAzureConfigLocationDataCentre.Text = "Data centre:";
            // 
            // cmbLocation
            // 
            this.cmbLocation.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbLocation.FormattingEnabled = true;
            this.cmbLocation.Location = new System.Drawing.Point(187, 161);
            this.cmbLocation.Name = "cmbLocation";
            this.cmbLocation.Size = new System.Drawing.Size(180, 21);
            this.cmbLocation.TabIndex = 118;
            // 
            // lblGUIAzureConfigLocationHeader
            // 
            this.lblGUIAzureConfigLocationHeader.AutoSize = true;
            this.lblGUIAzureConfigLocationHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureConfigLocationHeader.Location = new System.Drawing.Point(62, 139);
            this.lblGUIAzureConfigLocationHeader.Name = "lblGUIAzureConfigLocationHeader";
            this.lblGUIAzureConfigLocationHeader.Size = new System.Drawing.Size(206, 19);
            this.lblGUIAzureConfigLocationHeader.TabIndex = 127;
            this.lblGUIAzureConfigLocationHeader.Text = "Location for Azure Resources";
            // 
            // lblGUIAzureConfigDesc
            // 
            this.lblGUIAzureConfigDesc.AutoSize = true;
            this.lblGUIAzureConfigDesc.Location = new System.Drawing.Point(-3, 19);
            this.lblGUIAzureConfigDesc.Name = "lblGUIAzureConfigDesc";
            this.lblGUIAzureConfigDesc.Size = new System.Drawing.Size(323, 13);
            this.lblGUIAzureConfigDesc.TabIndex = 125;
            this.lblGUIAzureConfigDesc.Text = "Where should the setup wizard create Azure PaaS for the solution?";
            // 
            // lblGUIAzureConfigHeader
            // 
            this.lblGUIAzureConfigHeader.AutoSize = true;
            this.lblGUIAzureConfigHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureConfigHeader.Location = new System.Drawing.Point(-4, 0);
            this.lblGUIAzureConfigHeader.Name = "lblGUIAzureConfigHeader";
            this.lblGUIAzureConfigHeader.Size = new System.Drawing.Size(145, 19);
            this.lblGUIAzureConfigHeader.TabIndex = 124;
            this.lblGUIAzureConfigHeader.Text = "Azure Configuration";
            // 
            // lblGUIAzureConfigRGHeader
            // 
            this.lblGUIAzureConfigRGHeader.AutoSize = true;
            this.lblGUIAzureConfigRGHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureConfigRGHeader.Location = new System.Drawing.Point(62, 54);
            this.lblGUIAzureConfigRGHeader.Name = "lblGUIAzureConfigRGHeader";
            this.lblGUIAzureConfigRGHeader.Size = new System.Drawing.Size(118, 19);
            this.lblGUIAzureConfigRGHeader.TabIndex = 123;
            this.lblGUIAzureConfigRGHeader.Text = "Resource Group";
            // 
            // txtResourceGroup
            // 
            this.txtResourceGroup.Location = new System.Drawing.Point(187, 78);
            this.txtResourceGroup.Name = "txtResourceGroup";
            this.txtResourceGroup.Size = new System.Drawing.Size(180, 20);
            this.txtResourceGroup.TabIndex = 117;
            this.txtResourceGroup.Text = "txtResourceGroup";
            // 
            // lblGUIAzureConfigRGName
            // 
            this.lblGUIAzureConfigRGName.AutoSize = true;
            this.lblGUIAzureConfigRGName.Location = new System.Drawing.Point(63, 78);
            this.lblGUIAzureConfigRGName.Name = "lblGUIAzureConfigRGName";
            this.lblGUIAzureConfigRGName.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureConfigRGName.TabIndex = 121;
            this.lblGUIAzureConfigRGName.Text = "Name:";
            // 
            // refreshSubbackgroundWorker
            // 
            this.refreshSubbackgroundWorker.DoWork += new System.ComponentModel.DoWorkEventHandler(this.refreshSubbackgroundWorker_DoWork);
            this.refreshSubbackgroundWorker.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.refreshSubbackgroundWorker_RunWorkerCompleted);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(63, 416);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(108, 13);
            this.label1.TabIndex = 137;
            this.label1.Text = "Starting performance:";
            // 
            // cmbEnvType
            // 
            this.cmbEnvType.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbEnvType.FormattingEnabled = true;
            this.cmbEnvType.Items.AddRange(new object[] {
            "Testing",
            "Production"});
            this.cmbEnvType.Location = new System.Drawing.Point(187, 416);
            this.cmbEnvType.Name = "cmbEnvType";
            this.cmbEnvType.Size = new System.Drawing.Size(180, 21);
            this.cmbEnvType.TabIndex = 134;
            this.cmbEnvType.SelectedIndexChanged += new System.EventHandler(this.cmbEnvType_SelectedIndexChanged);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.Location = new System.Drawing.Point(62, 394);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(288, 19);
            this.label2.TabIndex = 136;
            this.label2.Text = "Environment Type (New Resources Only)";
            // 
            // flowLayoutPanel1
            // 
            this.flowLayoutPanel1.Controls.Add(this.grpTestingTierDetails);
            this.flowLayoutPanel1.Controls.Add(this.grpProdTierDetails);
            this.flowLayoutPanel1.Location = new System.Drawing.Point(62, 439);
            this.flowLayoutPanel1.Name = "flowLayoutPanel1";
            this.flowLayoutPanel1.Size = new System.Drawing.Size(379, 87);
            this.flowLayoutPanel1.TabIndex = 138;
            // 
            // grpTestingTierDetails
            // 
            this.grpTestingTierDetails.Controls.Add(this.lnkSQLLevelBasic);
            this.grpTestingTierDetails.Controls.Add(this.label4);
            this.grpTestingTierDetails.Controls.Add(this.lnkPlanLevelB1);
            this.grpTestingTierDetails.Controls.Add(this.label3);
            this.grpTestingTierDetails.Location = new System.Drawing.Point(3, 3);
            this.grpTestingTierDetails.Name = "grpTestingTierDetails";
            this.grpTestingTierDetails.Size = new System.Drawing.Size(308, 36);
            this.grpTestingTierDetails.TabIndex = 0;
            // 
            // lnkSQLLevelBasic
            // 
            this.lnkSQLLevelBasic.AutoSize = true;
            this.lnkSQLLevelBasic.Location = new System.Drawing.Point(168, 23);
            this.lnkSQLLevelBasic.Name = "lnkSQLLevelBasic";
            this.lnkSQLLevelBasic.Size = new System.Drawing.Size(130, 13);
            this.lnkSQLLevelBasic.TabIndex = 3;
            this.lnkSQLLevelBasic.TabStop = true;
            this.lnkSQLLevelBasic.Text = "Basic pricing tier (5 DTUs)";
            this.lnkSQLLevelBasic.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkSQLLevelBasic_LinkClicked);
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(3, 23);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(166, 13);
            this.label4.TabIndex = 2;
            this.label4.Text = "- SQL Database will be created at";
            // 
            // lnkPlanLevelB1
            // 
            this.lnkPlanLevelB1.AutoSize = true;
            this.lnkPlanLevelB1.Location = new System.Drawing.Point(176, 0);
            this.lnkPlanLevelB1.Name = "lnkPlanLevelB1";
            this.lnkPlanLevelB1.Size = new System.Drawing.Size(41, 13);
            this.lnkPlanLevelB1.TabIndex = 1;
            this.lnkPlanLevelB1.TabStop = true;
            this.lnkPlanLevelB1.Text = "B1 Tier";
            this.lnkPlanLevelB1.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkPlanLevelB1_LinkClicked);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(3, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(175, 13);
            this.label3.TabIndex = 0;
            this.label3.Text = "- App service plan will be created at";
            // 
            // grpProdTierDetails
            // 
            this.grpProdTierDetails.Controls.Add(this.lnkSQLLevelStandard);
            this.grpProdTierDetails.Controls.Add(this.label5);
            this.grpProdTierDetails.Controls.Add(this.lnkPlanLevelB2);
            this.grpProdTierDetails.Controls.Add(this.label6);
            this.grpProdTierDetails.Location = new System.Drawing.Point(3, 45);
            this.grpProdTierDetails.Name = "grpProdTierDetails";
            this.grpProdTierDetails.Size = new System.Drawing.Size(340, 36);
            this.grpProdTierDetails.TabIndex = 1;
            // 
            // lnkSQLLevelStandard
            // 
            this.lnkSQLLevelStandard.AutoSize = true;
            this.lnkSQLLevelStandard.Location = new System.Drawing.Point(168, 23);
            this.lnkSQLLevelStandard.Name = "lnkSQLLevelStandard";
            this.lnkSQLLevelStandard.Size = new System.Drawing.Size(153, 13);
            this.lnkSQLLevelStandard.TabIndex = 3;
            this.lnkSQLLevelStandard.TabStop = true;
            this.lnkSQLLevelStandard.Text = "Standard pricing tier (20 DTUs)";
            this.lnkSQLLevelStandard.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkSQLLevelStandard_LinkClicked);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(3, 23);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(166, 13);
            this.label5.TabIndex = 2;
            this.label5.Text = "- SQL Database will be created at";
            // 
            // lnkPlanLevelB2
            // 
            this.lnkPlanLevelB2.AutoSize = true;
            this.lnkPlanLevelB2.Location = new System.Drawing.Point(176, 0);
            this.lnkPlanLevelB2.Name = "lnkPlanLevelB2";
            this.lnkPlanLevelB2.Size = new System.Drawing.Size(41, 13);
            this.lnkPlanLevelB2.TabIndex = 1;
            this.lnkPlanLevelB2.TabStop = true;
            this.lnkPlanLevelB2.Text = "B2 Tier";
            this.lnkPlanLevelB2.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkPlanLevelB2_LinkClicked);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(3, 0);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(175, 13);
            this.label6.TabIndex = 0;
            this.label6.Text = "- App service plan will be created at";
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(63, 331);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(118, 13);
            this.label7.TabIndex = 141;
            this.label7.Text = "Create on all resources:";
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label8.Location = new System.Drawing.Point(62, 309);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(177, 19);
            this.label8.TabIndex = 140;
            this.label8.Text = "Tags for Azure Resources";
            // 
            // pictureBox3
            // 
            this.pictureBox3.Image = global::App.ControlPanel.Properties.Resources.tag;
            this.pictureBox3.Location = new System.Drawing.Point(0, 309);
            this.pictureBox3.Name = "pictureBox3";
            this.pictureBox3.Size = new System.Drawing.Size(56, 56);
            this.pictureBox3.TabIndex = 139;
            this.pictureBox3.TabStop = false;
            // 
            // pictureBox2
            // 
            this.pictureBox2.Image = global::App.ControlPanel.Properties.Resources.autoscale;
            this.pictureBox2.Location = new System.Drawing.Point(0, 394);
            this.pictureBox2.Name = "pictureBox2";
            this.pictureBox2.Size = new System.Drawing.Size(56, 56);
            this.pictureBox2.TabIndex = 135;
            this.pictureBox2.TabStop = false;
            // 
            // btnRefreshSubs
            // 
            this.btnRefreshSubs.Image = global::App.ControlPanel.Properties.Resources.Refresh_grey_16xMD;
            this.btnRefreshSubs.Location = new System.Drawing.Point(451, 246);
            this.btnRefreshSubs.Name = "btnRefreshSubs";
            this.btnRefreshSubs.Size = new System.Drawing.Size(31, 21);
            this.btnRefreshSubs.TabIndex = 120;
            this.btnRefreshSubs.UseVisualStyleBackColor = true;
            this.btnRefreshSubs.Click += new System.EventHandler(this.btnRefreshSubs_Click);
            // 
            // pictureBox11
            // 
            this.pictureBox11.Image = global::App.ControlPanel.Properties.Resources.Azure_subscription;
            this.pictureBox11.Location = new System.Drawing.Point(0, 224);
            this.pictureBox11.Name = "pictureBox11";
            this.pictureBox11.Size = new System.Drawing.Size(56, 56);
            this.pictureBox11.TabIndex = 129;
            this.pictureBox11.TabStop = false;
            // 
            // pictureBox10
            // 
            this.pictureBox10.Image = global::App.ControlPanel.Properties.Resources.Azure_Stack;
            this.pictureBox10.Location = new System.Drawing.Point(0, 139);
            this.pictureBox10.Name = "pictureBox10";
            this.pictureBox10.Size = new System.Drawing.Size(56, 56);
            this.pictureBox10.TabIndex = 126;
            this.pictureBox10.TabStop = false;
            // 
            // pictureBox1
            // 
            this.pictureBox1.Image = global::App.ControlPanel.Properties.Resources.ResourceGroup;
            this.pictureBox1.Location = new System.Drawing.Point(0, 54);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(56, 56);
            this.pictureBox1.TabIndex = 122;
            this.pictureBox1.TabStop = false;
            // 
            // tagsEditor
            // 
            this.tagsEditor.Location = new System.Drawing.Point(187, 331);
            this.tagsEditor.Name = "tagsEditor";
            this.tagsEditor.Size = new System.Drawing.Size(295, 52);
            this.tagsEditor.TabIndex = 142;
            // 
            // AzureBaseConfigControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.tagsEditor);
            this.Controls.Add(this.label7);
            this.Controls.Add(this.label8);
            this.Controls.Add(this.pictureBox3);
            this.Controls.Add(this.flowLayoutPanel1);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.cmbEnvType);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.pictureBox2);
            this.Controls.Add(this.grpSubDetails);
            this.Controls.Add(this.btnSwitchSubscriptionInput);
            this.Controls.Add(this.btnRefreshSubs);
            this.Controls.Add(this.lblGUIAzureConfigSubsAvailable);
            this.Controls.Add(this.cmbSubscriptions);
            this.Controls.Add(this.lblGUIAzureConfigSubscrHeader);
            this.Controls.Add(this.lblGUIAzureConfigLocationDataCentre);
            this.Controls.Add(this.cmbLocation);
            this.Controls.Add(this.lblGUIAzureConfigLocationHeader);
            this.Controls.Add(this.lblGUIAzureConfigDesc);
            this.Controls.Add(this.lblGUIAzureConfigHeader);
            this.Controls.Add(this.lblGUIAzureConfigRGHeader);
            this.Controls.Add(this.txtResourceGroup);
            this.Controls.Add(this.lblGUIAzureConfigRGName);
            this.Controls.Add(this.pictureBox11);
            this.Controls.Add(this.pictureBox10);
            this.Controls.Add(this.pictureBox1);
            this.Name = "AzureBaseConfigControl";
            this.Size = new System.Drawing.Size(524, 521);
            this.Load += new System.EventHandler(this.AzureBaseConfigControl_Load);
            this.grpSubDetails.ResumeLayout(false);
            this.grpSubDetails.PerformLayout();
            this.flowLayoutPanel1.ResumeLayout(false);
            this.grpTestingTierDetails.ResumeLayout(false);
            this.grpTestingTierDetails.PerformLayout();
            this.grpProdTierDetails.ResumeLayout(false);
            this.grpProdTierDetails.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox11)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox10)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.GroupBox grpSubDetails;
        private System.Windows.Forms.Label lblGUIAzureConfigManualSubID;
        private System.Windows.Forms.Label lblGUIAzureConfigManualSubName;
        private System.Windows.Forms.TextBox txtSubscriptionId;
        private System.Windows.Forms.TextBox txtSubscriptionName;
        private System.Windows.Forms.LinkLabel btnSwitchSubscriptionInput;
        private System.Windows.Forms.Button btnRefreshSubs;
        private System.Windows.Forms.Label lblGUIAzureConfigSubsAvailable;
        private System.Windows.Forms.ComboBox cmbSubscriptions;
        private System.Windows.Forms.Label lblGUIAzureConfigSubscrHeader;
        private System.Windows.Forms.Label lblGUIAzureConfigLocationDataCentre;
        private System.Windows.Forms.ComboBox cmbLocation;
        private System.Windows.Forms.Label lblGUIAzureConfigLocationHeader;
        private System.Windows.Forms.Label lblGUIAzureConfigDesc;
        private System.Windows.Forms.Label lblGUIAzureConfigHeader;
        private System.Windows.Forms.Label lblGUIAzureConfigRGHeader;
        private System.Windows.Forms.TextBox txtResourceGroup;
        private System.Windows.Forms.Label lblGUIAzureConfigRGName;
        private System.Windows.Forms.PictureBox pictureBox11;
        private System.Windows.Forms.PictureBox pictureBox10;
        private System.Windows.Forms.PictureBox pictureBox1;
        private System.ComponentModel.BackgroundWorker refreshSubbackgroundWorker;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.ComboBox cmbEnvType;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.PictureBox pictureBox2;
        private System.Windows.Forms.FlowLayoutPanel flowLayoutPanel1;
        private System.Windows.Forms.Panel grpTestingTierDetails;
        private System.Windows.Forms.LinkLabel lnkSQLLevelBasic;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.LinkLabel lnkPlanLevelB1;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Panel grpProdTierDetails;
        private System.Windows.Forms.LinkLabel lnkSQLLevelStandard;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.LinkLabel lnkPlanLevelB2;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.PictureBox pictureBox3;
        private Controls.TagsEditor tagsEditor;
    }
}
